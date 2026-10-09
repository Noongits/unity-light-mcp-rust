//! Script adapters preserve the server-side coordinate, routing, and concurrency contract.
use crate::bridge::UnityBridge;
use anyhow::{anyhow, Result};
use base64::{engine::general_purpose::STANDARD, Engine};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

fn s<'a>(v: &'a Value, k: &str) -> &'a str {
    v.get(k).and_then(Value::as_str).unwrap_or("")
}
fn truth(v: &Value) -> bool {
    match v {
        Value::Null => false,
        Value::Bool(b) => *b,
        Value::String(s) => !s.is_empty(),
        Value::Array(a) => !a.is_empty(),
        Value::Object(m) => !m.is_empty(),
        Value::Number(n) => n.as_f64() != Some(0.0),
    }
}
fn int(v: &Value, default: i64) -> i64 {
    v.as_i64()
        .or_else(|| v.as_str().and_then(|s| s.parse().ok()))
        .unwrap_or(default)
}
fn err(code: &str, message: impl Into<String>) -> Value {
    json!({"success":false,"code":code,"message":message.into()})
}
fn success(v: &Value) -> bool {
    v.get("success").is_some_and(truth)
}
fn object(v: &Value) -> Value {
    if v.is_object() {
        v.clone()
    } else {
        json!({})
    }
}
fn norm_response(v: Value) -> Value {
    if v.is_object() {
        v
    } else {
        json!({"success":false,"message":v.to_string()})
    }
}
fn echo(mut v: Value, edits: &[Value], routing: Option<&str>) -> Value {
    if !v.is_object() {
        v = norm_response(v);
    }
    if !v["data"].is_object() {
        v["data"] = json!({});
    }
    if v["data"].get("normalizedEdits").is_none() {
        v["data"]["normalizedEdits"] = json!(edits);
    }
    if let Some(r) = routing {
        v["data"]["routing"] = json!(r);
    }
    v
}
fn normalize_path(path: &str) -> String {
    let path = path.replace('\\', "/");
    let absolute = path.starts_with('/');
    let mut out = Vec::new();
    for p in path.split('/') {
        match p {
            "" | "." => {}
            ".." if out.last().is_some_and(|p| *p != "..") => {
                out.pop();
            }
            ".." if !absolute => out.push(p),
            ".." => {}
            _ => out.push(p),
        }
    }
    if out.is_empty() && !absolute {
        return ".".into();
    }
    let prefix = if path.starts_with("//") && !path.starts_with("///") {
        "//"
    } else if absolute {
        "/"
    } else {
        ""
    };
    format!("{}{}", prefix, out.join("/"))
}
pub fn split_uri(uri: &str) -> (String, String) {
    let mut raw = if let Some(p) = uri.strip_prefix("mcpforunity://path/") {
        p.to_owned()
    } else if uri.starts_with("file://") {
        if let Ok(u) = url::Url::parse(uri) {
            let host = u.host_str().unwrap_or("");
            let p = if host.is_empty() || host.eq_ignore_ascii_case("localhost") {
                u.path().to_owned()
            } else {
                format!("//{host}{}", u.path())
            };
            percent_encoding::percent_decode_str(&p)
                .decode_utf8_lossy()
                .into_owned()
        } else {
            uri.trim_start_matches("file://").to_owned()
        }
    } else {
        uri.to_owned()
    };
    raw = percent_encoding::percent_decode_str(&raw)
        .decode_utf8_lossy()
        .into_owned();
    let norm = normalize_path(&raw);
    let parts: Vec<_> = norm.split('/').filter(|p| !p.is_empty()).collect();
    let effective = if let Some(i) = parts.iter().position(|p| p.eq_ignore_ascii_case("assets")) {
        parts[i..].join("/")
    } else {
        norm.strip_prefix('/').unwrap_or(&norm).to_owned()
    };
    let (dir, file) = effective.rsplit_once('/').unwrap_or(("", &effective));
    let name = file
        .rsplit_once('.')
        .map(|(n, _)| if n.is_empty() { file } else { n })
        .unwrap_or(file);
    (name.to_owned(), dir.to_owned())
}
fn normalize_locator(name: &str, path: &str) -> (String, String) {
    let n = name.trim();
    let p = path.trim();
    for value in [n, p] {
        let v = value
            .strip_prefix("mcpforunity://path/")
            .or_else(|| value.strip_prefix("file://"))
            .unwrap_or(value);
        if v.ends_with(".cs") || v.starts_with("Assets/") {
            let mut parts: Vec<_> = v.split('/').collect();
            if parts.len() > 1 && parts[parts.len() - 1] == parts[parts.len() - 2] {
                parts.pop();
            }
            let mut candidate = parts.join("/");
            if !candidate.ends_with(".cs") && n.ends_with(".cs") {
                candidate = format!(
                    "{}/{}",
                    candidate.trim_end_matches('/'),
                    n.rsplit('/').next().unwrap_or(n)
                );
            }
            if candidate.ends_with(".cs") {
                let (dir, file) = candidate.rsplit_once('/').unwrap_or(("Assets", &candidate));
                return (file[..file.len() - 3].into(), dir.into());
            }
            break;
        }
    }
    (
        n.strip_suffix(".cs").unwrap_or(n).into(),
        if p.is_empty() {
            "Assets".into()
        } else {
            p.into()
        },
    )
}
fn contents(v: &Value) -> Option<String> {
    let d = if v["data"].is_object() {
        &v["data"]
    } else {
        &v["result"]["data"]
    };
    if let Some(t) = d["contents"].as_str() {
        if !t.is_empty() || !truth(&d["contentsEncoded"]) {
            return Some(t.into());
        }
    }
    if truth(&d["contentsEncoded"]) {
        if let Ok(bytes) = STANDARD.decode(s(d, "encodedContents")) {
            return Some(String::from_utf8_lossy(&bytes).into_owned());
        }
    }
    d["contents"].as_str().map(str::to_owned)
}
fn lc(text: &str, index: usize) -> (usize, usize) {
    let mut line = 1;
    let mut col = 1;
    for c in text.chars().take(index) {
        if c == '\n' {
            line += 1;
            col = 1;
        } else {
            col += 1;
        }
    }
    if index > text.chars().count() {
        col += index - text.chars().count();
    }
    (line, col)
}
fn byte_lc(text: &str, index: usize) -> (usize, usize) {
    lc(text, text[..index].chars().count())
}
fn span(text: &str, start: usize, end: usize, replacement: &str) -> Value {
    let (a, b) = byte_lc(text, start);
    let (c, d) = byte_lc(text, end);
    json!({"startLine":a,"startCol":b,"endLine":c,"endCol":d,"newText":replacement})
}
async fn read(
    bridge: &dyn UnityBridge,
    name: &str,
    path: &str,
    instance: Option<&str>,
) -> Result<Value> {
    bridge
        .send(
            "manage_script",
            json!({"action":"read","name":name,"path":path}),
            instance,
        )
        .await
}
async fn wait_ready(bridge: &dyn UnityBridge, instance: Option<&str>) -> bool {
    crate::tools::wait_for_editor_ready(bridge, instance, std::time::Duration::from_secs(30)).await
}

async fn mutation(
    bridge: &dyn UnityBridge,
    params: Value,
    instance: Option<&str>,
    pre_sha: Option<&str>,
) -> Result<Value> {
    let mut response = match bridge.send("manage_script", params.clone(), instance).await {
        Ok(v) => v,
        Err(e) => json!({"success":false,"error":e.to_string()}),
    };
    if !success(&response)
        && response["data"]["reason"] == "reloading"
        && response["hint"] == "retry"
    {
        if !wait_ready(bridge, instance).await {
            return Ok(norm_response(response));
        }
        response = bridge
            .send("manage_script", params.clone(), instance)
            .await
            .unwrap_or_else(|e| json!({"success":false,"error":e.to_string()}));
    }
    let message = response
        .get("error")
        .or_else(|| response.get("message"))
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_lowercase();
    if !success(&response)
        && ["connection closed", "disconnected", "aborted"]
            .iter()
            .any(|s| message.contains(s))
    {
        wait_ready(bridge, instance).await;
        let action = s(&params, "action");
        let name = s(&params, "name");
        let path = s(&params, "path");
        if matches!(action, "create" | "delete") {
            if let Ok(v) = read(bridge, name, path, instance).await {
                let expected = if truth(&params["contentsEncoded"]) {
                    STANDARD
                        .decode(s(&params, "encodedContents"))
                        .ok()
                        .and_then(|bytes| String::from_utf8(bytes).ok())
                } else {
                    params["contents"].as_str().map(str::to_owned)
                };
                if action == "create"
                    && success(&v)
                    && expected
                        .as_ref()
                        .is_some_and(|expected| contents(&v).as_ref() == Some(expected))
                {
                    response = json!({"success":true,"message":"Script created (verified after domain reload).","data":v["data"]});
                }
                if action == "delete"
                    && !success(&v)
                    && ["error", "message"]
                        .iter()
                        .any(|key| s(&v, key).starts_with("Script not found at '"))
                {
                    response = json!({"success":true,"message":"Script deleted (verified after domain reload)."});
                }
            }
        } else if let Some(sha) = pre_sha.filter(|s| !s.is_empty()) {
            if let Ok(v) = bridge
                .send(
                    "manage_script",
                    json!({"action":"get_sha","name":name,"path":path}),
                    instance,
                )
                .await
            {
                if success(&v)
                    && !s(&v["data"], "sha256").is_empty()
                    && s(&v["data"], "sha256") != sha
                {
                    // A different writer or a partial operation can also change the SHA.
                    // Preserve the failure: changed content is not proof of this edit.
                    if !response["data"].is_object() {
                        response["data"] = json!({});
                    }
                    response["data"]["outcome_unknown"] = json!(true);
                    response["data"]["observed_sha256"] = v["data"]["sha256"].clone();
                }
            }
        }
    }
    wait_ready(bridge, instance).await;
    Ok(norm_response(response))
}

