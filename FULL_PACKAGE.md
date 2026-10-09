# Full Unity MCP Light source package

This archive contains the complete source checkout of `Talhasarac/unity-mcp-light`
based on commit `b40c07a1c3dfb98f45a05c4e3233d8de138dc6a6`, with the native Rust
server and the C# fixes described below. It excludes Git history, local virtual
environments, generated build output, and compiler caches. It is a source
distribution, not a prebuilt Windows/macOS installer.

## Contents

- `MCPForUnity/`: the Unity C# package, including UXML/native-resource/lifecycle fixes.
- `ServerRust/`: the native server, lockfile, schemas, tests, migration instructions,
  timeline audit, and recorded Rust validation.
- `Server/`: the original Python server, retained for reference and rollback.
- `TestProjects/UnityMCPTests/`: existing Unity test project plus C# regression tests.
- Original documentation, tools, website, examples, configuration, and license files.

## Use the Rust server

1. Keep a backup of your current project and any local work from other agents.
2. Extract the archive to a source directory. Do not overwrite a working checkout
   blindly if it contains unrelated edits.
3. Install/build the Unity package from this local source, rather than the unchanged
   remote Git URL. Unity Package Manager's **Add package from disk** can select
   `MCPForUnity/package.json`.
4. Follow `ServerRust/README.md` to build with `cargo build --release --locked` and
   configure the native executable for stdio or HTTP.
5. The Unity **Configure** buttons and automatic launcher still generate Python
   commands. Use the documented manual Rust configuration; those controls were
   not converted. The complete Python Click CLI also remains a fallback; the
   Rust CLI provides raw `call`, `instances`, and `status`.

On Windows the built executable ends in `.exe`. Build for the operating system
and architecture where the server will run. The archive does not contain an
untested cross-platform binary.

## Validation and C# testing

Rust validation, benchmark conditions, and limits are recorded in
`ServerRust/VALIDATION.md`. The Rust audit is in `ServerRust/AUDIT.md`.

The initial C# changes are summarized in `CSHARP_AUDIT.md`. The subsequent
file-by-file pass is in `CSHARP_FULL_REVIEW.md`, with exact coverage in
`CSHARP_REVIEW_COVERAGE.md` and final static checks in `CSHARP_STATIC_VALIDATION.md`.
`REVIEW_HANDOFF_PROMPT.md` is the ready-to-use prompt for independent verification. No Unity Editor was
available in the build environment: C# EditMode tests and semantic compilation
were **not run**. The repository's Unity version checks skipped all four configured
versions. Syntax parsing is not a substitute for Unity compilation or execution.

Open the included Unity test project using a supported installed Editor and run
the relevant EditMode fixtures in Test Runner. Ensure its package dependency
resolves to this updated `MCPForUnity` directory. Graphics tests need a real
graphics device; no-PanelSettings cases require a clean project without unrelated
PanelSettings assets. Inspect skipped tests rather than treating them as passes.

Before replacing a working setup, smoke-test discovery, console/resources,
script edits, domain reload/reconnect, UI captures, and the optional tools you use.
The source was not pushed to GitHub, and the remote fork is unchanged by this work.

## Scope of the memory claims

The UXML fix targets the C# owner/RenderTexture cleanup bug; merely running Rust
does not repair that allocation. Install the updated C# package to receive it.
The Rust benchmarks measure only the server process. They do not establish total
Unity Editor RAM, GPU-memory reduction, or end-to-end Unity operation speed.


## Independent Unity verification — 2026-10-09

The archive was independently compiled, tested, profiled and repaired in disposable Unity projects. See [INDEPENDENT_REVIEW.md](INDEPENDENT_REVIEW.md) and `Verification/` for current findings, executed checks, skips and remaining risks. Earlier validation statements in this document describe the historical source-review boundary.
