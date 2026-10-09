//! Resource routing. Wire names intentionally follow the Python/C# contract.
use crate::bridge::UnityBridge;
use anyhow::{bail, Result};
use serde_json::{json, Value};

fn decode(s: &str) -> String {
    percent_encoding::percent_decode_str(s)
        .decode_utf8_lossy()
        .into_owned()
}
fn failure(error: impl Into<String>) -> Value {
    json!({"success":false,"message":null,"error":error.into(),"data":null})
}

pub async fn read(bridge: &dyn UnityBridge, uri: &str, instance: Option<&str>) -> Result<Value> {
    let static_resources: Value =
        serde_json::from_str(include_str!("../contracts/static_resources.json"))?;
    if let Some(value) = static_resources.get(uri) {
        return Ok(value.clone());
    }
    if uri == "mcpforunity://instances" {
        return bridge.instances().await;
    }
    if uri == "mcpforunity://custom-tools" {
        let Some(instance) = instance else {
            return Ok(
                json!({"success":false,"message":"No active Unity instance. Call set_active_instance with Name@hash from mcpforunity://instances.","error":null,"data":null}),
            );
        };
        let response = bridge
            .send("get_tool_states", json!({}), Some(instance))
            .await?;
        let tools: Vec<Value> = response
            .pointer("/data/tools")
            .or_else(|| response.get("tools"))
            .and_then(Value::as_array)
            .map(|t| {
                t.iter()
                    .filter(|v| v.get("is_built_in") == Some(&Value::Bool(false)))
                    .cloned()
                    .collect()
            })
            .unwrap_or_default();
        return Ok(
            json!({"success":true,"message":"Custom tools retrieved successfully.","error":null,"data":{"project_id":instance.rsplit('@').next().unwrap_or(instance),"tool_count":tools.len(),"tools":tools}}),
        );
    }
    let parsed = url::Url::parse(uri)?;
    let base = uri.split('?').next().unwrap_or(uri);
    let (command, params) = if let Some(rest) = base.strip_prefix("mcpforunity://scene/gameobject/")
    {
        let parts: Vec<_> = rest.split('/').collect();
        let id: i64 = match parts[0].parse() {
            Ok(id) => id,
            Err(_) => return Ok(failure(format!("Invalid instance ID: {}", parts[0]))),
        };
        match parts.as_slice() {
            [_] => ("get_gameobject", json!({"instanceID":id})),
            [_, "components"] => {
                let query: std::collections::HashMap<_, _> =
                    parsed.query_pairs().into_owned().collect();
                let size = query
                    .get("page_size")
                    .or_else(|| query.get("pageSize"))
                    .and_then(|s| s.parse::<i64>().ok())
                    .unwrap_or(25);
                let cursor = query
                    .get("cursor")
                    .and_then(|s| s.parse::<i64>().ok())
                    .unwrap_or(0);
                let props = query
                    .get("include_properties")
                    .or_else(|| query.get("includeProperties"))
                    .map(|s| s != "false" && s != "0")
                    .unwrap_or(true);
                (
                    "get_gameobject_components",
                    json!({"instanceID":id,"pageSize":size,"cursor":cursor,"includeProperties":props}),
                )
            }
            [_, "component", name] => (
                "get_gameobject_component",
                json!({"instanceID":id,"componentName":decode(name)}),
            ),
            _ => bail!("Unknown resource: {uri}"),
        }
    } else if let Some(rest) = base.strip_prefix("mcpforunity://prefab/") {
        let (path, action) = match rest.strip_suffix("/hierarchy") {
            Some(p) => (p, "get_hierarchy"),
            None => (rest, "get_info"),
        };
        (
            "manage_prefabs",
            json!({"action":action,"prefabPath":decode(path)}),
        )
    } else if let Some(mode) = base.strip_prefix("mcpforunity://tests/") {
        if !["EditMode", "PlayMode"].contains(&mode) {
            return Ok(failure("mode must be EditMode or PlayMode"));
        }
        ("get_tests_for_mode", json!({"mode":mode}))
    } else {
        let name = match base {
            "mcpforunity://editor/state" => "get_editor_state",
            "mcpforunity://editor/active-tool" => "get_active_tool",
            "mcpforunity://editor/prefab-stage" => "get_prefab_stage",
            "mcpforunity://editor/selection" => "get_selection",
            "mcpforunity://editor/windows" => "get_windows",
            "mcpforunity://project/info" => "get_project_info",
            "mcpforunity://project/tags" => "get_tags",
            "mcpforunity://project/layers" => "get_layers",
            "mcpforunity://menu-items" => "get_menu_items",
            "mcpforunity://scene/cameras" => "get_cameras",
            "mcpforunity://scene/volumes" => "get_volumes",
            "mcpforunity://pipeline/renderer-features" => "get_renderer_features",
            "mcpforunity://rendering/stats" => "get_rendering_stats",
            "mcpforunity://tests" => "get_tests",
            _ => bail!("Unknown resource: {uri}"),
        };
        (
            name,
            if name == "get_menu_items" {
                json!({"refresh":true,"search":""})
            } else {
                json!({})
            },
        )
    };
    let mut response = bridge.send(command, params, instance).await?;
    if command == "get_editor_state" && response.get("success") != Some(&Value::Bool(false)) {
        let inferred;
        let instance = if instance.is_some() {
            instance
        } else {
            inferred = bridge.instances().await.ok().and_then(|v| {
                let items = v.get("instances")?.as_array()?;
                if items.len() == 1 {
                    items[0].get("id")?.as_str().map(str::to_owned)
                } else {
                    None
                }
            });
            inferred.as_deref()
        };
        enrich_editor_state(&mut response, instance);
        if let Some(id) = instance.filter(|_| bridge.local_filesystem_allowed()) {
            let project = bridge
                .send("get_project_info", json!({}), Some(id))
                .await
                .ok();
            let root = project
                .as_ref()
                .and_then(|v| v.pointer("/data/projectRoot"))
                .and_then(Value::as_str)
                .filter(|s| !s.is_empty())
                .map(std::path::PathBuf::from);
            let id = id.to_owned();
            let changes = crate::scanner::update_async(id, root).await?;
            if !response["data"]["assets"].is_object() {
                response["data"]["assets"] = json!({});
            }
            if let Some(fields) = changes.as_object() {
                for (k, v) in fields {
                    response["data"]["assets"][k] = v.clone();
                }
            }
        }
    }
    if response.get("success").is_some() {
        let schemas: Value =
            serde_json::from_str(include_str!("../contracts/resource_response_schemas.json"))?;
        let template = if base.starts_with("mcpforunity://scene/gameobject/") {
            if base.ends_with("/components") {
                "mcpforunity://scene/gameobject/{instance_id}/components"
            } else if base.contains("/component/") {
                "mcpforunity://scene/gameobject/{instance_id}/component/{component_name}"
            } else {
                "mcpforunity://scene/gameobject/{instance_id}"
            }
        } else if base.starts_with("mcpforunity://prefab/") {
            if base.ends_with("/hierarchy") {
                "mcpforunity://prefab/{encoded_path}/hierarchy"
            } else {
                "mcpforunity://prefab/{encoded_path}"
            }
        } else if base.starts_with("mcpforunity://tests/") {
            "mcpforunity://tests/{mode}"
        } else {
            base
        };
        if command == "get_editor_state" && response["success"] == true {
            let schema = &schemas["editor_state_data"];
            response["data"] = apply_defaults(&response["data"], schema, schema);
        }
        let key = if response["success"] == false {
            "mcpforunity://scene/gameobject/{instance_id}"
        } else {
            template
        };
        if let Some(schema) = schemas.get(key) {
            response = apply_defaults(&response, schema, schema);
        }
    }
    Ok(response)
}

