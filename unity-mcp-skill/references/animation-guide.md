# Animation authoring and control

`manage_animation` restores the animation tool from [CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp/tree/aa5fc638d623f56178d50329d9ad8c541a57fe66/MCPForUnity/Editor/Tools/Animation). It runs through the native Rust server or the Python fallback. It needs Unity's Animator/Animation APIs, without an additional animation package.

Read editor state and find the target before editing. Prefer an instance ID or scene path when names are ambiguous. Inspect existing clips and controllers before changing them; shared controller defaults and clip curves affect every object using those assets.

HTTP sessions must enable the optional group:

```python
manage_tools(action="activate", group="animation")
```

Stdio initially enables all groups, subject to Unity's tool toggles. Enable `manage_animation` in the Unity Tools panel if it was disabled.

## Create a clip and AnimatorController

Run dependent steps in order and check each response's `success`:

```python
manage_animation(action="clip_create", clip_path="Assets/Animations/Move.anim",
                 properties={"length": 1, "loop": True})
manage_animation(action="clip_set_curve", clip_path="Assets/Animations/Move.anim",
                 properties={"type": "Transform", "relative_path": "",
                             "property_path": "m_LocalPosition.x",
                             "keys": [{"time": 0, "value": 0}, {"time": 1, "value": 2}]})
manage_animation(action="controller_create", controller_path="Assets/Animators/Actor.controller")
manage_animation(action="controller_add_state", controller_path="Assets/Animators/Actor.controller",
                 clip_path="Assets/Animations/Move.anim",
                 properties={"state_name": "Move", "is_default": True})
manage_animation(action="controller_assign", target="Actor", search_method="by_name",
                 controller_path="Assets/Animators/Actor.controller")
manage_animation(action="controller_get_info", controller_path="Assets/Animators/Actor.controller")
manage_animation(action="clip_get_info", clip_path="Assets/Animations/Move.anim")
```

Asset paths must be under `Assets/`. Creation rejects an occupied path. `properties` accepts an object or JSON string; snake_case keys are normalized, including nested objects. Top-level parameters take precedence over duplicate properties.

## Parameters, transitions, layers and blend trees

Use `controller_add_parameter` with `parameter_name`, `parameter_type` (`float`, `int`, `bool`, `trigger`) and `default_value`. Create both states before `controller_add_transition`, supplying `from_state`, `to_state`, `duration`, `has_exit_time`, `exit_time` and optional `conditions` objects (`parameter`, `mode`, `threshold`). `AnyState` is accepted as the source.

`controller_add_layer`, `controller_remove_layer` and `controller_set_layer_weight` operate on `layer_name` or `layer_index`; layer creation supports `blending_mode` and `weight`. The base layer cannot be removed.

Create `controller_create_blend_tree_1d` with `state_name` and `blend_parameter`, or `controller_create_blend_tree_2d` with `state_name`, `blend_parameter_x`, `blend_parameter_y` and `blend_type`. Add a clip with `controller_add_blend_tree_child`, `clip_path`, `state_name` and `threshold` (1D) or `position: [x, y]` (2D). Keep clips non-legacy when using Mecanim and blend trees.

## Curves, events and presets

`clip_add_curve` appends keys; `clip_set_curve` replaces a binding. Use serialized property names, verified against the target component. `clip_set_vector_curve` sets three component curves for a vector property. Curves accept key objects or `[time, value]` pairs.

`clip_add_event` accepts `time`, `function_name` and optional `string_parameter`, `float_parameter`, `int_parameter`; `clip_remove_event` uses `event_index`. A matching MonoBehaviour receiver must exist on the animated object for runtime events.

`clip_create_preset` accepts `preset`, `duration`, `amplitude`, `loop` and optional `target` or `offset`. Presets are `bounce`, `rotate`, `pulse`, `fade`, `shake`, `hover`, `spin`, `sway`, `bob`, `wiggle`, `blink`, `slide_in`, `elastic`, `grow`, `shrink`. Position presets can use the target's local position to avoid jumping to the origin.

`clip_assign` uses the legacy Animation component, converting the shared clip to legacy and warning about Mecanim incompatibility. For Animator objects, use `controller_add_state` and `controller_assign` instead.

## Playback and verification

In Play Mode use `animator_play` (`state_name`, `layer`) or `animator_crossfade` (`state_name`, `duration`, `layer`). `animator_set_parameter` accepts `parameter_name`, `parameter_type`, `value`. In Edit Mode it changes the assigned controller's parameter defaults; triggers have no persistent default. `animator_set_speed` uses `speed`; `animator_set_enabled` uses `enabled`.

`animator_get_info` and `animator_get_parameter` inspect the resolved Animator. An Animator on the target wins; a single inactive or active descendant can resolve automatically; multiple descendant Animators return an ambiguity error. Check the returned resolved object, console errors, and a screenshot after previewing playback.
