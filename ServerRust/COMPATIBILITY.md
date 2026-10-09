# Compatibility and migration scope

## Reference and interpretation

Reference commit: **`b40c07a1c3dfb98f45a05c4e3233d8de138dc6a6`** in the parent Unity MCP Light repository.

Rust embeds the reference's **39 built-in MCP tool definitions** and **25 resource definitions: 19 fixed URIs and six templates**. Names, descriptions, input schemas, annotations, metadata, and resource templates are captured in `contracts/tools.json` and `contracts/resources.json`. Internal grouping/Unity-target fields are removed from public tool definitions. Group visibility means an initial `tools/list` need not contain all 39 tools. The `custom_tools` resource listing is conditional on project-scoped configuration. Editor custom tools may add definitions beyond the 39 built-ins.

These are API snapshots, not a blanket behavioral-equivalence claim. Native implementations are in `src/protocol.rs`, `src/tools.rs`, `src/scripts.rs`, `src/resources.rs`, `src/scanner.rs`, `src/focus.rs` and `src/transport.rs`. Unit tests, Python-captured fixtures and black-box tests of the executable provide complementary coverage. They do not replace live Unity verification.

## Tool implementation matrix

Every name below has a built-in definition and explicit routing. Where C# is the substantive implementation, Rust preserves Python-side normalization and dispatches the C# command rather than reimplementing Unity Editor APIs.

| Area | Tools | Implemented server behavior |
| --- | --- | --- |
| Scripts (8) | `manage_script`, `create_script`, `delete_script`, `validate_script`, `get_sha`, `apply_text_edits`, `script_apply_edits`, `find_in_file` | URI/locator normalization, Base64, diagnostics, SHA preconditions, LSP/index conversion, overlap checks, structured/mixed routing, C#-aware anchors, regex search, reload recovery and read-only previews |
| Session controls (2) | `manage_tools`, `set_active_instance` | Session-local groups, registration sync, tool-list notifications, ambiguous-instance rejection and per-call overrides |
| Scene and Editor (8) | `manage_scene`, `manage_gameobject`, `manage_components`, `find_gameobjects`, `manage_prefabs`, `inspect_prefab`, `manage_editor`, `refresh_unity` | Wire names, defaults, typed/vector/object coercion, validation, readiness preflight, prefab text extraction, refresh/reload recovery and response shaping |
| Assets and UI (6) | `manage_asset`, `manage_material`, `manage_shader`, `manage_texture`, `manage_ui`, `manage_scriptable_object` | Aliases, JSON payload parsing, colors/textures, content encoding, action validation, UI readiness and C# dispatch |
| Rendering and physics (3) | `manage_camera`, `manage_graphics`, `manage_physics` | Action-specific normalization and dispatch; camera screenshots preserve native MCP image content |
| Testing (2) | `run_tests`, `get_test_job` | Filters/defaults, readiness checks, bounded job polling and gated focus nudges for stalled jobs |
| Build, execution and utilities (5) | `manage_build`, `execute_code`, `batch_execute`, `execute_menu_item`, `read_console` | Build/action validation, execution options, Unity batch limits, rejection of per-command instance routing within batches, menu wire names and console stacktrace filtering |
| Custom dispatch (1) | `execute_custom_tool` | Registration lookup, input validation, selected-instance routing and metadata-sensitive dispatch |
| Reflection (1) | `unity_reflect` | Action validation, defaults and Unity reflection dispatch |
| Asset-generation bridge (2) | `blender_bridge`, `import_model_file` | Existing C# command mappings and Blender timeout budgets; requires corresponding Unity/Blender-side functionality |

For example, advertising `manage_scene` alone would not prove that `build_index` becomes `buildIndex` or that an Editor-busy response is handled. Those transformations and preflight behaviors live in the substantive handlers and have forwarding/regression tests.

### Script details

