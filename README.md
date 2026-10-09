> **Unity MCP Light with a native Rust server.** On Windows x86-64, macOS arm64/x86-64, and Linux x86-64, install `MCPForUnity` and click **Start Server** in HTTP Local mode. The package includes the Rust executable; no Python, Cargo, or terminal is required. See [native setup](ServerRust/README.md) and [launch verification](Verification/NATIVE_LAUNCH.md).

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/unity-mcp-light-logo-dark.png">
    <img alt="Unity MCP Light" src="docs/images/unity-mcp-light-logo.png" width="420">
  </picture>
</p>

<p align="center">
  A slimmed-down fork of <a href="https://github.com/CoplayDev/unity-mcp">MCP for Unity</a> that spends less of your AI's context window on tool definitions.
</p>

---

Based on upstream **v10.3.0**. This update keeps Light’s reduced tool set and adds the local **Blender Bridge**, Unity 6.6 compatibility fixes, improved test recovery after domain reloads, and Pi / Trae CN client support.

## Why this fork

Every MCP tool a server exposes has a name, a description and a parameter schema, and many clients send all of them to the model before you type a word. The upstream MCP for Unity 10.3.0 ships 49 tools, about **30k tokens** of definitions. That context is paid on every conversation, whether or not you ever touch ProBuilder or the Profiler.

This fork removes tools that most Unity workflows don't need and trims the definitions of the ones that remain:

| | Upstream | Light |
|---|---|---|
| Tools | 49 | **39** |
| Tool definitions (approx. tokens) | ~30.5k | **~19.5k** |
| Skill: `SKILL.md`, loaded when the skill triggers (approx. tokens) | ~3.8k | **~4.1k** |
| Skill: reference docs, read on demand (approx. tokens) | ~41.2k (4 files) | **~34.3k** (4 files) |
| Skill total (approx. tokens) | ~45.0k | **~38.5k** |

The skill drops the ProBuilder guide and every section about removed tools, and adds a short guide to `inspect_prefab`. Token counts are characters ÷ 4.

Everything else — scenes, GameObjects, components, scripts, assets, prefabs, materials, shaders, procedural textures, camera, graphics, physics, builds, tests, UI Toolkit, API reflection — works the same as upstream.

## What was added

`blender_bridge` connects Unity directly to a running BlenderMCP addon. It inspects Blender objects, imports selections or scenes, captures screenshots, and provides addon diagnostics. It belongs to the optional `asset_gen` group and has settings on the **Generative** tab. Blender must already be running with its addon connected.

**`inspect_prefab`**: a read-only prefab inspector (about 460 tokens of definition) that answers in compact, indented text under a character budget instead of JSON. It never opens a Prefab Stage, marks anything dirty or saves.

| Mode | Answers |
|---|---|
| `tree` | What is in this prefab? Hierarchy to `depth` 2 with short component names; identical siblings collapse (`Wheel_FL..RR x4`); nested prefabs, inactive objects and missing scripts are marked. `root=` and `filter=` narrow it. |
| `node` | What are this object's settings? Only fields that differ from a freshly added component, with references resolved to paths and UnityEvent listeners spelled out. |
| `refs` | What is wired to what? Every object reference and persistent listener, by object; asset links from built-in components grouped by asset. |
| `overrides` | What does this variant or nested prefab change? Base chain, `old -> new` values, added and removed components and objects. |
| `usages` | Which prefabs and scenes use script X or asset Y, including through nested prefabs? |
| `problems` | Missing scripts, references to deleted objects, broken nested prefab links, broken listeners and material/submesh mismatches, for a prefab or a folder. |

The normal path is `manage_prefabs get_info` → `inspect_prefab tree` → `node` or `refs`.

## Animation restored

`manage_animation` is available again in native Rust and the Python fallback: Animator control, controller states/transitions/layers/blend trees, AnimationClip curves/events and 15 presets. See the [animation skill guide](unity-mcp-skill/references/animation-guide.md) and [restoration verification](Verification/ANIMATION_RESTORATION.md). HTTP clients enable the `animation` group; stdio enables it initially.

## What was removed

| Tool | What it did |
|---|---|
| `generate_image`, `generate_model`, `generate_audio` | AI asset generation through third-party providers |
| `import_model` | Sketchfab marketplace search and import |
| `manage_probuilder` | ProBuilder mesh modeling |
| `manage_profiler` | Profiler sessions, counters, memory snapshots, Frame Debugger |
| `manage_vfx` | VFX Graph, particles, line and trail renderers |
| `manage_packages` | Package Manager install/remove/search |
| `unity_docs` | Fetching docs from docs.unity3d.com |
| `debug_request_context` | Server debugging helper |
| `manage_script_capabilities` | Listed the supported script-edit operations |

