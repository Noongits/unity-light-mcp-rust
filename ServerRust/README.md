# Unity MCP Light: native Rust server

A Python-free runtime for the MCP server in this repository. The compiled executable speaks MCP over stdio or local HTTP and talks to the existing `MCPForUnity` C# Editor bridge. It does not embed Python, launch `uv`, or import `Server/` at runtime.

The compatibility reference is commit `b40c07a1c3dfb98f45a05c4e3233d8de138dc6a6`. Snapshots cover 39 built-in tool definitions and 25 resource definitions, including six URI templates. Implementations include argument normalization, script editing, reload recovery, session routing, custom tools, resources, and image responses. Schema parity is not proof of complete behavioral parity; see [COMPATIBILITY.md](COMPATIBILITY.md).

This distribution includes reviewed C# ownership/lifecycle fixes and the retained Python fallback. Native Rust tests and a live Unity Editor session were verified on macOS arm64 with Unity 6000.3.23f1; see [INDEPENDENT_REVIEW.md](../INDEPENDENT_REVIEW.md) for evidence, skips, and remaining platform/rollback limits.

## Build

Install a current stable Rust toolchain and the native compiler/linker required for your target OS. From this directory:

~~~sh
cargo build --release --locked
./target/release/unity-mcp-light --help
~~~

The executable is `target/release/unity-mcp-light` (`unity-mcp-light.exe` on Windows). Build for the OS and architecture where it will run. `Cargo.lock` is checked in; `--locked` prevents unnoticed dependency re-resolution. The first build downloads dependencies unless cached.

Only the native executable is needed to run this server. A supported Unity Editor with this repository's C# package is still required for Unity operations. Contract JSON files are embedded at compile time.

## Stdio: one MCP client process

1. Keep the existing C# package installed in your project.
2. Enable the Editor's legacy/stdio bridge so it writes discovery files under `~/.unity-mcp` and listens on its local TCP port.
3. Configure the MCP client manually to launch the native executable.

Example for clients using an `mcpServers` JSON configuration:

~~~json
{
  "mcpServers": {
    "unity-mcp-light": {
      "command": "/absolute/path/to/ServerRust/target/release/unity-mcp-light",
      "args": ["--transport", "stdio"]
    }
  }
}
~~~

Use an absolute executable path. On Windows, escape backslashes in JSON or use forward slashes. Stdout is reserved for newline-delimited MCP JSON-RPC; diagnostics go to stderr.

Discovery defaults to `$HOME/.unity-mcp`, or `%USERPROFILE%/.unity-mcp` when `HOME` is absent. Override with `--status-dir /path` or `UNITY_MCP_STATUS_DIR`. The bridge reads current status files and legacy port files and uses Unity's framed TCP protocol. Do not run competing stdio clients against the same single-client legacy Unity listener; use HTTP for multiple clients.

Tool listing uses a single, bounded metadata request instead of command/reload retries. With no Editor listening, connection refusal returns immediately; the default legacy port 6400 remains supported even without discovery files. TCP custom definitions are cached for up to five seconds (at most 64 instances), scoped to the selected instance and socket connection. Reconnects, reloads, and command I/O invalidate that cache; `manage_tools` with `action: "sync"` explicitly refreshes it. HTTP custom definitions continue to come from live WebSocket registration.

### Unity configuration buttons

The Unity package includes prebuilt native servers for Windows x86-64, macOS
arm64/x86-64, and Linux x86-64 (glibc 2.28+). In **HTTP Local**, click **Start Server**:
Unity verifies and copies the executable to a user-owned cache, launches it without
a terminal, and connects the Editor automatically. **Stop Server** stops that owned
process. No Rust toolchain, Python, uv, or Git is needed to run the included server.

The **Configure** flow writes the HTTP `/mcp` endpoint or, for stdio clients, the
cached Rust executable with `--transport stdio`. Existing source/tool overrides
for Python do not select the native executable. `Server/` remains available for
people who deliberately configure the Python fallback outside this launcher.

Update the Unity package using
`https://github.com/Noongits/unity-light-mcp-rust.git?path=/MCPForUnity#main`.
See [launch verification](../Verification/NATIVE_LAUNCH.md) for the platforms
actually executed and the limits of cross-compilation evidence.

## HTTP: shared local server

~~~sh
./target/release/unity-mcp-light \
  --transport http \
  --http-url http://127.0.0.1:8080
~~~