pub async fn call(
    bridge: &dyn UnityBridge,
    name: &str,
    args: Value,
    instance: Option<&str>,
) -> Result<Value> {
    match name {
        "apply_text_edits" => apply_text(bridge, args, instance).await,
        "script_apply_edits" => structured(bridge, args, instance).await,
        "find_in_file" => find(bridge, args, instance).await,
        "manage_script" => {
            let action = s(&args, "action");
            let mut p = json!({"action":action,"name":args["name"],"path":args["path"]});
            for (a, b) in [("namespace", "namespace"), ("script_type", "scriptType")] {
                if !args[a].is_null() {
                    p[b] = args[a].clone();
                }
            }
            if !s(&args, "contents").is_empty() {
                if action == "create" {
                    p["encodedContents"] = json!(STANDARD.encode(s(&args, "contents")));
                    p["contentsEncoded"] = json!(true);
                } else {
                    p["contents"] = args["contents"].clone();
                }
            }
            let result = if action == "read" {
                bridge.send("manage_script", p, instance).await
            } else {
                mutation(bridge, p, instance, None).await
            };
            match result {
                Ok(mut r) => {
                    if success(&r) {
                        if truth(&r["data"]["contentsEncoded"]) {
                            if let Some(c) = contents(&r) {
                                r["data"]["contents"] = json!(c);
                                if let Some(m) = r["data"].as_object_mut() {
                                    m.remove("encodedContents");
                                    m.remove("contentsEncoded");
                                }
                            }
                        }
                        Ok(
                            json!({"success":true,"message":r.get("message").cloned().unwrap_or(json!("Operation successful.")),"data":r["data"]}),
                        )
                    } else {
                        Ok(norm_response(r))
                    }
                }
                Err(e) => Ok(
                    json!({"success":false,"message":format!("Rust error managing script: {e}")}),
                ),
            }
        }
        "create_script" => {
            let path = s(&args, "path");
            let (dir, file) = path.rsplit_once('/').unwrap_or(("", path));
            if !dir
                .split('/')
                .next()
                .unwrap_or("")
                .eq_ignore_ascii_case("assets")
            {
                return Ok(err(
                    "path_outside_assets",
                    format!("path must be under 'Assets/'; got '{path}'."),
                ));
            }
            let norm = normalize_path(path);
            if norm.split('/').any(|p| p == "..")
                || norm.starts_with('/')
                || !norm
                    .split('/')
                    .next()
                    .unwrap_or("")
                    .eq_ignore_ascii_case("assets")
            {
                return Ok(err(
                    "bad_path",
                    "path must not contain traversal or be absolute.",
                ));
            }
            let name = file.rsplit_once('.').map(|x| x.0).unwrap_or(file);
            if name.is_empty() {
                return Ok(err("bad_path", "path must include a script file name."));
            }
            if !norm.to_lowercase().ends_with(".cs") {
                return Ok(err("bad_extension", "script file must end with .cs."));
            }
            let mut p = json!({"action":"create","name":name,"path":dir});
            for (a, b) in [("namespace", "namespace"), ("script_type", "scriptType")] {
                if !args[a].is_null() {
                    p[b] = args[a].clone();
                }
            }
            if !s(&args, "contents").is_empty() {
                p["encodedContents"] = json!(STANDARD.encode(s(&args, "contents")));
                p["contentsEncoded"] = json!(true);
            }
            mutation(bridge, p, instance, None).await
        }
        "delete_script" | "validate_script" | "get_sha" => {
            let (n, p) = split_uri(s(&args, "uri"));
            if name != "get_sha"
                && !p
                    .split('/')
                    .next()
                    .unwrap_or("")
                    .eq_ignore_ascii_case("assets")
            {
                return Ok(err(
                    "path_outside_assets",
                    "URI must resolve under 'Assets/'.",
                ));
            }
            let action = match name {
                "delete_script" => "delete",
                "validate_script" => "validate",
                _ => "get_sha",
            };
            let mut params = json!({"action":action,"name":n,"path":p});
            if name == "validate_script" {
                let level = args["level"].as_str().unwrap_or("basic");
                if !["basic", "standard"].contains(&level) {
                    return Ok(err("bad_level", "level must be 'basic' or 'standard'."));
                }
                params["level"] = json!(level);
            }
            let r = if action == "delete" {
                mutation(bridge, params, instance, None).await?
            } else {
                bridge.send("manage_script", params, instance).await?
            };
            if success(&r) && action == "get_sha" {
                return Ok(
                    json!({"success":true,"data":{"sha256":r["data"]["sha256"],"lengthBytes":r["data"]["lengthBytes"]}}),
                );
            }
            if success(&r) && action == "validate" {
                let diags = r["data"]["diagnostics"]
                    .as_array()
                    .cloned()
                    .unwrap_or_default();
                let warnings = diags
                    .iter()
                    .filter(|d| s(d, "severity").eq_ignore_ascii_case("warning"))
                    .count();
                let errors = diags
                    .iter()
                    .filter(|d| {
                        ["error", "fatal"].contains(&s(d, "severity").to_lowercase().as_str())
                    })
                    .count();
                return Ok(if truth(&args["include_diagnostics"]) {
                    json!({"success":true,"data":{"diagnostics":diags,"summary":{"warnings":warnings,"errors":errors}}})
                } else {
                    json!({"success":true,"data":{"warnings":warnings,"errors":errors}})
                });
            }
            Ok(norm_response(r))
        }
        _ => Err(anyhow!("Unknown script tool: {name}")),
    }
}

