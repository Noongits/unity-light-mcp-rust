# Validation record

Reference: `Talhasarac/unity-mcp-light`, commit `b40c07a1c3dfb98f45a05c4e3233d8de138dc6a6`.

Verified on Linux x86_64 with Rust 1.99.0 on 2026-10-09. The Rust checks initially ran without changing the Python/C# reference. This full-project distribution subsequently includes separately reviewed C# fixes; their compilation and Unity tests remain unrun, as detailed in `../CSHARP_AUDIT.md`. The Python reference remains unchanged.

## Passed checks

- `cargo test --all-targets --locked`: **142 passed**, comprising 134 library tests and 8 binary tests; none failed or ignored.
- `cargo clippy --all-targets --locked -- -D warnings`: clean.
- `cargo build --release --locked`: compiled successfully.
- `cargo fmt --all -- --check`: formatting checked.
- `tests/contract_stdio.py` against the release binary: **9 test groups passed**.
- `tests/contract_http.py` against the release binary: **7 test groups passed**.
- `tests/cleanup_stress.py` against the final release binary: **7 test groups passed**.

The black-box tests launch the native executable and communicate over actual loopback TCP, WebSocket, HTTP, and stdio. They use deterministic fake Unity peers, not a live Editor.

## What the evidence covers

- Exact snapshots of all 38 built-in tool schemas and all 25 resource/template definitions, captured from the reference with FastMCP 3.0.2 and Pydantic 2.12.5.
- 28 argument/forwarding cases captured through the actual FastMCP validation boundary, including a rejected invalid case; separate direct-adapter fixtures retain lower-level behavior.
- 15 script golden cases derived from the original Python implementation with mocked Unity calls; invalid placeholder precondition SHAs are normalized to their fixture read snapshots for the stricter safety invariant. Original captured fixtures and Python code remain unchanged.
- Script URI/encoding, edit normalization, SHA preconditions, overlap handling, regex/anchor logic, and safe disconnect recovery.
- Actual framed TCP handshakes, fragmented frames, heartbeat handling, discovery freshness, cancellation, and no ambiguous mutation replay.
- No-Editor tool listings complete three repetitions within a 200 ms regression budget; later legacy default-port connections still expose custom tools. Cache tests cover repeated reads, explicit refresh, expiry, reconnect, and bounded capacity.
- HTTP initialization through the official Python MCP SDK; session-local routing/groups, inline overrides, custom tools, cancellation/deletion, REST endpoints, Origin rejection, API-key validation, cross-user isolation, and graceful WebSocket closure.
- Bounded queue/cache/scanner ownership and task cleanup after cancellation or request-handler drop.
- OS focus-nudge logic through mocks; no real window activation occurred.

## Not established

No live Unity Editor was available. Actual Unity import/build/test execution, C# compilation, domain reloads, Blender integration, UI screenshots, and macOS/Windows native behavior remain unverified end to end. The unchanged Python test suite is not evidence that the Rust implementation passes those tests. The narrowly scoped benchmark below does not establish live-Editor command performance or long-running memory behavior.

The entire Python Click CLI and Unity's automatic Python launcher were not converted. The native runtime offers the MCP server plus raw `call`, `instances`, and `status` commands. See `COMPATIBILITY.md` for intentional differences and unsupported surfaces.

## Tool-list latency regression benchmark

Same Linux host, release build, empty temporary discovery directory, no Editor connected, both servers launched with stdio and project-scoped tools. Each implementation returned 38 tools. Three process trials per implementation, with three measured `tools/list` calls per trial; numbers below are medians of those trial medians. This is a small, local benchmark, not a tail-latency or live-Unity claim.

- Before fix: Rust `tools/list` approximately 506 ms, Python approximately 5.48 ms.
- After fix: Rust `tools/list` **2.73 ms**, Python **5.74 ms** on the rerun, approximately **185× faster than the previous Rust path**.
- The removed overhead was two unnecessary 250 ms connection-retry sleeps while discovering custom tools. Real command retries and reload handling are retained.
- Cold metadata lookup remains bounded by a two-second listing deadline if a peer accepts connections but stalls. A refused absent localhost Editor responds immediately without retry sleeps.
- Raw before/after measurements, including startup and idle memory samples, are in `benchmarks/stdio_no_editor_before.json` and `benchmarks/stdio_no_editor_after.json`. Those idle samples are not a memory-leak test.

## Post-audit release verification

The complete checks above were rerun after all audit fixes, including the lexer
depth bound. See `AUDIT.md` for timelines and fixes. The cleanup harness covers
300 create/delete cycles, 25 cancellation/late-callback cycles, 12 dropped HTTP
requests, 15 editor replacements, malformed initialization, and shutdown with
active requests and registered/unregistered sockets.

During the final release session-churn run, descriptors remained at 11 and RSS
samples were 7,536 / 7,544 / 7,584 / 7,596 KiB. This short bounded observation
does not establish long-running leak freedom.

A final identical three-run no-Editor benchmark after the broader audit measured
Rust tool listing at **2.54 ms** median versus Python **5.67 ms**. Rust settled
RSS median was **8.88 MiB**, versus Python **86.84 MiB**. Raw results are in
`benchmarks/stdio_no_editor_final_audit.json`. These numbers characterize this
Linux host and disconnected-server workload only.


## Independent native/live verification — 2026-10-09

See `../INDEPENDENT_REVIEW.md` and `../Verification/evidence`. Final native tests: 134 library plus 8 binary; stdio/HTTP/stress: 9/7/7 groups with no skips, release binary explicitly selected. Live Unity 6000.3.23f1 verified resources, script editing/compilation, capture, prefab, reload, intentional compile failure/recovery and test jobs. Structured union-result wrapping was repaired after the official SDK rejected refresh replies. HTTP real-Unity and other operating systems remain untested.
