//! Unity tool adapters. Wire names and defaults mirror the Python MCP interface.
use crate::bridge::UnityBridge;
use anyhow::{anyhow, bail, Result};
use base64::{engine::general_purpose::STANDARD, Engine};
use serde_json::{json, Map, Value};
use std::time::{Duration, Instant};

const DEFINITIONS: &str = r###"{
  "manage_animation": {"map":{"action":"action","target":"target","search_method":"searchMethod","clip_path":"clipPath","controller_path":"controllerPath","properties":"properties"},"defaults":{},"actions":["animator_get_info","animator_get_parameter","animator_play","animator_crossfade","animator_set_parameter","animator_set_speed","animator_set_enabled","controller_create","controller_add_state","controller_add_transition","controller_add_parameter","controller_get_info","controller_assign","controller_add_layer","controller_remove_layer","controller_set_layer_weight","controller_create_blend_tree_1d","controller_create_blend_tree_2d","controller_add_blend_tree_child","clip_create","clip_get_info","clip_add_curve","clip_set_curve","clip_set_vector_curve","clip_create_preset","clip_assign","clip_add_event","clip_remove_event"]},
  "manage_build": {"map":{"action":"action","target":"target","output_path":"output_path","scenes":"scenes","development":"development","options":"options","subtarget":"subtarget","scripting_backend":"scripting_backend","profile":"profile","property":"property","value":"value","activate":"activate","targets":"targets","profiles":"profiles","output_dir":"output_dir","job_id":"job_id"},"defaults":{},"actions":["build","status","platform","settings","scenes","profiles","batch","cancel"]},
  "find_gameobjects": {"map":{"search_term":"searchTerm","search_method":"searchMethod","include_inactive":"includeInactive","page_size":"pageSize","cursor":"cursor"},"defaults":{"search_method":"by_name"}},
  "execute_code": {"map":{"action":"action","code":"code","safety_checks":"safety_checks","index":"index","limit":"limit","compiler":"compiler"},"defaults":{"safety_checks":true,"limit":10,"compiler":"auto"}},
  "manage_scriptable_object": {"map":{"action":"action","type_name":"typeName","folder_path":"folderPath","asset_name":"assetName","overwrite":"overwrite","target":"target","patches":"patches","dry_run":"dryRun"},"defaults":{}},
  "run_tests": {"map":{"mode":"mode","test_names":"testNames","group_names":"groupNames","category_names":"categoryNames","assembly_names":"assemblyNames","include_failed_tests":"includeFailedTests","include_details":"includeDetails","init_timeout":"initTimeout","clear_stuck":"clear_stuck"},"defaults":{"mode":"EditMode","include_failed_tests":false,"include_details":false,"clear_stuck":false}},
  "get_test_job": {"map":{"job_id":"job_id","include_failed_tests":"includeFailedTests","include_details":"includeDetails","wait_timeout":"wait_timeout"},"defaults":{"include_failed_tests":false,"include_details":false}},
  "manage_editor": {"map":{"action":"action","tool_name":"toolName","tag_name":"tagName","layer_name":"layerName"},"defaults":{}},
  "batch_execute": {"map":{"commands":"commands","parallel":"parallel","fail_fast":"failFast","max_parallelism":"maxParallelism"},"defaults":{}},
  "manage_scene": {"map":{"action":"action","name":"name","path":"path","build_index":"buildIndex","scene_view_target":"sceneViewTarget","parent":"parent","page_size":"pageSize","cursor":"cursor","max_nodes":"maxNodes","max_depth":"maxDepth","max_children_per_node":"maxChildrenPerNode","include_transform":"includeTransform","scene_name":"sceneName","scene_path":"scenePath","target":"target","remove_scene":"removeScene","additive":"additive","template":"template","auto_repair":"autoRepair"},"defaults":{}},
  "execute_custom_tool": {"map":{"tool_name":"tool_name","parameters":"parameters"},"defaults":{}},
  "blender_bridge": {"map":{"action":"action","object_name":"objectName","object_names":"objectNames","selection_only":"selectionOnly","format":"format","name":"name","target_size":"targetSize","position":"position","place_in_scene":"placeInScene","apply_modifiers":"applyModifiers","output_folder":"outputFolder","animation_type":"animationType","auto_animate":"autoAnimate","save_prefab":"savePrefab","ensure_bloom":"ensureBloom","game_object":"gameObject","code":"code","max_size":"maxSize","force":"force","timeout_seconds":"timeoutSeconds"},"defaults":{}},
  "manage_material": {"map":{"action":"action","material_path":"materialPath","property":"property","shader":"shader","properties":"properties","value":"value","color":"color","target":"target","search_method":"searchMethod","slot":"slot","mode":"mode"},"defaults":{}},
  "inspect_prefab": {"map":{"mode":"mode","prefab_path":"prefab_path","path":"path","component":"component","root":"root","depth":"depth","filter":"filter","all_fields":"all_fields","script":"script","asset":"asset","max_chars":"max_chars"},"defaults":{"mode":"tree"}},
  "import_model_file": {"map":{"source_path":"sourcePath","name":"name","output_folder":"outputFolder","target_size":"targetSize","animation_type":"animationType"},"defaults":{}},
  "manage_shader": {"map":{"action":"action","name":"name","path":"path","contents":"contents"},"defaults":{}},
  "manage_graphics": {"map":{"action":"action","target":"target","effect":"effect","parameters":"parameters","properties":"properties","settings":"settings","name":"name","is_global":"is_global","weight":"weight","priority":"priority","profile_path":"profile_path","effects":"effects","path":"path","level":"level","position":"position","grid_size":"grid_size","spacing":"spacing","size":"size","resolution":"resolution","mode":"mode","hdr":"hdr","box_projection":"box_projection","positions":"positions","index":"index","active":"active","order":"order","async_bake":"async","feature_type":"type","material":"material","color":"color","intensity":"intensity","ambient_mode":"ambient_mode","equator_color":"equator_color","ground_color":"ground_color","fog_enabled":"fog_enabled","fog_mode":"fog_mode","fog_color":"fog_color","fog_density":"fog_density","fog_start":"fog_start","fog_end":"fog_end","bounces":"bounces","reflection_mode":"reflection_mode"},"defaults":{},"actions":["ping","volume_create","volume_add_effect","volume_set_effect","volume_remove_effect","volume_get_info","volume_set_properties","volume_list_effects","volume_create_profile","bake_start","bake_cancel","bake_status","bake_clear","bake_reflection_probe","bake_get_settings","bake_set_settings","bake_create_light_probe_group","bake_create_reflection_probe","bake_set_probe_positions","stats_get","stats_list_counters","stats_set_scene_debug","stats_get_memory","pipeline_get_info","pipeline_set_quality","pipeline_get_settings","pipeline_set_settings","feature_list","feature_add","feature_remove","feature_configure","feature_toggle","feature_reorder","skybox_get","skybox_set_material","skybox_set_properties","skybox_set_ambient","skybox_set_fog","skybox_set_reflection","skybox_set_sun"]},
  "unity_reflect": {"map":{"action":"action","class_name":"class_name","member_name":"member_name","query":"query","scope":"scope"},"defaults":{},"actions":["get_type","get_member","search"]},
  "execute_menu_item": {"map":{"menu_path":"menuPath"},"defaults":{}},
  "manage_asset": {"map":{"action":"action","path":"path","asset_type":"assetType","properties":"properties","destination":"destination","generate_preview":"generatePreview","search_pattern":"searchPattern","filter_type":"filterType","filter_date_after":"filterDateAfter","page_size":"pageSize","page_number":"pageNumber"},"defaults":{"generate_preview":false}},
  "manage_camera": {"map":{"action":"action","target":"target","search_method":"searchMethod","properties":"properties","screenshot_file_name":"fileName","screenshot_super_size":"superSize","camera":"camera","include_image":"includeImage","max_resolution":"maxResolution","capture_source":"captureSource","batch":"batch","view_target":"viewTarget","view_position":"viewPosition","view_rotation":"viewRotation","orbit_angles":"orbitAngles","orbit_elevations":"orbitElevations","orbit_distance":"orbitDistance","orbit_fov":"orbitFov","output_folder":"outputFolder"},"defaults":{},"actions":["ping","ensure_brain","get_brain_status","create_camera","set_target","set_priority","set_lens","set_body","set_aim","set_noise","add_extension","remove_extension","set_blend","force_camera","release_override","list_cameras","screenshot","screenshot_multiview"]},
  "refresh_unity": {"map":{"mode":"mode","scope":"scope","compile":"compile","wait_for_ready":"wait_for_ready"},"defaults":{"mode":"if_dirty","scope":"all","compile":"none","wait_for_ready":true}},
  "manage_prefabs": {"map":{"action":"action","prefab_path":"prefabPath","target":"target","allow_overwrite":"allowOverwrite","search_inactive":"searchInactive","unlink_if_instance":"unlinkIfInstance","position":"position","rotation":"rotation","scale":"scale","name":"name","tag":"tag","layer":"layer","set_active":"setActive","parent":"parent","components_to_add":"componentsToAdd","components_to_remove":"componentsToRemove","create_child":"createChild","delete_child":"deleteChild","component_properties":"componentProperties"},"defaults":{}},
  "manage_gameobject": {"map":{"action":"action","target":"target","search_method":"searchMethod","name":"name","tag":"tag","parent":"parent","position":"position","rotation":"rotation","scale":"scale","components_to_add":"componentsToAdd","primitive_type":"primitiveType","save_as_prefab":"saveAsPrefab","prefab_path":"prefabPath","prefab_folder":"prefabFolder","set_active":"setActive","layer":"layer","is_static":"isStatic","components_to_remove":"componentsToRemove","component_properties":"componentProperties","new_name":"new_name","offset":"offset","reference_object":"reference_object","direction":"direction","distance":"distance","world_space":"world_space","look_at_target":"look_at_target","look_at_up":"look_at_up"},"defaults":{}},
  "manage_components": {"map":{"action":"action","target":"target","component_type":"componentType","search_method":"searchMethod","property":"property","value":"value","properties":"properties","component_index":"componentIndex"},"defaults":{}},
  "manage_texture": {"map":{"action":"action","path":"path","width":"width","height":"height","fill_color":"fillColor","pattern":"pattern","palette":"palette","pattern_size":"patternSize","pixels":"pixels","image_path":"imagePath","gradient_type":"gradientType","gradient_angle":"gradientAngle","noise_scale":"noiseScale","octaves":"octaves","set_pixels":"setPixels","as_sprite":"spriteSettings","import_settings":"importSettings"},"defaults":{}},
  "manage_ui": {"map":{"action":"action","path":"path","contents":"contents","target":"target","source_asset":"sourceAsset","panel_settings":"panelSettings","sort_order":"sortOrder","scale_mode":"scaleMode","reference_resolution":"referenceResolution","settings":"settings","max_depth":"maxDepth","width":"width","height":"height","include_image":"include_image","max_resolution":"max_resolution","screenshot_file_name":"file_name","output_folder":"output_folder","stylesheet":"stylesheet","filter_type":"filterType","page_size":"pageSize","page_number":"pageNumber","element_name":"elementName","text":"text","add_classes":"addClasses","remove_classes":"removeClasses","toggle_classes":"toggleClasses","style":"style","enabled":"enabled","visible":"visible","tooltip":"tooltip"},"defaults":{}},
  "read_console": {"map":{"action":"action","types":"types","count":"count","filter_text":"filterText","page_size":"pageSize","cursor":"cursor","format":"format","include_stacktrace":"includeStacktrace"},"defaults":{}},
  "manage_physics": {"map":{"action":"action","dimension":"dimension","settings":"settings","layer_a":"layer_a","layer_b":"layer_b","collide":"collide","name":"name","path":"path","dynamic_friction":"dynamic_friction","static_friction":"static_friction","bounciness":"bounciness","friction":"friction","friction_combine":"friction_combine","bounce_combine":"bounce_combine","material_path":"material_path","target":"target","collider_type":"collider_type","search_method":"search_method","joint_type":"joint_type","connected_body":"connected_body","motor":"motor","limits":"limits","spring":"spring","drive":"drive","properties":"properties","origin":"origin","direction":"direction","max_distance":"max_distance","layer_mask":"layer_mask","query_trigger_interaction":"query_trigger_interaction","shape":"shape","position":"position","size":"size","start":"start","end":"end","point1":"point1","point2":"point2","height":"height","capsule_direction":"capsule_direction","angle":"angle","force":"force","force_mode":"force_mode","force_type":"force_type","torque":"torque","explosion_position":"explosion_position","explosion_radius":"explosion_radius","explosion_force":"explosion_force","upwards_modifier":"upwards_modifier","steps":"steps","step_size":"step_size","page_size":"page_size","cursor":"cursor","component_index":"componentIndex"},"defaults":{}}
}"###;

