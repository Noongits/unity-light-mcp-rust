//! MCP protocol and session-local state. Unity mutations live in the tool adapters.
use crate::bridge::UnityBridge;
use anyhow::{anyhow, Result};
use serde_json::{json, Value};
use std::{
    collections::{HashMap, HashSet},
    sync::{Arc, OnceLock},
    time::{Duration, Instant},
};
use tokio::sync::{broadcast, watch, Mutex};

pub fn contracts() -> &'static Vec<Value> {
    static TOOLS: OnceLock<Vec<Value>> = OnceLock::new();
    TOOLS.get_or_init(|| {
        serde_json::from_str(include_str!("../contracts/tools.json"))
            .expect("embedded tool contract")
    })
}
pub fn resource_contracts() -> &'static Vec<Value> {
    static RESOURCES: OnceLock<Vec<Value>> = OnceLock::new();
    RESOURCES.get_or_init(|| {
        serde_json::from_str(include_str!("../contracts/resources.json"))
            .expect("embedded resource contract")
    })
}
pub const GROUPS: &[(&str, &str)] = &[
    ("asset_gen", "Local 3D model file import and Blender Bridge"),
    (
        "core",
        "Essential scene, script, asset & editor tools (always on by default)",
    ),
    ("docs", "Unity API reflection"),
    ("scripting_ext", "ScriptableObject management"),
    ("testing", "Test runner & async test jobs"),
    ("ui", "UI Toolkit (UXML, USS, UIDocument)"),
    ("vfx", "Shaders & procedural textures"),
];