Configure the existing Unity HTTP bridge with base URL `http://127.0.0.1:8080`. It connects over WebSocket at `/hub/plugin`. MCP clients use `http://127.0.0.1:8080/mcp`, not the WebSocket route.

| Endpoint | Purpose |
| --- | --- |
| `http://127.0.0.1:8080/mcp` | MCP requests (POST), notification stream (GET), session deletion (DELETE) |
| `ws://127.0.0.1:8080/hub/plugin` | Existing Unity Editor plugin connection |
| `http://127.0.0.1:8080/health` | Process health; does not prove an Editor is connected |
| `http://127.0.0.1:8080/api/instances` | Local REST instance discovery |
| `http://127.0.0.1:8080/api/custom-tools` | Local REST custom-tool discovery |
| `http://127.0.0.1:8080/api/command` | Local REST raw Unity command submission |

A typical HTTP client entry follows; exact keys depend on the client:

~~~json
{
  "mcpServers": {
    "unity-mcp-light": {
      "type": "http",
      "url": "http://127.0.0.1:8080/mcp"
    }
  }
}
~~~

Initialization returns `Mcp-Session-Id`; subsequent requests require that header. Active instance and tool-group choices belong to each session. Read `mcpforunity://instances`, then call `set_active_instance` with exact `Name@hash` when multiple Editors are connected. A `unity_instance` override on a tool call does not change the session selection.

Local mode has no API-key authentication. Keep it bound to loopback. Do not expose it as an authenticated LAN/Internet service.

## Native command-line helpers

These contact an already running local HTTP server:

~~~sh
./target/release/unity-mcp-light status
./target/release/unity-mcp-light instances
./target/release/unity-mcp-light call get_project_info '{}'
./target/release/unity-mcp-light call manage_scene \
  '{"action":"get_hierarchy"}' --instance 'MyProject@01234567'
~~~

Use `--http-url`, `--http-host`, or `--http-port` before the subcommand for another address. `call` sends parameters directly to Unity via `/api/command`; it does **not** invoke MCP adapters or snake_case-to-wire-name conversion. Supply Unity wire fields, for example `searchTerm` instead of `search_term`.

Python Click subcommands and friendly aliases have not been ported. They remain reference-only in `Server/`; Rust exposes `call`, `instances`, and `status`. Raw REST helpers are unavailable in remote-hosted mode.

## Remote-hosted mode

Remote-hosted mode requires an API-key validation service. Terminate HTTPS/WSS at a correctly configured reverse proxy: the executable binds plain HTTP and does not load TLS certificates.

Example without real credentials:

~~~sh
export UNITY_MCP_API_KEY_VALIDATION_URL='https://auth.example.test/validate'
export UNITY_MCP_API_KEY_CACHE_TTL='300'
./target/release/unity-mcp-light \
  --transport http --http-host 127.0.0.1 --http-port 8080 \
  --http-remote-hosted
~~~

Both MCP requests and the Unity WebSocket handshake require `X-API-Key`. The server POSTs `{"api_key":"..."}` to the validation service and requires a successful JSON response containing `"valid": true` and a nonempty string `"user_id"`. Optional `UNITY_MCP_API_KEY_SERVICE_TOKEN_HEADER` and `UNITY_MCP_API_KEY_SERVICE_TOKEN` add a validation-service credential. Keep credentials out of committed configuration and logs.

Validated user IDs select separate Unity hubs. Sessions are bound to their authenticated user. Remote tool calls require explicit instance selection. Local project-filesystem scanning is disabled for remote sessions. Local REST command/discovery routes return 404; use authenticated MCP. `/health` remains unauthenticated. Optional `--api-key-login-url` exposes a configured URL at `/api/auth/login-url`; it is not OAuth login.

The validation cache uses SHA-256 hashes of API keys and a lifetime capped at one hour. Key revocation may not take effect until the cache entry expires; TTL zero forces revalidation. Loopback fake-service tests cover isolation, but this has not been security-audited or validated as a production remote deployment.

## Configuration

CLI options override matching environment variables. `--help` is authoritative.