fn failure(message: impl ToString) -> Value {
    json!({"success":false,"message":message.to_string()})
}
fn text(v: &Value) -> String {
    v.as_str()
        .map(str::to_owned)
        .unwrap_or_else(|| v.to_string())
}
fn parsed(v: &Value) -> Value {
    let Some(s) = v.as_str() else {
        return v.clone();
    };
    let t = s.trim();
    let numeric = t
        .replacen('.', "", 1)
        .replacen('-', "", 1)
        .chars()
        .all(|c| c.is_ascii_digit())
        && !t.is_empty();
    if (t.starts_with('{') && t.ends_with('}'))
        || (t.starts_with('[') && t.ends_with(']'))
        || ["true", "false", "null"].contains(&t)
        || numeric
    {
        serde_json::from_str(s).unwrap_or_else(|_| v.clone())
    } else {
        v.clone()
    }
}
fn number(v: &Value) -> Option<f64> {
    v.as_f64()
        .or_else(|| v.as_str()?.trim().parse::<f64>().ok())
        .filter(|n| n.is_finite())
}
fn integer(v: &Value) -> Option<i64> {
    number(v)
        .filter(|n| *n >= i64::MIN as f64 && *n < i64::MAX as f64)
        .map(|n| n as i64)
}
fn boolean(v: &Value) -> Option<bool> {
    match v {
        Value::Bool(b) => Some(*b),
        Value::Number(n) => Some(n.as_f64()? != 0.0),
        Value::String(s) => match s.trim().to_lowercase().as_str() {
            "true" | "1" | "yes" | "on" => Some(true),
            "false" | "0" | "no" | "off" => Some(false),
            _ => None,
        },
        _ => None,
    }
}
fn get<'a>(a: &'a Map<String, Value>, k: &str) -> &'a Value {
    a.get(k).unwrap_or(&Value::Null)
}
fn has(a: &Map<String, Value>, k: &str) -> bool {
    !get(a, k).is_null()
}
fn truth(v: &Value) -> bool {
    match v {
        Value::Null => false,
        Value::Bool(b) => *b,
        Value::String(s) => !s.is_empty(),
        Value::Array(a) => !a.is_empty(),
        Value::Object(a) => !a.is_empty(),
        _ => number(v) != Some(0.0),
    }
}
fn default(a: &mut Map<String, Value>, k: &str, v: Value) {
    if !has(a, k) {
        a.insert(k.into(), v);
    }
}
fn ints(a: &mut Map<String, Value>, keys: &str) {
    for k in keys.split_whitespace() {
        if has(a, k) {
            let v = integer(get(a, k)).map(Value::from).unwrap_or(Value::Null);
            a.insert(k.into(), v);
        }
    }
}
fn bools(a: &mut Map<String, Value>, keys: &str) {
    for k in keys.split_whitespace() {
        if has(a, k) {
            let v = boolean(get(a, k)).map(Value::from).unwrap_or(Value::Null);
            a.insert(k.into(), v);
        }
    }
}
fn objects(a: &mut Map<String, Value>, keys: &str) -> Result<()> {
    for k in keys.split_whitespace() {
        if has(a, k) {
            let v = parsed(get(a, k));
            if !v.is_object() {
                bail!("{k} must be a JSON object (dict)")
            }
            a.insert(k.into(), v);
        }
    }
    Ok(())
}
fn vector(v: &Value, k: &str) -> Result<Value> {
    let mut v = parsed(v);
    if v.is_null() {
        return Ok(v);
    }
    if let Some(s) = v.as_str() {
        v = json!(s
            .trim_matches(|c| c == '(' || c == ')' || c == '[' || c == ']')
            .split(',')
            .map(str::trim)
            .collect::<Vec<_>>());
    }
    let values = if let Some(o) = v.as_object() {
        vec![
            o.get("x").cloned().unwrap_or(Value::Null),
            o.get("y").cloned().unwrap_or(Value::Null),
            o.get("z").cloned().unwrap_or(Value::Null),
        ]
    } else {
        v.as_array()
            .cloned()
            .ok_or_else(|| anyhow!("{k} must be a vector [x, y, z]"))?
    };
    if values.len() != 3 {
        bail!("{k} must contain exactly 3 values")
    }
    Ok(Value::Array(
        values
            .iter()
            .map(|v| {
                number(v)
                    .map(Value::from)
                    .ok_or_else(|| anyhow!("{k} values must be finite numbers"))
            })
            .collect::<Result<_>>()?,
    ))
}
fn vectors(a: &mut Map<String, Value>, keys: &str) -> Result<()> {
    for k in keys.split_whitespace() {
        if has(a, k) {
            let v = vector(get(a, k), k)?;
            a.insert(k.into(), v);
        }
    }
    Ok(())
}
fn color(v: &Value, byte: bool) -> Result<Value> {
    if v.is_null() {
        return Ok(Value::Null);
    }
    let mut v = parsed(v);
    if let Some(s) = v.as_str() {
        if let Some(h) = s.strip_prefix('#') {
            let h = if h.len() == 3 || h.len() == 4 {
                h.chars().flat_map(|c| [c, c]).collect::<String>()
            } else {
                h.to_string()
            };
            if !h.is_ascii() || (h.len() != 6 && h.len() != 8) {
                bail!("Invalid hex color")
            };
            let mut a = (0..h.len())
                .step_by(2)
                .map(|i| u8::from_str_radix(&h[i..i + 2], 16).map(|n| n as f64))
                .collect::<std::result::Result<Vec<_>, _>>()?;
            if a.len() == 3 {
                a.push(255.0)
            }
            return Ok(json!(a
                .into_iter()
                .map(|n| if byte {
                    json!(n as i64)
                } else {
                    json!(n / 255.0)
                })
                .collect::<Vec<_>>()));
        } else {
            v = json!(s
                .trim_matches(|c| c == '(' || c == ')' || c == '[' || c == ']')
                .split(',')
                .map(str::trim)
                .collect::<Vec<_>>());
        }
    }
    let vals = if let Some(o) = v.as_object() {
        let mut vs = vec![];
        for k in ["r", "g", "b"] {
            vs.push(
                o.get(k)
                    .cloned()
                    .ok_or_else(|| anyhow!("color requires r, g, b"))?,
            );
        }
        if let Some(x) = o.get("a") {
            vs.push(x.clone())
        }
        vs
    } else {
        v.as_array()
            .cloned()
            .ok_or_else(|| anyhow!("color must be a list, dict, hex string, or JSON string"))?
    };
    if vals.len() != 3 && vals.len() != 4 {
        bail!("color must have 3 or 4 values")
    };
    let mut nums = vals
        .iter()
        .map(|v| number(v).ok_or_else(|| anyhow!("color values must be numbers")))
        .collect::<Result<Vec<_>>>()?;
    if nums.len() == 3 {
        nums.push(if !byte || nums.iter().all(|n| (0.0..=1.0).contains(n)) {
            1.0
        } else {
            255.0
        });
    }
    if byte {
        let normalized = nums.iter().all(|n| (0.0..=1.0).contains(n));
        Ok(json!(nums
            .iter()
            .map(|n| (if normalized {
                (n * 255.0).round_ties_even()
            } else {
                *n
            }) as i64)
            .collect::<Vec<_>>()))
    } else {
        let scale = if nums.iter().any(|n| *n > 1.0) {
            255.0
        } else {
            1.0
        };
        Ok(json!(nums
            .into_iter()
            .map(|n| n / scale)
            .collect::<Vec<_>>()))
    }
}
fn components(v: &Value) -> Result<Value> {
    let v = parsed(v);
    let xs = if v.is_array() {
        v.as_array().unwrap().clone()
    } else {
        vec![v]
    };
    let mut out = vec![];
    for v in xs {
        if let Some(s) = v.as_str() {
            if s.is_empty()
                || ["undefined", "null", "[object Object]"].contains(&s)
                || s.starts_with('[')
                || s.starts_with('{')
            {
                bail!("components_to_add received invalid value")
            };
            out.push(v);
        } else if v.is_object() {
            let ty = v
                .get("typeName")
                .or_else(|| v.get("type_name"))
                .and_then(Value::as_str)
                .filter(|s| !s.is_empty())
                .ok_or_else(|| {
                    anyhow!("components_to_add object entries must include a string 'typeName'")
                })?;
            let mut entry = json!({"typeName":ty});
            if let Some(p) = v.get("properties").filter(|p| !p.is_null()) {
                if !p.is_object() {
                    bail!("components_to_add properties must be an object")
                };
                entry["properties"] = p.clone();
            }
            out.push(entry);
        } else {
            bail!("components_to_add entries must be strings or objects")
        }
    }
    Ok(json!(out))
}
fn string_list(v: &Value) -> Result<Value> {
    let v = parsed(v);
    if let Some(xs) = v.as_array() {
        if xs.iter().all(Value::is_string) {
            return Ok(v);
        }
    } else if let Some(s) = v.as_str() {
        if !s.is_empty()
            && !s.starts_with('[')
            && !["undefined", "null", "[object Object]"].contains(&s)
        {
            return Ok(json!([s]));
        }
    }
    bail!("Expected a list of strings or a string")
}