- Explicit text coordinates remain 1-based. Zero/negative explicit coordinates are clamped with warnings unless strict mode rejects them.
- LSP input ranges are 0-based and converted. Index ranges use Unicode character positions rather than UTF-8 byte offsets.
- Multiple text spans default to atomic application. Intersecting nonzero ranges are rejected before submission; zero-width insertions do not trigger that check.
- `apply_text_edits` forwards a supplied SHA without implicitly fetching one.
- `script_apply_edits` hashes the read buffer for converted text edits. Method/class/anchor operations use Unity's structured editor.
- Mixed batches apply text before structured operations. This is not a transaction across both stages: a later structured failure can leave text edits applied.
- A nonexecuted reload rejection can be retried. Uncertain disconnects are verified rather than blindly resending mutations.
- Reference locator precedence is retained, including the surprising treatment of bare `name="Foo.cs"` with a separate directory. Prefer `name="Foo"`, `path="Assets/Scripts"`, or a full Assets path for structured edits.
- Reference text-only `prepend`/`append` routing rejects those operations; mixed batches support them. Use explicit ranges for reliable pure-text insertion.
- Text-only regex conversion preserves the reference's last/best-match behavior and text-field precedence. It is not a global replacement engine.

## Resource implementation matrix

| Area | Fixed URIs or templates | Implementation |
| --- | --- | --- |
| Discovery (2) | `mcpforunity://instances`, `mcpforunity://custom-tools` | Transport instance list and selected-project registration metadata |
| Static reference (3) | `mcpforunity://tool-groups`, `mcpforunity://scene/gameobject-api`, `mcpforunity://prefab-api` | Embedded reference payloads; `manage_tools list_groups` supplies live session group state |
| Editor (5) | `mcpforunity://editor/active-tool`, `.../prefab-stage`, `.../selection`, `.../state`, `.../windows` | C# reads, response defaults, state identity/staleness/readiness enrichment and local external-change detection |
| Project (3) | `mcpforunity://project/info`, `.../tags`, `.../layers` | C# project reads and response shaping |
| Menu (1) | `mcpforunity://menu-items` | Menu listing with refresh/search defaults |
| Rendering (4) | `mcpforunity://scene/cameras`, `.../scene/volumes`, `.../pipeline/renderer-features`, `.../rendering/stats` | C# rendering reads |
| Tests (2) | `mcpforunity://tests`, `mcpforunity://tests/{mode}` | Enumeration; mode constrained to `EditMode` or `PlayMode` |
| GameObjects (3 templates) | `mcpforunity://scene/gameobject/{instance_id}`, `.../{instance_id}/components`, `.../{instance_id}/component/{component_name}` | Numeric IDs, component-name decoding, pagination and property flags |
| Prefabs (2 templates) | `mcpforunity://prefab/{encoded_path}`, `.../{encoded_path}/hierarchy` | Decoded asset path and `get_info`/`get_hierarchy` dispatch |

The local scanner is a bounded modification-time heuristic, not a watcher or complete journal. It samples Assets, ProjectSettings, Packages and discoverable local package directories. A minimum interval and file-count limit bound the work. It can miss changes that do not increase the newest observed modification time, including some deletions. Remote sessions disable it so an Editor cannot direct scanning of the server's filesystem.

## Transport and MCP matrix

| Feature | Status and limit |
| --- | --- |
| Native stdio MCP | Newline-delimited JSON-RPC, stderr diagnostics, concurrent requests and cancellation |
| Legacy Unity TCP | Status/port-file discovery, framing handshake, keepalives, targeting, bounded timeouts and nonexecuted-reload retries |
| HTTP MCP | `/mcp` and `/mcp/`; POST JSON, GET SSE notifications, DELETE session cleanup; session header required after initialization |
| Unity WebSocket | `/hub/plugin`; registration, heartbeat, command correlation, reconnect cleanup and tool registration |
| MCP versions | `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`; fallback `2025-06-18` |
| Tools/resources | Captured definitions, schema validation/coercion, reads and tool-list notifications |
| MCP extras | Empty prompts/completions; logging-level calls acknowledged. Not a replacement for every FastMCP feature |
| Images | Camera screenshot results retain MCP image content |
| Local REST | Health, instances, custom tools and raw command endpoints |
| Remote auth | API-key validation/cache, user hubs and session ownership; local raw command/discovery REST disabled |
| TLS | No native TLS listener; terminate HTTPS/WSS separately |
| Auto-configuration | Not ported; unchanged C# helpers still generate Python `uv`/`uvx` config |
| CLI aliases | Click hierarchy not ported; native `call`, `instances`, `status` available |
| Telemetry | Disabled and unported; status reports false and ping does not send telemetry |
| Focus nudges | Environment-gated stalled-test-job handling with bounded attempts; OS interactions tested with mocks |
| macOS / live Unity | Actual OS focus behavior and live Editor execution remain unverified |