async fn apply_text(
    bridge: &dyn UnityBridge,
    args: Value,
    instance: Option<&str>,
) -> Result<Value> {
    let (n, p) = split_uri(s(&args, "uri"));
    let Some(edits) = args["edits"].as_array() else {
        return Ok(err("missing_field", "edits must be a list"));
    };
    let fields = ["startLine", "startCol", "endLine", "endCol"];
    let needs = edits.iter().any(|e| {
        fields.iter().any(|k| e.get(k).is_none())
            || (e.get("newText").is_none() && e.get("text").is_some())
    });
    let text = if needs {
        let r = read(bridge, &n, &p, instance).await?;
        if !success(&r) {
            return Ok(norm_response(r));
        }
        contents(&r).unwrap_or_default()
    } else {
        String::new()
    };
    if needs {
        if let Some(expected) = args["precondition_sha256"]
            .as_str()
            .filter(|s| !s.is_empty())
        {
            let actual = format!("{:x}", Sha256::digest(text.as_bytes()));
            if !actual.eq_ignore_ascii_case(expected) {
                return Ok(err(
                    "stale_file",
                    "Script changed before range normalization; read it again before editing.",
                ));
            }
        }
    }
    let mut normalized = Vec::new();
    let mut warnings = Vec::new();
    for e in edits {
        if !e.is_object() {
            return Ok(err("missing_field", "Each edit must be an object"));
        }
        let mut e = e.clone();
        if e.get("newText").is_none() {
            if let Some(t) = e.as_object_mut().unwrap().remove("text") {
                e["newText"] = t;
            }
        }
        if fields.iter().all(|k| e.get(k).is_some()) {
            if fields.iter().any(|k| int(&e[k], 1) < 1) {
                if truth(&args["strict"]) {
                    return Ok(
                        json!({"success":false,"code":"zero_based_explicit_fields","message":"Explicit line/col fields are 1-based; received zero-based.","data":{"normalizedEdits":if needs {normalized.clone()}else{vec![e]}}}),
                    );
                }
                for k in fields {
                    if int(&e[k], 1) < 1 {
                        e[k] = json!(1);
                    }
                }
                if !warnings.contains(&"zero_based_explicit_fields_normalized") {
                    warnings.push("zero_based_explicit_fields_normalized");
                }
            }
        } else if e["range"].is_object() {
            let r = e["range"].clone();
            for (key, side, axis) in [
                ("startLine", "start", "line"),
                ("startCol", "start", "character"),
                ("endLine", "end", "line"),
                ("endCol", "end", "character"),
            ] {
                e[key] = json!(int(&r[side][axis], 0) + 1);
            }
            e.as_object_mut().unwrap().remove("range");
        } else if let Some(r) = e["range"].as_array().filter(|r| r.len() == 2) {
            let a = int(&r[0], 0);
            let b = int(&r[1], 0);
            let (sl, sc) = lc(&text, a.min(b).max(0) as usize);
            let (el, ec) = lc(&text, a.max(b).max(0) as usize);
            for (k, v) in [
                ("startLine", sl),
                ("startCol", sc),
                ("endLine", el),
                ("endCol", ec),
            ] {
                e[k] = json!(v);
            }
            e.as_object_mut().unwrap().remove("range");
        } else {
            return Ok(
                json!({"success":false,"code":"missing_field","message":"apply_text_edits requires startLine/startCol/endLine/endCol/newText or a normalizable 'range'","data":{"expected":["startLine","startCol","endLine","endCol","newText"],"got":e}}),
            );
        }
        normalized.push(e);
    }
    let mut spans: Vec<_> = normalized
        .iter()
        .filter_map(|e| {
            let a = (int(&e["startLine"], 1), int(&e["startCol"], 1));
            let b = (int(&e["endLine"], 1), int(&e["endCol"], 1));
            if a == b {
                None
            } else {
                Some((a, b))
            }
        })
        .collect();
    spans.sort();
    for w in spans.windows(2) {
        if w[0].1 > w[1].0 {
            let (a, b) = w[0];
            let (c, d) = w[1];
            return Ok(
                json!({"success":false,"code":"overlap","data":{"status":"overlap","conflicts":[{"startA":{"line":a.0,"col":a.1},"endA":{"line":b.0,"col":b.1},"startB":{"line":c.0,"col":c.1},"endB":{"line":d.0,"col":d.1}}]}}),
            );
        }
    }
    let mut options = object(&args["options"]);
    if normalized.len() > 1 && options.get("applyMode").is_none() {
        options["applyMode"] = json!("atomic");
    }
    if truth(&options["debug_preview"]) {
        return Ok(
            json!({"success":true,"message":"Preview only (no write)","data":{"normalizedEdits":normalized,"preview":true}}),
        );
    }
    let mut params =
        json!({"action":"apply_text_edits","name":n,"path":p,"edits":normalized,"options":options});
    let pre = args["precondition_sha256"].as_str();
    if let Some(sha) = pre {
        params["precondition_sha256"] = json!(sha);
    }
    let mut r = echo(
        mutation(bridge, params, instance, pre).await?,
        &normalized,
        None,
    );
    if !warnings.is_empty() && r["data"].get("warnings").is_none() {
        r["data"]["warnings"] = json!(warnings);
    }
    if success(&r) && truth(&options["force_sentinel_reload"]) {
        let state = bridge
            .send("get_editor_state", json!({}), instance)
            .await
            .ok();
        let already_reloading = state.as_ref().is_some_and(|v| {
            v.pointer("/data/compilation/is_domain_reload_pending") == Some(&Value::Bool(true))
                || v.pointer("/data/activity/phase") == Some(&json!("domain_reload"))
        });
        if already_reloading {
            return Ok(r);
        }
        let _ = bridge
            .send(
                "execute_menu_item",
                json!({"menuPath":"MCP/Flip Reload Sentinel"}),
                instance,
            )
            .await;
    }
    Ok(r)
}
fn regex(pattern: &str, ignore_case: bool) -> Result<fancy_regex::Regex> {
    Ok(fancy_regex::Regex::new(&format!(
        "(?m{}){}",
        if ignore_case { "i" } else { "" },
        pattern
    ))?)
}
async fn find(bridge: &dyn UnityBridge, args: Value, instance: Option<&str>) -> Result<Value> {
    let (n, p) = split_uri(s(&args, "uri"));
    let r = read(bridge, &n, &p, instance).await?;
    if !success(&r) {
        return Ok(norm_response(r));
    }
    let Some(text) = contents(&r) else {
        return Ok(json!({"success":false,"message":"Could not read file content."}));
    };
    let ic = match args.get("ignore_case") {
        None => true,
        Some(Value::String(s)) => ["true", "1", "yes"].contains(&s.to_lowercase().as_str()),
        Some(v) => truth(v),
    };
    let re = match regex(s(&args, "pattern"), ic) {
        Ok(r) => r,
        Err(e) => {
            return Ok(json!({"success":false,"message":format!("Invalid regex pattern: {e}")}))
        }
    };
    let max = int(&args["max_results"], 200).max(0) as usize;
    let mut matches = Vec::new();
    let mut total = 0;
    let mut retained_bytes: usize = 0;
    for m in re.find_iter(&text) {
        let m = match m {
            Ok(m) => m,
            Err(e) => return Ok(err("regex_failed", e.to_string())),
        };
        total += 1;
        if matches.len() >= max.min(10_000) || retained_bytes >= 8 * 1024 * 1024 {
            continue;
        }
        let start = m.start();
        let end = m.end();
        let line_start = text[..start].rfind('\n').map(|p| p + 1).unwrap_or(0);
        let line_end = text[start..]
            .find('\n')
            .map(|p| start + p)
            .unwrap_or(text.len());
        let content = text[line_start..line_end].trim();
        let bytes = content.len().saturating_add(m.as_str().len());
        if retained_bytes.saturating_add(bytes) > 8 * 1024 * 1024 {
            continue;
        }
        retained_bytes += bytes;
        matches.push(json!({"line":byte_lc(&text,start).0,"content":text[line_start..line_end].trim(),"match":m.as_str(),"start":text[..start].chars().count(),"end":text[..end].chars().count()}));
    }
    Ok(
        json!({"success":true,"data":{"count":matches.len(),"total_matches":total,"matches":matches}}),
    )
}