fn normalize(name: &str, args: Value) -> Result<(Map<String, Value>, Value)> {
    static DEFINITIONS_VALUE: std::sync::OnceLock<Value> = std::sync::OnceLock::new();
    let definitions = DEFINITIONS_VALUE
        .get_or_init(|| serde_json::from_str(DEFINITIONS).expect("valid embedded tool mappings"));
    let def = definitions
        .get(name)
        .ok_or_else(|| anyhow!("Unknown tool: {name}"))?;
    let mut a = args
        .as_object()
        .cloned()
        .ok_or_else(|| anyhow!("Tool arguments must be an object"))?;
    for (k, v) in def["defaults"].as_object().unwrap() {
        if !a.contains_key(k) {
            a.insert(k.clone(), v.clone());
        }
    }
    let mut action = text(get(&a, "action"));
    if matches!(
        name,
        "manage_asset"
            | "manage_animation"
            | "manage_material"
            | "manage_texture"
            | "manage_build"
            | "manage_graphics"
            | "manage_physics"
            | "manage_camera"
            | "unity_reflect"
            | "manage_ui"
            | "read_console"
    ) {
        action = action.to_lowercase();
        if has(&a, "action") {
            a.insert("action".into(), json!(action));
        }
    }
    if let Some(allowed) = def["actions"].as_array() {
        if !allowed.contains(&json!(action)) {
            bail!(
                "Unknown action '{action}'. Valid actions: {}",
                allowed.iter().map(text).collect::<Vec<_>>().join(", ")
            );
        }
    }
    match name {
        "manage_asset" => {
            objects(&mut a, "properties")?;
            ints(&mut a, "page_size page_number");
            if action == "search" {
                let path = text(get(&a, "path"));
                if !truth(get(&a, "search_pattern")) && path.trim().starts_with("t:") {
                    a.insert("search_pattern".into(), json!(path.trim()));
                    a.insert("path".into(), json!("Assets"));
                }
                if !truth(get(&a, "filter_type")) && has(&a, "asset_type") {
                    a.insert("filter_type".into(), get(&a, "asset_type").clone());
                }
            }
        }
        "manage_material" => {
            objects(&mut a, "properties")?;
            ints(&mut a, "slot");
            if has(&a, "color") {
                a.insert("color".into(), color(get(&a, "color"), false)?);
            }
            if has(&a, "value") {
                a.insert("value".into(), parsed(get(&a, "value")));
                if [json!("undefined"), json!("[object Object]")].contains(get(&a, "value")) {
                    bail!("value received invalid input")
                }
            }
        }
        "manage_components" => {
            for k in ["action", "target", "component_type"] {
                if !truth(get(&a, k)) {
                    bail!("Missing required parameter '{k}'")
                }
            }
            objects(&mut a, "properties")?;
            if [json!("undefined"), json!("[object Object]")].contains(get(&a, "value")) {
                bail!("value received invalid input")
            };
            if action != "set_property" || !truth(get(&a, "property")) || !has(&a, "value") {
                a.remove("property");
                a.remove("value");
            }
            if action != "set_property" && action != "add" {
                a.remove("properties");
            }
        }
        "manage_gameobject" => {
            if !has(&a, "action") {
                bail!("Missing required parameter 'action'")
            }
            vectors(&mut a, "position rotation scale offset")?;
            bools(&mut a, "save_as_prefab set_active is_static world_space");
            default(&mut a, "world_space", json!(true));
            objects(&mut a, "component_properties")?;
            if has(&a, "components_to_add") {
                a.insert(
                    "components_to_add".into(),
                    components(get(&a, "components_to_add"))?,
                );
            }
            if has(&a, "components_to_remove") {
                a.insert(
                    "components_to_remove".into(),
                    string_list(get(&a, "components_to_remove"))?,
                );
            }
            if action == "create" && get(&a, "save_as_prefab") == &json!(true) {
                if !has(&a, "prefab_path") {
                    if !truth(get(&a, "name")) {
                        bail!("Cannot create default prefab path: 'name' parameter is missing.")
                    }
                    let folder = a
                        .get("prefab_folder")
                        .and_then(Value::as_str)
                        .unwrap_or("None");
                    a.insert(
                        "prefab_path".into(),
                        json!(format!("{}/{}.prefab", folder, text(get(&a, "name")))
                            .replace('\\', "/")),
                    );
                } else if !text(get(&a, "prefab_path"))
                    .to_lowercase()
                    .ends_with(".prefab")
                {
                    bail!("Invalid prefab_path: must end with .prefab")
                }
            }
            a.remove("prefab_folder");
        }
        "manage_prefabs" => {
            if action == "create_from_gameobject" && !has(&a, "target") && has(&a, "name") {
                a.insert("target".into(), get(&a, "name").clone());
            }
            if [
                "get_info",
                "get_hierarchy",
                "create_from_gameobject",
                "modify_contents",
                "open_prefab_stage",
            ]
            .contains(&action.as_str())
                && (!truth(get(&a, "prefab_path"))
                    || get(&a, "prefab_path")
                        .as_str()
                        .is_some_and(|s| s.trim().is_empty()))
            {
                bail!("Action '{action}' requires parameter 'prefab_path'.")
            }
            if action == "create_from_gameobject"
                && (!truth(get(&a, "target"))
                    || get(&a, "target")
                        .as_str()
                        .is_some_and(|s| s.trim().is_empty()))
            {
                bail!("Action '{action}' requires parameter 'target'.")
            };
            vectors(&mut a, "position rotation scale")?;
            bools(
                &mut a,
                "allow_overwrite search_inactive unlink_if_instance set_active",
            );
            if has(&a, "create_child") {
                let original = get(&a, "create_child");
                let is_array = original.is_array();
                let mut children = if is_array {
                    original.as_array().unwrap().clone()
                } else {
                    vec![original.clone()]
                };
                for c in &mut children {
                    let m = c.as_object_mut().ok_or_else(|| {
                        anyhow!("create_child must be a dict with child properties")
                    })?;
                    vectors(m, "position rotation scale")?;
                }
                a.insert(
                    "create_child".into(),
                    if is_array {
                        json!(children)
                    } else {
                        children.remove(0)
                    },
                );
            }
        }
        "manage_scene" => {
            ints(
                &mut a,
                "build_index page_size cursor max_nodes max_depth max_children_per_node",
            );
            bools(
                &mut a,
                "include_transform remove_scene additive auto_repair",
            );
        }
        "find_gameobjects" => {
            if !truth(get(&a, "search_term")) {
                bail!("Missing required parameter 'search_term'. Specify what to search for.")
            };
            ints(&mut a, "page_size cursor");
            bools(&mut a, "include_inactive");
            default(&mut a, "page_size", json!(50));
            default(&mut a, "cursor", json!(0));
            default(&mut a, "include_inactive", json!(false));
        }
        "manage_build" => {
            bools(&mut a, "development activate");
            for k in ["scenes", "options", "targets", "profiles"] {
                if has(&a, k) {
                    a.insert(k.into(), parsed(get(&a, k)));
                }
            }
            if let Some(s) = get(&a, "scenes").as_str() {
                a.insert(
                    "scenes".into(),
                    json!(s
                        .split(',')
                        .map(str::trim)
                        .filter(|s| !s.is_empty())
                        .collect::<Vec<_>>()),
                );
            }
        }
        "manage_scriptable_object" => {
            objects(&mut a, "target")?;
            if has(&a, "patches") {
                let v = parsed(get(&a, "patches"));
                if !v.is_array() {
                    bail!("manage_scriptable_object: 'patches' must be a list (or JSON string of a list).")
                };
                a.insert("patches".into(), v);
            }
            bools(&mut a, "overwrite dry_run");
        }
        "execute_code" => match action.as_str() {
            "execute" => {
                if !has(&a, "code") {
                    bail!("Parameter 'code' is required for 'execute' action.")
                };
                a.remove("index");
                a.remove("limit");
            }
            "replay" => {
                if !has(&a, "index") {
                    bail!("Parameter 'index' is required for 'replay' action.")
                };
                a.retain(|k, _| k == "action" || k == "index");
            }
            "get_history" => {
                let limit = integer(get(&a, "limit")).unwrap_or(10).clamp(1, 50);
                a.retain(|k, _| k == "action");
                a.insert("limit".into(), json!(limit));
            }
            _ => a.retain(|k, _| k == "action"),
        },
        "unity_reflect" => {
            if action == "get_type" && !truth(get(&a, "class_name")) {
                bail!("get_type requires class_name.")
            }
            if action == "get_member"
                && (!truth(get(&a, "class_name")) || !truth(get(&a, "member_name")))
            {
                bail!("get_member requires class_name and member_name.")
            }
            if action == "search" {
                if !truth(get(&a, "query")) {
                    bail!("search requires query.")
                }
                if has(&a, "scope")
                    && !["unity", "packages", "project", "all"]
                        .contains(&text(get(&a, "scope")).as_str())
                {
                    bail!("Invalid scope")
                }
            } else {
                a.remove("scope");
            }
        }
        "inspect_prefab" => {
            let mode = get(&a, "mode").as_str().unwrap_or("tree").to_lowercase();
            if !["tree", "node", "refs", "overrides", "usages", "problems"].contains(&mode.as_str())
            {
                bail!("Unknown mode '{mode}'")
            };
            if mode == "usages" {
                if !truth(get(&a, "script")) && !truth(get(&a, "asset")) {
                    bail!("usages needs script= (class name) or asset= (Assets/... path).")
                }
            } else if !truth(get(&a, "prefab_path")) {
                bail!("{mode} needs prefab_path (Assets/...prefab or scene:Path/To/Object).")
            };
            a.insert("mode".into(), json!(mode));
            ints(&mut a, "depth max_chars");
            bools(&mut a, "all_fields");
            if get(&a, "all_fields") != &json!(true) {
                a.remove("all_fields");
            }
        }
        "read_console" => {
            default(&mut a, "action", json!("get"));
            default(&mut a, "format", json!("plain"));
            default(&mut a, "types", json!(["error"]));
            let ts = parsed(get(&a, "types"));
            let ts = ts
                .as_array()
                .ok_or_else(|| anyhow!("types must be a list"))?;
            let mut normalized = vec![];
            for t in ts {
                let t = t
                    .as_str()
                    .ok_or_else(|| anyhow!("types entries must be strings"))?
                    .trim()
                    .to_lowercase();
                if !["error", "warning", "log", "all"].contains(&t.as_str()) {
                    bail!("invalid types entry '{t}'")
                };
                normalized.push(t);
            }
            a.insert("types".into(), json!(normalized));
            ints(&mut a, "count page_size cursor");
            bools(&mut a, "include_stacktrace");
            default(&mut a, "include_stacktrace", json!(false));
            if text(get(&a, "action")) == "get" {
                default(&mut a, "count", json!(10));
            }
            a.insert(
                "format".into(),
                json!(text(get(&a, "format")).to_lowercase()),
            );
        }
        "manage_camera" => normalize_camera(&mut a, &action)?,
        "manage_texture" => normalize_texture(&mut a, &action)?,
        "manage_shader" | "manage_ui" => {
            if name == "manage_ui" {
                if ["create", "read", "update", "delete"].contains(&action.as_str())
                    && truth(get(&a, "path"))
                {
                    let p = text(get(&a, "path")).replace('\\', "/");
                    let mut parts = Vec::new();
                    for part in p.split('/') {
                        match part {
                            "." | "" => {}
                            ".." => {
                                if parts.pop().is_none() {
                                    bail!("path must not contain traversal sequences.")
                                }
                            }
                            p => parts.push(p),
                        }
                    }
                    if p.starts_with('/')
                        || parts
                            .first()
                            .is_none_or(|p| !p.eq_ignore_ascii_case("assets"))
                    {
                        bail!("path must be under 'Assets/'")
                    };
                    if !p.to_lowercase().ends_with(".uxml") && !p.to_lowercase().ends_with(".uss") {
                        bail!("Invalid file extension. Must be .uxml or .uss.")
                    }
                }
                if has(&a, "visible") {
                    a.insert(
                        "visible".into(),
                        json!(text(get(&a, "visible")).to_lowercase()),
                    );
                }
                if has(&a, "output_folder") {
                    a.insert(
                        "output_folder".into(),
                        json!(text(get(&a, "output_folder")).trim()),
                    );
                }
            }
        }
        "run_tests" | "get_test_job" => {
            if name == "run_tests" {
                if boolean(get(&a, "clear_stuck")) == Some(true) {
                    return Ok((a, json!({"clear_stuck":true})));
                }
                if has(&a, "init_timeout") && integer(get(&a, "init_timeout")).unwrap_or(0) <= 0 {
                    bail!("init_timeout must be a positive integer (milliseconds) or None")
                };
                for k in [
                    "test_names",
                    "group_names",
                    "category_names",
                    "assembly_names",
                ] {
                    if has(&a, k) {
                        let v = get(&a, k);
                        let vs = if let Some(s) = v.as_str() {
                            if s.trim().is_empty() {
                                vec![]
                            } else {
                                vec![s.to_string()]
                            }
                        } else {
                            v.as_array()
                                .map(|xs| {
                                    xs.iter()
                                        .filter(|v| truth(v))
                                        .map(|v| text(v).trim().to_string())
                                        .filter(|s| !s.is_empty())
                                        .collect()
                                })
                                .unwrap_or_default()
                        };
                        a.insert(
                            k.into(),
                            if vs.is_empty() {
                                Value::Null
                            } else {
                                json!(vs)
                            },
                        );
                    }
                }
            }
            for k in ["include_failed_tests", "include_details"] {
                if boolean(get(&a, k)) != Some(true) {
                    a.remove(k);
                }
            }
            a.remove("clear_stuck");
        }
        _ => {}
    }
    if name == "manage_scene" {
        for k in ["name", "path"] {
            if !truth(get(&a, k)) {
                a.remove(k);
            }
        }
    }
    if name == "manage_prefabs" {
        for k in ["prefab_path", "target"] {
            if !truth(get(&a, k)) {
                a.remove(k);
            }
        }
    }
    if name == "manage_components" {
        for k in ["search_method", "properties"] {
            if !truth(get(&a, k)) {
                a.remove(k);
            }
        }
    }
    if name == "manage_camera" {
        for k in ["screenshot_file_name", "camera", "batch"] {
            if !truth(get(&a, k)) {
                a.remove(k);
            }
        }
    }
    let mut p = Map::new();
    for (k, target) in def["map"].as_object().unwrap() {
        if let Some(v) = a.get(k).filter(|v| !v.is_null()) {
            p.insert(target.as_str().unwrap().into(), v.clone());
        }
    }
    if name == "read_console" && !p.contains_key("count") {
        p.insert("count".into(), Value::Null);
    }
    if name == "get_test_job" {
        p.remove("wait_timeout");
    }
    if name == "manage_shader" || name == "manage_ui" {
        if ["create", "update"].contains(&action.as_str()) && has(&a, "contents") {
            let content = text(get(&a, "contents"));
            if name == "manage_shader" || !content.is_empty() {
                p.insert("encodedContents".into(), json!(STANDARD.encode(content)));
                p.insert("contentsEncoded".into(), json!(true));
            }
        }
        if name == "manage_ui" || ["create", "update"].contains(&action.as_str()) {
            p.remove("contents");
        }
    }
    Ok((a, Value::Object(p)))
}

