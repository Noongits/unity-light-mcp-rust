# Animation restoration — 9 October 2026

Restored the animation implementation from [CoplayDev/unity-mcp, main commit aa5fc638](https://github.com/CoplayDev/unity-mcp/tree/aa5fc638d623f56178d50329d9ad8c541a57fe66/MCPForUnity/Editor/Tools/Animation). The import retains upstream Unity GUID metadata and the existing MIT attribution. Other removed tools remain excluded.

The Unity handler, Python fallback/CLI, native Rust schema and forwarding adapter now expose `manage_animation`: 7 Animator actions, 12 controller actions, and 9 clip actions. The optional `animation` group is initially enabled in stdio and requires activation in HTTP. The server now has 39 built-in tools.

The bundled skill has an [animation guide](../unity-mcp-skill/references/animation-guide.md) and an animation tool reference. The Unity skill installer defaults to this repository's public `unity-mcp-skill` directory. Existing saved repository preferences are retained; if the installer already shows the older fork, select `https://github.com/Noongits/unity-light-mcp-rust` and branch `main`.

## Adjustments to the upstream import

- Save only the affected clip/controller, avoiding global `AssetDatabase.SaveAssets` in animation handlers. Scene-only controller assignment does not save unrelated assets.
- Reject occupied creation paths even when the existing asset has another type.
- Return an ordinary tool error for malformed properties JSON.
- Preserve the controller's main object when adopting blend trees and register the new tree with Undo.
- Keep object/JSON-string properties forwarding consistent across Python and Rust; reject unknown animation actions before dispatch.
- Update the Light tool-set guard to require animation while retaining the remaining exclusions.
- Adapt the HTTP test harness to MCP SDK 1.x/2.x stream tuples and serialized initialization names.

## Executed verification

macOS arm64, Unity 6000.3.23f1 with graphics enabled; Rust 1.96.0; MCP SDK 2.3.0 for live and HTTP checks. Compilation ran before discovery or test execution in a newly created disposable project. The user's open Unity project was not operated on.

| Check | Result |
| --- | --- |
| Unity compilation | Exit 0; no C# compiler errors. |
| EditMode discovery | 1,706 cases; all 80 animation cases runnable, none Explicit. |
| Full EditMode regression suite | 1,630 passed, 0 failed, 76 skipped. |
| Animation subset within that full run | 80 passed, 0 failed, 0 skipped. |
| PlayMode regression suite | 5 passed, 0 failed, 0 skipped. |
| Rust native tests | 135 library + 8 binary tests passed. |
| Rust stdio contracts | 9 groups passed, zero skips; includes four actual Python animation forwarding fixtures. |
| Rust HTTP contracts | 8 groups passed, zero skips; includes animation activation isolation and forwarding. |
| Rust formatting, clippy, release build | Passed; clippy uses `-D warnings`, builds/tests use `--locked`. |
| Python fallback and CLI suite | 1,294 passed, 18 skipped; includes 54 upstream animation tests. |
| Website production build | Passed after a fresh lockfile install. |
| Live native Rust → Unity | Created clip/curves/controller/parameter/state/blend tree, assigned controller, entered Play Mode, played state and set speed to 1.5; observed advancing normalized state time; stopped Play Mode and closed the owned editor normally. |

The 76 EditMode skips comprise the original 74 plus two optional Roslyn example tests, because this new disposable project does not install that example. No skipped case is credited as a pass. Every skipped case and reason is listed in `animation-evidence/skips.json`.

The new authoring regression saves/reimports the clip and controller, checks the clip remains non-legacy, its curve/event persist, controller parameters/layers persist, and a blend tree retains native file membership and a distinct local ID. It checks an unrelated dirty texture after every request. Three collision cases verify that clip/preset/controller creation retains an occupied asset of another type. A malformed JSON case verifies an error response.

The first full run had one failure in the new regression's `IsSubAsset` assertion. Investigation showed the hidden BlendTree was already persistent in the controller file and its main controller was retained; Unity reported `IsSubAsset=false` for that hidden object. The final assertion verifies save/reimport, file membership, GUID and local file IDs directly. This initial assertion failure is not presented as an application persistence bug. Earlier failed results are retained in `animation-evidence/editmode-initial.xml`.

Raw machine logs stay local. Published structured results replace the local user's home path with `/Users/reviewer/`. Older review reports describe their original 38-tool verification; this report records the subsequent animation restoration. Existing rollback/platform limits still apply; this change is verified on the stated Unity/macOS setup.

## Using the restored tool

Update the Unity package from this repository and rebuild the native server:

```sh
cargo build --release --locked --manifest-path ServerRust/Cargo.toml
```

Use the rebuilt executable in the MCP client configuration. HTTP clients activate `animation` with `manage_tools(action="activate", group="animation")`. Read the animation guide for clip/controller workflows. The automatic Unity client configuration still selects the retained Python fallback.