pub struct Session {
    pub id: String,
    pub bridge: Arc<dyn UnityBridge>,
    pub hub: Option<Arc<crate::transport::HubBridge>>,
    pub user: String,
    pub http: bool,
    pub remote: bool,
    pub project_scoped: bool,
    pub state: Mutex<SessionState>,
    pub events: broadcast::Sender<Value>,
    pub jobs: std::sync::Mutex<HashMap<String, tokio::task::AbortHandle>>,
    closed: watch::Sender<bool>,
}
pub struct SessionState {
    pub active_instance: Option<String>,
    pub enabled: HashSet<String>,
    pub unity_disabled: HashSet<String>,
    pub overrides: HashMap<String, bool>,
    pub registration: Option<Value>,
    pub touched: Instant,
    pub initialized: bool,
    pub visibility_revision: u64,
}
impl Session {
    // Constructor keeps transport/security choices explicit at each session boundary.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        id: String,
        bridge: Arc<dyn UnityBridge>,
        hub: Option<Arc<crate::transport::HubBridge>>,
        user: String,
        http: bool,
        remote: bool,
        project_scoped: bool,
        default_instance: Option<String>,
    ) -> Arc<Self> {
        let (events, _) = broadcast::channel(64);
        Arc::new(Self {
            id,
            bridge,
            hub,
            user,
            http,
            remote,
            project_scoped,
            events,
            closed: watch::channel(false).0,
            jobs: std::sync::Mutex::new(HashMap::new()),
            state: Mutex::new(SessionState {
                active_instance: default_instance,
                enabled: GROUPS
                    .iter()
                    .filter(|(g, _)| !http || *g == "core")
                    .map(|(g, _)| g.to_string())
                    .collect(),
                unity_disabled: HashSet::new(),
                overrides: HashMap::new(),
                registration: None,
                touched: Instant::now(),
                initialized: false,
                visibility_revision: 0,
            }),
        })
    }
    pub fn is_closed(&self) -> bool {
        *self.closed.borrow()
    }
    pub async fn closed(&self) {
        let mut closed = self.closed.subscribe();
        let _ = closed.wait_for(|closed| *closed).await;
    }
    pub async fn close(&self) {
        // Admission and terminal state share the jobs lock. A previously looked-up
        // session must never admit work after DELETE, expiry, or shutdown.
        let mut jobs = self.jobs.lock().unwrap();
        self.closed.send_replace(true);
        for (_, job) in jobs.drain() {
            job.abort();
        }
    }
    pub async fn expire_if_idle(&self, timeout: Duration) -> bool {
        let state = self.state.lock().await;
        let jobs = self.jobs.lock().unwrap();
        if state.touched.elapsed() <= timeout || !jobs.is_empty() {
            return false;
        }
        self.closed.send_replace(true);
        true
    }
    pub fn changed(&self) {
        let _ = self
            .events
            .send(json!({"jsonrpc":"2.0","method":"notifications/tools/list_changed"}));
    }
}
pub fn valid_request(request: &Value) -> bool {
    request["jsonrpc"] == "2.0"
        && request["method"].is_string()
        && request
            .get("id")
            .is_none_or(|id| id.is_null() || id.is_string() || id.is_number())
        && request
            .get("params")
            .is_none_or(|params| params.is_object() || params.is_array())
}
pub fn rpc_error(id: Value, code: i64, message: impl Into<String>) -> Value {
    json!({"jsonrpc":"2.0","id":id,"error":{"code":code,"message":message.into()}})
}
pub fn public_definition(mut v: Value) -> Value {
    if let Some(m) = v.as_object_mut() {
        m.remove("group");
        m.remove("unity_target");
        if let Some(meta) = m.remove("meta") {
            m.insert("_meta".into(), meta);
        }
    }
    v
}
pub async fn visible_tools(session: &Session) -> Vec<Value> {
    visible_tools_snapshot(session).await.0
}
async fn visible_tools_snapshot(session: &Session) -> (Vec<Value>, Option<String>) {
    loop {
        let (instance, revision) = {
            let state = session.state.lock().await;
            (state.active_instance.clone(), state.visibility_revision)
        };
        let registered = session
            .bridge
            .custom_tools(instance.as_deref())
            .await
            .unwrap_or_default();
        let mut state = session.state.lock().await;
        if state.visibility_revision != revision {
            continue;
        }
        if session.http {
            let has_builtins = registered
                .iter()
                .any(|t| contracts().iter().any(|c| c["unity_target"] == t["name"]));
            let signature = json!(registered);
            if state.registration.as_ref() != Some(&signature) {
                let names = registered
                    .iter()
                    .filter_map(|t| t["name"].as_str())
                    .collect::<HashSet<_>>();
                state.unity_disabled = contracts()
                    .iter()
                    .filter_map(|t| t["unity_target"].as_str())
                    .filter(|t| has_builtins && !names.contains(t))
                    .map(str::to_owned)
                    .collect();
                for (g, _) in GROUPS {
                    let enabled = state.overrides.get(*g).copied().unwrap_or_else(|| {
                        if !has_builtins {
                            return *g == "core";
                        }
                        contracts().iter().any(|t| {
                            t["group"] == *g
                                && t["unity_target"]
                                    .as_str()
                                    .is_some_and(|t| names.contains(t))
                        })
                    });
                    if enabled {
                        state.enabled.insert(g.to_string());
                    } else {
                        state.enabled.remove(*g);
                    }
                }
                state.registration = Some(signature);
            }
        }
        let mut result = contracts()
            .iter()
            .filter(|t| {
                let group = t.get("group").and_then(Value::as_str);
                let target = t.get("unity_target").and_then(Value::as_str);
                group.is_none_or(|g| state.enabled.contains(g))
                    && target.is_none_or(|t| !state.unity_disabled.contains(t))
            })
            .cloned()
            .map(public_definition)
            .collect::<Vec<_>>();
        drop(state);
        {
            let tools = registered;
            {
                for tool in tools {
                    if contracts()
                        .iter()
                        .any(|known| known["name"] == tool["name"])
                    {
                        continue;
                    }
                    if let Some(def) = custom_definition(tool) {
                        if !result.iter().any(|t| t["name"] == def["name"]) {
                            result.push(def);
                        }
                    }
                }
            }
        }
        return (result, instance);
    }
}
pub fn custom_definition(v: Value) -> Option<Value> {
    let name = v["name"].as_str()?;
    let mut properties = serde_json::Map::new();
    let mut required = Vec::new();
    for p in v["parameters"].as_array().into_iter().flatten() {
        let Some(n) = p["name"].as_str() else {
            continue;
        };
        let ty = match p["type"].as_str().unwrap_or("string") {
            "int" | "integer" => "integer",
            "float" | "double" | "number" => "number",
            "bool" | "boolean" => "boolean",
            "dict" | "object" => "object",
            "list" | "array" => "array",
            _ => "string",
        };
        let mut schema = json!({"type":ty});
        if let Some(d) = p.get("description") {
            schema["description"] = d.clone();
        }
        if p["required"].as_bool().unwrap_or(true) {
            required.push(n.to_string());
        } else if let Some(default) = p.get("default_value").filter(|v| !v.is_null()) {
            let mut value = default.clone();
            if ["object", "array"].contains(&ty) {
                if let Some(raw) = value.as_str() {
                    if let Ok(parsed) = serde_json::from_str::<Value>(raw) {
                        value = parsed;
                    }
                }
            }
            coerce_schema(&schema, &mut value);
            schema["default"] = value;
        }
        properties.insert(n.to_string(), schema);
    }
    Some(
        json!({"name":name,"description":v["description"].as_str().unwrap_or("Unity custom tool"),"inputSchema":{"type":"object","properties":properties,"required":required}}),
    )
}
fn group_tools(group: &str) -> Vec<String> {
    contracts()
        .iter()
        .filter(|t| t["group"].as_str() == Some(group))
        .filter_map(|t| t["name"].as_str().map(str::to_owned))
        .collect()
}
pub async fn groups(session: &Session) -> Value {
    let state = session.state.lock().await;
    let groups=GROUPS.iter().map(|(g,d)| {let tools=group_tools(g);json!({"name":g,"description":d,"enabled":state.enabled.contains(*g),"default_enabled":*g=="core","tool_count":tools.len(),"tools":tools})}).collect::<Vec<_>>();
    json!({"groups":groups,"note":"Use activate/deactivate to toggle groups for this session. Tools with group=None (server meta-tools) are always visible."})
}
async fn manage_tools(session: &Session, args: &Value) -> Result<Value> {
    let action = args["action"].as_str().unwrap_or("");
    if action == "list_groups" {
        return Ok(groups(session).await);
    }
    if action == "sync" {
        let (instance, revision) = {
            let state = session.state.lock().await;
            (state.active_instance.clone(), state.visibility_revision)
        };
        let result = session
            .bridge
            .send("get_tool_states", json!({}), instance.as_deref())
            .await?;
        let data = result.get("data").unwrap_or(&result);
        let states = data
            .get("tools")
            .or_else(|| data.get("tool_states"))
            .unwrap_or(data);
        let mut disabled = HashSet::new();
        let mut seen = 0;
        if let Some(arr) = states.as_array() {
            for t in arr {
                if let Some(name) = t["name"].as_str() {
                    seen += 1;
                    if t["enabled"] == false {
                        disabled.insert(name.to_string());
                    }
                }
            }
        } else if let Some(map) = states.as_object() {
            for (name, value) in map {
                if value.is_boolean() {
                    seen += 1;
                    if value == false {
                        disabled.insert(name.clone());
                    }
                } else if value.get("enabled").is_some() {
                    seen += 1;
                    if value["enabled"] == false {
                        disabled.insert(name.clone());
                    }
                }
            }
        }
        if seen == 0 {
            return Ok(
                json!({"error":"Connected Unity Editor returned no tool states","details":result}),
            );
        }
        let mut state = session.state.lock().await;
        if state.visibility_revision != revision {
            return Err(anyhow!("Tool visibility changed while syncing; retry sync"));
        }
        state.visibility_revision = state.visibility_revision.wrapping_add(1);
        state.unity_disabled = disabled;
        for (g, _) in GROUPS {
            let tools = group_tools(g);
            if tools.iter().any(|t| !state.unity_disabled.contains(t)) {
                state.enabled.insert(g.to_string());
            } else {
                state.enabled.remove(*g);
            }
        }
        let enabled = state.enabled.iter().cloned().collect::<Vec<_>>();
        let disabled_groups = GROUPS
            .iter()
            .filter(|(g, _)| !state.enabled.contains(*g))
            .map(|(g, _)| *g)
            .collect::<Vec<_>>();
        let result = json!({"synced":true,"enabled_groups":enabled,"disabled_groups":disabled_groups,"enabled_tool_count":seen-state.unity_disabled.len().min(seen),"total_tool_count":seen});
        drop(state);
        session.changed();
        return Ok(result);
    }
    let mut state = session.state.lock().await;
    if action == "reset" {
        state.visibility_revision = state.visibility_revision.wrapping_add(1);
        state.unity_disabled.clear();
        state.overrides.clear();
        state.registration = None;
        state.enabled = GROUPS
            .iter()
            .filter(|(g, _)| !session.http || *g == "core")
            .map(|(g, _)| g.to_string())
            .collect();
        drop(state);
        session.changed();
        return Ok(
            json!({"reset":true,"default_groups":["core"],"message":"Tool visibility restored to server defaults."}),
        );
    }
    let group = args["group"].as_str().unwrap_or("").trim().to_lowercase();
    if !GROUPS.iter().any(|(g, _)| *g == group) {
        return Ok(json!({"error":format!("Unknown group '{group}'.")}));
    }
    let result = match action {
        "activate" => {
            state.visibility_revision = state.visibility_revision.wrapping_add(1);
            state.overrides.insert(group.clone(), true);
            state.enabled.insert(group.clone());
            json!({"activated":group,"tools":group_tools(&group),"message":format!("Group '{group}' is now visible. Its tools will appear in tool listings.")})
        }
        "deactivate" => {
            state.visibility_revision = state.visibility_revision.wrapping_add(1);
            state.overrides.insert(group.clone(), false);
            state.enabled.remove(&group);
            json!({"deactivated":group,"tools":group_tools(&group),"message":format!("Group '{group}' is now hidden.")})
        }
        _ => json!({"error":format!("Unknown action '{action}'")}),
    };
    drop(state);
    session.changed();
    Ok(result)
}
pub async fn select_instance(session: &Session, token: &str) -> Result<Value> {
    if session.http && !token.is_empty() && token.bytes().all(|b| b.is_ascii_digit()) {
        return Ok(
            json!({"success":false,"error":"Port-based targeting is not supported in HTTP transport mode. Use Name@hash or a hash prefix."}),
        );
    }
    let values = session.bridge.instances().await?;
    let instances = values
        .as_array()
        .or_else(|| values["instances"].as_array())
        .ok_or_else(|| anyhow!("Invalid Unity instance list"))?;
    let matches = instances
        .iter()
        .filter(|i| {
            let id = i["id"].as_str().unwrap_or("");
            let hash = i["hash"].as_str().unwrap_or("");
            if token.contains('@') {
                id == token
            } else {
                (!token.is_empty() && hash.to_lowercase().starts_with(&token.to_lowercase()))
                    || (!session.http && i["port"].as_u64().is_some_and(|p| p.to_string() == token))
            }
        })
        .collect::<Vec<_>>();
    if matches.len() != 1 {
        return Ok(
            json!({"success":false,"error":if matches.is_empty(){format!("Instance '{token}' not found. Read mcpforunity://instances.")}else{format!("Instance '{token}' is ambiguous. Use full Name@hash.")}}),
        );
    }
    let id = matches[0]["id"]
        .as_str()
        .ok_or_else(|| anyhow!("Missing instance id"))?
        .to_owned();
    let mut state = session.state.lock().await;
    state.active_instance = Some(id.clone());
    state.visibility_revision = state.visibility_revision.wrapping_add(1);
    state.registration = None;
    state.unity_disabled.clear();
    state.enabled = GROUPS
        .iter()
        .filter(|(group, _)| {
            state
                .overrides
                .get(*group)
                .copied()
                .unwrap_or(!session.http || *group == "core")
        })
        .map(|(group, _)| group.to_string())
        .collect();
    drop(state);
    session.changed();
    Ok(
        json!({"success":true,"message":format!("Active instance set to {id}"),"data":{"instance":id,"session_id":session.id}}),
    )
}
fn content_result(value: Value, tool_name: &str) -> Value {
    if value.get("content").is_some_and(Value::is_array) {
        return value;
    }
    let mut result = json!({"content":[{"type":"text","text":value.as_str().map(str::to_owned).unwrap_or_else(||serde_json::to_string(&value).unwrap_or_default())}],"isError":false});
    if value.is_object() {
        // FastMCP wraps union/non-object return types in the advertised
        // output schema. Text keeps the original result for existing clients.
        let wrapped = contracts()
            .iter()
            .find(|tool| tool["name"] == tool_name)
            .is_some_and(|tool| tool["outputSchema"]["x-fastmcp-wrap-result"] == true);
        result["structuredContent"] = if wrapped {
            json!({"result": value})
        } else {
            value
        };
    }
    result
}
fn tool_error(message: impl Into<String>) -> Value {
    json!({"content":[{"type":"text","text":message.into()}],"isError":true})
}
fn coerce_schema(schema: &Value, value: &mut Value) {
    if let Some(branches) = schema
        .get("anyOf")
        .or_else(|| schema.get("oneOf"))
        .and_then(Value::as_array)
    {
        if branches
            .iter()
            .any(|s| jsonschema::validator_for(s).is_ok_and(|v| v.is_valid(value)))
        {
            return;
        }
        for branch in branches {
            let mut candidate = value.clone();
            coerce_schema(branch, &mut candidate);
            if jsonschema::validator_for(branch).is_ok_and(|v| v.is_valid(&candidate)) {
                *value = candidate;
                return;
            }
        }
    }
    if let Some(text) = value.as_str() {
        let replacement = match schema["type"].as_str() {
            Some("integer") => text.parse::<i64>().ok().map(|n| json!(n)),
            Some("number") => text
                .parse::<f64>()
                .ok()
                .filter(|n| n.is_finite())
                .map(|n| json!(n)),
            Some("boolean") => match text.to_ascii_lowercase().as_str() {
                "true" | "1" | "yes" | "on" | "t" | "y" => Some(json!(true)),
                "false" | "0" | "no" | "off" | "f" | "n" => Some(json!(false)),
                _ => None,
            },
            _ => None,
        };
        if let Some(v) = replacement {
            *value = v;
        }
    }
    if schema["type"] == "boolean" {
        if value == &json!(0) {
            *value = json!(false)
        } else if value == &json!(1) {
            *value = json!(true)
        }
    }
    if schema["type"] == "object" {
        if let Some(map) = value.as_object_mut() {
            for (k, v) in map.iter_mut() {
                if let Some(property) = schema["properties"].get(k) {
                    coerce_schema(property, v);
                }
            }
        }
    }
    if schema["type"] == "array" {
        if let Some(items) = value.as_array_mut() {
            for item in items {
                coerce_schema(&schema["items"], item);
            }
        }
    }
}
fn validate_args(schema: &Value, args: &Value) -> Result<()> {
    let mut value = args.clone();
    if let Some(m) = value.as_object_mut() {
        m.remove("unity_instance");
        let required = schema["required"].as_array().cloned().unwrap_or_default();
        m.retain(|k, v| !v.is_null() || required.contains(&Value::String(k.clone())));
    }
    let validator = jsonschema::validator_for(schema)?;
    if let Err(e) = validator.validate(&value) {
        return Err(anyhow!("Invalid tool arguments: {e}"));
    }
    Ok(())
}
pub async fn dispatch(session: &Arc<Session>, request: Value) -> Option<Value> {
    let id = request.get("id").cloned();
    if session.is_closed() {
        return id.map(|id| rpc_error(id, -32000, "MCP session is closed"));
    }
    if !valid_request(&request) {
        return Some(rpc_error(
            id.unwrap_or(Value::Null),
            -32600,
            "Invalid JSON-RPC request",
        ));
    }
    let method = request["method"].as_str().unwrap();
    let params = request.get("params").cloned().unwrap_or_else(|| json!({}));
    session.state.lock().await.touched = Instant::now();
    if method == "notifications/cancelled" {
        if let Some(id) = params.get("requestId") {
            if let Some(job) = session.jobs.lock().unwrap().get(&id.to_string()) {
                job.abort();
            }
        }
        return None;
    }
    if id.is_none() {
        if method == "notifications/initialized" {
            session.state.lock().await.initialized = true;
            if !session.http {
                let _ = manage_tools(session, &json!({"action":"sync"})).await;
            }
        }
        return None;
    }
    let id = id.unwrap();
    let result:Result<Value>=async { match method {
        "initialize"=>{
            let requested=params["protocolVersion"].as_str().unwrap_or("");
            let version=if ["2024-11-05","2025-03-26","2025-06-18","2025-11-25"].contains(&requested){requested}else{"2025-06-18"};
            Ok(json!({"protocolVersion":version,"capabilities":{"tools":{"listChanged":true},"resources":{"listChanged":false},"prompts":{"listChanged":false},"logging":{}},"serverInfo":{"name":"unity-mcp-light","version":env!("CARGO_PKG_VERSION")},"instructions":serde_json::from_str::<Value>(include_str!("../contracts/instructions.json")).expect("embedded instructions")[if session.project_scoped{"project_scoped"}else{"unscoped"}]}))
        },
        "ping"=>Ok(json!({})),
        "tools/list"=>Ok(json!({"tools":visible_tools(session).await})),
        "resources/list"=>Ok(json!({"resources":resource_contracts().iter().filter(|r|r.get("uri").is_some()&&(session.project_scoped||r["name"]!="custom_tools")).cloned().map(public_definition).collect::<Vec<_>>()})),
        "resources/templates/list"=>Ok(json!({"resourceTemplates":resource_contracts().iter().filter(|r|r.get("uriTemplate").is_some()).cloned().map(public_definition).collect::<Vec<_>>()})),
        "prompts/list"=>Ok(json!({"prompts":[]})),
        "logging/setLevel"=>Ok(json!({})),
        "completion/complete"=>Ok(json!({"completion":{"values":[],"total":0,"hasMore":false}})),
        "tools/call"=>{
            let name=params["name"].as_str().ok_or_else(||anyhow!("Tool name is required"))?;
            let mut args=params.get("arguments").cloned().unwrap_or_else(||json!({}));
            if !args.is_object(){return Ok(tool_error("Tool arguments must be an object"));}
            let (definitions, active_instance)=visible_tools_snapshot(session).await;
            let Some(def)=definitions.iter().find(|t|t["name"]==name) else {return Ok(tool_error(format!("Unknown or disabled tool '{name}'")));};
            coerce_schema(&def["inputSchema"],&mut args);
            if let Err(e)=validate_args(&def["inputSchema"],&args){return Ok(tool_error(e.to_string()));}
            if args.get("unity_instance").is_some_and(|v|!v.is_null()&&!v.is_string()){return Ok(tool_error("unity_instance must be a string, such as Name@hash or a quoted port"));}
            let instance=args.as_object_mut().and_then(|m|m.remove("unity_instance")).and_then(|v|v.as_str().map(str::to_owned));
            let selected=instance.or(active_instance);
            if session.remote && selected.is_none() && !["manage_tools","set_active_instance"].contains(&name){return Ok(tool_error("Unity instance selection is required. Call set_active_instance with Name@hash from mcpforunity://instances."));}
            let response=match name {
                "manage_tools"=>manage_tools(session,&args).await,
                "set_active_instance"=>select_instance(session,args["instance"].as_str().unwrap_or("").trim()).await,
                _ if contracts().iter().any(|t|t["name"]==name)=>crate::tools::call(session.bridge.as_ref(),name,args,selected.as_deref()).await,
                _=>crate::tools::call_custom(session.bridge.as_ref(),name,args,selected.as_deref()).await,
            };
            Ok(match response {Ok(v)=>content_result(v, name),Err(e)=>tool_error(e.to_string())})
        },
        "resources/read"=>{
            let uri=params["uri"].as_str().ok_or_else(||anyhow!("Resource URI required"))?;
            let instance=session.state.lock().await.active_instance.clone();
            if session.remote && instance.is_none() && !["mcpforunity://instances","mcpforunity://tool-groups","mcpforunity://scene/gameobject-api","mcpforunity://prefab-api"].contains(&uri){return Err(anyhow!("Unity instance selection is required. Call set_active_instance first."));}
            let value=if uri=="mcpforunity://custom-tools" && session.http { if let Some(instance)=instance.as_deref(){ let tools=session.bridge.custom_tools(Some(instance)).await?;json!({"success":true,"message":"Custom tools retrieved successfully.","error":null,"hint":null,"data":{"project_id":instance.rsplit('@').next().unwrap_or(instance),"tool_count":tools.len(),"tools":tools}}) } else {json!({"success":false,"message":"No active Unity instance selected","error":"No active Unity instance selected","hint":null,"data":null})} }
                else {crate::resources::read(session.bridge.as_ref(),uri,instance.as_deref()).await?};
            Ok(json!({"contents":[{"uri":uri,"mimeType":"text/plain","text":serde_json::to_string(&value)?}]}))
        },
        _=>Err(anyhow!("Method not found: {method}")),
    }}.await;
    Some(match result {
        Ok(result) => json!({"jsonrpc":"2.0","id":id,"result":result}),
        Err(e) => rpc_error(
            id,
            if e.to_string().starts_with("Method not found") {
                -32601
            } else {
                -32602
            },
            e.to_string(),
        ),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Fake;
    #[test]
    fn wrapped_tools_conform_to_advertised_output_schema() {
        for name in ["refresh_unity", "run_tests", "get_test_job"] {
            let value = json!({"success":true,"message":"verified","data":null});
            let output = content_result(value.clone(), name);
            assert_eq!(output["structuredContent"], json!({"result":value}));
            let schema = &contracts()
                .iter()
                .find(|tool| tool["name"] == name)
                .unwrap()["outputSchema"];
            assert!(jsonschema::validator_for(schema)
                .unwrap()
                .is_valid(&output["structuredContent"]));
            assert_eq!(
                serde_json::from_str::<Value>(output["content"][0]["text"].as_str().unwrap())
                    .unwrap(),
                value
            );
        }
    }

    #[async_trait::async_trait]
    impl UnityBridge for Fake {
        async fn send(&self, _: &str, p: Value, _: Option<&str>) -> Result<Value> {
            Ok(p)
        }
        async fn instances(&self) -> Result<Value> {
            Ok(
                json!({"instances":[{"id":"A@abc1","hash":"abc1","port":6400},{"id":"B@abc2","hash":"abc2","port":6401}]}),
            )
        }
    }
    fn session(http: bool) -> Arc<Session> {
        Session::new(
            "test".into(),
            Arc::new(Fake),
            None,
            "local".into(),
            http,
            false,
            false,
            None,
        )
    }
    #[tokio::test]
    async fn schemas_and_visibility() {
        let s = session(false);
        assert_eq!(visible_tools(&s).await.len(), 38);
        let s = session(true);
        assert!(visible_tools(&s).await.len() < 38);
        manage_tools(&s, &json!({"action":"activate","group":"vfx"}))
            .await
            .unwrap();
        assert!(visible_tools(&s)
            .await
            .iter()
            .any(|t| t["name"] == "manage_shader"));
    }
    #[tokio::test]
    async fn selection_is_session_local() {
        let a = session(true);
        let b = session(true);
        assert_eq!(select_instance(&a, "abc").await.unwrap()["success"], false);
        assert_eq!(
            select_instance(&a, "A@abc1").await.unwrap()["success"],
            true
        );
        assert!(b.state.lock().await.active_instance.is_none());
        assert_eq!(select_instance(&a, "6400").await.unwrap()["success"], false);
    }
    #[tokio::test]
    async fn notifications_have_no_reply() {
        assert!(dispatch(
            &session(false),
            json!({"jsonrpc":"2.0","method":"notifications/initialized"})
        )
        .await
        .is_none());
    }
    #[tokio::test]
    async fn expiry_is_terminal_but_active_work_prevents_it() {
        let s = session(true);
        s.state.lock().await.touched = Instant::now() - Duration::from_secs(10);
        let job = tokio::spawn(std::future::pending::<()>());
        s.jobs
            .lock()
            .unwrap()
            .insert("1".into(), job.abort_handle());
        assert!(!s.expire_if_idle(Duration::from_secs(1)).await);
        s.jobs.lock().unwrap().clear();
        job.abort();
        assert!(s.expire_if_idle(Duration::from_secs(1)).await);
        assert!(s.is_closed());
        let result = dispatch(&s, json!({"jsonrpc":"2.0","id":2,"method":"ping"}))
            .await
            .unwrap();
        assert_eq!(result["error"]["code"], -32000);
    }
    #[tokio::test]
    async fn switching_instance_and_reset_clear_old_restrictions() {
        let s = session(true);
        {
            let mut state = s.state.lock().await;
            state.unity_disabled.insert("read_console".into());
            state.registration = Some(json!([{"name":"other"}]));
        }
        select_instance(&s, "A@abc1").await.unwrap();
        assert!(visible_tools(&s)
            .await
            .iter()
            .any(|t| t["name"] == "read_console"));
        s.state
            .lock()
            .await
            .unity_disabled
            .insert("read_console".into());
        manage_tools(&s, &json!({"action":"reset"})).await.unwrap();
        assert!(visible_tools(&s)
            .await
            .iter()
            .any(|t| t["name"] == "read_console"));
    }
    struct DelayedRegistration {
        started: tokio::sync::Notify,
        release: tokio::sync::Notify,
    }
    #[async_trait::async_trait]
    impl UnityBridge for DelayedRegistration {
        async fn send(&self, _: &str, _: Value, _: Option<&str>) -> Result<Value> {
            Ok(json!({}))
        }
        async fn instances(&self) -> Result<Value> {
            Fake.instances().await
        }
        async fn custom_tools(&self, instance: Option<&str>) -> Result<Vec<Value>> {
            if instance == Some("A@abc1") {
                self.started.notify_one();
                self.release.notified().await;
                return Ok(vec![
                    json!({"name":"manage_shader"}),
                    json!({"name":"old_custom"}),
                ]);
            }
            Ok(vec![])
        }
    }
    #[tokio::test]
    async fn late_registration_cannot_restore_previous_instance_tools() {
        let bridge = Arc::new(DelayedRegistration {
            started: tokio::sync::Notify::new(),
            release: tokio::sync::Notify::new(),
        });
        let s = Session::new(
            "test".into(),
            bridge.clone(),
            None,
            "local".into(),
            true,
            false,
            false,
            Some("A@abc1".into()),
        );
        let copy = s.clone();
        let listing = tokio::spawn(async move { visible_tools_snapshot(&copy).await });
        bridge.started.notified().await;
        select_instance(&s, "B@abc2").await.unwrap();
        bridge.release.notify_one();
        let (tools, selected) = listing.await.unwrap();
        assert_eq!(selected.as_deref(), Some("B@abc2"));
        assert!(!tools
            .iter()
            .any(|t| t["name"] == "old_custom" || t["name"] == "manage_shader"));
        assert!(tools.iter().any(|t| t["name"] == "read_console"));
        assert_eq!(s.state.lock().await.registration, Some(json!([])));
    }
    struct DelayedSync {
        started: tokio::sync::Notify,
        release: tokio::sync::Notify,
    }
    #[async_trait::async_trait]
    impl UnityBridge for DelayedSync {
        async fn send(&self, _: &str, _: Value, _: Option<&str>) -> Result<Value> {
            self.started.notify_one();
            self.release.notified().await;
            Ok(json!({"tools":[{"name":"read_console","enabled":false}]}))
        }
        async fn instances(&self) -> Result<Value> {
            Fake.instances().await
        }
    }
    #[tokio::test]
    async fn late_sync_cannot_overwrite_new_instance_visibility() {
        let bridge = Arc::new(DelayedSync {
            started: tokio::sync::Notify::new(),
            release: tokio::sync::Notify::new(),
        });
        let s = Session::new(
            "test".into(),
            bridge.clone(),
            None,
            "local".into(),
            true,
            false,
            false,
            Some("A@abc1".into()),
        );
        let copy = s.clone();
        let sync =
            tokio::spawn(async move { manage_tools(&copy, &json!({"action":"sync"})).await });
        bridge.started.notified().await;
        select_instance(&s, "B@abc2").await.unwrap();
        bridge.release.notify_one();
        assert!(sync.await.unwrap().is_err());
        assert!(s.state.lock().await.unity_disabled.is_empty());
    }
    struct ChangingRegistration(std::sync::atomic::AtomicBool);
    #[async_trait::async_trait]
    impl UnityBridge for ChangingRegistration {
        async fn send(&self, _: &str, _: Value, _: Option<&str>) -> Result<Value> {
            Ok(json!({}))
        }
        async fn instances(&self) -> Result<Value> {
            Fake.instances().await
        }
        async fn custom_tools(&self, _: Option<&str>) -> Result<Vec<Value>> {
            Ok(if self.0.load(std::sync::atomic::Ordering::Relaxed) {
                vec![json!({"name":"manage_shader"})]
            } else {
                vec![]
            })
        }
    }
    #[tokio::test]
    async fn registration_removal_restores_default_visibility() {
        let bridge = Arc::new(ChangingRegistration(std::sync::atomic::AtomicBool::new(
            true,
        )));
        let s = Session::new(
            "test".into(),
            bridge.clone(),
            None,
            "local".into(),
            true,
            false,
            false,
            None,
        );
        assert!(!visible_tools(&s)
            .await
            .iter()
            .any(|t| t["name"] == "read_console"));
        bridge.0.store(false, std::sync::atomic::Ordering::Relaxed);
        assert!(visible_tools(&s)
            .await
            .iter()
            .any(|t| t["name"] == "read_console"));
        assert!(s.state.lock().await.unity_disabled.is_empty());
    }
    #[test]
    fn compact_optional_nulls_accepted() {
        let schema = json!({"type":"object","properties":{"foo":{"type":"string"}},"additionalProperties":false});
        assert!(validate_args(&schema, &json!({"foo":null,"unity_instance":"abc"})).is_ok());
    }
    struct Registered;
    #[async_trait::async_trait]
    impl UnityBridge for Registered {
        async fn send(&self, _: &str, p: Value, _: Option<&str>) -> Result<Value> {
            Ok(p)
        }
        async fn instances(&self) -> Result<Value> {
            Ok(json!({"instances":[]}))
        }
        async fn custom_tools(&self, _: Option<&str>) -> Result<Vec<Value>> {
            Ok(vec![
                json!({"name":"read_console"}),
                json!({"name":"manage_shader"}),
                json!({"name":"custom_one","parameters":[]}),
            ])
        }
    }
    #[tokio::test]
    async fn registered_builtins_cannot_bypass_disabled_group() {
        let s = Session::new(
            "registered".into(),
            Arc::new(Registered),
            None,
            "local".into(),
            true,
            false,
            false,
            None,
        );
        assert!(visible_tools(&s)
            .await
            .iter()
            .any(|t| t["name"] == "manage_shader"));
        manage_tools(&s, &json!({"action":"deactivate","group":"vfx"}))
            .await
            .unwrap();
        let tools = visible_tools(&s).await;
        assert!(!tools.iter().any(|t| t["name"] == "manage_shader"));
        assert!(tools.iter().any(|t| t["name"] == "custom_one"));
    }
    #[test]
    fn custom_defaults_and_required_match_unity_model() {
        let definition=custom_definition(json!({"name":"custom","parameters":[{"name":"required","type":"string"},{"name":"count","type":"int","required":false,"default_value":"7"}]})).unwrap();
        assert_eq!(definition["inputSchema"]["required"], json!(["required"]));
        assert_eq!(
            definition["inputSchema"]["properties"]["count"]["default"],
            7
        );
    }
}