HTTP state is in-memory. Idle sessions expire after an hour when no jobs are running. Restarting requires reinitialization and Editor reconnection. SSE has no durable event replay. MCP bodies/stdio lines are limited to 16 MiB and TCP frames to 64 MiB; jobs and sessions also have limits. These are native implementation details, not promises of byte-for-byte FastMCP lifecycle parity.

## Intentional safety differences

1. **Preview never mutates.** `script_apply_edits` returns planned edits for `options.preview=true`. Python's early routing can bypass its intended preview branch. Rust does not reproduce that write behavior; its preview is a plan, not a unified diff.
2. **Create paths stay in Assets.** Paths normalizing outside Assets are rejected, including traversal the reference's initial directory check can miss.
3. **Mixed verification uses the intermediate SHA.** After text edits succeed, a fresh SHA precedes the structured edit. A failed structured step cannot be verified merely because the earlier text step changed the original SHA.
4. **No blind replay after an uncertain mutation send.** Transport retries require an explicit nonexecuted reload indication; adapters use read/SHA verification where supported.
5. **Remote isolation is explicit.** Selected instances and user-bound sessions are required, raw local REST is unavailable, and local scanning is disabled.
6. **Origin checks.** Requests with mismatched Origins are rejected. This is defense in depth, not authentication for local mode.

The opt-in reload sentinel runs synchronously after inspecting the selected Editor's state, rather than through Python's background task/local status-file lookup.

## Remaining qualifications

- `fancy-regex` supports typical lookaround/backreference patterns but is not Python `re`. Exotic syntax, Unicode casing, errors and backtracking limits can differ.
- Python exception names, Pydantic coercion edge cases and all error strings are not universally reproduced. Common payload transformations have golden comparisons.
- Unity action support depends on installed C#, Unity version and optional packages. Adapter tests do not prove real compilation, screenshots, builds, test execution or Blender integration.
- Resource snapshots preserve reference descriptions/shapes; runtime enrichment is native logic. Snapshots alone do not establish working behavior.
- Python is absent from runtime but remains optional development infrastructure for reference capture and black-box tests.
- The Python suite does not automatically test Rust. Run native tests and explicit `ServerRust/tests/contract_*.py` harnesses against the built executable.
- Only environment options documented in [README.md](README.md) and read by source apply. Python logging configuration, telemetry and automatic launcher behavior are not silently reproduced.
- `--unity-instance-token` is accepted to remain visible in the process command line for C# process identification. It does not select a target or authenticate a caller; use native selection or `X-API-Key`.

## Verify before replacing a working installation

1. Build the locked release; run native tests.
2. Run explicit stdio/HTTP harnesses against the binary. They use fake peers, not Unity projects.
3. In a disposable real project, verify discovery, resource/console reads and tool-group changes.
4. Verify creation/edit/deletion with SHA protection and an actual compilation/domain reload.
5. Exercise screenshots, prefab edits, test jobs and needed optional groups.
6. On macOS/Windows, verify launching, discovery paths, unfocused Editor behavior and reconnection.
7. For remote hosting, test invalid/revoked keys, isolation, cache behavior, proxy Origins and HTTPS/WSS before exposure.
8. Keep the Python configuration for rollback; avoid competing legacy stdio clients.

Recorded test/build results are in [VALIDATION.md](VALIDATION.md). A total test count is not a substitute for live-editor compatibility evidence.


## Post-port audit safeguards

See [AUDIT.md](AUDIT.md) for verified timeline defects and fixes. Disconnect
recovery no longer treats an arbitrary changed SHA or an unavailable read as
proof that a mutation succeeded. Mixed-phase editing reports partial application.
Caller-supplied SHA preconditions are checked against the snapshot used for
coordinate normalization. C# interpolation nesting above 64 levels returns
`conversion_failed` before mutation. Remote requests cannot activate host windows.
These deliberate safety differences are covered by regression tests.
