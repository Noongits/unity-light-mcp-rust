---
title: inspect_prefab
sidebar_label: inspect_prefab
description: "Read-only prefab inspector with compact text output; prefer it over manage_prefabs get_hierarchy."
---

# `inspect_prefab`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.inspect_prefab`

## Description

Read-only prefab inspector with compact text output; prefer it over manage_prefabs get_hierarchy. Usual path: manage_prefabs get_info -> tree -> node or refs. tree: hierarchy (depth=2, root=subtree, filter=name/component). node: one object's non-default fields and listeners (path, component, all_fields). refs: object references and UnityEvent listeners, by object. overrides: variant and nested-prefab changes. usages: prefabs/scenes using script=Class or asset=path. problems: missing scripts/refs, broken prefab links, event and material issues (prefab or folder).

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `mode` | `Literal['tree', 'node', 'refs', 'overrides', 'usages', 'problems']` | — | What to show. |
| `prefab_path` | `str \| None` | — | Assets/...prefab, a folder (problems), or scene:Path/To/Object. |
| `path` | `str \| None` | — | node: object path in the prefab. |
| `component` | `str \| None` | — | node/refs: component type. |
| `root` | `str \| None` | — | tree/refs: only this subtree. |
| `depth` | `int \| None` | — | tree: depth, default 2. |
| `filter` | `str \| None` | — | tree: name or component substring. |
| `all_fields` | `bool \| None` | — | node: include default values. |
| `script` | `str \| None` | — | usages: class name. |
| `asset` | `str \| None` | — | usages: asset path. |
| `max_chars` | `int \| None` | — | Output budget, default 6000. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

