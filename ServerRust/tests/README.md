# Compatibility regression tests

The contract snapshots come from the Python implementation at commit
`b40c07a1c3dfb98f45a05c4e3233d8de138dc6a6` in this repository. Capture uses the
lockfile's FastMCP 3.0.2 and Pydantic 2.12.5. They are build-time data only; the
Rust executable does not start or import Python.

## Run

From the repository root:

```sh
cargo test --manifest-path ServerRust/Cargo.toml
cargo build --manifest-path ServerRust/Cargo.toml
python ServerRust/tests/contract_stdio.py
python ServerRust/tests/contract_http.py
```

The stdio suite uses only Python's standard library. The HTTP suite uses
`httpx`, `websockets`, and the official `mcp` Python SDK, which are included in
the Python server's environment. Set `UNITY_MCP_RUST_BIN` to test another build.
All external processes and listeners used by these suites are loopback fake
Unity editors or fake authentication services; a real Unity project is not
required and is not modified.

## Cleanup stress dependencies

`cleanup_stress.py` requires Python 3.11 or newer (`asyncio.timeout`).
On macOS, install `psutil` for process RSS and descriptor measurements;
Linux uses `/proc`. Run against the release binary with `UNITY_MCP_RUST_BIN`.

## Coverage

- Exact 38-tool advertised schemas, annotations and metadata
- Exact 25 resources/templates and static resource contents
- Legacy TCP greeting, eight-byte framing, zero-length heartbeats
- Python-to-Unity argument transformations and actual MCP validation
- Tool-group changes, unknown methods, invalid resource IDs
- Official MCP SDK initialization and HTTP listing
- Real Unity WebSocket handshake, command/results, and graceful close
- Independent session selection and tool visibility; per-call overrides
- Custom tool registration, listing, invocation, and resource data
- Request cancellation and session deletion
- REST command routing and Origin rejection
- Remote authentication, identical project hashes across distinct users,
  rejection of another user's session ID, and disabled remote filesystem scans

## Refresh snapshots

Use a Python environment with `Server/uv.lock` dependencies, then run:

```sh
python ServerRust/tests/capture_contract.py
python ServerRust/tests/capture_forwarding.py
```

`forwarding.json` captures direct Python function behavior, including
permissive values callers can pass when bypassing MCP validation.
`forwarding_wire.json` runs the same examples through FastMCP's actual
`FunctionTool.run` validation boundary. The black-box stdio tests use the latter.
The adapter-only snapshot deliberately stubs Unity transport and does not
capture preflight reads; wire assertions filter only explicit preflight
`get_editor_state` and `get_project_info` calls, plus connection pings and tool
state discovery. This preserves the Rust preflight checks.

These tests establish bridge and MCP contract compatibility. They do not
replace a live Unity smoke test for editor API behavior, image output, domain
reloads, platform-specific focus restoration, or real project compilation.