fn apply_defaults(value: &Value, schema: &Value, root: &Value) -> Value {
    if let Some(reference) = schema.get("$ref").and_then(Value::as_str) {
        if let Some(target) = root.pointer(reference.trim_start_matches('#')) {
            return apply_defaults(value, target, root);
        }
    }
    if let Some(variants) = schema.get("anyOf").and_then(Value::as_array) {
        if value.is_null() {
            return Value::Null;
        }
        if let Some(branch) = variants.iter().find(|s| s["type"] != "null") {
            return apply_defaults(value, branch, root);
        }
    }
    if let (Some(object), Some(props)) = (
        value.as_object(),
        schema.get("properties").and_then(Value::as_object),
    ) {
        let mut out = serde_json::Map::new();
        for (key, prop) in props {
            if let Some(v) = object.get(key) {
                out.insert(key.clone(), apply_defaults(v, prop, root));
            } else if let Some(v) = prop.get("default") {
                out.insert(key.clone(), apply_defaults(v, prop, root));
            } else if prop["type"] == "array"
                && !schema
                    .get("required")
                    .and_then(Value::as_array)
                    .is_some_and(|r| r.contains(&json!(key)))
            {
                out.insert(key.clone(), json!([]));
            }
        }
        return Value::Object(out);
    }
    if let (Some(array), Some(items)) = (value.as_array(), schema.get("items")) {
        return Value::Array(
            array
                .iter()
                .map(|v| apply_defaults(v, items, root))
                .collect(),
        );
    }
    value.clone()
}

