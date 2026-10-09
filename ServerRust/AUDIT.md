# Timeline and ownership audit

Reviewed and corrected on 2026-10-09. Scope: the native Rust server, including its
transports, MCP sessions, tool/script adapters, and background work. The existing
Unity C# package and Python reference were not changed during that Rust audit.
This full-project distribution subsequently includes a separate C# repair pass;
see `../CSHARP_AUDIT.md` for its distinct validation limits.

The review traced each state owner through normal completion and alternate
orders: cancellation, timeout, late/duplicate replies, reconnect, instance
selection changes, deletion, and shutdown. The findings below are concrete
control-flow defects backed by regression tests. Passing these tests does not
prove that the program is free of other bugs or leaks.

## Tool-list latency

1. An MCP client lists tools while no Unity Editor is running.
2. Metadata discovery falls back to the legacy localhost port.
3. Ordinary command retry policy adds two 250 ms sleeps.

Metadata lookup now uses a bounded single-attempt path and a bounded, expiring
cache. Real commands retain their retry policy. Explicit refresh and reconnect
invalidate cached definitions; default-port legacy Editors remain supported.
See `VALIDATION.md` for the before/after benchmark and its limits.

## Transport ownership and retention

- **Historical TCP connections:** discovering successive ports retained idle
  sockets indefinitely. Idle entries are now evicted toward a 64-entry pool
  target. Actively borrowed connections retain their identity until released.
- **Cancelled queued mutations:** a caller cancelled or timed out, but its
  outbound queue entry could later execute. Dequeue now checks the live session,
  matching pending-command ownership, and receiver liveness before handoff.
  Commands already handed to the socket may still execute; cancellation cannot
  retract them.
- **Unbounded unanswered commands:** draining a bounded outbound queue did not
  bound the pending-response map. There is now a 128-outstanding-command limit
  per Unity session, independent of queue occupancy.
- **Blocked control writes:** a non-reading peer could strand welcome,
  registration, heartbeat, or pong handling. All WebSocket writes now have a
  five-second deadline.
- **Sockets surviving shutdown:** registered and not-yet-registered sockets
  could outlive server shutdown. A retained shutdown signal closes both,
  resolves pending work, and fences later registration/resolution.

Normal response, cancellation before handoff, timeout before dequeue, duplicate
late response, replacement session, and server shutdown all have regression
coverage. Tests do not assert that a timed-out remote mutation was rolled back.

## MCP session and request ownership

- **Request-ID reuse race:** cancellation removed an ID, a new request reused
  it, and the old task's cleanup removed the new entry. Cancellation now aborts
  without releasing ownership; task cleanup releases the ID.
- **Admission after deletion:** a request already holding a session reference
  could start after DELETE or expiry. Closure is terminal and synchronized with
  admission. Expiry does not close active work.
- **SSE and graceful shutdown retention:** retained session references kept
  SSE alive, and waiting for HTTP drain before closing it could prevent exit.
  Streams observe terminal closure; sessions and Unity hubs close before drain.
- **Malformed initialization:** an invalid initialize envelope allocated a
  session before returning an error. Validation now precedes allocation.
- **Unbounded stdio workers:** notifications and completed responses waiting
  on stdout were outside the active-job cap. The reader now applies a
  128-worker backpressure limit.

## Visibility during instance changes

1. A tool-list or synchronization request starts for instance A.
2. The user selects B, resets groups, or changes an override.
3. A's delayed response arrives and used to overwrite newer visibility state.

Revision guards now reject stale updates. Derived restrictions clear when the
selected instance or registration changes; explicit overrides are preserved.
Tool validation and default routing use the same instance snapshot.

## Script mutation proof and partial failure

- A changed SHA after disconnection is not proof that this operation succeeded:
  another writer may have changed the file. Recovery now preserves an unknown
  outcome unless the requested result is actually established.
- A failed read is not proof that a delete succeeded. Recovery distinguishes
  absence from an unavailable or failed read.
- Coordinate normalization must use the caller's expected file snapshot.
  When a caller supplies a SHA, range normalization validates that read snapshot
  before dispatch; Unity still enforces required mutation preconditions. Missing
  caller SHAs are not automatically inferred.
- Mixed text/structured edits can commit the first phase and fail the second.
  Responses now expose partial application rather than implying atomic rollback
  or complete success.

These are intentional safety differences from some reference recovery paths.
No automatic retry may blindly repeat a mutation whose outcome is unknown.

## Background work and bounded results

Scanner admission remains owned by an executing blocking worker even if its
requester cancels, preventing abandoned callers from accumulating queued scans.
Changing a scanner's project root resets its comparison baseline. Readiness and custom-tool polling
deadlines cover stalled sends and refreshes, and polling does not dispatch a new
request after the deadline.

Focus tracking uses stable bridge-owner identities instead of reusable memory
addresses, and remote sessions cannot drive host-OS focus. Progress, focus restoration, and background-mode recovery cancel
obsolete nudges. Native command output and retained search output have explicit
bounds. C# interpolation nesting is limited to 64 levels; deeper input returns
`conversion_failed` before mutation rather than exhausting the Rust stack.
See regression tests and compatibility notes for exact behavior.

## Black-box checks and remaining limits

`tests/cleanup_stress.py` covers repeated session create/delete, cancelled calls
with late/duplicate replies, dropped HTTP clients, editor replacement, malformed
initialization, and SIGINT with active SSE, requests, and sockets. It samples
Linux file descriptors and RSS during a bounded run. Stable samples are useful
evidence, not a mathematical proof of leak freedom.

No live Unity Editor was available. Unity-native allocations, actual domain
reloads, macOS/Windows behavior, and long-running production load remain outside
this validation. This Rust audit does not fix the earlier C# RenderTexture leak.

See `VALIDATION.md` for final aggregate results and `COMPATIBILITY.md` for
remaining migration and behavior differences.