fn normalize_camera(a: &mut Map<String, Value>, action: &str) -> Result<()> {
    if !["screenshot", "screenshot_multiview"].contains(&action) {
        a.retain(|k, _| ["action", "target", "search_method", "properties"].contains(&k.as_str()));
        return Ok(());
    }
    ints(a, "screenshot_super_size max_resolution orbit_angles");
    bools(a, "include_image");
    if integer(get(a, "max_resolution")).is_some_and(|n| n <= 0) {
        bail!("max_resolution must be a positive integer.")
    }
    if has(a, "output_folder") {
        let s = text(get(a, "output_folder")).trim().to_string();
        if s.is_empty() {
            a.remove("output_folder");
        } else {
            a.insert("output_folder".into(), json!(s));
        }
    }
    if has(a, "capture_source") {
        let s = text(get(a, "capture_source")).trim().to_lowercase();
        if !["game_view", "scene_view"].contains(&s.as_str()) {
            bail!("capture_source must be either 'game_view' or 'scene_view'.")
        };
        a.insert("capture_source".into(), json!(s));
    }
    if has(a, "orbit_elevations") {
        let v = parsed(get(a, "orbit_elevations"));
        if !v
            .as_array()
            .is_some_and(|xs| xs.iter().all(Value::is_number))
        {
            bail!("orbit_elevations must be a list of numbers.")
        };
        a.insert("orbit_elevations".into(), v);
    }
    for k in ["orbit_distance", "orbit_fov"] {
        if has(a, k) {
            let n = number(get(a, k)).ok_or_else(|| anyhow!("{k} must be a number."))?;
            a.insert(k.into(), json!(n));
        }
    }
    vectors(a, "view_position view_rotation")?;
    if get(a, "capture_source") == &json!("scene_view") {
        if integer(get(a, "screenshot_super_size")).is_some_and(|n| n > 1) {
            bail!("capture_source='scene_view' does not support super_size above 1.")
        }
        for (k, msg) in [
            ("batch", "batch modes"),
            ("view_position", "view_position/view_rotation"),
            ("view_rotation", "view_position/view_rotation"),
            ("camera", "camera selection"),
        ] {
            if truth(get(a, k)) {
                bail!("capture_source='scene_view' does not support {msg}.")
            }
        }
    }
    Ok(())
}
fn pixels(v: &Value, width: i64, height: i64) -> Result<Value> {
    let v = parsed(v);
    if let Some(s) = v.as_str() {
        return Ok(json!(if s.starts_with("base64:") {
            s.to_string()
        } else {
            format!("base64:{s}")
        }));
    }
    let xs = v
        .as_array()
        .ok_or_else(|| anyhow!("pixels must be a list or base64 string"))?;
    let count = width
        .checked_mul(height)
        .ok_or_else(|| anyhow!("Texture dimensions overflow"))?;
    if xs.len() as i64 != count {
        bail!(
            "pixels array must have {count} entries for {width}x{height} texture, got {}",
            xs.len()
        )
    }
    Ok(Value::Array(
        xs.iter().map(|x| color(x, true)).collect::<Result<_>>()?,
    ))
}
fn normalize_texture(a: &mut Map<String, Value>, action: &str) -> Result<()> {
    if has(a, "fill_color") {
        a.insert("fill_color".into(), color(get(a, "fill_color"), true)?);
    }
    if has(a, "image_path") {
        if !["create", "create_sprite"].contains(&action) {
            bail!("image_path is only supported for create/create_sprite.")
        }
        if ["fill_color", "pattern", "pixels"]
            .iter()
            .any(|k| has(a, k))
        {
            bail!("image_path cannot be combined with fill_color, pattern, or pixels.")
        };
        a.remove("width");
        a.remove("height");
    } else {
        for k in ["width", "height"] {
            default(a, k, json!(64));
        }
        for k in ["width", "height", "pattern_size", "octaves"] {
            if has(a, k) {
                let n = integer(get(a, k))
                    .filter(|n| *n > 0)
                    .ok_or_else(|| anyhow!("{k} must be a positive integer"))?;
                a.insert(k.into(), json!(n));
            }
        }
    }
    if action == "create"
        && !["fill_color", "pattern", "pixels", "image_path"]
            .iter()
            .any(|k| has(a, k))
    {
        a.insert("fill_color".into(), json!([255, 255, 255, 255]));
    }
    if has(a, "palette") {
        let v = parsed(get(a, "palette"));
        let xs = v
            .as_array()
            .ok_or_else(|| anyhow!("palette must be a list of colors"))?;
        a.insert(
            "palette".into(),
            Value::Array(xs.iter().map(|x| color(x, true)).collect::<Result<_>>()?),
        );
    }
    if has(a, "pixels") {
        a.insert(
            "pixels".into(),
            pixels(
                get(a, "pixels"),
                integer(get(a, "width")).unwrap_or(64),
                integer(get(a, "height")).unwrap_or(64),
            )?,
        );
    }
    if has(a, "set_pixels") {
        objects(a, "set_pixels")?;
        let m = a.get_mut("set_pixels").unwrap().as_object_mut().unwrap();
        if has(m, "color") {
            m.insert("color".into(), color(get(m, "color"), true)?);
        }
        if has(m, "pixels") {
            let w = integer(get(m, "width"))
                .filter(|n| *n > 0)
                .ok_or_else(|| anyhow!("set_pixels width and height must be positive integers"))?;
            let h = integer(get(m, "height"))
                .filter(|n| *n > 0)
                .ok_or_else(|| anyhow!("set_pixels width and height must be positive integers"))?;
            m.insert("pixels".into(), pixels(get(m, "pixels"), w, h)?);
        }
    }
    if has(a, "as_sprite") {
        let v = parsed(get(a, "as_sprite"));
        let mut s = Map::new();
        if v == json!(true) {
            s.insert("pivot".into(), json!([0.5, 0.5]));
            s.insert("pixelsPerUnit".into(), json!(100));
        } else if let Some(m) = v.as_object() {
            if let Some(p) = m.get("pivot") {
                s.insert("pivot".into(), pair(p, "sprite pivot")?);
            }
            if let Some(n) = m.get("pixels_per_unit").or_else(|| m.get("pixelsPerUnit")) {
                s.insert(
                    "pixelsPerUnit".into(),
                    json!(number(n).ok_or_else(|| anyhow!("pixels_per_unit must be a number"))?),
                );
            }
        } else {
            bail!("as_sprite must be a dict or boolean")
        };
        a.insert("as_sprite".into(), Value::Object(s));
    }
    if has(a, "import_settings") {
        let v = parsed(get(a, "import_settings"));
        a.insert("import_settings".into(), import_settings(&v)?);
    }
    Ok(())
}
fn pair(v: &Value, name: &str) -> Result<Value> {
    let xs = v
        .as_array()
        .filter(|v| v.len() == 2)
        .ok_or_else(|| anyhow!("{name} must be [x, y]"))?;
    Ok(json!([
        number(&xs[0]).ok_or_else(|| anyhow!("{name} must contain numbers"))?,
        number(&xs[1]).ok_or_else(|| anyhow!("{name} must contain numbers"))?
    ]))
}
fn import_settings(v: &Value) -> Result<Value> {
    let a = v
        .as_object()
        .ok_or_else(|| anyhow!("import_settings must be a dict"))?;
    let mut out = Map::new();
    for(snake,camel,variants)in [
 ("texture_type","textureType","default:Default normal_map:NormalMap editor_gui:GUI sprite:Sprite cursor:Cursor cookie:Cookie lightmap:Lightmap directional_lightmap:DirectionalLightmap shadow_mask:Shadowmask single_channel:SingleChannel"),
 ("texture_shape","textureShape","2d:Texture2D cube:TextureCube"),("alpha_source","alphaSource","none:None from_input:FromInput from_gray_scale:FromGrayScale"),
 ("wrap_mode","wrapMode","repeat:Repeat clamp:Clamp mirror:Mirror mirror_once:MirrorOnce"),("wrap_mode_u","wrapModeU","repeat:Repeat clamp:Clamp mirror:Mirror mirror_once:MirrorOnce"),("wrap_mode_v","wrapModeV","repeat:Repeat clamp:Clamp mirror:Mirror mirror_once:MirrorOnce"),
 ("filter_mode","filterMode","point:Point bilinear:Bilinear trilinear:Trilinear"),("mipmap_filter","mipmapFilter","box:BoxFilter kaiser:KaiserFilter"),
 ("compression","textureCompression","none:Uncompressed low_quality:CompressedLQ normal_quality:Compressed high_quality:CompressedHQ"),("sprite_mode","spriteImportMode","single:Single multiple:Multiple polygon:Polygon"),("sprite_mesh_type","spriteMeshType","full_rect:FullRect tight:Tight")]{if a.contains_key(snake){let s=text(get(a,snake)).to_lowercase();let val=variants.split_whitespace().filter_map(|p|p.split_once(':')).find(|(k,_)|*k==s).map(|(_,v)|v).ok_or_else(||anyhow!("Invalid {snake} '{s}'"))?;out.insert(camel.into(),json!(val));}}
    for (snake, camel) in [
        ("srgb", "sRGBTexture"),
        ("alpha_is_transparency", "alphaIsTransparency"),
        ("readable", "isReadable"),
        ("generate_mipmaps", "mipmapEnabled"),
        ("compression_crunched", "crunchedCompression"),
    ] {
        if has(a, snake) {
            let v = get(a, snake);
            if v.is_number() && number(v) != Some(0.0) && number(v) != Some(1.0) {
                bail!("{snake} must be a boolean")
            };
            out.insert(
                camel.into(),
                json!(boolean(v).ok_or_else(|| anyhow!("{snake} must be a boolean"))?),
            );
        }
    }
    for (snake, camel, min, max) in [
        ("aniso_level", "anisoLevel", 0, 16),
        ("compression_quality", "compressionQuality", 0, 100),
        ("sprite_extrude", "spriteExtrude", 0, 32),
        ("max_texture_size", "maxTextureSize", 32, 16384),
    ] {
        if has(a, snake) {
            let n = integer(get(a, snake)).ok_or_else(|| anyhow!("{snake} must be an integer"))?;
            if n < min || n > max || (snake == "max_texture_size" && !(n as u64).is_power_of_two())
            {
                bail!("{snake} must be in range {min}-{max}")
            };
            out.insert(camel.into(), json!(n));
        }
    }
    if has(a, "sprite_pixels_per_unit") {
        out.insert(
            "spritePixelsPerUnit".into(),
            json!(number(get(a, "sprite_pixels_per_unit"))
                .ok_or_else(|| anyhow!("sprite_pixels_per_unit must be a number"))?),
        );
    }
    if has(a, "sprite_pivot") {
        out.insert(
            "spritePivot".into(),
            pair(get(a, "sprite_pivot"), "sprite_pivot")?,
        );
    }
    Ok(Value::Object(out))
}

