# Native Rust launch from Unity

Unity package 10.3.1 makes **Start Server** sufficient for HTTP Local: it prepares
and starts the packaged Rust executable, then connects the Unity bridge. The user
needs no terminal, Python, uv, Git, Cargo, or separate Rust installation to run it.

The original failure came from the Unity launcher invoking `uvx` with an old
Python-server repository URL. The default launcher now uses `NativeServerRuntime`,
which selects the editor OS/architecture, verifies the bundled SHA-256, and copies
the binary into a per-user cache keyed by its checksum. Cached corruption is repaired;
invalid package content is rejected. Package-cache files are never made executable
or overwritten. Multiple native versions can coexist without replacing a running file.
The native process launches directly with redirected output and no console window.
A console-signal registration failure no longer initiates HTTP shutdown: headless
processes keep serving until their owner stops them. Two injected-signal regressions
verify both the unavailable-signal and received-signal paths.

JSON, TOML, OpenCode, OpenClaw, and Claude Code configuration builders now use the
native executable for stdio and the normal `/mcp` endpoint for HTTP. HTTP configuration
no longer requires uv. Python and uv are optional fallback dependencies. The retained
Python source defaults to the current public repository when used deliberately.

Native process detection recognizes `unity-mcp-light`. Windows process inspection uses
PowerShell CIM instead of removed `wmic`, including launch-token validation for Stop.
The existing PID/token and unrelated-process guards remain in place.

## Verification

Native Rust tests after the headless-signal fix: **135 library + 10 binary tests passed**.

- Compile first: Unity 6000.3.23f1, macOS arm64, disposable project; exit 0.
- Semantic editor compilation with Windows, macOS, and Linux preprocessor definitions:
  all three passed using Unity's generated response file/reference assemblies.
- Native release builds: Windows x86-64 MSVC, macOS arm64 and x86-64, Linux x86-64
  glibc 2.28. All built with the checked-in Cargo lockfile. Private source paths were
  remapped out of the distributed executables. Each bundle's SHA-256 is recorded in
  `MCPForUnity/Server~/manifest.json`.
- Windows PE imports: Windows system DLLs only; the CRT is statically linked. An extra
  Visual C++ redistributable is not needed for this binary.
- Test discovery: 1,713 EditMode cases. Final graphics-enabled run: **1,637 passed,
  0 failed, 76 skipped**. Seven new native-runtime tests cover architecture selection,
  invalid-package rejection, corrupted-cache repair, version coexistence, and Windows
  argument quoting. The skips retain the previous suite's Explicit/Ignore/package
  conditions; they are not counted as passes.
- Live button path: invoked `McpConnectionSection.OnHttpServerToggleClicked`, the actual
  Start/Stop Server callback, in the disposable Unity editor. Two complete cycles each
  launched Rust, returned healthy `/health`, registered the Unity WebSocket instance,
  and connected the bridge. A newly constructed server service rediscovered the native
  listener; Stop ended both the bridge and listener. This was repeated with the final
  packaged executable after private-path remapping. See `native-launch-evidence/button.json`.
- The first regression run had five stale Python-command assertions and two failures
  from a disposable validator package accidentally copied from pre-review source.
  Updated assertions verify native behavior, and the fixture was synchronized with the
  reviewed validator. The final full run above has no failures.

The full Unity interaction was executed on macOS. Windows/Linux builds and platform
compilation are verified; a live Windows/Linux Unity button session has not been run.
The Intel macOS executable also returned its version successfully under Rosetta.
No claim of universal Unity-version or platform certification is made.

## Install/update

Add or update this Git package in Unity Package Manager:

`https://github.com/Noongits/unity-light-mcp-rust.git?path=/MCPForUnity#main`

Open **Window → Unity MCP Light**, select **HTTP Local**, and click **Start Server**.
The default base URL is `http://127.0.0.1:8080`; MCP clients use `/mcp`. Do not edit
`Library/PackageCache` by hand. This update preserves project scenes and assets.