fn enrich_editor_state(response: &mut Value, instance: Option<&str>) {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64;
    if !response.get("data").is_some_and(Value::is_object) {
        response["data"] = json!({});
    }
    let data = &mut response["data"];
    let map = data.as_object_mut().unwrap();
    map.entry("schema_version")
        .or_insert(json!("unity-mcp/editor_state@2"));
    map.entry("observed_at_unix_ms").or_insert(json!(now));
    map.entry("sequence").or_insert(json!(0));
    if !data.get("unity").is_some_and(Value::is_object) {
        data["unity"] = json!({});
    }
    if data
        .pointer("/unity/instance_id")
        .is_none_or(|v| v.is_null() || v == "")
    {
        if let Some(id) = instance {
            data["unity"]["instance_id"] = json!(id);
        }
    }
    let age = now
        .saturating_sub(data["observed_at_unix_ms"].as_i64().unwrap_or(now))
        .max(0);
    let mut blockers = Vec::new();
    for (pointer, reason) in [
        ("/compilation/is_compiling", "compiling"),
        ("/compilation/is_domain_reload_pending", "domain_reload"),
        ("/tests/is_running", "running_tests"),
        ("/assets/refresh/is_refresh_in_progress", "asset_refresh"),
    ] {
        if data.pointer(pointer) == Some(&Value::Bool(true)) {
            blockers.push(reason);
        }
    }
    if age > 2000 {
        blockers.push("stale_status");
    }
    let ready = blockers.is_empty();
    data["advice"] = json!({"ready_for_tools":ready,"blocking_reasons":blockers,"recommended_retry_after_ms":if ready{0}else{500},"recommended_next_action":if ready{"none"}else{"retry_later"}});
    data["staleness"] = json!({"age_ms":age,"is_stale":age>2000});
    response["success"] = json!(true);
    response["message"] = json!("Retrieved editor state.");
}

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    struct Echo;
    #[async_trait]
    impl UnityBridge for Echo {
        async fn send(&self, c: &str, p: Value, i: Option<&str>) -> Result<Value> {
            Ok(json!({"command":c,"params":p,"instance":i}))
        }
        async fn instances(&self) -> Result<Value> {
            Ok(json!({"instances":[]}))
        }
    }
    #[tokio::test]
    async fn components_routing() {
        let r = read(
            &Echo,
            "mcpforunity://scene/gameobject/-123/components",
            Some("Game@abc"),
        )
        .await
        .unwrap();
        assert_eq!(r["command"], "get_gameobject_components");
        assert_eq!(
            r["params"],
            json!({"instanceID":-123,"pageSize":25,"cursor":0,"includeProperties":true})
        );
        assert_eq!(r["instance"], "Game@abc");
    }
    #[tokio::test]
    async fn prefab_decodes_path() {
        let r = read(
            &Echo,
            "mcpforunity://prefab/Assets%2FPrefabs%2FMy%20Object.prefab/hierarchy",
            None,
        )
        .await
        .unwrap();
        assert_eq!(
            r["params"],
            json!({"action":"get_hierarchy","prefabPath":"Assets/Prefabs/My Object.prefab"})
        );
    }
    #[tokio::test]
    async fn invalid_id_stays_local() {
        assert_eq!(
            read(&Echo, "mcpforunity://scene/gameobject/nope", None)
                .await
                .unwrap()["success"],
            false
        );
    }
    #[tokio::test]
    async fn docs_do_not_need_unity() {
        assert_eq!(
            read(&Echo, "mcpforunity://prefab-api", None).await.unwrap()["success"],
            true
        );
    }
    struct Remote;
    #[async_trait]
    impl UnityBridge for Remote {
        fn local_filesystem_allowed(&self) -> bool {
            false
        }
        async fn send(&self, command: &str, _: Value, _: Option<&str>) -> Result<Value> {
            assert_eq!(
                command, "get_editor_state",
                "Remote resources must not query project roots for host scanning"
            );
            Ok(json!({"success":true,"data":{}}))
        }
        async fn instances(&self) -> Result<Value> {
            Ok(json!({"instances":[]}))
        }
    }
    #[tokio::test]
    async fn remote_cannot_scan_filesystem() {
        let result = read(&Remote, "mcpforunity://editor/state", Some("Remote@abc"))
            .await
            .unwrap();
        assert_eq!(result["success"], true);
        assert!(result["data"]["assets"].is_null());
    }
    #[test]
    fn extreme_observation_timestamp_saturates_without_panicking() {
        let mut r = json!({"data":{"observed_at_unix_ms":i64::MIN}});
        enrich_editor_state(&mut r, None);
        assert_eq!(r["data"]["staleness"]["age_ms"], i64::MAX);
    }
    #[test]
    fn readiness_and_staleness() {
        let mut r = json!({"success":true,"data":{"observed_at_unix_ms":1,"compilation":{"is_compiling":true}}});
        enrich_editor_state(&mut r, Some("Game@abc"));
        assert_eq!(
            r["data"]["advice"]["blocking_reasons"],
            json!(["compiling", "stale_status"])
        );
        assert_eq!(r["data"]["unity"]["instance_id"], "Game@abc");
    }
}