fn is_busy(state: &Value) -> bool {
    let d = state.get("data").unwrap_or(state);
    let phase = d
        .get("activity")
        .and_then(|x| x.get("phase"))
        .or_else(|| d.get("activity_phase"))
        .and_then(Value::as_str)
        .unwrap_or("");
    [
        "compiling",
        "domain_reload",
        "running_tests",
        "asset_import",
    ]
    .contains(&phase)
        || d.pointer("/compilation/is_compiling") == Some(&json!(true))
        || d.pointer("/compilation/is_domain_reload_pending") == Some(&json!(true))
        || d.pointer("/tests/is_running") == Some(&json!(true))
        || d.pointer("/assets/is_updating") == Some(&json!(true))
        || d.pointer("/assets/refresh/is_refresh_in_progress") == Some(&json!(true))
}
pub async fn wait_for_editor_ready(
    bridge: &dyn UnityBridge,
    instance: Option<&str>,
    timeout: Duration,
) -> bool {
    let end = Instant::now() + timeout;
    loop {
        if let Ok(Ok(s)) = tokio::time::timeout_at(
            tokio::time::Instant::from_std(end),
            bridge.send("get_editor_state", json!({}), instance),
        )
        .await
        {
            if s.get("success") != Some(&json!(false)) && !is_busy(&s) {
                if s.pointer("/data/advice/ready_for_tools") == Some(&json!(true)) {
                    return true;
                }
                if let Some(reasons) = s
                    .pointer("/data/advice/blocking_reasons")
                    .and_then(Value::as_array)
                {
                    if !reasons.iter().any(|v| {
                        [
                            "compiling",
                            "domain_reload",
                            "running_tests",
                            "asset_import",
                            "asset_refresh",
                        ]
                        .contains(&v.as_str().unwrap_or(""))
                    }) {
                        return true;
                    }
                } else if s.get("data").is_some() && !is_busy(&s) {
                    return true;
                }
            }
        }
        if Instant::now() >= end {
            return false;
        }
        tokio::time::sleep(
            Duration::from_millis(250).min(end.saturating_duration_since(Instant::now())),
        )
        .await;
    }
}
fn reload_rejected(v: &Value) -> bool {
    v.get("success") == Some(&json!(false))
        && v.pointer("/data/reason") == Some(&json!("reloading"))
        && v.get("hint") == Some(&json!("retry"))
}
pub async fn send_mutation(
    bridge: &dyn UnityBridge,
    command: &str,
    params: Value,
    instance: Option<&str>,
) -> Result<Value> {
    let mut r = bridge.send(command, params.clone(), instance).await?;
    if reload_rejected(&r) {
        if !wait_for_editor_ready(bridge, instance, Duration::from_secs(30)).await {
            return Ok(r);
        }
        r = bridge.send(command, params, instance).await?;
    }
    wait_for_editor_ready(bridge, instance, Duration::from_secs(30)).await;
    Ok(r)
}
async fn preflight(
    bridge: &dyn UnityBridge,
    instance: Option<&str>,
    tests: bool,
    dirty: bool,
) -> Option<Value> {
    let mut s = crate::resources::read(bridge, "mcpforunity://editor/state", instance)
        .await
        .ok()?;
    if s.get("success") != Some(&json!(true)) {
        return None;
    }
    if dirty && s.pointer("/data/assets/external_changes_dirty") == Some(&json!(true)) {
        let _ = refresh(
            bridge,
            json!({"mode":"if_dirty","scope":"all","compile":"request","wait_for_ready":true}),
            instance,
        )
        .await;
    }
    if tests && s.pointer("/data/tests/is_running") == Some(&json!(true)) {
        return Some(
            json!({"success":false,"error":"busy","message":"tests_running","hint":"retry","data":{"reason":"tests_running","retry_after_ms":5000}}),
        );
    }
    let end = Instant::now() + Duration::from_secs(30);
    while s.pointer("/data/compilation/is_compiling") == Some(&json!(true))
        || s.pointer("/data/compilation/is_domain_reload_pending") == Some(&json!(true))
    {
        if Instant::now() >= end {
            return Some(
                json!({"success":false,"error":"busy","message":"compiling","hint":"retry","data":{"reason":"compiling","retry_after_ms":500}}),
            );
        }
        tokio::time::sleep(Duration::from_millis(250)).await;
        s = crate::resources::read(bridge, "mcpforunity://editor/state", instance)
            .await
            .ok()?;
    }
    None
}
async fn refresh(bridge: &dyn UnityBridge, p: Value, instance: Option<&str>) -> Result<Value> {
    let wait = p["wait_for_ready"] == json!(true);
    let compile = p["compile"] == json!("request");
    let response = bridge
        .send("refresh_unity", p, instance)
        .await
        .unwrap_or_else(failure);
    let mut recovered = false;
    if response.get("success") == Some(&json!(false)) {
        let err = response
            .get("error")
            .or_else(|| response.get("message"))
            .map(text)
            .unwrap_or_default()
            .to_lowercase();
        let lost = ["connection closed", "disconnected", "aborted", "timeout"]
            .iter()
            .any(|s| err.contains(s))
            || response.pointer("/data/reason") == Some(&json!("reloading"));
        if (lost && compile)
            || ((response["hint"] == json!("retry") || err.contains("could not connect")) && wait)
        {
            recovered = true
        } else {
            return Ok(response);
        }
    }
    if wait && !wait_for_editor_ready(bridge, instance, Duration::from_secs(60)).await {
        return Ok(
            json!({"success":false,"message":"Refresh triggered but timed out after 60s waiting for editor readiness.","data":{"timeout":true,"wait_seconds":60.0}}),
        );
    }
    if let Some(id) = instance {
        crate::scanner::clear_dirty(id);
    } else if let Ok(v) = bridge.instances().await {
        if let Some(xs) = v
            .get("instances")
            .and_then(Value::as_array)
            .filter(|xs| xs.len() == 1)
        {
            if let Some(id) = xs[0].get("id").and_then(Value::as_str) {
                crate::scanner::clear_dirty(id);
            }
        }
    }
    if recovered {
        Ok(
            json!({"success":true,"message":"Refresh recovered after Unity disconnect/retry; editor is ready.","data":{"recovered_from_disconnect":true}}),
        )
    } else {
        Ok(response)
    }
}
fn screenshot(mut response: Value) -> Value {
    if response["success"] != json!(true) {
        return response;
    }
    let Some(data) = response.get_mut("data").and_then(Value::as_object_mut) else {
        return response;
    };
    let mut images = vec![];
    let is_batch = data
        .get("screenshots")
        .and_then(Value::as_array)
        .is_some_and(|v| !v.is_empty());
    if let Some(shots) = data
        .get_mut("screenshots")
        .and_then(Value::as_array_mut)
        .filter(|s| !s.is_empty())
    {
        for shot in shots.iter_mut() {
            if let Some(m) = shot.as_object_mut() {
                if let Some(img) = m.remove("imageBase64").filter(truth) {
                    images.push(json!({"type":"text","text":format!("[Angle: {}]",m.get("angle").map(text).unwrap_or_else(||"?".into()))}));
                    images.push(json!({"type":"image","data":img,"mimeType":"image/png"}));
                }
            }
        }
        data.retain(|k, _| ["sceneCenter", "sceneRadius", "screenshots"].contains(&k.as_str()));
        data.entry("sceneCenter").or_insert(Value::Null);
        data.entry("sceneRadius").or_insert(Value::Null);
    } else if let Some(img) = data.remove("imageBase64").filter(truth) {
        images.push(json!({"type":"image","data":img,"mimeType":"image/png"}));
    }
    if images.is_empty() && !is_batch {
        return response;
    }
    response = json!({"success":true,"message":response.get("message").cloned().unwrap_or(json!("")),"data":response["data"]});
    let mut content = vec![json!({"type":"text","text":response.to_string()})];
    content.extend(images);
    json!({"content":content})
}
fn decode_contents(response: &mut Value) -> Result<()> {
    if response["success"] == json!(true) {
        if let Some(d) = response.get_mut("data").and_then(Value::as_object_mut) {
            if d.get("contentsEncoded") == Some(&json!(true)) {
                if let Some(encoded) = d.get("encodedContents").and_then(Value::as_str) {
                    let decoded = String::from_utf8(STANDARD.decode(encoded)?)?;
                    d.insert("contents".into(), json!(decoded));
                    d.remove("encodedContents");
                    d.remove("contentsEncoded");
                }
            }
        }
    }
    Ok(())
}

