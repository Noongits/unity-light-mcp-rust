# Full C# source review

Date: 2026-10-09. Reference fork: `Talhasarac/unity-mcp-light`, based on
`b40c07a1c3dfb98f45a05c4e3233d8de138dc6a6` plus the earlier local repairs.

## Coverage and meaning

All 696 C# files present at the start of this pass were read: the Unity package,
custom-tool examples, test fixtures, and bundled Asset Store tooling. Added C#
helpers/regression files were source-reviewed as well. The exact list is in
`CSHARP_REVIEW_COVERAGE.md`.

This was a source-only review. It traced resource/state owners through normal
completion and alternate orders such as errors, cancellation, timeout, reload,
replacement, late callbacks, partial mutation, and teardown. It is not a proof
that every possible defect has been found. Unity compilation, NUnit execution,
graphics behavior, and production memory profiling remain unverified.

## Main implemented repair areas

### Native resources, rendering, and scene ownership

- Temporary UXML panel/RenderTexture ownership, persistent-panel reuse, original
  target restoration, capture cancellation/generation, and play-mode transitions.
- Texture creation, image loading/composition, screenshot readback, partial
  multi-angle batches, native wrapper disposal, and preview resource transfer.
- Generated materials, profiles, effects, and renderer features distinguish
  unadopted temporaries from saved or borrowed assets. Invalid input is checked
  before avoidable allocations or assignments.
- Camera overrides remember their owner; fallback priorities are reversible.
  Pipeline replacement validates before removing the old component and honors Undo.
- Scene replacement protects dirty additive scenes. Explicit paths take priority;
  build indexes count enabled scenes. Prefab saves preserve existing materials,
  untouched property blocks, target-scene ownership, and borrowed stages.

### Async transport, jobs, and callbacks

- Dispatcher cancellation publication, completion cleanup without a future editor
  tick, accepted-socket disposal, pending-bind cancellation, and write timeout ownership.
- WebSocket stop/reconnect resources use captured lifetimes and guarded publication;
  status reads use the authoritative client snapshot.
- UI completion/update work checks lifetime or operation ownership before applying
  delayed results. Polling and rebuild behavior no longer silently lose callbacks.
- Build status reflects active children and pending work; setup failures become
  failed jobs. Package recovery checks requested version/source, and backup metadata
  is published before replacement can fail.

### Scripts, data mutation, and validation

- Failed recompilation cannot leave old executable code published; compiler windows
  destroy only their own temporary objects.
- Exact text edits retain their requested scope, payload limits, and options;
  expression-bodied method parsing does not stop inside strings/lambdas.
- Boxed nested values, array edits, required fields, pagination, batch success flags,
  reflection type ambiguity, console severity, and JSON reader position were repaired.
- Physics input validation precedes avoidable partial writes/forces; simulation no
  longer reports success when its mode change or requested step fails.
- UXML root parsing respects quoted delimiters/comments/namespaces, and output paths
  and newly created assets reject collisions or escapes before writing.

### Configuration, security-related ownership, and providers

- Malformed or unreadable client config fails closed instead of replacing unrelated
  settings. Atomic writes use invocation-owned temporary/backup files.
- CLI configuration is idempotent; queued UI actions preserve the clicked action.
- Existing encrypted-store key material is never silently regenerated; initialization
  publishes one winner. Keychain helper processes have bounded drains/timeouts and cleanup.
- Server termination no longer trusts an arbitrary Python/uv process; escalation
  rechecks process lifetime. Skill sync rejects linked paths and conflicting plans,
  verifies downloads before publishing, and serializes in-process syncs.
- Asset-generation timeout cancellation, result dispatch/extensions, provider
  fallbacks, import path handling, ZIP allowlists, and model bounds were corrected.

### Bundled Asset Store tools and tests

- Bundled tooling now respects scene-save Cancel, preserves borrowed materials and
  camera targets, and cleans temporary previews and importer/stream resources.
- Authentication/download/upload operations use ownership generations and finally
  cleanup. Retired upload preparation cannot start a later upload after logout/refresh.
- Temporary directory rename rollback tracks only successful moves, attempts
  restoration in reverse order, and always releases assembly locks.
- Static events, cached windows, validator result state, progress UI and settings
  notifications have targeted lifecycle repairs.
- Tests use unique owned assets/scenes, preserve existing preferences/environment,
  avoid sweeping user resources, and skip/mark explicit destructive integration work.
  Test isolation improvements do not make every test safe to run in a live project.

## Important unresolved or runtime-dependent risks

These are deliberately retained for independent verification, not hidden behind
the coverage count:

1. **Cross-mode stop/start ordering:** an all-mode stop awaiting HTTP may still stop
   a newer stdio start. Reused auto-start markers and complex overlapping lifecycle
   generations need real integration tests.
2. **Irreversible/partial Unity operations:** native setter failures, asset adoption,
   prefab unpack/save, renderer-feature/volume persistence, and disk write failure
   do not universally roll back all prior mutations.
3. **Graphics and capture timing:** actual UXML pixels, end-of-frame cancellation,
   screenshot file-write completion, GPU allocation recovery, and pipeline-specific
   behavior have not been exercised in Unity.
4. **Filesystem/process races:** lexical/reparse-point checks cannot eliminate an
   external filesystem replacement race; skill sync lacks complete rollback after
   a disk failure. PID-addressed commands retain a final check-to-signal race; full
   protection would require identity-bound OS handles.
5. **Bundled validator scene restoration:** respecting Cancel is fixed, but complete
   unsaved/additive scene restoration across every scanner failure still needs work.
   Handwritten parser limitations, recursive directory cycles, and cache trust risks
   are not all redesigned.
6. **API/version behavior:** Unity 2021/2022/6.x, URP/Cinemachine, importer/native
   callbacks, Undo, modern identifiers, and optional provider capabilities require
   compilation and feature tests with actual installed packages.
7. **Tests with global effects:** Play Mode, physics, PlayerSettings, layers,
   domain reload, and global session state can affect a project even with restoration.
   Use a disposable project. Confirm test discovery and review skipped/Explicit cases.

No real account authentication, upload, provider transaction, process termination,
or Unity scene/asset operation was used to validate these changes here.

## Validation boundary

C# verification consists of source review, focused source/fake-model assertions,
syntax parsing with conditional-compilation handling, and whitespace/metadata
checks. Syntax parsing is not a C# compiler. See `CSHARP_STATIC_VALIDATION.md` for
the final recorded counts and known parser exception.

The Rust server has its separate executed test record in `ServerRust/VALIDATION.md`.
Those tests use fake Unity peers and do not validate this modified C# package.

Use `REVIEW_HANDOFF_PROMPT.md` for the next agent's compile/test/profile pass.
Nothing in this source package has been pushed or published to GitHub.


## Independent Unity verification — 2026-10-09

The archive was independently compiled, tested, profiled and repaired in disposable Unity projects. See [INDEPENDENT_REVIEW.md](INDEPENDENT_REVIEW.md) and `Verification/` for current findings, executed checks, skips and remaining risks. Earlier validation statements in this document describe the historical source-review boundary.