| Environment variable | CLI option | Default / scope |
| --- | --- | --- |
| `UNITY_MCP_TRANSPORT` | `--transport` | `stdio`; accepts `stdio` or `http` |
| `UNITY_MCP_HTTP_URL` | `--http-url` | `http://127.0.0.1:8080` |
| `UNITY_MCP_HTTP_HOST` | `--http-host` | Overrides URL host |
| `UNITY_MCP_HTTP_PORT` | `--http-port` | Overrides URL port |
| `UNITY_MCP_DEFAULT_INSTANCE` | `--default-instance` | Initial instance |
| `UNITY_MCP_STATUS_DIR` | `--status-dir` | Stdio discovery directory |
| `UNITY_MCP_PROJECT_SCOPED_TOOLS` | `--project-scoped-tools` | Conditional custom-tools resource listing |
| `UNITY_MCP_HTTP_REMOTE_HOSTED` | `--http-remote-hosted` | Off; validation URL required when enabled |
| `UNITY_MCP_API_KEY_VALIDATION_URL` | `--api-key-validation-url` | Remote authentication service |
| `UNITY_MCP_API_KEY_LOGIN_URL` | `--api-key-login-url` | Optional API-key management URL |
| `UNITY_MCP_API_KEY_CACHE_TTL` | `--api-key-cache-ttl` | 300 seconds; capped at 3600 |
| `UNITY_MCP_API_KEY_SERVICE_TOKEN_HEADER` | `--api-key-service-token-header` | Optional service header name |
| `UNITY_MCP_API_KEY_SERVICE_TOKEN` | `--api-key-service-token` | Optional service credential |
| `UNITY_MCP_SESSION_READY_WAIT_SECONDS` | None | HTTP fast-command readiness; default 6 seconds, maximum 120 |
| `UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S` | None | HTTP session resolution; default 20 seconds |
| `UNITY_MCP_CONNECTION_TIMEOUT` | None | Legacy TCP connection/read timeout; default 300 seconds |
| `UNITY_MCP_COMMAND_TOTAL_TIMEOUT` | None | Legacy TCP total command budget; default 600 seconds |
| `UNITY_MCP_RELOAD_MAX_WAIT_S` | None | Legacy nonexecuted-reload retry window; default and maximum 20 seconds |
| `UNITY_MCP_DISABLE_FOCUS_NUDGE` | None | Disables stalled-test focus nudges when truthy; default false |
| `UNITY_MCP_NUDGE_BASE_INTERVAL_S` | None | Initial nudge interval; default 1 second |
| `UNITY_MCP_NUDGE_MAX_INTERVAL_S` | None | Maximum nudge interval; default 10 seconds |
| `UNITY_MCP_NUDGE_DURATION_S` | None | Nudge duration; default 3 seconds |
| `RUST_LOG` | None | Diagnostics filter; default `warn` |

`--pidfile PATH` writes and cleans up a PID file. The accepted `--unity-instance-token` remains visible in the process command line for the existing C# process detector. It does not select an instance or authenticate a caller; use `--default-instance`, MCP `set_active_instance`, or remote API-key authentication as appropriate.

Not all Python settings are implemented. Python log-directory/configuration files and telemetry settings do not configure corresponding Rust features. The focus-nudge variables above are implemented; real macOS/Windows focus behavior remains unverified. Telemetry is disabled, with no sender. A path in `--http-url` does not relocate fixed routes; an `https://` URL does not enable server-side TLS.

## Tests and reference snapshots

~~~sh
cargo test --locked
cargo build --locked
~~~

From the repository root, optional black-box tests exercise the Rust executable with fake Unity peers:

~~~sh
python ServerRust/tests/contract_stdio.py
python ServerRust/tests/contract_http.py
python ServerRust/tests/cleanup_stress.py
~~~

The stdio harness uses Python's standard library. HTTP additionally requires reference development dependencies including `httpx`, `websockets`, and the MCP SDK. Set `UNITY_MCP_RUST_BIN` to test a release executable or another build. Python is needed for optional verification and snapshot capture, not native runtime.

`tests/capture_contract.py` and `tests/capture_forwarding.py` extract contracts from `Server/` using its Python development environment. Do not regenerate snapshots just to make failing tests pass; review differences against the baseline. Running `Server/tests/` by itself tests the Python reference, not Rust.

The latest recorded build and test evidence is in [VALIDATION.md](VALIDATION.md).

## License and attribution

MIT, with the parent notices preserved in [LICENSE](LICENSE). Derived from [Unity MCP Light](https://github.com/Talhasarac/unity-mcp-light) and its MCP for Unity implementation. The parent license credits Copyright (c) 2025 CoplayDev. Rust dependencies retain their own licenses.


For the subsequent bug and ownership review, see [AUDIT.md](AUDIT.md). The cleanup
stress harness uses Linux `/proc` for optional memory/file-descriptor observations.