pub async fn call(
    bridge: &dyn UnityBridge,
    name: &str,
    args: Value,
    instance: Option<&str>,
) -> Result<Value> {
    if [
        "manage_script",
        "create_script",
        "delete_script",
        "validate_script",
        "get_sha",
        "apply_text_edits",
        "script_apply_edits",
        "find_in_file",
    ]
    .contains(&name)
    {
        return crate::scripts::call(bridge, name, args, instance).await;
    }
    let (a, p) = match normalize(name, args) {
        Ok(x) => x,
        Err(e) => {
            if name == "inspect_prefab" || e.to_string().starts_with("Unknown tool:") {
                return Err(e);
            }
            return Ok(failure(e));
        }
    };
    if name == "execute_custom_tool" {
        if instance.is_none() {
            return Ok(failure("No active Unity instance. Call set_active_instance with Name@hash from mcpforunity://instances."));
        }
        let params = a
            .get("parameters")
            .filter(|v| !v.is_null())
            .cloned()
            .unwrap_or(json!({}));
        if !params.is_object() {
            return Ok(failure("parameters must be an object/dictionary"));
        }
        let target = get(&a, "tool_name")
            .as_str()
            .filter(|s| !s.is_empty())
            .ok_or_else(|| anyhow!("tool_name is required"))?;
        return call_custom(bridge, target, params, instance).await;
    }
    if name == "manage_editor" {
        match get(&a, "action").as_str() {
            Some("telemetry_status") => {
                return Ok(json!({"success":true,"telemetry_enabled":false}))
            }
            Some("telemetry_ping") => {
                return Ok(json!({"success":false,"message":"Telemetry disabled in Rust server"}))
            }
            _ => {}
        }
    }
    if name == "refresh_unity" {
        return refresh(bridge, p, instance).await;
    }
    if name == "batch_execute" {
        let xs = get(&a, "commands")
            .as_array()
            .filter(|a| !a.is_empty())
            .ok_or_else(|| {
                anyhow!("'commands' must be a non-empty list of command specifications")
            })?;
        let state = bridge
            .send("get_editor_state", json!({}), instance)
            .await
            .unwrap_or(Value::Null);
        let max = state
            .pointer("/data/settings/batch_execute_max_commands")
            .and_then(Value::as_u64)
            .filter(|n| (1..=100).contains(n))
            .unwrap_or(25);
        if xs.len() > max as usize {
            bail!(
                "batch_execute supports up to {max} commands (configured in Unity); received {}",
                xs.len()
            )
        }
        let mut commands = vec![];
        for (i, c) in xs.iter().enumerate() {
            let tool = c
                .get("tool")
                .and_then(Value::as_str)
                .filter(|s| !s.is_empty())
                .ok_or_else(|| anyhow!("Command at index {i} is missing a valid 'tool' name"))?;
            let params = c
                .get("params")
                .filter(|v| !v.is_null())
                .cloned()
                .unwrap_or(json!({}));
            if !params.is_object() {
                bail!("Command '{tool}' must specify parameters as an object/dict")
            };
            if params.get("unity_instance").is_some() {
                bail!("Command '{tool}' at index {i} contains 'unity_instance'. Per-command instance routing is not supported inside batch_execute. Set unity_instance on the outer batch_execute call to route the entire batch.")
            };
            commands.push(json!({"tool":tool,"params":params}));
        }
        let mut p = p;
        p["commands"] = json!(commands);
        return bridge.send(name, p, instance).await;
    }
    if [
        "manage_asset",
        "manage_components",
        "manage_gameobject",
        "manage_prefabs",
        "manage_scene",
        "find_gameobjects",
        "manage_texture",
        "inspect_prefab",
        "run_tests",
    ]
    .contains(&name)
        && !(name == "run_tests" && p["clear_stuck"] == json!(true))
    {
        if let Some(gate) = preflight(
            bridge,
            instance,
            name == "run_tests",
            name != "inspect_prefab",
        )
        .await
        {
            if name == "inspect_prefab" {
                bail!("{}", gate["message"])
            }
            return Ok(gate);
        }
    }
    let mut response = if name == "get_test_job" {
        let end = Instant::now()
            .checked_add(Duration::from_secs(
                integer(get(&a, "wait_timeout")).unwrap_or(0).max(0) as u64,
            ))
            .ok_or_else(|| anyhow!("wait_timeout is too large"))?;
        loop {
            let observation = crate::focus::next_observation();
            let mut r = bridge.send(name, p.clone(), instance).await?;
            if r.get("success") != Some(&json!(false)) {
                if let Some(data) = r.get_mut("data") {
                    crate::focus::update_job_nudge_observed(
                        bridge,
                        instance,
                        get(&a, "job_id").as_str().unwrap_or(""),
                        data,
                        integer(get(&a, "wait_timeout")).unwrap_or(0) > 0,
                        observation,
                    )
                    .await;
                }
            }
            let terminal = ["succeeded", "failed", "cancelled"].contains(
                &r.pointer("/data/status")
                    .and_then(Value::as_str)
                    .unwrap_or(""),
            );
            if !r.is_object() || r["success"] == json!(false) || terminal || Instant::now() >= end {
                break r;
            }
            tokio::time::sleep(
                Duration::from_secs(2).min(end.saturating_duration_since(Instant::now())),
            )
            .await;
            if Instant::now() >= end {
                break r;
            }
        }
    } else if name == "manage_ui"
        && [
            "create",
            "update",
            "delete",
            "attach_ui_document",
            "detach_ui_document",
            "create_panel_settings",
            "update_panel_settings",
            "render_ui",
            "link_stylesheet",
            "modify_visual_element",
        ]
        .contains(&get(&a, "action").as_str().unwrap_or(""))
    {
        send_mutation(bridge, name, p.clone(), instance).await?
    } else {
        bridge.send(name, p.clone(), instance).await?
    };
    if name == "inspect_prefab" {
        if response["success"] != json!(true) {
            bail!(
                "{}",
                response
                    .get("error")
                    .or_else(|| response.get("message"))
                    .map(text)
                    .unwrap_or_else(|| "inspect_prefab failed.".into())
            )
        };
        return response
            .pointer("/data/text")
            .filter(|v| v.is_string())
            .cloned()
            .ok_or_else(|| anyhow!("Unity returned no text; update the Unity MCP Light package."));
    }
    if !response.is_object() {
        return Ok(failure(text(&response)));
    }
    if name == "manage_shader" {
        if let Err(e) = decode_contents(&mut response) {
            return Ok(failure(format!("Error managing shader: {e}")));
        }
    }
    if name == "manage_ui" && get(&a, "action") == &json!("read") {
        let _ = decode_contents(&mut response);
    }
    if name == "manage_texture" {
        response["_debug_params"] = p;
    }
    if name == "manage_prefabs" && response.get("success").is_none() {
        response["success"] = json!(false);
    }
    if name == "read_console"
        && response["success"] == json!(true)
        && get(&a, "include_stacktrace") != &json!(true)
    {
        if let Some(d) = response.get_mut("data") {
            let entries = if d.is_array() {
                d.as_array_mut()
            } else if d.get("lines").is_some_and(Value::is_array) {
                d.get_mut("lines").and_then(Value::as_array_mut)
            } else {
                d.get_mut("items").and_then(Value::as_array_mut)
            };
            if let Some(entries) = entries {
                for e in entries {
                    if let Some(e) = e.as_object_mut() {
                        e.remove("stacktrace");
                    }
                }
            }
        }
    }
    if name == "manage_camera"
        && ["screenshot", "screenshot_multiview"]
            .contains(&get(&a, "action").as_str().unwrap_or(""))
    {
        return Ok(screenshot(response));
    }
    if name == "execute_code" {
        return Ok(
            json!({"success":response.get("success").unwrap_or(&json!(false)),"message":response.get("message").or_else(||response.get("error")).unwrap_or(&json!("")),"data":response.get("data")}),
        );
    }
    if name == "manage_components" && response["success"] == json!(true) {
        return Ok(
            json!({"success":true,"message":response.get("message").cloned().unwrap_or(json!(format!("Component {} successful.",text(get(&a,"action"))))),"data":response.get("data")}),
        );
    }
    if response["success"] == json!(true) {
        let msg = match name {
            "manage_scene" => Some("Scene operation successful."),
            "manage_editor" => Some("Editor operation successful."),
            "manage_gameobject" => Some("GameObject operation successful."),
            "manage_shader" => Some("Operation successful."),
            "find_gameobjects" => Some("Search completed."),
            "manage_components" => None,
            _ => None,
        };
        if let Some(msg) = msg {
            return Ok(
                json!({"success":true,"message":response.get("message").cloned().unwrap_or(json!(msg)),"data":response.get("data")}),
            );
        }
    }
    Ok(response)
}