`import_model_file` (local `.fbx`/`.obj`/`.glb`/`.gltf`/`.zip`) is **kept**.

Each removed tool is gone from the Python server, the Unity handlers, the CLI, the tests and the bundled `unity-mcp-skill` docs, so nothing still describes a tool that no longer exists.

Other changes:
- **Smaller schemas.** Optional parameters no longer carry Pydantic's null padding. `Optional[str] = None` used to be advertised as `{"anyOf": [{"type": "string"}, {"type": "null"}], "default": null}` and is now just `{"type": "string"}`. That alone saves about 3.5k tokens. Explicit `null` arguments are still accepted.
- `read_console` returns **errors only** by default (it used to return errors, warnings and logs). Projects with many warnings no longer flood the context; pass `types=["error", "warning"]` when you want warnings.
- `get_test_job` now recommends a `wait_timeout` of 15–30 seconds instead of 30–60.

## Install the Python fallback

**Requirements:** Unity 2021.3 LTS – 6.x, Python 3.10+ and [`uv`](https://docs.astral.sh/uv/). Works with any MCP client (Claude Code, Claude Desktop, Cursor, VS Code, Windsurf, Cline, Gemini CLI and others).

1. **Add the Unity package.** In Unity, open **Window → Package Manager → + → Add package from git URL** and enter:

   ```
   https://github.com/Noongits/unity-light-mcp-rust.git?path=/MCPForUnity#main
   ```

   The package keeps the upstream package id (`com.coplaydev.unity-mcp`), so remove the upstream package first if you have it installed.

2. **Configure your client.** Open **Window → Unity MCP Light**, and on the **Connect** tab choose **Configure All Detected Clients** (or configure one client), then restart your MCP client.

   Clients are registered under the server name **`unity-mcp-light`** (for example `claude mcp add ... unity-mcp-light` in Claude Code, `[mcp_servers.unity-mcp-light]` in Codex). The Python server is fetched from this repository's `Server/` folder on `main`, not from PyPI, so you get this fork's trimmed tool set without extra setup.

3. **Install the skill (optional).** **Install Skills** on the Connect tab copies the agent skill to `~/.claude/skills/unity-mcp-light` (Claude Code) or `~/.codex/skills/unity-mcp-light` (Codex).

4. **Try it.** Ask: *"Create a cube at the origin and add a Rigidbody."*

To run the server from a local clone instead, set **Advanced → Server Source** to your clone's `Server` folder.

## Keeping context small

- **Tool groups.** Tools are grouped (`core`, `vfx`, `ui`, `testing`, `docs`, `scripting_ext`, `asset_gen`). Only `core` is on by default over HTTP; turn the others on when you need them with `manage_tools`, or on the **Tools** tab in Unity. The `vfx` group now holds only `manage_shader` and `manage_texture`.
- **Clients with tool search** (such as Claude Code) load tool definitions only when needed, so the savings here matter most for clients that load every definition up front.
- **The skill.** `unity-mcp-skill/` gives agents usage guidance. It is large; install it only if your agent benefits from it.

## Development

The server lives in `Server/`, and the Unity package lives in `MCPForUnity/`.

```bash
cd Server
uv run --extra dev pytest -q
```

To measure how many tokens the tool definitions cost, list the tools through FastMCP and count the characters of each serialized definition (roughly 4 characters per token).

## Credits

This is a fork of [MCP for Unity](https://github.com/CoplayDev/unity-mcp) by [Coplay](https://github.com/CoplayDev) and its contributors. All the real work is theirs; this fork only trims it. For the full feature set, documentation and community, use the upstream project.

If MCP for Unity helped your research, please cite the original work:

```bibtex
@inproceedings{wu2025mcpunity,
  author    = {Wu, Shutong and Barnett, Justin P.},
  title     = {{MCP-Unity}: {Protocol-Driven} Framework for Interactive {3D} Authoring},
  year      = {2025},
  isbn      = {9798400721366},
  publisher = {Association for Computing Machinery},
  address   = {New York, NY, USA},
  url       = {https://doi.org/10.1145/3757376.3771417},
  doi       = {10.1145/3757376.3771417},
  series    = {SA Technical Communications '25}
}
```

Not affiliated with Unity Technologies.

**License:** MIT — see [LICENSE](LICENSE).
