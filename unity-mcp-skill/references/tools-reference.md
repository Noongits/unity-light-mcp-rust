# Unity-MCP Tools Reference

Complete reference for all MCP tools. Each tool includes parameters, types, and usage examples.

> **Template warning:** Examples in this file are skill templates and may be inaccurate for some Unity versions, packages, or project setups. Validate parameters and payload shapes against your active tool schema and runtime behavior.

## Table of Contents

- [Infrastructure Tools](#infrastructure-tools)
- [Scene Tools](#scene-tools)
- [GameObject Tools](#gameobject-tools)
- [Script Tools](#script-tools)
- [Asset Tools](#asset-tools)
- [Material & Shader Tools](#material--shader-tools)
- [UI Tools](#ui-tools)
- [Editor Control Tools](#editor-control-tools)
- [Testing Tools](#testing-tools)
- [Camera Tools](#camera-tools)
- [Graphics Tools](#graphics-tools)
- [Physics Tools](#physics-tools)
- [Docs Tools](#docs-tools)

---

## Project Info Resource

Read `mcpforunity://project/info` to detect project capabilities before making assumptions about UI, input, or rendering setup.

**Returned fields:**

| Field | Type | Description |
|-------|------|-------------|
| `projectRoot` | string | Absolute path to project root |
| `projectName` | string | Project folder name |
| `unityVersion` | string | e.g. `"2022.3.20f1"` |
| `platform` | string | Active build target e.g. `"StandaloneWindows64"` |
| `assetsPath` | string | Absolute path to Assets folder |
| `renderPipeline` | string | `"BuiltIn"`, `"Universal"`, `"HighDefinition"`, or `"Custom"` |
| `activeInputHandler` | string | `"Old"`, `"New"`, or `"Both"` |
| `packages.ugui` | bool | `com.unity.ugui` installed (Canvas, Image, Button, etc.) |
| `packages.textmeshpro` | bool | `com.unity.textmeshpro` installed (TMP_Text, TMP_InputField) |
| `packages.inputsystem` | bool | `com.unity.inputsystem` installed (InputAction, PlayerInput) |
| `packages.uiToolkit` | bool | Always `true` for Unity 2021.3+ (UIDocument, VisualElement, UXML/USS) |
| `packages.screenCapture` | bool | `com.unity.modules.screencapture` enabled (ScreenCapture API for screenshots) |

**Key decision points:**

- **UI system**: If `packages.uiToolkit` is true (always for Unity 2021+), use `manage_ui` for UI Toolkit workflows (UXML/USS). If `packages.ugui` is true, use Canvas + uGUI components via `batch_execute`. UI Toolkit is preferred for new UI — it uses a frontend-like workflow (UXML for structure, USS for styling).
- **Text**: If `packages.textmeshpro` is true, use `TextMeshProUGUI` instead of legacy `Text`.
- **Input**: Use `activeInputHandler` to decide EventSystem module — `StandaloneInputModule` (Old) vs `InputSystemUIInputModule` (New). See [workflows.md — Input System](workflows.md#input-system-old-vs-new).
- **Shaders**: Use `renderPipeline` to pick correct shader names — `Standard` (BuiltIn) vs `Universal Render Pipeline/Lit` (URP) vs `HDRP/Lit` (HDRP).

---

## Infrastructure Tools

### batch_execute

Execute multiple MCP commands in a single batch (10-100x faster).

```python
batch_execute(
    commands=[                    # list[dict], required, max 25
        {"tool": "tool_name", "params": {...}},
        ...
    ],
    parallel=False,              # bool, optional - advisory only (Unity may still run sequentially)
    fail_fast=False,             # bool, optional - stop on first failure
    max_parallelism=None         # int, optional - max parallel workers
)
```

`batch_execute` is not transactional: earlier commands are not rolled back if a later command fails.

### set_active_instance

Route commands to a specific Unity instance (multi-instance workflows).

```python
set_active_instance(
    instance="ProjectName@abc123"  # str, required - Name@hash or hash prefix
)
```

### refresh_unity

Refresh asset database and trigger script compilation.

```python
refresh_unity(
    mode="if_dirty",             # "if_dirty" | "force"
    scope="all",                 # "assets" | "scripts" | "all"
    compile="none",              # "none" | "request"
    wait_for_ready=True          # bool - wait until editor ready
)
```

---

## Scene Tools

### manage_scene

Scene CRUD operations, hierarchy queries, screenshots, and scene view control.

```python
# Get hierarchy (paginated)
manage_scene(
    action="get_hierarchy",
    page_size=50,                # int, default 50, max 500
    cursor=0,                    # int, pagination cursor
    parent=None,                 # str|int, optional - filter by parent
    include_transform=False      # bool - include local transforms
)

# Screenshot (file only — saves to Assets/Screenshots/)
manage_camera(action="screenshot")

# Screenshot with inline image (base64 PNG returned to AI)
manage_scene(
    action="screenshot",
    camera="MainCamera",         # str, optional - camera name, path, or instance ID
    include_image=True,          # bool, default False - return base64 PNG inline
    max_resolution=512           # int, optional - downscale cap (default 640)
)

# Batch surround — contact sheet of 6 fixed angles (front/back/left/right/top/bird_eye)
manage_scene(
    action="screenshot",
    batch="surround",            # str - "surround" for 6-angle contact sheet
    max_resolution=256           # int - per-tile resolution cap
)
# Returns: single composite contact sheet image with labeled tiles

# Batch surround centered on a specific target
manage_scene(
    action="screenshot",
    batch="surround",
    view_target="Player",        # str|int|list[float] - center surround on this target
    max_resolution=256
)

# Batch orbit — configurable multi-angle grid around a target
manage_scene(
    action="screenshot",
    batch="orbit",               # str - "orbit" for configurable angle grid
    view_target="Player",        # str|int|list[float] - target to orbit around
    orbit_angles=8,              # int, default 8 - number of azimuth steps
    orbit_elevations=[0, 30],    # list[float], default [0, 30, -15] - vertical angles in degrees
    orbit_distance=10,           # float, optional - camera distance (auto-fit if omitted)
    orbit_fov=60,                # float, default 60 - camera FOV in degrees
    max_resolution=256           # int - per-tile resolution cap
)
# Returns: single composite contact sheet (angles × elevations tiles in a grid)

# Positioned screenshot (temp camera at viewpoint, no file saved)
manage_scene(
    action="screenshot",
    view_target="Enemy",         # str|int|list[float] - target to aim at
    view_position=[0, 10, -10],  # list[float], optional - camera position
    view_rotation=[45, 0, 0],    # list[float], optional - euler angles (overrides view_target aim)
    max_resolution=512
)

# Frame scene view on target
manage_scene(
    action="scene_view_frame",
    scene_view_target="Player"   # str|int - GO name, path, or instance ID to frame
)

# Other actions
manage_scene(action="get_active")        # Current scene info
manage_scene(action="get_build_settings") # Build settings
manage_scene(action="create", name="NewScene", path="Assets/Scenes/")
manage_scene(action="load", path="Assets/Scenes/Main.unity")
manage_scene(action="save")
```

### find_gameobjects

Search for GameObjects (returns instance IDs only).

```python
find_gameobjects(
    search_term="Player",        # str, required
    search_method="by_name",     # "by_name"|"by_tag"|"by_layer"|"by_component"|"by_path"|"by_id"
    include_inactive=False,      # bool|str
    page_size=50,                # int, default 50, max 500
    cursor=0                     # int, pagination cursor
)
# Returns: {"ids": [12345, 67890], "next_cursor": 50, ...}
```

---

## GameObject Tools

### manage_gameobject

Create, modify, delete, duplicate GameObjects.

```python
# Create
manage_gameobject(
    action="create",
    name="MyCube",               # str, required
    primitive_type="Cube",       # "Cube"|"Sphere"|"Capsule"|"Cylinder"|"Plane"|"Quad"
    position=[0, 1, 0],          # list[float] or JSON string "[0,1,0]"
    rotation=[0, 45, 0],         # euler angles
    scale=[1, 1, 1],
    components_to_add=["Rigidbody", "BoxCollider"],
    save_as_prefab=False,
    prefab_path="Assets/Prefabs/MyCube.prefab"
)

# Prefab instantiation — place a prefab instance in the scene
manage_gameobject(
    action="create",
    name="Enemy_1",
    prefab_path="Assets/Prefabs/Enemy.prefab",
    position=[5, 0, 3],
    parent="Enemies"                # optional parent GameObject
)
# Smart lookup — just the prefab name works too:
manage_gameobject(action="create", name="Enemy_2", prefab_path="Enemy", position=[10, 0, 3])

# Modify
manage_gameobject(
    action="modify",
    target="Player",             # name, path, or instance ID
    search_method="by_name",     # how to find target
    position=[10, 0, 0],
    rotation=[0, 90, 0],
    scale=[2, 2, 2],
    set_active=True,
    layer="Player",
    components_to_add=["AudioSource"],
    components_to_remove=["OldComponent"],
    component_properties={       # nested dict for property setting
        "Rigidbody": {
            "mass": 10.0,
            "useGravity": True
        }
    }
)

# Delete
manage_gameobject(action="delete", target="OldObject")

# Duplicate
manage_gameobject(
    action="duplicate",
    target="Player",
    new_name="Player2",
    offset=[5, 0, 0]             # position offset from original
)

# Move relative
manage_gameobject(
    action="move_relative",
    target="Player",
    reference_object="Enemy",    # optional reference
    direction="left",            # "left"|"right"|"up"|"down"|"forward"|"back"
    distance=5.0,
    world_space=True
)

# Look at target (rotates GO to face a point or another GO)
manage_gameobject(
    action="look_at",
    target="MainCamera",         # the GO to rotate
    look_at_target="Player",     # str (GO name/path) or list[float] world position
    look_at_up=[0, 1, 0]        # optional up vector, default [0,1,0]
)
```

### manage_components

Add, remove, or set properties on components.

```python
# Add component
manage_components(
    action="add",
    target=12345,                # instance ID (preferred) or name
    component_type="Rigidbody",
    search_method="by_id"
)

# Remove component
manage_components(
    action="remove",
    target="Player",
    component_type="OldScript"
)

# Set single property
manage_components(
    action="set_property",
    target=12345,
    component_type="Rigidbody",
    property="mass",
    value=5.0
)

# Set multiple properties
manage_components(
    action="set_property",
    target=12345,
    component_type="Transform",
    properties={
        "position": [1, 2, 3],
        "localScale": [2, 2, 2]
    }
)

# Set object reference property (reference another GameObject by name)
manage_components(
    action="set_property",
    target="GameManager",
    component_type="GameManagerScript",
    property="targetObjects",
    value=[{"name": "Flower_1"}, {"name": "Flower_2"}, {"name": "Bee_1"}]
)

# Object reference formats supported:
# - {"name": "ObjectName"}     → Find GameObject in scene by name
# - {"instanceID": 12345}      → Direct instance ID reference
# - {"guid": "abc123..."}      → Asset GUID reference
# - {"path": "Assets/..."}     → Asset path reference
# - "Assets/Prefabs/My.prefab" → String shorthand for asset paths
# - "ObjectName"               → String shorthand for scene name lookup
# - 12345                      → Integer shorthand for instanceID
```

---

## Script Tools

### create_script

Create a new C# script.

```python
create_script(
    path="Assets/Scripts/MyScript.cs",  # str, required
    contents='''using UnityEngine;

public class MyScript : MonoBehaviour
{
    void Start() { }
    void Update() { }
}''',
    script_type="MonoBehaviour",  # optional hint
    namespace="MyGame"            # optional namespace
)
```

### script_apply_edits

Apply structured edits to C# scripts (safer than raw text edits).

```python
script_apply_edits(
    name="MyScript",             # script name (no .cs)
    path="Assets/Scripts",       # folder path
    edits=[
        # Replace entire method
        {
            "op": "replace_method",
            "methodName": "Update",
            "replacement": "void Update() { transform.Rotate(Vector3.up); }"
        },
        # Insert new method
        {
            "op": "insert_method",
            "afterMethod": "Start",
            "code": "void OnEnable() { Debug.Log(\"Enabled\"); }"
        },
        # Delete method
        {
            "op": "delete_method",
            "methodName": "OldMethod"
        },
        # Anchor-based insert
        {
            "op": "anchor_insert",
            "anchor": "void Start()",
            "position": "before",  # "before" | "after"
            "text": "// Called before Start\n"
        },
        # Regex replace
        {
            "op": "regex_replace",
            "pattern": "Debug\\.Log\\(",
            "text": "Debug.LogWarning("
        },
        # Prepend/append to file
        {"op": "prepend", "text": "// File header\n"},
        {"op": "append", "text": "\n// File footer"}
    ]
)
```

### apply_text_edits

Apply precise character-position edits (1-indexed lines/columns).

```python
apply_text_edits(
    uri="mcpforunity://path/Assets/Scripts/MyScript.cs",
    edits=[
        {
            "startLine": 10,
            "startCol": 5,
            "endLine": 10,
            "endCol": 20,
            "newText": "replacement text"
        }
    ],
    precondition_sha256="abc123...",  # optional, prevents stale edits
    strict=True                        # optional, stricter validation
)
```

### validate_script

Check script for syntax/semantic errors.

```python
validate_script(
    uri="mcpforunity://path/Assets/Scripts/MyScript.cs",
    level="standard",            # "basic" | "standard"
    include_diagnostics=True     # include full error details
)
```

### get_sha

Get file hash without content (for preconditions).

```python
get_sha(uri="mcpforunity://path/Assets/Scripts/MyScript.cs")
# Returns: {"sha256": "...", "lengthBytes": 1234, "lastModifiedUtc": "..."}
```

### delete_script

Delete a script file.

```python
delete_script(uri="mcpforunity://path/Assets/Scripts/OldScript.cs")
```

---

## Asset Tools

### manage_asset

Asset operations: search, import, create, modify, delete.

```python
# Search assets (paginated)
manage_asset(
    action="search",
    path="Assets",               # search scope
    search_pattern="*.prefab",   # glob or "t:MonoScript" filter
    filter_type="Prefab",        # optional type filter
    page_size=25,                # keep small to avoid large payloads
    page_number=1,               # 1-based
    generate_preview=False       # avoid base64 bloat
)

# Get asset info
manage_asset(action="get_info", path="Assets/Prefabs/Player.prefab")

# Create asset
manage_asset(
    action="create",
    path="Assets/Materials/NewMaterial.mat",
    asset_type="Material",
    properties={"color": [1, 0, 0, 1]}
)

# Duplicate/move/rename
manage_asset(action="duplicate", path="Assets/A.prefab", destination="Assets/B.prefab")
manage_asset(action="move", path="Assets/A.prefab", destination="Assets/Prefabs/A.prefab")
manage_asset(action="rename", path="Assets/A.prefab", destination="Assets/B.prefab")

# Create folder
manage_asset(action="create_folder", path="Assets/NewFolder")

# Delete
manage_asset(action="delete", path="Assets/OldAsset.asset")
```

### manage_prefabs

Headless prefab operations.

```python
# Get prefab info
manage_prefabs(action="get_info", prefab_path="Assets/Prefabs/Player.prefab")

# Get prefab hierarchy (full JSON, large on big prefabs; prefer inspect_prefab below)
manage_prefabs(action="get_hierarchy", prefab_path="Assets/Prefabs/Player.prefab")

# Create prefab from scene GameObject
manage_prefabs(
    action="create_from_gameobject",
    target="Player",             # GameObject in scene
    prefab_path="Assets/Prefabs/Player.prefab",
    allow_overwrite=False
)

# Modify prefab contents (headless)
manage_prefabs(
    action="modify_contents",
    prefab_path="Assets/Prefabs/Player.prefab",
    target="ChildObject",        # object within prefab
    position=[0, 1, 0],
    components_to_add=["AudioSource"]
)

# Delete child GameObjects from prefab
manage_prefabs(
    action="modify_contents",
    prefab_path="Assets/Prefabs/Player.prefab",
    delete_child=["OldChild", "Turret/Barrel"]  # single string or list
)

# Create child GameObject in prefab
manage_prefabs(
    action="modify_contents",
    prefab_path="Assets/Prefabs/Player.prefab",
    create_child={"name": "SpawnPoint", "primitive_type": "Sphere", "position": [0, 2, 0]}
)

# Set component properties on prefab contents
manage_prefabs(
    action="modify_contents",
    prefab_path="Assets/Prefabs/Player.prefab",
    target="ChildObject",
    component_properties={"Rigidbody": {"mass": 5.0}, "MyScript": {"health": 100}}
)
```

---

### inspect_prefab

Read-only prefab inspection in compact plain text (not JSON). Nothing is opened in a Prefab Stage, marked dirty or saved. Objects are named by hierarchy path relative to the prefab root (`""` or the root's name is the root; duplicate sibling names get `#2`, `#3`).

| Parameter | Modes | Description |
|-----------|-------|-------------|
| `mode` | all | `tree` (default), `node`, `refs`, `overrides`, `usages`, `problems` |
| `prefab_path` | all but usages | `Assets/...prefab`, a folder (problems), or `scene:Path/To/Object` |
| `depth` | tree | Levels to expand, default 2 (unlimited when `filter` is set) |
| `root` | tree, refs | Only this subtree |
| `filter` | tree | Name, component or nested-prefab substring; matches are shown with their ancestors |
| `path`, `component` | node | Object path and component type (`Door`, `Door#2`, or a full name) |
| `all_fields` | node | Include fields that still have their default value |
| `script` / `asset` | usages | Class name, or an asset path |
| `max_chars` | all | Output budget, default 6000; truncated output ends with a hint |

```python
inspect_prefab(prefab_path="Assets/Cars/E36_CAR.prefab")
# E36_CAR.prefab  410 objs  depth<=2  (shown 24)
# E36_CAR [Rigidbody, VehicleController, UserCar, BoxCollider, +12]
#   Araciciisigi @Araciciisigi.prefab [Light, HDAdditionalLightData, HalogenLightTween]
#   Body [MeshRenderer] ...4 below
#   Wheels
#     Wheel_FL..RR x4 [WheelCollider]
#   Parts (-) ...118 below [@Seat.prefab x2]

inspect_prefab(mode="node", prefab_path="Assets/Cars/E36_CAR.prefab", component="UserCar")
# E36_CAR.prefab:E36_CAR children=12
#   UserCar
#     value: 12000
#     carDataSheet -> Assets/Data/E36.asset
#     eventGETIN: 2 listeners
#       -> Interior/Cam (Camera).set_enabled(bool true)
#       -> E36_CAR (Lights).DashOn()

inspect_prefab(mode="refs", prefab_path="Assets/Cars/E36_CAR.prefab", root="Body")
inspect_prefab(mode="overrides", prefab_path="Assets/Cars/E36_Blue.prefab")   # variant: base chain, old -> new
inspect_prefab(mode="usages", script="VehicleController")                     # direct users with object paths, "via @X.prefab"
inspect_prefab(mode="usages", asset="Assets/Materials/Paint.mat")
inspect_prefab(mode="problems", prefab_path="Assets/Cars")                    # whole folder
```

Markers: `(-)` inactive, `@X.prefab` nested prefab root, `@MISSING-PREFAB` broken nested link, `!missing` missing script, `xN` identical siblings shown once, `...N below` collapsed children. References print as `-> Path (Component)` inside the prefab, `-> Assets/...` for assets, `-> null` when never assigned and `-> MISSING` when the target was deleted.

---

## Material & Shader Tools

### manage_material

Create and modify materials.

```python
# Create material
manage_material(
    action="create",
    material_path="Assets/Materials/Red.mat",
    shader="Standard",
    properties={"_Color": [1, 0, 0, 1]}
)

# Get material info
manage_material(action="get_material_info", material_path="Assets/Materials/Red.mat")

# Set shader property
manage_material(
    action="set_material_shader_property",
    material_path="Assets/Materials/Red.mat",
    property="_Metallic",
    value=0.8
)

# Set color
manage_material(
    action="set_material_color",
    material_path="Assets/Materials/Red.mat",
    property="_BaseColor",
    color=[0, 1, 0, 1]           # RGBA
)

# Assign to renderer
manage_material(
    action="assign_material_to_renderer",
    target="MyCube",
    material_path="Assets/Materials/Red.mat",
    slot=0                       # material slot index
)

# Set renderer color directly
manage_material(
    action="set_renderer_color",
    target="MyCube",
    color=[1, 0, 0, 1],
    mode="create_unique"          # Creates a unique .mat asset per object (persistent)
    # Other modes: "property_block" (default, not persistent),
    #              "shared" (mutates shared material — avoid for primitives),
    #              "instance" (runtime only, not persistent)
)
```

### manage_texture

Create procedural textures.

```python
manage_texture(
    action="create",
    path="Assets/Textures/Checker.png",
    width=64,
    height=64,
    fill_color=[255, 255, 255, 255]  # or [1.0, 1.0, 1.0, 1.0]
)

# Apply pattern
manage_texture(
    action="apply_pattern",
    path="Assets/Textures/Checker.png",
    pattern="checkerboard",      # "checkerboard"|"stripes"|"dots"|"grid"|"brick"
    palette=[[0,0,0,255], [255,255,255,255]],
    pattern_size=8
)

# Apply gradient
manage_texture(
    action="apply_gradient",
    path="Assets/Textures/Gradient.png",
    gradient_type="linear",      # "linear"|"radial"
    gradient_angle=45,
    palette=[[255,0,0,255], [0,0,255,255]]
)
```

---

## UI Tools

### manage_ui

Manage Unity UI Toolkit elements: UXML documents, USS stylesheets, UIDocument components, and visual tree inspection.

```python
# Create a UXML file
manage_ui(
    action="create",
    path="Assets/UI/MainMenu.uxml",
    contents='<ui:UXML xmlns:ui="UnityEngine.UIElements"><ui:Label text="Hello" /></ui:UXML>'
)

# Create a USS stylesheet
manage_ui(
    action="create",
    path="Assets/UI/Styles.uss",
    contents=".title { font-size: 32px; color: white; }"
)

# Read a UXML/USS file
manage_ui(
    action="read",
    path="Assets/UI/MainMenu.uxml"
)
# Returns: {"success": true, "data": {"contents": "...", "path": "..."}}

# Update an existing file
manage_ui(
    action="update",
    path="Assets/UI/Styles.uss",
    contents=".title { font-size: 48px; color: yellow; -unity-font-style: bold; }"
)

# Attach UIDocument to a GameObject
manage_ui(
    action="attach_ui_document",
    target="UICanvas",                    # GameObject name or path
    source_asset="Assets/UI/MainMenu.uxml",
    panel_settings="Assets/UI/Panel.asset",  # optional, auto-creates if omitted
    sort_order=0                          # optional, default 0
)

# Create PanelSettings asset
manage_ui(
    action="create_panel_settings",
    path="Assets/UI/Panel.asset",
    scale_mode="ScaleWithScreenSize",     # optional: "ConstantPixelSize"|"ConstantPhysicalSize"|"ScaleWithScreenSize"
    reference_resolution={"width": 1920, "height": 1080}  # optional, for ScaleWithScreenSize
)

# Inspect the visual tree of a UIDocument
manage_ui(
    action="get_visual_tree",
    target="UICanvas",                    # GameObject with UIDocument
    max_depth=10                          # optional, default 10
)
# Returns: hierarchy of VisualElements with type, name, classes, styles, text, children
```

**UI Toolkit workflow:**

1. Create UXML (structure, like HTML) and USS (styling, like CSS) files
2. Create a PanelSettings asset (or let `attach_ui_document` auto-create one)
3. Create an empty GameObject and attach UIDocument with the UXML source
4. Use `get_visual_tree` to inspect the result

**Important:** Always use `<ui:Style>` (with the `ui:` namespace prefix) in UXML files, not bare `<Style>`. UI Builder will fail to open files that use `<Style>` without the prefix.

---

## Editor Control Tools

### manage_editor

Control Unity Editor state.

```python
manage_editor(action="play")               # Enter play mode
manage_editor(action="pause")              # Pause play mode
manage_editor(action="stop")               # Exit play mode

manage_editor(action="set_active_tool", tool_name="Move")  # Move/Rotate/Scale/etc.

manage_editor(action="add_tag", tag_name="Enemy")
manage_editor(action="remove_tag", tag_name="OldTag")

manage_editor(action="add_layer", layer_name="Projectiles")
manage_editor(action="remove_layer", layer_name="OldLayer")

manage_prefabs(action="open_prefab_stage", prefab_path="Assets/Prefabs/Enemy.prefab")
manage_prefabs(action="save_prefab_stage")   # Save changes in the open prefab stage
manage_prefabs(action="close_prefab_stage")  # Exit prefab editing mode back to main scene

# Package deployment (no confirmation dialog — designed for LLM-driven iteration)
manage_editor(action="deploy_package")     # Copy configured MCPForUnity source into installed package
manage_editor(action="restore_package")    # Revert to pre-deployment backup
```

**Deploy workflow:** Set the source path in MCP for Unity Advanced Settings first. `deploy_package` copies the source into the project's package location, creates a backup, and triggers `AssetDatabase.Refresh`. Follow with `refresh_unity(wait_for_ready=True)` to wait for recompilation.

### execute_menu_item

Execute any Unity menu item.

```python
execute_menu_item(menu_path="File/Save Project")
execute_menu_item(menu_path="GameObject/3D Object/Cube")
execute_menu_item(menu_path="Window/General/Console")
```

### read_console

Read or clear Unity console messages.

```python
# Get recent messages
read_console(
    action="get",
    types=["error"],             # default; add "warning"/"log" or use ["all"] only when needed
    count=10,                    # max messages (ignored with paging)
    filter_text="NullReference", # optional text filter
    page_size=50,
    cursor=0,
    format="detailed",           # "plain"|"detailed"|"json"
    include_stacktrace=True
)

# Clear console
read_console(action="clear")
```

---

## Testing Tools

### run_tests

Start async test execution.

```python
result = run_tests(
    mode="EditMode",             # "EditMode"|"PlayMode"
    test_names=["MyTests.TestA", "MyTests.TestB"],  # specific tests
    group_names=["Integration*"],  # regex patterns
    category_names=["Unit"],     # NUnit categories
    assembly_names=["Tests"],    # assembly filter
    include_failed_tests=True,   # include failure details
    include_details=False        # include all test details
)
# Returns: {"job_id": "abc123", ...}
```

### get_test_job

Poll test job status.

```python
result = get_test_job(
    job_id="abc123",
    wait_timeout=60,             # wait up to N seconds
    include_failed_tests=True,
    include_details=False
)
# Returns: {"status": "complete"|"running"|"failed", "results": {...}}
```

---

## Search Tools

### find_in_file

Search file contents with regex.

```python
find_in_file(
    uri="mcpforunity://path/Assets/Scripts/MyScript.cs",
    pattern="public void \\w+",  # regex pattern
    max_results=200,
    ignore_case=True
)
# Returns: line numbers, content excerpts, match positions
```

---

## Custom Tools

### execute_custom_tool

Execute project-specific custom tools.

```python
execute_custom_tool(
    tool_name="my_custom_tool",
    parameters={"param1": "value", "param2": 42}
)
```

Discover available custom tools via `mcpforunity://custom-tools` resource.

---

## Camera Tools

### manage_camera

Unified camera management (Unity Camera + Cinemachine). Works without Cinemachine using basic Camera; unlocks presets, pipelines, and blending when Cinemachine is installed. Use `ping` to check availability.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | Action to perform (see categories below) |
| `target` | string | Sometimes | Target camera (name, path, or instance ID) |
| `search_method` | string | No | `by_id`, `by_name`, `by_path` |
| `properties` | dict \| string | No | Action-specific parameters |

**Screenshot parameters** (for `screenshot` and `screenshot_multiview` actions):

| Parameter | Type | Description |
|-----------|------|-------------|
| `capture_source` | string | `"game_view"` (default) or `"scene_view"` (editor viewport) |
| `view_target` | string\|int\|list | Target to focus on (GO name/path/ID or [x,y,z]). game_view: aims camera; scene_view: frames viewport |
| `camera` | string | Camera to capture from (defaults to Camera.main). game_view only |
| `include_image` | bool | Return base64 PNG inline (default false) |
| `max_resolution` | int | Downscale cap in px (default 640) |
| `batch` | string | `"surround"` (6 angles) or `"orbit"` (configurable grid). game_view only |
| `view_position` | list[float] | World position [x,y,z] to place camera. game_view only |
| `view_rotation` | list[float] | Euler rotation [x,y,z] (overrides view_target). game_view only |

**Actions by category:**

**Setup:**
- `ping` — Check Cinemachine availability and version
- `ensure_brain` — Ensure CinemachineBrain exists on main camera. Properties: `camera` (target camera), `defaultBlendStyle`, `defaultBlendDuration`
- `get_brain_status` — Get Brain state (active camera, blend status)

**Creation:**
- `create_camera` — Create camera with optional preset. Properties: `name`, `preset` (follow/third_person/freelook/dolly/static/top_down/side_scroller), `follow`, `lookAt`, `priority`, `fieldOfView`. Falls back to basic Camera without Cinemachine.

**Configuration:**
- `set_target` — Set Follow and/or LookAt targets. Properties: `follow`, `lookAt` (GO name/path/ID)
- `set_priority` — Set camera priority for Brain selection. Properties: `priority` (int)
- `set_lens` — Configure lens. Properties: `fieldOfView`, `nearClipPlane`, `farClipPlane`, `orthographicSize`, `dutch`
- `set_body` — Configure Body component (Cinemachine). Properties: `bodyType` (to swap), plus component-specific properties
- `set_aim` — Configure Aim component (Cinemachine). Properties: `aimType` (to swap), plus component-specific properties
- `set_noise` — Configure Noise (Cinemachine). Properties: `amplitudeGain`, `frequencyGain`

**Extensions (Cinemachine):**
- `add_extension` — Add extension. Properties: `extensionType` (CinemachineConfiner2D, CinemachineDeoccluder, CinemachineImpulseListener, CinemachineFollowZoom, CinemachineRecomposer, etc.)
- `remove_extension` — Remove extension by type. Properties: `extensionType`

**Control:**
- `list_cameras` — List all cameras with status
- `set_blend` — Configure default blend on Brain. Properties: `style` (Cut/EaseInOut/Linear/etc.), `duration`
- `force_camera` — Override Brain to use specific camera
- `release_override` — Release camera override

**Capture:**
- `screenshot` — Capture screenshot. Supports `capture_source="game_view"` (default, camera-based) or `"scene_view"` (editor viewport). game_view supports inline base64, batch surround/orbit, positioned capture. scene_view supports `view_target` for framing.
- `screenshot_multiview` — Shorthand for screenshot with batch='surround' and include_image=true.

**Examples:**

```python
# Check Cinemachine availability
manage_camera(action="ping")

# Create a third-person camera following the player
manage_camera(action="create_camera", properties={
    "name": "FollowCam", "preset": "third_person",
    "follow": "Player", "lookAt": "Player", "priority": 20
})

# Ensure Brain exists on main camera
manage_camera(action="ensure_brain")

# Configure body component
manage_camera(action="set_body", target="FollowCam", properties={
    "bodyType": "CinemachineThirdPersonFollow",
    "cameraDistance": 5.0, "shoulderOffset": [0.5, 0.5, 0]
})

# Set aim
manage_camera(action="set_aim", target="FollowCam", properties={
    "aimType": "CinemachineRotationComposer"
})

# Add camera shake
manage_camera(action="set_noise", target="FollowCam", properties={
    "amplitudeGain": 0.5, "frequencyGain": 1.0
})

# Set priority to make this the active camera
manage_camera(action="set_priority", target="FollowCam", properties={"priority": 50})

# Force a specific camera
manage_camera(action="force_camera", target="CinematicCam")

# Release override (return to priority-based selection)
manage_camera(action="release_override")

# Configure blend transitions
manage_camera(action="set_blend", properties={"style": "EaseInOut", "duration": 2.0})

# Add deoccluder extension
manage_camera(action="add_extension", target="FollowCam", properties={
    "extensionType": "CinemachineDeoccluder"
})

# Screenshot from a specific camera (game_view, default)
manage_camera(action="screenshot", camera="FollowCam", include_image=True, max_resolution=512)

# Scene View screenshot (captures editor viewport — gizmos, wireframes, grid)
manage_camera(action="screenshot", capture_source="scene_view", include_image=True)

# Scene View screenshot framed on a specific object
manage_camera(action="screenshot", capture_source="scene_view", view_target="Canvas", include_image=True)

# Multi-view screenshot (6-angle contact sheet)
manage_camera(action="screenshot_multiview", max_resolution=480)

# List all cameras
manage_camera(action="list_cameras")
```

**Tier system:**
- Tier 1 actions (ping, create_camera, set_target, set_lens, set_priority, list_cameras, screenshot, screenshot_multiview) work without Cinemachine — they fall back to basic Unity Camera.
- Tier 2 actions (ensure_brain, get_brain_status, set_body, set_aim, set_noise, add/remove_extension, set_blend, force_camera, release_override) require `com.unity.cinemachine`. If called without Cinemachine, they return an error with a fallback suggestion.

**Resource:** Read `mcpforunity://scene/cameras` for current camera state before modifying.

---

## Graphics Tools

### manage_graphics

Unified rendering and graphics management: volumes/post-processing, light baking, rendering stats, pipeline configuration, and URP renderer features. Requires URP or HDRP for volume/feature actions. Use `ping` to check pipeline status and available features.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | Action to perform (see categories below) |
| `target` | string | Sometimes | Target object name or instance ID |
| `effect` | string | Sometimes | Effect type name (e.g., `Bloom`, `Vignette`) |
| `properties` | dict | No | Action-specific properties to set |
| `parameters` | dict | No | Effect parameter values |
| `settings` | dict | No | Bake or pipeline settings |
| `name` | string | No | Name for created objects |
| `profile_path` | string | No | Asset path for VolumeProfile |
| `path` | string | No | Asset path (for `volume_create_profile`) |
| `position` | list[float] | No | Position [x,y,z] |

**Actions by category:**

**Status:**
- `ping` — Check render pipeline type, available features, and package status

**Volume (require URP/HDRP):**
- `volume_create` — Create a Volume GameObject with optional effects. Properties: `name`, `is_global` (default true), `weight` (0-1), `priority`, `profile_path` (existing profile), `effects` (list of effect defs)
- `volume_add_effect` — Add effect override to a Volume. Params: `target` (Volume GO), `effect` (e.g., "Bloom")
- `volume_set_effect` — Set effect parameters. Params: `target`, `effect`, `parameters` (dict of param name to value)
- `volume_remove_effect` — Remove effect override. Params: `target`, `effect`
- `volume_get_info` — Get Volume details (profile, effects, parameters). Params: `target`
- `volume_set_properties` — Set Volume component properties (weight, priority, isGlobal). Params: `target`, `properties`
- `volume_list_effects` — List all available volume effects for the active pipeline
- `volume_create_profile` — Create a standalone VolumeProfile asset. Params: `path`, `effects` (optional)

**Bake (Edit mode only):**
- `bake_start` — Start lightmap bake. Params: `async_bake` (default true)
- `bake_cancel` — Cancel in-progress bake
- `bake_status` — Check bake progress
- `bake_clear` — Clear baked lightmap data
- `bake_reflection_probe` — Bake a specific reflection probe. Params: `target`
- `bake_get_settings` — Get current Lightmapping settings
- `bake_set_settings` — Set Lightmapping settings. Params: `settings` (dict)
- `bake_create_light_probe_group` — Create a Light Probe Group. Params: `name`, `position`, `grid_size` [x,y,z], `spacing`
- `bake_create_reflection_probe` — Create a Reflection Probe. Params: `name`, `position`, `size` [x,y,z], `resolution`, `mode`, `hdr`, `box_projection`
- `bake_set_probe_positions` — Set Light Probe positions manually. Params: `target`, `positions` (array of [x,y,z])

**Stats:**
- `stats_get` — Get rendering counters (draw calls, batches, triangles, vertices, etc.)
- `stats_list_counters` — List all available ProfilerRecorder counters
- `stats_set_scene_debug` — Set Scene View debug/draw mode. Params: `mode`
- `stats_get_memory` — Get rendering memory usage

**Pipeline:**
- `pipeline_get_info` — Get active render pipeline info (type, quality level, asset paths)
- `pipeline_set_quality` — Switch quality level. Params: `level` (name or index)
- `pipeline_get_settings` — Get pipeline asset settings
- `pipeline_set_settings` — Set pipeline asset settings. Params: `settings` (dict)

**Features (URP only):**
- `feature_list` — List renderer features on the active URP renderer
- `feature_add` — Add a renderer feature. Params: `feature_type`, `name`, `material` (for full-screen effects)
- `feature_remove` — Remove a renderer feature. Params: `index` or `name`
- `feature_configure` — Set feature properties. Params: `index` or `name`, `properties` (dict)
- `feature_toggle` — Enable/disable a feature. Params: `index` or `name`, `active` (bool)
- `feature_reorder` — Reorder features. Params: `order` (list of indices)

**Examples:**

```python
# Check pipeline status
manage_graphics(action="ping")

# Create a global post-processing volume with Bloom and Vignette
manage_graphics(action="volume_create", name="PostProcessing", is_global=True,
    effects=[
        {"type": "Bloom", "parameters": {"intensity": 1.5, "threshold": 0.9}},
        {"type": "Vignette", "parameters": {"intensity": 0.4}}
    ])

# Add an effect to an existing volume
manage_graphics(action="volume_add_effect", target="PostProcessing", effect="ColorAdjustments")

# Configure effect parameters
manage_graphics(action="volume_set_effect", target="PostProcessing",
    effect="ColorAdjustments", parameters={"postExposure": 0.5, "saturation": 10})

# Get volume info
manage_graphics(action="volume_get_info", target="PostProcessing")

# List all available effects for the active pipeline
manage_graphics(action="volume_list_effects")

# Create a VolumeProfile asset
manage_graphics(action="volume_create_profile", path="Assets/Settings/MyProfile.asset",
    effects=[{"type": "Bloom"}, {"type": "Tonemapping"}])

# Start async lightmap bake
manage_graphics(action="bake_start", async_bake=True)

# Check bake progress
manage_graphics(action="bake_status")

# Create a Light Probe Group with a 3x2x3 grid
manage_graphics(action="bake_create_light_probe_group", name="ProbeGrid",
    position=[0, 1, 0], grid_size=[3, 2, 3], spacing=2.0)

# Create a Reflection Probe
manage_graphics(action="bake_create_reflection_probe", name="RoomProbe",
    position=[0, 2, 0], size=[10, 5, 10], resolution=256, hdr=True)

# Get rendering stats
manage_graphics(action="stats_get")

# Get memory usage
manage_graphics(action="stats_get_memory")

# Get pipeline info
manage_graphics(action="pipeline_get_info")

# Switch quality level
manage_graphics(action="pipeline_set_quality", level="High")

# List URP renderer features
manage_graphics(action="feature_list")

# Add a full-screen renderer feature
manage_graphics(action="feature_add", feature_type="FullScreenPassRendererFeature",
    name="NightVision", material="Assets/Materials/NightVision.mat")

# Toggle a feature off
manage_graphics(action="feature_toggle", index=0, active=False)

# Reorder features
manage_graphics(action="feature_reorder", order=[2, 0, 1])
```

**Resources:**
- `mcpforunity://scene/volumes` — Lists all Volume components in the scene with their profiles and effects
- `mcpforunity://rendering/stats` — Current rendering performance counters
- `mcpforunity://pipeline/renderer-features` — URP renderer features on the active renderer

---

## Physics Tools

### `manage_physics`

Manage 3D and 2D physics: settings, collision matrix, materials, joints, queries, validation, and simulation. All actions support `dimension="3d"` (default) or `dimension="2d"` where applicable.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | See action groups below |
| `dimension` | string | No | `"3d"` (default) or `"2d"` |
| `settings` | object | For set_settings | Key-value physics settings dict |
| `layer_a` / `layer_b` | string | For collision matrix | Layer name or index |
| `collide` | bool | For set_collision_matrix | `true` to enable, `false` to disable |
| `name` | string | For create_physics_material | Material asset name |
| `path` | string | No | Asset folder path (create) or asset path (configure) |
| `dynamic_friction` / `static_friction` / `bounciness` | float | No | Material properties (0–1) |
| `friction_combine` / `bounce_combine` | string | No | `Average`, `Minimum`, `Multiply`, `Maximum` |
| `material_path` | string | For assign_physics_material | Path to physics material asset |
| `target` | string | For joints/queries/validate | GameObject name or instance ID |
| `joint_type` | string | For joints | 3D: `fixed`, `hinge`, `spring`, `character`, `configurable`; 2D: `distance`, `fixed`, `friction`, `hinge`, `relative`, `slider`, `spring`, `target`, `wheel` |
| `connected_body` | string | For add_joint | Connected body GameObject |
| `motor` / `limits` / `spring` / `drive` | object | For configure_joint | Joint sub-config objects |
| `properties` | object | For configure_joint/material | Direct property dict |
| `origin` / `direction` | float[] | For raycast | Ray origin and direction `[x,y,z]` or `[x,y]` |
| `max_distance` | float | No | Max raycast distance |
| `shape` | string | For overlap | `sphere`, `box`, `capsule` (3D); `circle`, `box`, `capsule` (2D) |
| `position` | float[] | For overlap | `[x,y,z]` or `[x,y]` |
| `size` | float or float[] | For overlap | Radius (sphere/circle) or half-extents `[x,y,z]` (box) |
| `layer_mask` | string | No | Layer name or int mask for queries |
| `start` / `end` | float[] | For linecast | Start and end points `[x,y,z]` or `[x,y]` |
| `point1` / `point2` | float[] | For shapecast capsule | Capsule endpoints (3D alternative) |
| `height` | float | For shapecast capsule | Capsule height |
| `capsule_direction` | int | For shapecast capsule | 0=X, 1=Y (default), 2=Z |
| `angle` | float | For 2D shapecasts | Rotation angle in degrees |
| `force` | float[] | For apply_force | Force vector `[x,y,z]` or `[x,y]` |
| `force_mode` | string | For apply_force | `Force`, `Impulse`, `Acceleration`, `VelocityChange` (3D); `Force`, `Impulse` (2D) |
| `force_type` | string | For apply_force | `normal` (default) or `explosion` (3D only) |
| `torque` | float[] | For apply_force | Torque `[x,y,z]` (3D) or `[z]` (2D) |
| `explosion_position` | float[] | For apply_force explosion | Explosion center `[x,y,z]` |
| `explosion_radius` | float | For apply_force explosion | Explosion sphere radius |
| `explosion_force` | float | For apply_force explosion | Explosion force magnitude |
| `upwards_modifier` | float | For apply_force explosion | Y-axis offset (default 0) |
| `steps` | int | For simulate_step | Number of steps (1–100) |
| `step_size` | float | No | Step size in seconds (default: `Time.fixedDeltaTime`) |

**Action groups:**

- **Settings:** `ping`, `get_settings`, `set_settings`
- **Collision Matrix:** `get_collision_matrix`, `set_collision_matrix`
- **Materials:** `create_physics_material`, `configure_physics_material`, `assign_physics_material`
- **Joints:** `add_joint`, `configure_joint`, `remove_joint`
- **Queries:** `raycast`, `raycast_all`, `linecast`, `shapecast`, `overlap`
- **Forces:** `apply_force`
- **Rigidbody:** `get_rigidbody`, `configure_rigidbody`
- **Validation:** `validate`
- **Simulation:** `simulate_step`

```python
# Check physics status
manage_physics(action="ping")

# Get/set gravity
manage_physics(action="get_settings", dimension="3d")
manage_physics(action="set_settings", dimension="3d", settings={"gravity": [0, -20, 0]})

# Collision matrix
manage_physics(action="get_collision_matrix")
manage_physics(action="set_collision_matrix", layer_a="Player", layer_b="Enemy", collide=False)

# Create a bouncy physics material and assign it
manage_physics(action="create_physics_material", name="Bouncy", bounciness=0.9, dynamic_friction=0.2)
manage_physics(action="assign_physics_material", target="Ball", material_path="Assets/Physics Materials/Bouncy.physicMaterial")

# Add and configure a hinge joint
manage_physics(action="add_joint", target="Door", joint_type="hinge", connected_body="DoorFrame")
manage_physics(action="configure_joint", target="Door", joint_type="hinge",
               motor={"targetVelocity": 90, "force": 100},
               limits={"min": -90, "max": 0, "bounciness": 0})

# Raycast and overlap
manage_physics(action="raycast", origin=[0, 10, 0], direction=[0, -1, 0], max_distance=50)
manage_physics(action="overlap", shape="sphere", position=[0, 0, 0], size=5.0)

# Validate scene physics setup
manage_physics(action="validate")                    # whole scene
manage_physics(action="validate", target="Player")  # single object

# Multi-hit raycast (returns all hits sorted by distance)
manage_physics(action="raycast_all", origin=[0, 10, 0], direction=[0, -1, 0])

# Linecast (point A to point B)
manage_physics(action="linecast", start=[0, 0, 0], end=[10, 0, 0])

# Shapecast (sphere/box/capsule sweep)
manage_physics(action="shapecast", shape="sphere", origin=[0, 5, 0], direction=[0, -1, 0], size=0.5)
manage_physics(action="shapecast", shape="box", origin=[0, 5, 0], direction=[0, -1, 0], size=[1, 1, 1])

# Apply force (works with simulate_step for edit-mode previewing)
manage_physics(action="apply_force", target="Ball", force=[0, 500, 0], force_mode="Impulse")
manage_physics(action="apply_force", target="Ball", torque=[0, 10, 0])

# Explosion force (3D only)
manage_physics(action="apply_force", target="Crate", force_type="explosion",
               explosion_force=1000, explosion_position=[0, 0, 0], explosion_radius=10)

# Configure rigidbody properties
manage_physics(action="configure_rigidbody", target="Player",
               properties={"mass": 80, "drag": 0.5, "useGravity": True, "collisionDetectionMode": "Continuous"})

# Step physics in edit mode
manage_physics(action="simulate_step", steps=10, step_size=0.02)
```

---

## Docs Tools

Tools for verifying Unity C# APIs. Group: `docs`.

### `unity_reflect`

Inspect Unity's live C# API via reflection. **Always use this before writing C# code that references Unity APIs** — LLM training data frequently contains incorrect, outdated, or hallucinated APIs.

Requires Unity connection.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | `search`, `get_type`, or `get_member` |
| `class_name` | string | For get_type, get_member | Fully qualified or simple C# class name |
| `member_name` | string | For get_member | Method, property, or field name to inspect |
| `query` | string | For search | Search query for type name search |
| `scope` | string | No | Assembly scope for search: `unity`, `packages`, `project`, `all` (default: `unity`) |

**Actions:**

- **`search`**: Search for types by name across loaded assemblies. Returns matching type names.
- **`get_type`**: Get a member summary (names only) for a class. Returns list of methods, properties, fields.
- **`get_member`**: Get full signature detail for one member. Returns parameter types, return type, overloads.

```python
# Search for types matching a name
unity_reflect(action="search", query="NavMesh")
unity_reflect(action="search", query="Camera", scope="all")

# Get all members of a type
unity_reflect(action="get_type", class_name="UnityEngine.AI.NavMeshAgent")

# Get detailed signature for a specific member
unity_reflect(action="get_member", class_name="Physics", member_name="Raycast")
unity_reflect(action="get_member", class_name="NavMeshAgent", member_name="SetDestination")
```

## Blender Bridge (`blender_bridge`)

Optional `asset_gen` group. Connects through Unity to an already-running BlenderMCP addon socket; configure host/port on the Generative tab.

- Inspect: `action="status"`, `scene_info`, `object_info` (`object_name`).
- Import: `action="import_model"`, `selection_only=true`, `format="glb"` or `"fbx"`; optional `object_names`, `name`, `output_folder`, `target_size`, `position`, `save_prefab`. GLB needs glTFast. FBX animation needs `animation_type`.
- Capture: `action="screenshot"` or `compare_screenshot` (`game_object`).
- Execute Blender Python: `action="run_python"`, `code="..."`.
- Diagnose addon checkout: `action="check_updates"`; `sync_addon` copies the configured checkout’s addon.py into Blender’s addons directory.

Imports use the local model pipeline. The bridge does not restore the removed paid asset-generation tools.