fn custom_response(r: Value) -> Value {
    if r.is_object() {
        json!({"success":r.get("success").cloned().unwrap_or(json!(true)),"message":r.get("message"),"error":r.get("error"),"data":r.get("data").unwrap_or(&r)})
    } else {
        json!({"success":false,"message":text(&r),"error":null,"data":null})
    }
}
/// Execute a registered project tool, including bounded custom-tool status polling.
pub async fn call_custom(
    bridge: &dyn UnityBridge,
    name: &str,
    params: Value,
    instance: Option<&str>,
) -> Result<Value> {
    if !params.is_object() {
        return Ok(failure("parameters must be an object/dictionary"));
    }
    let defs = bridge.custom_tools(instance).await?;
    let Some(def) = defs
        .iter()
        .find(|d| d.get("name").and_then(Value::as_str) == Some(name))
    else {
        return Ok(failure(format!(
            "Tool '{name}' not found for project {}",
            instance.unwrap_or("unknown")
        )));
    };
    let mut response = bridge.send(name, params.clone(), instance).await?;
    if def["requires_polling"] != json!(true) {
        return Ok(custom_response(response));
    }
    let mut poll = params;
    poll["action"] = def
        .get("poll_action")
        .filter(|v| truth(v))
        .cloned()
        .unwrap_or(json!("status"));
    let timeout = integer(&def["max_poll_seconds"])
        .filter(|n| *n > 0)
        .unwrap_or(600);
    let end = Instant::now()
        .checked_add(Duration::from_secs(timeout as u64))
        .ok_or_else(|| anyhow!("max_poll_seconds is too large"))?;
    loop {
        let pending = response.is_null()
            || response.as_object().is_some_and(|m| m.is_empty())
            || response["_mcp_status"] == json!("pending");
        if !pending {
            return Ok(custom_response(response));
        }
        if Instant::now() >= end {
            return Ok(
                json!({"success":false,"message":format!("Timeout waiting for {name} to complete"),"data":response}),
            );
        }
        let interval = number(&response["_mcp_poll_interval"])
            .unwrap_or(1.0)
            .clamp(0.1, 5.0);
        tokio::time::sleep(
            Duration::from_secs_f64(interval).min(end.saturating_duration_since(Instant::now())),
        )
        .await;
        if Instant::now() >= end {
            continue;
        }
        response = match tokio::time::timeout_at(
            tokio::time::Instant::from_std(end),
            bridge.send(name, poll.clone(), instance),
        )
        .await
        {
            Ok(Ok(r)) => r,
            Err(_) => continue,
            Ok(Err(e)) => {
                json!({"_mcp_status":"pending","_mcp_poll_interval":(interval*2.0).clamp(1.0,5.0),"message":format!("Retrying after transient error: {e}")})
            }
        };
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use std::sync::Mutex;
    struct Mock {
        calls: Mutex<Vec<(String, Value, Option<String>)>>,
        responses: Mutex<std::collections::VecDeque<Value>>,
    }
    impl Mock {
        fn new(responses: Vec<Value>) -> Self {
            Self {
                calls: Mutex::new(vec![]),
                responses: Mutex::new(responses.into()),
            }
        }
    }
    #[async_trait]
    impl UnityBridge for Mock {
        async fn send(&self, name: &str, p: Value, i: Option<&str>) -> Result<Value> {
            self.calls
                .lock()
                .unwrap()
                .push((name.into(), p, i.map(str::to_owned)));
            if name == "get_editor_state" {
                return Ok(
                    json!({"success":true,"data":{"compilation":{"is_compiling":false},"advice":{"ready_for_tools":true}}}),
                );
            }
            if name == "get_project_info" {
                return Ok(json!({"success":false}));
            }
            Ok(self
                .responses
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or(json!({"success":true,"data":{}})))
        }
        async fn instances(&self) -> Result<Value> {
            Ok(json!({"instances":[]}))
        }
        async fn custom_tools(&self, _instance: Option<&str>) -> Result<Vec<Value>> {
            Ok(vec![
                json!({"name":"custom_job","requires_polling":true,"poll_action":"poll","max_poll_seconds":1}),
                json!({"name":"custom_now","requires_polling":false}),
            ])
        }
    }
    #[tokio::test]
    async fn custom_tools_poll_and_preserve_parameters() {
        let m = Mock::new(vec![
            json!({"_mcp_status":"pending","_mcp_poll_interval":0.1}),
            json!({"_mcp_status":"complete","data":{"x":1}}),
        ]);
        let r = call_custom(
            &m,
            "custom_job",
            json!({"action":"start","id":"A"}),
            Some("I"),
        )
        .await
        .unwrap();
        assert_eq!(r["data"]["x"], 1);
        let cs = m.calls.lock().unwrap();
        assert_eq!(cs.len(), 2);
        assert_eq!(cs[1].1, json!({"action":"poll","id":"A"}));
    }
    #[tokio::test]
    async fn custom_unknown_never_dispatches() {
        let m = Mock::new(vec![]);
        assert_eq!(
            call_custom(&m, "missing", json!({}), Some("I"))
                .await
                .unwrap()["success"],
            false
        );
        assert!(m.calls.lock().unwrap().is_empty());
    }
    #[test]
    fn batch_screenshots_and_read_decoding() {
        let v = screenshot(
            json!({"success":true,"data":{"screenshots":[{"angle":"top","imageBase64":"AA"},{"angle":"left"}]}}),
        );
        assert_eq!(v["content"].as_array().unwrap().len(), 3);
        assert_eq!(v["content"][1]["text"], "[Angle: top]");
        let mut r = json!({"success":true,"data":{"contentsEncoded":true,"encodedContents":STANDARD.encode("hello")}});
        decode_contents(&mut r).unwrap();
        assert_eq!(r["data"]["contents"], "hello");
        assert!(r["data"].get("encodedContents").is_none());
    }
    #[test]
    fn normalization_edge_cases() {
        assert!(color(&json!("#💗"), true).is_err());
        assert_eq!(parsed(&json!("1e3")), json!("1e3"));
        assert!(normalize(
            "manage_prefabs",
            json!({"action":"get_info","prefab_path":"   "})
        )
        .is_err());
        assert!(normalize(
            "manage_ui",
            json!({"action":"create","path":"Assets/UI/../a.uxml"})
        )
        .is_ok());
    }
    fn params(n: &str, v: Value) -> Value {
        normalize(n, v).unwrap().1
    }
    #[tokio::test]
    async fn invalid_animation_action_never_reaches_unity() {
        let mock = Mock::new(vec![]);
        let result = call(
            &mock,
            "manage_animation",
            json!({"action":"controller_typo"}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(result["success"], false);
        assert!(mock.calls.lock().unwrap().is_empty());
    }
    #[test]
    fn captured_python_forwarding_parity() {
        let fixtures: Value =
            serde_json::from_str(include_str!("../contracts/forwarding.json")).unwrap();
        for case in fixtures.as_array().unwrap() {
            let name = case["tool"].as_str().unwrap();
            if name == "batch_execute" {
                continue;
            }
            let actual = params(name, case["arguments"].clone());
            let calls = case["calls"].as_array().unwrap();
            let expected = &calls.last().unwrap()["params"];
            assert_eq!(&actual, expected, "{name} args={}", case["arguments"]);
        }
    }
    #[test]
    fn explicit_mapping_and_defaults() {
        assert_eq!(
            params("find_gameobjects", json!({"search_term":"Cube"})),
            json!({"searchTerm":"Cube","searchMethod":"by_name","includeInactive":false,"pageSize":50,"cursor":0})
        );
        assert_eq!(
            params(
                "manage_scene",
                json!({"action":"get_hierarchy","build_index":"2.9","page_size":"5","include_transform":"false"})
            ),
            json!({"action":"get_hierarchy","buildIndex":2,"pageSize":5,"includeTransform":false})
        );
        assert_eq!(
            params(
                "execute_menu_item",
                json!({"menu_path":"File/Save Project"})
            ),
            json!({"menuPath":"File/Save Project"})
        );
    }
    #[test]
    fn asset_search_safety() {
        assert_eq!(
            params(
                "manage_asset",
                json!({"action":"search","path":" t:MonoScript ","asset_type":"MonoScript","properties":"{\"x\":1}"})
            ),
            json!({"action":"search","path":"Assets","assetType":"MonoScript","filterType":"MonoScript","searchPattern":"t:MonoScript","generatePreview":false,"properties":{"x":1}})
        );
    }
    #[test]
    fn gameobjects_accept_loose_vectors_components() {
        let p = params(
            "manage_gameobject",
            json!({"action":"create","position":"1,2,3","components_to_add":{"type_name":"Rigidbody","properties":{"mass":5}},"set_active":"false"}),
        );
        assert_eq!(p["position"], json!([1., 2., 3.]));
        assert_eq!(
            p["componentsToAdd"],
            json!([{"typeName":"Rigidbody","properties":{"mass":5}}])
        );
        assert_eq!(p["world_space"], true);
        assert_eq!(p["setActive"], false);
        assert!(normalize(
            "manage_gameobject",
            json!({"action":"create","position":[1,2]})
        )
        .is_err());
    }
    #[test]
    fn child_vectors_and_required_prefab() {
        let p = params(
            "manage_prefabs",
            json!({"action":"modify_contents","prefab_path":"Assets/a.prefab","create_child":[{"name":"A","position":{"x":1,"y":2,"z":3}}]}),
        );
        assert_eq!(p["createChild"][0]["position"], json!([1., 2., 3.]));
        assert!(normalize(
            "manage_prefabs",
            json!({"action":"create_from_gameobject","target":"x"})
        )
        .is_err());
    }
    #[test]
    fn component_actions_do_not_leak_values() {
        let p = params(
            "manage_components",
            json!({"action":"remove","target":123,"component_type":"X","property":"x","value":5,"properties":{"a":1}}),
        );
        assert_eq!(
            p,
            json!({"action":"remove","target":123,"componentType":"X"})
        );
    }
    #[test]
    fn texture_import_and_pixels() {
        let p = params(
            "manage_texture",
            json!({"action":"create","path":"Assets/a.png","width":1,"height":1,"pixels":[[1,0,0]],"import_settings":{"texture_type":"sprite","readable":"false","wrap_mode":"clamp","max_texture_size":1024},"as_sprite":true}),
        );
        assert_eq!(p["pixels"], json!([[255, 0, 0, 255]]));
        assert_eq!(
            p["importSettings"],
            json!({"textureType":"Sprite","isReadable":false,"wrapMode":"Clamp","maxTextureSize":1024})
        );
        assert_eq!(p["spriteSettings"]["pixelsPerUnit"], 100);
        assert!(normalize(
            "manage_texture",
            json!({"action":"create","pixels":[[255,0,0]]})
        )
        .is_err());
    }
    #[test]
    fn colors_preserve_python_ranges() {
        assert_eq!(
            color(&json!([255, 0, 0, 255]), false).unwrap(),
            json!([1., 0., 0., 1.])
        );
        assert_eq!(
            color(&json!("#f00"), true).unwrap(),
            json!([255, 0, 0, 255])
        );
        assert_eq!(
            color(&json!([0.5, 0., 0.]), true).unwrap(),
            json!([128, 0, 0, 255])
        );
    }
    #[test]
    fn build_lists_and_booleans() {
        let p = params(
            "manage_build",
            json!({"action":"BUILD","scenes":"Assets/a.unity, Assets/b.unity","development":"yes","options":"[\"clean_build\"]"}),
        );
        assert_eq!(p["scenes"], json!(["Assets/a.unity", "Assets/b.unity"]));
        assert_eq!(p["development"], true);
        assert_eq!(p["options"], json!(["clean_build"]));
    }
    #[test]
    fn shader_and_ui_contents_encoded() {
        for name in ["manage_shader", "manage_ui"] {
            let p = params(
                name,
                json!({"action":"create","name":"A","path":"Assets/a.uxml","contents":"hello\n世界"}),
            );
            assert_eq!(p["encodedContents"], STANDARD.encode("hello\n世界"));
            assert!(p.get("contents").is_none());
        }
        assert!(normalize("manage_ui", json!({"action":"create","path":"../a.uxml"})).is_err());
    }
    #[test]
    fn camera_screenshot_parameters() {
        let p = params(
            "manage_camera",
            json!({"action":"screenshot","include_image":"true","screenshot_super_size":"2","orbit_elevations":"[0, 30]","view_position":"1,2,3"}),
        );
        assert_eq!(p["superSize"], 2);
        assert_eq!(p["includeImage"], true);
        assert_eq!(p["viewPosition"], json!([1., 2., 3.]));
        assert!(normalize(
            "manage_camera",
            json!({"action":"screenshot","capture_source":"scene_view","camera":"Main"})
        )
        .is_err());
    }
    #[test]
    fn tests_normalize_filters() {
        assert_eq!(
            params(
                "run_tests",
                json!({"test_names":"My.Test","include_details":true,"init_timeout":120000})
            ),
            json!({"mode":"EditMode","testNames":["My.Test"],"includeDetails":true,"initTimeout":120000})
        );
        assert_eq!(
            params("run_tests", json!({"clear_stuck":true,"init_timeout":-1})),
            json!({"clear_stuck":true})
        );
    }
    #[tokio::test]
    async fn camera_returns_image_blocks() {
        let m = Mock::new(vec![
            json!({"success":true,"data":{"imageBase64":"AAAA","width":2}}),
        ]);
        let r = call(
            &m,
            "manage_camera",
            json!({"action":"screenshot"}),
            Some("A"),
        )
        .await
        .unwrap();
        assert_eq!(r["content"][1]["type"], "image");
        assert!(!r["content"][0]["text"].as_str().unwrap().contains("AAAA"));
        assert_eq!(m.calls.lock().unwrap()[0].2, Some("A".into()));
    }
    #[tokio::test]
    async fn console_strips_stacktraces() {
        let m = Mock::new(vec![
            json!({"success":true,"data":{"lines":[{"message":"x","stacktrace":"huge"}]}}),
        ]);
        let r = call(&m, "read_console", json!({}), None).await.unwrap();
        assert!(r["data"]["lines"][0].get("stacktrace").is_none());
        let c = m.calls.lock().unwrap();
        assert_eq!(c[0].1["count"], 10);
        assert_eq!(c[0].1["types"], json!(["error"]));
    }
    #[tokio::test]
    async fn inspect_prefab_returns_plain_text() {
        let m = Mock::new(vec![
            json!({"success":true,"data":{"text":"Root\n  Child"}}),
        ]);
        assert_eq!(
            call(
                &m,
                "inspect_prefab",
                json!({"prefab_path":"Assets/x.prefab"}),
                None
            )
            .await
            .unwrap(),
            "Root\n  Child"
        );
    }
    #[tokio::test]
    async fn batch_routes_single_command_and_rejects_nested_instance() {
        let m = Mock::new(vec![]);
        call(
            &m,
            "batch_execute",
            json!({"commands":[{"tool":"manage_gameobject","params":null}],"fail_fast":true}),
            Some("A"),
        )
        .await
        .unwrap();
        {
            let c = m.calls.lock().unwrap();
            assert_eq!(c.last().unwrap().0, "batch_execute");
            assert_eq!(c.last().unwrap().1["commands"][0]["params"], json!({}));
            assert_eq!(c.last().unwrap().1["failFast"], true);
        }
        assert!(call(
            &m,
            "batch_execute",
            json!({"commands":[{"tool":"x","params":{"unity_instance":"B"}}]}),
            None
        )
        .await
        .is_err());
    }
    #[tokio::test]
    async fn test_job_does_not_dispatch_again_after_wait_deadline() {
        let b = Mock::new(vec![json!({"success":true,"data":{"status":"running"}})]);
        let r = call(
            &b,
            "get_test_job",
            json!({"job_id":"job","wait_timeout":1}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["data"]["status"], "running");
        assert_eq!(b.calls.lock().unwrap().len(), 1);
    }
    #[tokio::test]
    async fn custom_job_does_not_dispatch_after_poll_deadline() {
        let b = Mock::new(vec![
            json!({"_mcp_status":"pending","_mcp_poll_interval":5}),
        ]);
        let r = call_custom(&b, "custom_job", json!({}), None)
            .await
            .unwrap();
        assert_eq!(r["success"], false);
        assert_eq!(b.calls.lock().unwrap().len(), 1);
    }
    #[test]
    fn asset_refresh_states_are_busy() {
        for state in [
            json!({"assets":{"is_updating":true}}),
            json!({"assets":{"refresh":{"is_refresh_in_progress":true}}}),
        ] {
            assert!(is_busy(&json!({"data":state})));
        }
    }
    #[tokio::test]
    async fn readiness_deadline_bounds_hung_bridge() {
        struct Hung;
        #[async_trait::async_trait]
        impl UnityBridge for Hung {
            async fn send(&self, _: &str, _: Value, _: Option<&str>) -> Result<Value> {
                std::future::pending().await
            }
            async fn instances(&self) -> Result<Value> {
                Ok(json!({}))
            }
        }
        let ready = tokio::time::timeout(
            Duration::from_secs(1),
            wait_for_editor_ready(&Hung, None, Duration::from_millis(10)),
        )
        .await
        .unwrap();
        assert!(!ready);
    }
    #[tokio::test]
    async fn test_poll_returns_terminal_early() {
        let m = Mock::new(vec![json!({"success":true,"data":{"status":"succeeded"}})]);
        let r = call(
            &m,
            "get_test_job",
            json!({"job_id":"1","wait_timeout":30}),
            None,
        )
        .await
        .unwrap();
        assert_eq!(r["data"]["status"], "succeeded");
        assert_eq!(m.calls.lock().unwrap().len(), 1);
    }
    #[tokio::test]
    async fn refresh_recovers_reload_without_resending() {
        let m = Mock::new(vec![json!({"success":false,"error":"connection closed"})]);
        let r = call(&m, "refresh_unity", json!({"compile":"request"}), Some("A"))
            .await
            .unwrap();
        assert_eq!(r["success"], true);
        assert_eq!(
            m.calls
                .lock()
                .unwrap()
                .iter()
                .filter(|x| x.0 == "refresh_unity")
                .count(),
            1
        );
    }
    #[tokio::test]
    async fn mutation_retries_only_explicit_rejection() {
        let m = Mock::new(vec![
            json!({"success":false,"data":{"reason":"reloading"},"hint":"retry"}),
            json!({"success":true}),
        ]);
        let r = send_mutation(&m, "manage_ui", json!({}), None)
            .await
            .unwrap();
        assert_eq!(r["success"], true);
        assert_eq!(
            m.calls
                .lock()
                .unwrap()
                .iter()
                .filter(|x| x.0 == "manage_ui")
                .count(),
            2
        );
    }
    #[tokio::test]
    async fn unknown_tool_is_error() {
        assert!(call(&Mock::new(vec![]), "missing", json!({}), None)
            .await
            .is_err());
    }
}
