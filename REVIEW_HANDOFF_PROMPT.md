# Prompt for the next agent

Review this whole Unity MCP Light project and verify the fixes. It includes a
native Rust server in `ServerRust/`, the C# Unity package in `MCPForUnity/`, the
original Python server as a fallback, and test projects/bundled tools.

Start with `FULL_PACKAGE.md`, `CSHARP_FULL_REVIEW.md`,
`CSHARP_REVIEW_COVERAGE.md`, `CSHARP_STATIC_VALIDATION.md`, and
`ServerRust/VALIDATION.md`. The reference fork commit is
`b40c07a1c3dfb98f45a05c4e3233d8de138dc6a6`; this archive contains additional
local changes and has not been pushed. Review the current archive, not only that
remote commit.

All 696 input C# files were reviewed as source, but **the C# changes have not been
compiled or run in Unity**. Static assertions and parser checks are not runtime
test passes. The Rust test results use simulated Unity peers. Don't assume the
project is production-ready or leak-free from those results.

## What to do

1. Preserve my existing local edits and work in a disposable copy/branch. Identify
   the installed Unity version and optional packages. Compile the C# package and
   test assemblies first; fix actual compiler/API/assembly errors before testing.
   Check supported Unity versions where available, and state which were untested.

2. Inspect tests for external/global side effects before running them. Use a
   disposable test project/profile. Don't use real saved credentials, upload to
   Asset Store/providers, kill real user processes, delete borrowed assets, or run
   destructive `Explicit` tests automatically. Preserve settings and user scenes.
   Prefer mocks/temporary files for keychain, config, provider and process tests.

3. Run the applicable EditMode tests and record discovery, passes, failures and
   skips separately. Verify the new regression fixtures really execute. Rendering
   tests need a graphics-enabled Editor; a headless skip is not a pass. Review
   no-PanelSettings test preconditions rather than deleting unrelated settings.

4. Profile the UXML leak in a clean project. Repeatedly render a UXML path without
   an existing PanelSettings asset, then test existing settings, repeated captures,
   resolution changes, failures and owner destruction. Compare native Texture2D /
   RenderTexture counts, generated assets and GPU memory before/after 50–100 calls.
   Confirm actual rendered pixels as well as cleanup, and that user-owned target
   textures/settings survive. Test play-mode exit/re-entry with domain reload off.

5. Exercise the timeline failures described in the reports: cancellation before
   publication, delayed/duplicate callbacks, disconnect/reconnect, stop then new
   start, domain reload, partial build jobs, script edits across method boundaries,
   failed compilation, failed asset saves, and test teardown. Prioritize the
   unresolved cross-mode stop/auto-start generation cases and partial native
   operations. Don't treat a source-order assertion as proof of correct timing.

6. Review configuration/key-store/process and skill-sync fixes with synthetic
   inputs, then check native material/prefab/profile ownership and Undo in Unity.
   Review bundled validator multi-scene restoration and cancellation before any
   real workflow use. Keep confirmed defects separate from possible risks.

7. Build Rust with the lockfile and rerun its native and explicit stdio/HTTP/stress
   suites. Connect the Rust server to the actual updated Unity bridge and smoke-test
   resources, script editing/compilation, screenshots, prefabs, test jobs, reloads
   and the optional groups I use. Unity's automatic launcher still selects Python;
   configure Rust manually as documented and confirm which server is running.

Fix verified issues with focused regression tests and rerun affected checks.
Don't push, open a PR, or publish anything unless I ask.

## Return

- Exact changes and remaining issues, each with a concrete reproduction timeline
- Unity/OS/package versions and test results, including skips and untested areas
- Before/after memory evidence with the workload and measurement method
- Any compatibility or setup changes needed
- The **whole updated project ZIP**, not just `ServerRust/`