fn alias(e: &mut Value, from: &str, to: &str) {
    if e.get(to).is_none() {
        if let Some(v) = e.as_object_mut().and_then(|m| m.remove(from)) {
            e[to] = v;
        }
    }
}
fn first<'a>(e: &'a Value, keys: &[&str]) -> &'a str {
    keys.iter()
        .find_map(|k| e[*k].as_str().filter(|s| !s.is_empty()))
        .unwrap_or("")
}
fn normalize_edit(raw: &Value, name: &str) -> Result<Value> {
    if !raw.is_object() {
        return Err(anyhow!("Each edit must be an object"));
    }
    let mut e = raw.clone();
    for op in [
        "replace_method",
        "insert_method",
        "delete_method",
        "replace_class",
        "delete_class",
        "anchor_insert",
        "anchor_replace",
        "anchor_delete",
    ] {
        if raw[op].is_object() {
            e = raw[op].clone();
            e["op"] = json!(op);
            break;
        }
    }
    let op = first(&e, &["op", "operation", "type", "mode"])
        .trim()
        .to_lowercase();
    if !op.is_empty() {
        e["op"] = json!(op);
    }
    for (from, to) in [
        ("class_name", "className"),
        ("class", "className"),
        ("method_name", "methodName"),
        ("target", "methodName"),
        ("method", "methodName"),
        ("new_content", "replacement"),
        ("newMethod", "replacement"),
        ("new_method", "replacement"),
        ("content", "replacement"),
        ("after", "afterMethodName"),
        ("after_method", "afterMethodName"),
        ("before", "beforeMethodName"),
        ("before_method", "beforeMethodName"),
        ("anchorText", "anchor"),
        ("newText", "text"),
    ] {
        alias(&mut e, from, to);
    }
    if let Some(anchor) = e.as_object_mut().unwrap().remove("anchor_method") {
        let key = if s(&e, "position").trim().eq_ignore_ascii_case("before") {
            "beforeMethodName"
        } else {
            "afterMethodName"
        };
        if e.get(key).is_none() {
            e[key] = anchor;
        }
    }
    if s(&e, "op").starts_with("anchor_") {
        alias(&mut e, "pattern", "anchor");
    }
    if e["op"] == "anchor_insert"
        && !truth(&e["anchor"])
        && (truth(&e["afterMethodName"]) || truth(&e["beforeMethodName"]))
    {
        e["op"] = json!("insert_method");
        if e.get("replacement").is_none() {
            e["replacement"] = json!(s(&e, "text"));
        }
    }
    if e["range"].is_object() {
        let r = e.as_object_mut().unwrap().remove("range").unwrap();
        e["op"] = json!("replace_range");
        for (k, side, axis) in [
            ("startLine", "start", "line"),
            ("startCol", "start", "character"),
            ("endLine", "end", "line"),
            ("endCol", "end", "character"),
        ] {
            e[k] = json!(int(&r[side][axis], 0) + 1);
        }
    }
    let op = s(&e, "op").to_owned();
    if [
        "replace_class",
        "delete_class",
        "replace_method",
        "delete_method",
        "insert_method",
    ]
    .contains(&op.as_str())
        && !truth(&e["className"])
    {
        e["className"] = json!(name);
    }
    match op.as_str() {
        "text_replace" => e["op"] = json!("replace_range"),
        "regex_delete" => {
            e["op"] = json!("regex_replace");
            if e.get("text").is_none() {
                e["text"] = json!("");
            }
        }
        "regex_replace" if e.get("replacement").is_none() => {
            if let Some(t) = e.get("text") {
                e["replacement"] = t.clone();
            } else if e.get("insert").is_some() || e.get("content").is_some() {
                e["replacement"] = json!(first(&e, &["insert", "content"]));
            }
        }
        "anchor_insert" if first(&e, &["text", "insert", "content", "replacement"]).is_empty() => {
            e["op"] = json!("anchor_delete")
        }
        _ => {}
    }
    Ok(e)
}
fn validate_edits(edits: &[Value]) -> Option<Value> {
    for e in edits {
        let op = s(e, "op");
        let missing = match op {
            "replace_method" | "delete_method" if !truth(&e["methodName"]) => {
                Some(("methodName", format!("{op} requires 'methodName'.")))
            }
            "replace_method" | "insert_method" if first(e, &["replacement", "text"]).is_empty() => {
                Some((
                    "replacement",
                    if op == "replace_method" {
                        "replace_method requires 'replacement' (inline or base64).".into()
                    } else {
                        "insert_method requires a non-empty 'replacement'.".into()
                    },
                ))
            }
            "insert_method"
                if s(e, "position").eq_ignore_ascii_case("after")
                    && !truth(&e["afterMethodName"]) =>
            {
                Some((
                    "afterMethodName",
                    "insert_method with position='after' requires 'afterMethodName'.".into(),
                ))
            }
            "insert_method"
                if s(e, "position").eq_ignore_ascii_case("before")
                    && !truth(&e["beforeMethodName"]) =>
            {
                Some((
                    "beforeMethodName",
                    "insert_method with position='before' requires 'beforeMethodName'.".into(),
                ))
            }
            "anchor_insert" | "anchor_replace" | "anchor_delete" if !truth(&e["anchor"]) => {
                Some(("anchor", format!("{op} requires 'anchor' (regex).")))
            }
            "anchor_insert" | "anchor_replace" if first(e, &["text", "replacement"]).is_empty() => {
                Some(("text", format!("{op} requires 'text'.")))
            }
            _ => None,
        };
        if let Some((field, msg)) = missing {
            let mut r = echo(err("missing_field", msg), edits, None);
            let expected = match op {
                "replace_method" => {
                    json!({"op":op,"required":["className","methodName","replacement"]})
                }
                "delete_method" => json!({"op":op,"required":["className","methodName"]}),
                "insert_method" if field == "replacement" => {
                    json!({"op":op,"required":["className","replacement"],"position":{"after_requires":"afterMethodName","before_requires":"beforeMethodName"}})
                }
                "insert_method" if field == "afterMethodName" => {
                    json!({"op":op,"position":{"after_requires":"afterMethodName"}})
                }
                "insert_method" => {
                    json!({"op":op,"position":{"before_requires":"beforeMethodName"}})
                }
                _ if field == "text" => json!({"op":op,"required":["anchor","text"]}),
                _ => json!({"op":op,"required":["anchor"]}),
            };
            let suggestion = match field {
                "methodName" if op == "replace_method" => "HasTarget",
                "methodName" => "PrintSeries",
                "replacement" if op == "replace_method" => "public bool X(){ return true; }",
                "replacement" => "public void PrintSeries(){ Debug.Log(\"1,2,3\"); }",
                "afterMethodName" | "beforeMethodName" => "GetCurrentTarget",
                "anchor" => r"(?m)^\s*public\s+bool\s+HasTarget\s*\(",
                _ => "/* comment */\n",
            };
            r["data"]["expected"] = expected;
            r["data"]["rewrite_suggestion"] = json!({format!("edits[0].{field}"):suggestion});
            return Some(r);
        }
    }
    None
}

