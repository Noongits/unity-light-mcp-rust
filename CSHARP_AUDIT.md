# C# ownership and timeline audit

This records the first focused pass. The later all-file review and its limits are
in `CSHARP_FULL_REVIEW.md` and `CSHARP_REVIEW_COVERAGE.md`.

Baseline: `b40c07a1c3dfb98f45a05c4e3233d8de138dc6a6` of
`Talhasarac/unity-mcp-light`. Changes are included in this full source package;
they have not been pushed to the remote fork.

The review covered the UI renderer, native texture/screenshot helpers, Blender
image composition, transport dispatcher, connection-state presentation, and
selected callback/reload paths. It traced normal completion plus cancellation,
failure, repeat, resize, destruction, and delayed callback orders. It is a
focused code review, not a guarantee that all C# bugs have been found.

## 1. UXML RenderTexture lifetime

Failure timeline:
1. Edit-mode `render_ui` receives a UXML path with no existing PanelSettings.
2. The handler creates temporary settings and caches a GPU RenderTexture under
   their instance ID.
3. Cleanup deletes the settings but leaves the texture cached and persisted.
4. Repeated calls create new identities and accumulate textures until reload/exit.

Fix: newly temporary settings and their RenderTexture are request-owned and
disposed in cleanup; neither is persisted or inserted into the persistent cache.
When settings already exist, the previous cache/retry behavior remains supported.
Capture records track ownership, restore the user's original target, and release
only generated resources. Resizing replaces the owned texture; deleted owners
are pruned. Generated asset names are unique to avoid overwriting unrelated files.

Additional same-method repairs cover screenshot Texture2D cleanup on errors,
restoring `RenderTexture.active`, play-mode encoding failures, and unconsumed
captures on reload/quit. A generation check prevents late callbacks from reviving
cleared capture state.

Checked orders: normal first/repeated capture; write failure; resize; user target
change; owner deletion; repeated cleanup; cleanup before asynchronous completion.
Newly temporary synchronous rendering may still produce a blank image: this
patch does not implement a new cross-frame UXML renderer. Persistent UIDocument
target rendering retains its multi-frame path.

Source: `MCPForUnity/Editor/Tools/ManageUI.cs`.
Regressions: existing `ManageUITests.cs`, including ownership, reuse, resize,
failure, deleted owner, unique asset names, and pending capture cleanup.

## 2. Procedural/imported working textures

Failure timeline:
1. Create/modify allocates a temporary Texture2D.
2. Malformed color/palette/pixel data, file I/O, or import configuration throws.
3. The error response bypasses success-path destruction.

Fix: each operation-owned working texture is released in `finally`; imported
asset textures remain borrowed and are never destroyed by this cleanup. Failed
image decoding is rejected explicitly.

Source: `MCPForUnity/Editor/Tools/ManageTexture.cs`.
Orders checked: success, malformed data, early validation/decode failure, and
exception after allocation.

## 3. Blender image composition and resampling

Failure timeline:
1. The left PNG loads successfully.
2. Loading the right PNG allocates another texture and then fails before the
   compositor's original cleanup scope.
3. The inputs lose their cleanup owner. Resampling had a similar destination
   allocation-before-failure gap.

Fix: both loads are inside the compositor's ownership scope; helper outputs are
destroyed on failure and transferred to callers only on success.

Source: `MCPForUnity/Editor/Tools/Blender/BlenderBridgeTool.cs`.
Orders checked: normal composition, missing first/second input, write failure,
different image sizes, and unreadable source sampling.

## 4. Screenshot batches, contact sheets, and readback outputs

Failure timeline:
1. Earlier camera angles produce standalone textures.
2. A later capture fails before handing the batch to the composer.
3. Camera-only cleanup leaves the earlier tiles alive. Composer setup could also
   fail before its own cleanup scope.

Fix: callers retain ownership until explicit transfer; the composer owns inputs
from entry, including setup failures. Temporary camera setup is inside cleanup
scopes. Downscale/editor-window capture destinations are disposed if readback,
Apply, or vertical flipping fails before returning ownership.

Sources: `ManageScene.cs`, `Runtime/Helpers/ScreenshotUtility.cs`, and
`Editor/Helpers/EditorWindowScreenshotUtility.cs`.
Orders checked: success, failure before the first tile, later-angle failure,
composer setup/copy failure, and caller cleanup after successful transfer.

Regressions for sections 2–4 are in `NativeTextureLifetimeTests.cs`. Tests clean
up only objects they explicitly own; resource snapshots are observation-only.

## 5. Dispatcher cancellation and completed-command retention

Failure timeline:
1. Cancellation registration runs before publishing the command in `Pending`.
2. Cancellation fires and cannot find the entry.
3. The entry is published afterward and waits for another editor pump despite
   already being cancelled.

Fix: reject pre-cancelled work and recheck after publication. Async completion
now removes managed queue state directly under its existing synchronization,
rather than retaining it until a future editor callback. Registration disposal
remains outside the queue lock.

Source: `Editor/Services/Transport/TransportCommandDispatcher.cs`.
Regressions: `TransportCommandDispatcherTests.cs`; cancellation before enqueue,
during publication, after publication, delayed pump, and async completion.

## 6. Stale connection-state presentation

Failure timeline:
1. The manager caches a connected snapshot.
2. The WebSocket independently disconnects/reconnects or receives a new session.
3. UI consumers continue reading the old connected/session/error state.

Fix: HTTP state reads use the client's authoritative snapshot. Socket closure
publishes a genuinely disconnected state rather than preserving connected=true.

Sources: `TransportManager.cs` and `Transports/WebSocketTransportClient.cs`.
Regressions: `TransportManagerTests.cs` and `WebSocketTransportClientTests.cs`.
Orders checked: normal connection, autonomous disconnect, reconnect/new session,
late registration, status before creation, and stop.

## 7. Stop before a queued reconnect callback

Failure timeline:
1. Socket closure schedules asynchronous reconnect work.
2. Stop disposes and clears the shared lifecycle source before that work runs.
3. The old delegate dereferences the now-null/disposed source, or touches
   unrelated connection tasks after its lifecycle ended.

Fix: capture the lifecycle token before teardown/scheduling, tolerate a source
already disposed by a concurrent stop, and return from cancelled callbacks before
shared connection-task cleanup. A regression exercises ForceStop before queued
work and verifies unrelated receive work remains untouched. This is a narrow
repair, not a full redesign of overlapping start/stop generations.

## Verification and limitations

- Changed/new C# files were parsed with the tree-sitter C# grammar, with no syntax
  error nodes found. This does **not** establish semantic compilation or Unity
  API compatibility.
- `git diff --check` passed.
- Repository Unity-version checks executed but skipped every configured Editor:
  no Unity installation is available in the build environment. Therefore new
  C# tests are written/reviewed, **not compiled or executed**.
- Graphics cases require a graphics device. No-existing-settings integration
  cases may skip when unrelated PanelSettings exist; use a clean test project.
- Partial camera-batch and native readback exceptions were source-reviewed, not
  fault-injected in a live Editor. No GPU-memory profiler measurements are claimed.
- Wider overlapping WebSocket start/stop behavior and borrowed generated-material
  caches still merit live integration testing. Potential ownership changes were
  not guessed: destroying materials borrowed by renderers could break user scenes.

Run the included Unity EditMode fixtures and smoke-test actual UXML rendering
before replacing a working installation. `FULL_PACKAGE.md` explains installation
and the separate Rust validation record.