// Byte-indexed C# lexer mask; non-ASCII bytes never affect delimiters. Interpolation
// holes remain code, while braces in comments and every string spelling are masked.
fn code_mask(text: &str) -> Result<Vec<bool>> {
    let b = text.as_bytes();
    let mut mask = vec![true; b.len()];
    let mut i = 0;
    while i < b.len() {
        scan_token(b, &mut mask, &mut i, 0)?;
    }
    Ok(mask)
}
fn scan_token(b: &[u8], mask: &mut [bool], i: &mut usize, nesting: usize) -> Result<()> {
    // Interpolated strings recursively lex their embedded expressions. Reject
    // excessive nesting before another stack frame can exhaust the server.
    if nesting > 64 {
        return Err(anyhow!(
            "C# interpolation nesting exceeds the supported limit of 64"
        ));
    }
    let start = *i;
    if b.get(*i..*i + 2) == Some(b"//") {
        while *i < b.len() && b[*i] != b'\n' {
            mask[*i] = false;
            *i += 1;
        }
        return Ok(());
    }
    if b.get(*i..*i + 2) == Some(b"/*") {
        *i += 2;
        while *i < b.len() && b.get(*i..*i + 2) != Some(b"*/") {
            *i += 1;
        }
        *i = (*i + 2).min(b.len());
        mask[start..*i].fill(false);
        return Ok(());
    }
    let mut j = *i;
    let mut dollars = 0;
    let mut verbatim = false;
    if b.get(j) == Some(&b'@') {
        verbatim = true;
        j += 1;
    }
    while b.get(j) == Some(&b'$') {
        dollars += 1;
        j += 1;
    }
    if b.get(j) == Some(&b'@') {
        verbatim = true;
        j += 1;
    }
    let quote = b.get(j).copied().unwrap_or(0);
    if quote != b'"' && !(j == *i && quote == b'\'') {
        *i += 1;
        return Ok(());
    }
    let mut q = 1;
    while quote == b'"' && b.get(j + q) == Some(&quote) {
        q += 1;
    }
    let raw = q >= 3;
    if !raw {
        q = 1;
    }
    *i = j + q;
    mask[start..*i].fill(false);
    while *i < b.len() {
        if b[*i] == quote {
            let mut run = 1;
            while b.get(*i + run) == Some(&quote) {
                run += 1;
            }
            if raw {
                let take = run.min(q);
                mask[*i..*i + take].fill(false);
                *i += take;
                if run >= q {
                    return Ok(());
                }
                continue;
            }
            if verbatim && run >= 2 {
                mask[*i..*i + 2].fill(false);
                *i += 2;
                continue;
            }
            mask[*i] = false;
            *i += 1;
            return Ok(());
        }
        if !raw && !verbatim && b[*i] == b'\\' {
            mask[*i] = false;
            *i += 1;
            if *i < b.len() {
                mask[*i] = false;
                *i += 1;
            }
            continue;
        }
        if dollars > 0 && b[*i] == b'{' {
            let mut run = 1;
            while b.get(*i + run) == Some(&b'{') {
                run += 1;
            }
            if !raw && run >= 2 {
                mask[*i..*i + 2].fill(false);
                *i += 2;
                continue;
            }
            let required = if raw { dollars } else { 1 };
            if run >= required {
                *i += required;
                let mut depth = 1;
                while *i < b.len() && depth > 0 {
                    match b[*i] {
                        b'{' => {
                            depth += 1;
                            *i += 1;
                        }
                        b'}' => {
                            depth -= 1;
                            *i += 1;
                        }
                        _ => scan_token(b, mask, i, nesting + 1)?,
                    }
                }
                continue;
            }
        }
        if !raw && dollars > 0 && b.get(*i..*i + 2) == Some(b"}}") {
            mask[*i..*i + 2].fill(false);
            *i += 2;
            continue;
        }
        mask[*i] = false;
        *i += 1;
    }
    Ok(())
}
fn best_match(
    re: &fancy_regex::Regex,
    pattern: &str,
    text: &str,
    prefer_last: bool,
) -> Result<Option<(usize, usize)>> {
    let anchor = prefer_last
        && pattern.contains('}')
        && (pattern.contains('$') || pattern.ends_with(r"\s*"));
    let mask = if anchor { Some(code_mask(text)?) } else { None };
    let mut depths = Vec::new();
    if let Some(mask) = &mask {
        depths = vec![0; text.len()];
        let mut depth: usize = 0;
        for (i, b) in text.bytes().enumerate() {
            if mask[i] {
                if b == b'{' {
                    depth += 1;
                }
                if b == b'}' {
                    depths[i] = depth;
                    depth = depth.saturating_sub(1);
                }
            }
        }
    }
    // Keep only the selected match, not one allocation per regex hit.
    let mut count = 0;
    let mut first = None;
    let mut selected = None;
    let mut best_key = None;
    for m in re.find_iter(text) {
        let m = m?;
        let range = (m.start(), m.end());
        count += 1;
        first.get_or_insert(range);
        if let Some(mask) = &mask {
            if let Some(p) = text[m.start()..m.end()].find('}').map(|p| p + m.start()) {
                let key = (depths[p], usize::MAX - p);
                if mask[p] && best_key.is_none_or(|best| key < best) {
                    best_key = Some(key);
                    selected = Some(range);
                }
            }
        } else if prefer_last || selected.is_none() {
            selected = Some(range);
        }
    }
    Ok(if count <= 1 { first } else { selected })
}
fn expand_dollars(rep: &str, caps: &fancy_regex::Captures<'_>) -> Result<String> {
    let re = regex::Regex::new(r"\$(\d+)").unwrap();
    let mut out = String::new();
    let mut last = 0;
    for c in re.captures_iter(rep) {
        let m = c.get(0).unwrap();
        out.push_str(&rep[last..m.start()]);
        let idx: usize = c[1].parse()?;
        if idx >= caps.len() {
            return Err(anyhow!("invalid group reference {idx}"));
        }
        out.push_str(caps.get(idx).map(|m| m.as_str()).unwrap_or(""));
        last = m.end();
    }
    out.push_str(&rep[last..]);
    Ok(out)
}
fn text_spans(text: &str, edits: &[Value], mixed: bool) -> std::result::Result<Vec<Value>, Value> {
    let mut spans = Vec::new();
    for e in edits {
        let op = s(e, "op");
        let replacement = if mixed {
            first(e, &["text", "insert", "content", "replacement"])
        } else {
            first(e, &["text", "insert", "content"])
        };
        match op {
            "replace_range" => {
                if !["startLine", "startCol", "endLine", "endCol"]
                    .iter()
                    .all(|k| e.get(k).is_some())
                {
                    return Err(err(
                        "missing_field",
                        "replace_range requires startLine/startCol/endLine/endCol",
                    ));
                }
                spans.push(json!({"startLine":int(&e["startLine"],1),"startCol":int(&e["startCol"],1),"endLine":int(&e["endLine"],1),"endCol":int(&e["endCol"],1),"newText":replacement}));
            }
            "regex_replace" => {
                let pattern = s(e, "pattern");
                let re = regex(pattern, truth(&e["ignore_case"]))
                    .map_err(|e| err("bad_regex", format!("Invalid regex pattern: {e}")))?;
                let chosen = if mixed {
                    re.find(text)
                        .map_err(|e| err("conversion_failed", e.to_string()))?
                        .map(|m| (m.start(), m.end()))
                } else {
                    best_match(&re, pattern, text, true)
                        .map_err(|e| err("conversion_failed", e.to_string()))?
                };
                if let Some((a, b)) = chosen {
                    let caps = re
                        .captures_from_pos(text, a)
                        .map_err(|e| err("conversion_failed", e.to_string()))?
                        .ok_or_else(|| {
                            err("conversion_failed", "Could not expand regex capture")
                        })?;
                    let repl = expand_dollars(replacement, &caps)
                        .map_err(|e| err("conversion_failed", e.to_string()))?;
                    spans.push(span(text, a, b, &repl));
                }
            }
            "prepend" if mixed => spans.push(span(text, 0, 0, replacement)),
            "append" if mixed => {
                let repl = format!(
                    "{}{}",
                    if text.ends_with('\n') { "" } else { "\n" },
                    replacement
                );
                spans.push(span(text, text.len(), text.len(), &repl));
            }
            _ => {
                return Err(err(
                    "unsupported_op",
                    format!("Unsupported text edit op for server-side apply_text_edits: {op}"),
                ))
            }
        }
    }
    Ok(spans)
}
async fn structured(
    bridge: &dyn UnityBridge,
    args: Value,
    instance: Option<&str>,
) -> Result<Value> {
    let raw = if let Some(t) = args["edits"].as_str() {
        serde_json::from_str::<Value>(t).unwrap_or(Value::Null)
    } else {
        args["edits"].clone()
    };
    let Some(raw) = raw.as_array() else {
        return Ok(
            json!({"success":false,"message":"Edits must be a list or JSON string of a list"}),
        );
    };
    let (name, path) = normalize_locator(s(&args, "name"), s(&args, "path"));
    let edits = match raw
        .iter()
        .map(|e| normalize_edit(e, &name))
        .collect::<Result<Vec<_>>>()
    {
        Ok(v) => v,
        Err(e) => return Ok(err("invalid_edits", e.to_string())),
    };
    if let Some(e) = validate_edits(&edits) {
        return Ok(e);
    }
    let struct_kinds = [
        "replace_class",
        "delete_class",
        "replace_method",
        "delete_method",
        "insert_method",
        "anchor_delete",
        "anchor_replace",
        "anchor_insert",
    ];
    let text_kinds = ["prepend", "append", "replace_range", "regex_replace"];
    let all_struct = edits.iter().all(|e| struct_kinds.contains(&s(e, "op")));
    let all_text = edits.iter().all(|e| text_kinds.contains(&s(e, "op")));
    let mixed = !(all_struct || all_text);
    let options = object(&args["options"]);
    let script_type = args["script_type"].as_str().unwrap_or("MonoBehaviour");
    let base =
        json!({"name":name,"path":path,"namespace":args["namespace"],"scriptType":script_type});
    if all_struct && truth(&options["preview"]) {
        return Ok(echo(
            json!({"success":true,"message":"Preview only (no write)","data":{"preview":true,"structuredEdits":edits}}),
            &edits,
            Some("structured"),
        ));
    }
    if all_struct {
        let sha = bridge
            .send(
                "manage_script",
                json!({"action":"get_sha","name":name,"path":path}),
                instance,
            )
            .await
            .ok()
            .filter(success)
            .map(|v| s(&v["data"], "sha256").to_owned());
        let mut p = base.clone();
        p["action"] = json!("edit");
        p["edits"] = json!(edits);
        let mut opts = options.clone();
        if opts.get("refresh").is_none() {
            opts["refresh"] = json!("immediate");
        }
        p["options"] = opts;
        return Ok(echo(
            mutation(bridge, p, instance, sha.as_deref()).await?,
            &edits,
            Some("structured"),
        ));
    }
    let mut rp = base.clone();
    rp["action"] = json!("read");
    let r = bridge.send("manage_script", rp, instance).await?;
    if !success(&r) {
        return Ok(norm_response(r));
    }
    let Some(text) = contents(&r) else {
        return Ok(json!({"success":false,"message":"No contents returned from Unity read."}));
    };
    let routing = if mixed { "mixed/text-first" } else { "text" };
    let text_edits: Vec<Value> = if mixed {
        edits
            .iter()
            .filter(|e| text_kinds.contains(&s(e, "op")))
            .cloned()
            .collect()
    } else {
        edits.clone()
    };
    let struct_edits: Vec<Value> = edits
        .iter()
        .filter(|e| struct_kinds.contains(&s(e, "op")))
        .cloned()
        .collect();
    let spans = match text_spans(&text, &text_edits, mixed) {
        Ok(v) => v,
        Err(e) => return Ok(echo(e, &edits, Some(routing))),
    };
    if spans.is_empty() && !mixed {
        return Ok(echo(
            err(
                "no_spans",
                "No applicable text edit spans computed (anchor not found or zero-length).",
            ),
            &edits,
            Some(routing),
        ));
    }
    let sha = format!("{:x}", Sha256::digest(text.as_bytes()));
    // Unlike the Python implementation's unreachable preview branch, never mutate
    // when callers explicitly request a preview.
    if truth(&options["preview"]) {
        return Ok(echo(
            json!({"success":true,"message":"Preview only (no write)","data":{"preview":true,"textEdits":spans,"structuredEdits":struct_edits,"precondition_sha256":sha}}),
            &edits,
            Some(routing),
        ));
    }
    let mut response = json!({"success":true,"message":"Applied text edits (no structured ops)"});
    if !spans.is_empty() {
        let mut p = base.clone();
        p["action"] = json!("apply_text_edits");
        p["edits"] = json!(spans);
        p["precondition_sha256"] = json!(sha);
        p["options"] = json!({"refresh":options.get("refresh").cloned().unwrap_or(json!("debounced")),"validate":options.get("validate").cloned().unwrap_or(json!("standard")),"applyMode":if spans.len()>1{json!("atomic")}else{options.get("applyMode").cloned().unwrap_or(json!("sequential"))}});
        response = mutation(bridge, p, instance, Some(&sha)).await?;
        if !success(&response) {
            return Ok(echo(response, &edits, Some(routing)));
        }
    }
    if mixed && !struct_edits.is_empty() {
        // Capture the intermediate SHA: comparing with the pre-text SHA would
        // falsely verify a failed structured edit after the text edit succeeded.
        let intermediate = bridge
            .send(
                "manage_script",
                json!({"action":"get_sha","name":name,"path":path}),
                instance,
            )
            .await
            .ok()
            .filter(success)
            .map(|v| s(&v["data"], "sha256").to_owned());
        let mut p = base;
        p["action"] = json!("edit");
        p["edits"] = json!(struct_edits);
        let mut opts = options;
        if opts.get("refresh").is_none() {
            opts["refresh"] = json!("debounced");
        }
        p["options"] = opts;
        response = mutation(bridge, p, instance, intermediate.as_deref()).await?;
        if !success(&response) && !spans.is_empty() {
            if !response["data"].is_object() {
                response["data"] = json!({});
            }
            response["data"]["partial_apply"] = json!(true);
            response["data"]["applied_phase"] = json!("text");
            response["data"]["failed_phase"] = json!("structured");
        }
    }
    Ok(echo(response, &edits, Some(routing)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;
    use std::sync::Mutex;
    struct Mock {
        calls: Mutex<Vec<(String, Value, Option<String>)>>,
        replies: Mutex<VecDeque<Value>>,
    }
    impl Mock {
        fn new(replies: Vec<Value>) -> Self {
            Self {
                calls: Mutex::new(vec![]),
                replies: Mutex::new(replies.into()),
            }
        }
        fn mutations(&self) -> Vec<Value> {
            self.calls
                .lock()
                .unwrap()
                .iter()
                .filter(|(c, p, _)| {
                    c == "manage_script" && p["action"] != "read" && p["action"] != "get_sha"
                })
                .map(|(_, p, _)| p.clone())
                .collect()
        }
    }
    #[async_trait::async_trait]
    impl UnityBridge for Mock {
        async fn send(
            &self,
            command: &str,
            params: Value,
            instance: Option<&str>,
        ) -> Result<Value> {
            self.calls
                .lock()
                .unwrap()
                .push((command.into(), params, instance.map(str::to_owned)));
            if command == "get_editor_state" {
                return Ok(json!({"success":true,"data":{"advice":{"ready_for_tools":true}}}));
            }
            Ok(self
                .replies
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or(json!({"success":true})))
        }
        async fn instances(&self) -> Result<Value> {
            Ok(json!([]))
        }
    }
    fn file(text: &str) -> Value {
        json!({"success":true,"data":{"contents":text}})
    }
    #[test]
    fn uri_variants() {
        for (uri, n, p) in [
            (
                "mcpforunity://path/Assets/Scripts/MyScript.cs",
                "MyScript",
                "Assets/Scripts",
            ),
            (
                "file:///Users/alex/Project/Assets/Scripts/Foo%20Bar.cs",
                "Foo Bar",
                "Assets/Scripts",
            ),
            (
                "file://localhost/Users/alex/Project/Assets/Hello.cs",
                "Hello",
                "Assets",
            ),
            (
                "file:///C:/Users/Alex/Proj/Assets/Scripts/Hello.cs",
                "Hello",
                "Assets/Scripts",
            ),
            ("file:///tmp/Other.cs", "Other", "tmp"),
            (r"C:\Project\Assets\Scripts\Foo.cs", "Foo", "Assets/Scripts"),
            ("file://server/share/Assets/Foo.cs", "Foo", "Assets"),
            ("Assets/A/../B.cs", "B", "Assets"),
        ] {
            assert_eq!(split_uri(uri), (n.into(), p.into()));
        }
    }
    #[test]
    fn locator_variants() {
        for (n, p, en, ep) in [
            ("Foo", "Assets/Scripts", "Foo", "Assets/Scripts"),
            ("Assets/Scripts/Foo.cs", "", "Foo", "Assets/Scripts"),
            ("", "Assets/Foo.cs/Foo.cs", "Foo", "Assets"),
            ("Foo.cs", "Assets/Scripts", "Foo", "Assets"),
        ] {
            assert_eq!(normalize_locator(n, p), (en.into(), ep.into()));
        }
    }
    #[tokio::test]
    async fn explicit_no_hidden_read() {
        let b = Mock::new(vec![]);
        let r=call(&b,"apply_text_edits",json!({"uri":"Assets/F.cs","edits":[{"startLine":1,"startCol":1,"endLine":1,"endCol":1,"newText":"x"}]}),Some("Unity@123")).await.unwrap();
        assert_eq!(r["success"], true);
        assert_eq!(
            b.calls
                .lock()
                .unwrap()
                .iter()
                .filter(|(c, _, _)| c == "manage_script")
                .count(),
            1
        );
        assert_eq!(b.mutations()[0].get("precondition_sha256"), None);
        assert!(b
            .calls
            .lock()
            .unwrap()
            .iter()
            .all(|(_, _, i)| i.as_deref() == Some("Unity@123")));
    }
    #[tokio::test]
    async fn lsp_normalization() {
        let b = Mock::new(vec![file("hello\n")]);
        let sha = format!("{:x}", Sha256::digest(b"hello\n"));
        let r=call(&b,"apply_text_edits",json!({"uri":"Assets/F.cs","edits":[{"range":{"start":{"line":10,"character":2},"end":{"line":10,"character":2}},"newText":"//x"}],"precondition_sha256":sha}),None).await.unwrap();
        assert_eq!(r["data"]["normalizedEdits"][0]["startLine"], 11);
        assert_eq!(b.mutations()[0]["precondition_sha256"], sha);
    }
    #[tokio::test]
    async fn index_ranges_unicode_and_reverse() {
        let b = Mock::new(vec![file("é\n世界")]);
        let r = call(
            &b,
            "apply_text_edits",
            json!({"uri":"Assets/F.cs","edits":[{"range":[4,2],"text":"x"}]}),
            None,
        )
        .await
        .unwrap();
        let e = &r["data"]["normalizedEdits"][0];
        assert_eq!(e["startLine"], 2);
        assert_eq!(e["startCol"], 1);
        assert_eq!(e["endCol"], 3);
        assert_eq!(e["newText"], "x");
    }
    #[tokio::test]
    async fn strict_rejects_before_send() {
        let b = Mock::new(vec![]);
        let r=call(&b,"apply_text_edits",json!({"uri":"Assets/F.cs","strict":true,"edits":[{"startLine":0,"startCol":0,"endLine":0,"endCol":0,"newText":"x"}]}),None).await.unwrap();
        assert_eq!(r["code"], "zero_based_explicit_fields");
        assert!(b.calls.lock().unwrap().is_empty());
    }
    #[tokio::test]
    async fn warns_and_clamps() {
        let b = Mock::new(vec![]);
        let r=call(&b,"apply_text_edits",json!({"uri":"Assets/F.cs","edits":[{"startLine":0,"startCol":0,"endLine":0,"endCol":0,"newText":"x"}]}),None).await.unwrap();
        assert_eq!(
            r["data"]["warnings"][0],
            "zero_based_explicit_fields_normalized"
        );
        assert_eq!(r["data"]["normalizedEdits"][0]["startCol"], 1);
    }
    #[tokio::test]
    async fn overlap_blocks_write() {
        let b = Mock::new(vec![]);
        let r=call(&b,"apply_text_edits",json!({"uri":"Assets/F.cs","edits":[{"startLine":1,"startCol":1,"endLine":1,"endCol":5,"newText":"x"},{"startLine":1,"startCol":3,"endLine":1,"endCol":7,"newText":"y"}]}),None).await.unwrap();
        assert_eq!(r["code"], "overlap");
        assert!(b.mutations().is_empty());
    }
    #[tokio::test]
    async fn insertion_does_not_overlap_and_atomic_default() {
        let b = Mock::new(vec![]);
        call(&b,"apply_text_edits",json!({"uri":"Assets/F.cs","edits":[{"startLine":1,"startCol":1,"endLine":1,"endCol":5,"newText":"x"},{"startLine":1,"startCol":3,"endLine":1,"endCol":3,"newText":"y"}],"options":{"validate":"relaxed"}}),None).await.unwrap();
        assert_eq!(
            b.mutations()[0]["options"],
            json!({"validate":"relaxed","applyMode":"atomic"})
        );
    }
    #[tokio::test]
    async fn debug_preview_never_writes() {
        let b = Mock::new(vec![]);
        let r = call(
            &b,
            "apply_text_edits",
            json!({"uri":"Assets/F.cs","edits":[],"options":{"debug_preview":true}}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["data"]["preview"], true);
        assert!(b.mutations().is_empty());
    }
    #[tokio::test]
    async fn create_encodes_and_read_decodes() {
        let b = Mock::new(vec![]);
        call(
            &b,
            "create_script",
            json!({"path":"Assets/F.cs","contents":"class F { string s=\"é\"; }"}),
            None,
        )
        .await
        .unwrap();
        let p = &b.mutations()[0];
        assert_eq!(p["contentsEncoded"], true);
        assert_eq!(
            STANDARD.decode(s(p, "encodedContents")).unwrap(),
            "class F { string s=\"é\"; }".as_bytes()
        );
        let b = Mock::new(vec![
            json!({"success":true,"data":{"contentsEncoded":true,"encodedContents":STANDARD.encode("é")}}),
        ]);
        let r = call(
            &b,
            "manage_script",
            json!({"action":"read","name":"F","path":"Assets"}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["data"]["contents"], "é");
        assert!(r["data"].get("encodedContents").is_none());
    }
    #[tokio::test]
    async fn rejects_outside_assets_and_bad_extension() {
        let b = Mock::new(vec![]);
        for (path, code) in [
            ("tmp/F.cs", "path_outside_assets"),
            ("Assets/../../F.cs", "bad_path"),
            ("Assets/F.txt", "bad_extension"),
        ] {
            let r = call(
                &b,
                "create_script",
                json!({"path":path,"contents":""}),
                None,
            )
            .await
            .unwrap();
            assert_eq!(r["code"], code);
        }
        assert!(b.calls.lock().unwrap().is_empty());
    }
    #[tokio::test]
    async fn validation_summary() {
        let b = Mock::new(vec![
            json!({"success":true,"data":{"diagnostics":[{"severity":"Warning"},{"severity":"error"},{"severity":"fatal"}]}}),
        ]);
        let r = call(&b, "validate_script", json!({"uri":"Assets/F.cs"}), None)
            .await
            .unwrap();
        assert_eq!(r, json!({"success":true,"data":{"warnings":1,"errors":2}}));
    }
    #[tokio::test]
    async fn minimal_sha() {
        let b = Mock::new(vec![
            json!({"success":true,"data":{"sha256":"abc","lengthBytes":30,"contents":"secret"}}),
        ]);
        let r = call(&b, "get_sha", json!({"uri":"Assets/F.cs"}), None)
            .await
            .unwrap();
        assert_eq!(
            r,
            json!({"success":true,"data":{"sha256":"abc","lengthBytes":30}})
        );
    }
    #[tokio::test]
    async fn search_lookaround_unicode_and_cap() {
        let b = Mock::new(vec![file("é hi\nHI hi")]);
        let r = call(
            &b,
            "find_in_file",
            json!({"uri":"Assets/F.cs","pattern":"(?<!x)hi","max_results":2}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["data"]["total_matches"], 3);
        assert_eq!(r["data"]["count"], 2);
        assert_eq!(r["data"]["matches"][0]["start"], 2);
        assert_eq!(r["data"]["matches"][1]["line"], 2);
    }
    #[tokio::test]
    async fn search_string_false_and_invalid_pattern() {
        let b = Mock::new(vec![file("Hi hi")]);
        let r = call(
            &b,
            "find_in_file",
            json!({"uri":"Assets/F.cs","pattern":"hi","ignore_case":"false"}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["data"]["count"], 1);
        let b = Mock::new(vec![file("hi")]);
        let r = call(
            &b,
            "find_in_file",
            json!({"uri":"Assets/F.cs","pattern":"["}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["success"], false);
    }
    #[tokio::test]
    async fn structured_aliases_and_defaults() {
        let b = Mock::new(vec![json!({"success":true,"data":{"sha256":"old"}})]);
        let r=call(&b,"script_apply_edits",json!({"name":"Foo","path":"Assets/Scripts","edits":"[{\"replace_method\":{\"method_name\":\"M\",\"new_content\":\"void M() {}\"}}]"}),None).await.unwrap();
        let p = &b.mutations()[0];
        assert_eq!(p["action"], "edit");
        assert_eq!(p["edits"][0]["className"], "Foo");
        assert_eq!(p["edits"][0]["replacement"], "void M() {}");
        assert_eq!(p["options"]["refresh"], "immediate");
        assert_eq!(r["data"]["routing"], "structured");
    }
    #[tokio::test]
    async fn structured_missing_field() {
        let b = Mock::new(vec![]);
        let r = call(
            &b,
            "script_apply_edits",
            json!({"name":"Foo","path":"Assets","edits":[{"op":"delete_method"}]}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["code"], "missing_field");
        assert!(r["data"]["rewrite_suggestion"].is_object());
        assert!(b.calls.lock().unwrap().is_empty());
    }
    #[tokio::test]
    async fn method_anchor_upgrade() {
        let b = Mock::new(vec![]);
        call(&b,"script_apply_edits",json!({"name":"Foo","path":"Assets","edits":[{"op":"anchor_insert","after":"M","text":"void N() {}"}]}),None).await.unwrap();
        let p = &b.mutations()[0];
        assert_eq!(p["edits"][0]["op"], "insert_method");
        assert_eq!(p["edits"][0]["afterMethodName"], "M");
    }
    #[tokio::test]
    async fn text_sha_and_last_regex_match() {
        let text = "int old=1;\nint old=2;";
        let b = Mock::new(vec![file(text)]);
        let r=call(&b,"script_apply_edits",json!({"name":"Foo","path":"Assets","edits":[{"op":"regex_replace","pattern":"old=(\\d)","text":"new=$1"}]}),None).await.unwrap();
        let p = &b.mutations()[0];
        assert_eq!(
            p["precondition_sha256"],
            format!("{:x}", Sha256::digest(text))
        );
        assert_eq!(p["edits"][0]["startLine"], 2);
        assert_eq!(p["edits"][0]["newText"], "new=2");
        assert_eq!(r["data"]["routing"], "text");
    }
    #[tokio::test]
    async fn mixed_text_first_stops_on_failure() {
        let b = Mock::new(vec![file("class Foo {}"), err("sha_mismatch", "Changed")]);
        let r=call(&b,"script_apply_edits",json!({"name":"Foo","path":"Assets","edits":[{"op":"prepend","text":"//x\n"},{"op":"insert_method","replacement":"void M(){}"}]}),None).await.unwrap();
        assert_eq!(r["code"], "sha_mismatch");
        assert_eq!(b.mutations().len(), 1);
    }
    #[tokio::test]
    async fn mixed_intermediate_sha_prevents_false_verification() {
        let b = Mock::new(vec![
            file("class Foo {}"),
            json!({"success":true}),
            json!({"success":true,"data":{"sha256":"after-text"}}),
            err("connection", "disconnected"),
            json!({"success":true,"data":{"sha256":"after-text"}}),
        ]);
        let r=call(&b,"script_apply_edits",json!({"name":"Foo","path":"Assets","edits":[{"op":"prepend","text":"//x\n"},{"op":"insert_method","replacement":"void M(){}"}]}),None).await.unwrap();
        assert_eq!(r["success"], false);
        assert_eq!(b.mutations().len(), 2);
    }
    #[tokio::test]
    async fn preview_structured_and_text_never_write() {
        for edits in [
            json!([{"op":"insert_method","replacement":"void M(){}"}]),
            json!([{"op":"regex_replace","pattern":"Foo","text":"Bar"}]),
        ] {
            let b = Mock::new(vec![file("class Foo {}")]);
            let r = call(
                &b,
                "script_apply_edits",
                json!({"name":"Foo","path":"Assets","edits":edits,"options":{"preview":true}}),
                None,
            )
            .await
            .unwrap();
            assert_eq!(r["data"]["preview"], true);
            assert!(b.mutations().is_empty());
        }
    }
    #[tokio::test]
    async fn reload_rejection_retries_once() {
        let b = Mock::new(vec![
            json!({"success":false,"data":{"reason":"reloading"},"hint":"retry"}),
            json!({"success":true}),
        ]);
        let r = call(&b, "delete_script", json!({"uri":"Assets/F.cs"}), None)
            .await
            .unwrap();
        assert_eq!(r["success"], true);
        assert_eq!(b.mutations().len(), 2);
    }
    #[tokio::test]
    async fn disconnect_changed_sha_does_not_prove_our_edit() {
        let b = Mock::new(vec![
            err("connection", "Connection closed"),
            json!({"success":true,"data":{"sha256":"new"}}),
        ]);
        let r = call(
            &b,
            "apply_text_edits",
            json!({"uri":"Assets/F.cs","edits":[],"precondition_sha256":"old"}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["success"], false);
        assert_eq!(r["data"]["outcome_unknown"], true);
        assert_eq!(b.mutations().len(), 1);
    }
    #[tokio::test]
    async fn disconnect_without_sha_never_claims_applied() {
        let b = Mock::new(vec![err("connection", "Connection closed")]);
        let r = call(
            &b,
            "apply_text_edits",
            json!({"uri":"Assets/F.cs","edits":[]}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["success"], false);
        assert_eq!(b.mutations().len(), 1);
    }
    #[test]
    fn lexer_csharp_strings_and_interpolation() {
        for (text, inside, outside) in [
            ("string s = \"hello world\"; int x;", "hello", "int"),
            (
                "string s = @\"He said \"\"hello\"\"\"; int x;",
                "hello",
                "int",
            ),
            ("string s = $\"Value: {value}\";", "Value", "value"),
            ("string s = $@\"Path: {dir}\\file\";", "Path", "dir"),
            ("string s = \"\"\"{raw}\"\"\"; int x;", "raw", "int"),
            (
                "string s = $$\"\"\"{literal} {{interp}}\"\"\";",
                "literal",
                "interp",
            ),
            ("/* block { } */ int x;", "block", "int"),
            ("// comment\nint x;", "comment", "int"),
        ] {
            let m = code_mask(text).unwrap();
            assert!(!m[text.find(inside).unwrap()], "{text}");
            assert!(m[text.find(outside).unwrap()], "{text}");
        }
    }
    #[test]
    fn lexer_nesting_boundary_is_explicit_and_bounded() {
        fn nested(depth: usize) -> String {
            format!("{}0{}\n}}\n", r#"$"{"#.repeat(depth), r#"}""#.repeat(depth))
        }
        assert!(code_mask(&nested(64)).is_ok());
        assert!(code_mask(&nested(65))
            .unwrap_err()
            .to_string()
            .contains("nesting"));
        let text = nested(100_000);
        let edits = vec![json!({"op":"regex_replace","pattern":r"^\s*}\s*$","text":"}"})];
        let error = text_spans(&text, &edits, false).unwrap_err();
        assert_eq!(error["code"], "conversion_failed");
        assert!(error["message"].as_str().unwrap().contains("nesting"));
    }
    #[test]
    fn anchor_prefers_outer_brace() {
        let text =
            "class Foo {\n string s = @\"{ }\";\n void M() {\n Debug.Log($\"x={x}\");\n }\n}\n";
        let pattern = r"^\s*}\s*$";
        let re = regex(pattern, false).unwrap();
        let (a, _) = best_match(&re, pattern, text, true).unwrap().unwrap();
        assert_eq!(byte_lc(text, a).0, 6);
    }
    // Captured by executing the unmodified Python function ASTs with a recording
    // Unity transport. This compares results and complete outbound payloads.
    #[tokio::test]
    async fn python_adapter_golden_parity() {
        struct PythonFixtureBridge {
            calls: Mutex<Vec<Value>>,
        }
        #[async_trait::async_trait]
        impl UnityBridge for PythonFixtureBridge {
            async fn send(&self, c: &str, p: Value, i: Option<&str>) -> Result<Value> {
                if c == "get_editor_state" {
                    return Ok(json!({"success":true,"data":{"advice":{"ready_for_tools":true}}}));
                }
                self.calls.lock().unwrap().push(json!([c, p, i]));
                Ok(match s(&p, "action") {
                    "read" => file("class Foo {\n int old = 1;\n void M() {}\n}\n"),
                    "get_sha" => {
                        json!({"success":true,"data":{"sha256":"before","lengthBytes":42}})
                    }
                    "validate" => {
                        json!({"success":true,"data":{"diagnostics":[{"severity":"Warning"},{"severity":"fatal"}]}})
                    }
                    _ => json!({"success":true,"message":"ok","data":{}}),
                })
            }
            async fn instances(&self) -> Result<Value> {
                Ok(json!([]))
            }
        }
        let fixtures: Value =
            serde_json::from_str(include_str!("../tests/fixtures/scripts_python_parity.json"))
                .unwrap();
        for fixture in fixtures.as_array().unwrap() {
            let mut fixture = fixture.clone();
            // The captured Python bridge used a placeholder SHA even for reads.
            // Use the actual read snapshot SHA to exercise the safe equivalent.
            if fixture["tool"] == "apply_text_edits"
                && fixture["args"]["precondition_sha256"].is_string()
                && fixture["calls"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|c| c[1]["action"] == "read")
            {
                let sha = json!(format!(
                    "{:x}",
                    Sha256::digest(b"class Foo {\n int old = 1;\n void M() {}\n}\n")
                ));
                fixture["args"]["precondition_sha256"] = sha.clone();
                for call in fixture["calls"].as_array_mut().unwrap() {
                    if call[1].get("precondition_sha256").is_some() {
                        call[1]["precondition_sha256"] = sha.clone();
                    }
                }
            }
            let bridge = PythonFixtureBridge {
                calls: Mutex::new(vec![]),
            };
            let result = call(
                &bridge,
                s(&fixture, "tool"),
                fixture["args"].clone(),
                Some("fixture-instance"),
            )
            .await
            .unwrap();
            assert_eq!(
                result, fixture["result"],
                "result for {} with {}",
                fixture["tool"], fixture["args"]
            );
            assert_eq!(
                json!(*bridge.calls.lock().unwrap()),
                fixture["calls"],
                "calls for {} with {}",
                fixture["tool"],
                fixture["args"]
            );
        }
    }
    #[tokio::test]
    async fn readiness_checks_raw_compilation_state() {
        struct Busy {
            polls: std::sync::atomic::AtomicUsize,
        }
        #[async_trait::async_trait]
        impl UnityBridge for Busy {
            async fn send(&self, _: &str, _: Value, _: Option<&str>) -> Result<Value> {
                let n = self
                    .polls
                    .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                Ok(json!({"success":true,"data":{"compilation":{"is_compiling":n==0}}}))
            }
            async fn instances(&self) -> Result<Value> {
                Ok(json!([]))
            }
        }
        let bridge = Busy {
            polls: std::sync::atomic::AtomicUsize::new(0),
        };
        wait_ready(&bridge, None).await;
        assert_eq!(bridge.polls.load(std::sync::atomic::Ordering::Relaxed), 2);
    }
    #[tokio::test]
    async fn disconnect_delete_requires_actual_not_found() {
        for (read_result, deleted) in [
            (err("busy", "Unity is compiling"), false),
            (err("io", "Permission denied"), false),
            (file("class Existing {}"), false),
            (
                json!({"success":false,"error":"Script not found at 'Assets/F.cs'."}),
                true,
            ),
        ] {
            let b = Mock::new(vec![err("connection", "Connection closed"), read_result]);
            let r = call(&b, "delete_script", json!({"uri":"Assets/F.cs"}), None)
                .await
                .unwrap();
            assert_eq!(r["success"], deleted);
            assert_eq!(b.mutations().len(), 1);
        }
    }
    #[tokio::test]
    async fn disconnect_create_requires_exact_requested_contents() {
        for (read_result, created) in [
            (file("unrelated"), false),
            (file("requested"), true),
            (err("busy", "reloading"), false),
        ] {
            let b = Mock::new(vec![err("connection", "Connection closed"), read_result]);
            let r = call(
                &b,
                "create_script",
                json!({"path":"Assets/F.cs","contents":"requested"}),
                None,
            )
            .await
            .unwrap();
            assert_eq!(r["success"], created);
            assert_eq!(b.mutations().len(), 1);
        }
    }
    #[tokio::test]
    async fn converted_range_rejects_stale_read_before_mutation() {
        let b = Mock::new(vec![file("concurrent writer")]);
        let r = call(&b, "apply_text_edits", json!({"uri":"Assets/F.cs","precondition_sha256":"old","edits":[{"range":[0,2],"text":"x"}]}), None).await.unwrap();
        assert_eq!(r["code"], "stale_file");
        assert!(b.mutations().is_empty());
    }
    #[tokio::test]
    async fn mixed_failure_reports_committed_text_phase() {
        let b = Mock::new(vec![
            file("class Foo {}"),
            json!({"success":true}),
            json!({"success":true,"data":{"sha256":"intermediate"}}),
            err("invalid", "method not found"),
        ]);
        let r = call(&b,"script_apply_edits",json!({"name":"Foo","path":"Assets","edits":[{"op":"append","text":"// committed"},{"op":"replace_method","methodName":"Missing","replacement":"void Missing() {}"}]}),None).await.unwrap();
        assert_eq!(r["success"], false);
        assert_eq!(r["data"]["partial_apply"], true);
        assert_eq!(r["data"]["applied_phase"], "text");
    }
    #[test]
    fn regex_selection_streams_many_matches() {
        let text = "x".repeat(100_000);
        let re = regex("x", false).unwrap();
        assert_eq!(
            best_match(&re, "x", &text, true).unwrap(),
            Some((99_999, 100_000))
        );
        assert_eq!(best_match(&re, "x", &text, false).unwrap(), Some((0, 1)));
    }
    #[test]
    fn unc_outside_assets_and_empty_uri() {
        assert_eq!(
            split_uri("file://server/share/Foo.cs"),
            ("Foo".into(), "/server/share".into())
        );
        assert_eq!(split_uri(""), (".".into(), "".into()));
    }
}
