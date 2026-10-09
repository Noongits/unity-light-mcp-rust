# Independent verification and repairs — 9 October 2026

> Public repository note: this report describes the completed review before publication. Structured evidence and PNGs are included here with local user paths anonymized. Raw runtime logs and the prebuilt macOS executable remain in the delivered project ZIP and are excluded from Git. Upstream release workflows are preserved as inactive reference files under `.github/upstream-workflows`.

The original download was preserved byte-for-byte. Work and Unity verification used separate copies; nothing was pushed, published, uploaded to a provider, or submitted as a PR. The source archive contained 2,309 files, including 728 C# files. `Verification/evidence/original-preservation.json` records the preservation check. Existing reports remain historical evidence; this report supersedes their statement that the C# had never compiled or run.

This review covered the package, test infrastructure, native server, fallback server, optional examples, bundled validator, configuration/ownership paths and website build. It combined targeted source inspection with compilation, regression execution, synthetic timing fixtures and live bridge checks. It is not a claim of a second line-by-line audit of every one of the 728 input C# files or universal rollback/leak safety.

## Confirmed findings and fixes

| Priority | Concrete reproduction / consequence | Repair and verification |
|---|---|---|
| P1 | All-mode Stop waits for HTTP; a newer stdio Start succeeds; the old Stop then issues a stdio stop against the new session. | Dispatch both captured retirements before yielding; generation-check state publication, failure cleanup and verification. `CrossModeLifecycleRegressionTests.AllModeStop…` executes a gated fake-client timeline and passes. |
| P1 | BridgeControl Start waits for the other mode to stop; Stop arrives; releasing the old wait starts the supposedly stopped transport. | Bridge-control operation generation retires old preflight starts. `bridge-control-repro.xml` failed before the fix; the new regression passes in the final full run. |
| P1 | An auto-connect attempt is cancelled; a new attempt reuses the pending marker; the old poll/finally continues or clears the new marker. | Session generation identifies each pending operation; post-await checks and owner-only completion. Both new auto-start runtime regressions pass. Session-scoped stdio reload intent prevents machine-wide intent sharing; batch verification avoids migrating/deleting the user's legacy HTTP reload preference. |
| P1 | Repeated path-based UXML rendering borrows a PanelSettings asset, creates/imports a RenderTexture asset, and returns successful but blank images. | Request-owned settings clones and native temporary textures replace asset creation; initialize targets, explicitly update/repaint/render the runtime panel, restore active camera/texture, and release path captures. Final frame profile rendered 100/100 nonblank captures in both configurations, with four red-pixel checks each and zero generated RT assets. |
| P1 | Validator scanners switch to scenes in Single mode and restore only a path; dirty unsaved objects and additive scene handles cannot be reconstructed from those paths. | `BeginScan` retains original live scenes, opens only scan-owned scenes additively, restores active scene and closes only newly opened scenes on disposal. All three scanner callers use the scope; particle scans traverse only the selected scene. Two test cases and a separate genuinely untitled/dirty/additive verification pass on completion and synthetic cancellation (`validator-unsaved.json`). |
| P1 | Eight competing encrypted-key initializers can publish different key material: Mono/macOS File.Move can replace an already published winner. | Flush private temporary files before atomic Unix hard-link publication without replacement; losers read the complete winner and remove their temporary files. Synthetic regression repeats 16 rounds of eight workers; final runs pass. No real credential store was used. |
| P1 | Native Rust refresh/run_tests/get_test_job replies publish unwrapped structured content despite advertising FastMCP's required `result` wrapper. The official MCP SDK rejects a successful operation. | Wrap structured content according to the advertised schema and preserve original text content. Added schema-validator regression; actual SDK 2.3.0 now accepts refresh, starts a Unity test job and reads its successful result. |
| P2 | Renderer-feature Add/Remove broadly saves all dirty assets; Remove destroys the subasset outside Undo. | Save only the owning renderer asset; register adopted features with Undo and remove via Undo.DestroyObjectImmediate. Real URP regression verifies an unrelated dirty material remains dirty and feature removal can be undone and saved/reimported as a native subasset. |
| P2 | Volume profile creation broadly saves unrelated assets; subasset adoption needs to retain the profile's main-asset identity. | Save only the profile and retain its main object during adoption. Native persistence helper regression saves/reimports and checks the owned effect survives. Live Rust calls also create a real VolumeProfile, add Bloom and read it back; this live check alone does not prove every volume failure/Undo path. |
| P2 | Inline class/method declarations are valid C#, but structured method replacement reports “header not found.” Simply relaxing the regex would risk deleting the class or neighboring methods. | Accept inline boundaries only at direct class brace depth outside comments/strings; backtrack only whitespace. Three span regressions preserve the class and other methods, including a same-named local function. A live Rust edit changes Value from 1 to 2 while Other remains 7, then compiles. |
| P2 | Optional Roslyn example uses the built-in tools namespace, so custom-tool discovery filters it out. | Move the example to `MCPForUnity.Examples.Editor` with the attribute import. New discovery regression passes, and live `execute_custom_tool` compiles and executes a synthetic static method. Its attachable compiler must live in a runtime assembly, with the MCP adapter in an Editor assembly. |
| P2 | Other baseline failures: constraints bitmasks lack FlagsAttribute, empty collider parent lookup fails, color changes replace borrowed materials, and by-ID operations miss objects in inactive additive scenes. | Validate native constraints as masks; include parent Rigidbody lookup; retain existing material ownership; resolve authoritative IDs across loaded scenes. Affected regressions pass in the full suite. |
| P2 | The disposable bridge port registry ignores UNITY_MCP_STATUS_DIR, and exported website builds require Git history absent from ZIPs. | Honor isolated registry directories; gate website revision metadata on available Git history. Actual bridge status/port files remain in the temporary directory, and the exported site builds. |

### Test infrastructure and packaging corrections

Unity's bundled NUnit cannot compile the supplied NonParallelizable attributes. Test code also shadowed `UnityEngine.Resources`, lacked the EnumField namespace import, used unsupported additive NewScene calls when an untitled scene was already open, and declared a serialized test component in a file with another name. These were corrected before relying on tests. New `TestSceneFactory` owns imported empty fixture scenes and deletes only its own assets on close.

Several assertions assumed managed wrapper identity survived CreateAsset/import, obsolete inspection output, exact rather than derived cancellation exceptions, or persistent capture assets. They were updated to match native Unity identity and the intended safer behavior. Regressions replaced the cross-mode source-order assertion with real delayed tasks. The complete change inventory and patch are in `Verification/changes.json` and `Verification/changes.patch`.

Unity rewrote some vendor importer metadata during verification. Existing metadata is restored from the original input before delivery; original GUIDs and assets are retained. New regression scripts receive their own metadata. Build caches and generated website/server outputs are excluded from the ZIP; a tested macOS arm64 Rust executable is included separately under `Verification/bin`.

## Environment and executed checks

- macOS 26.2 (25C56), Apple M4, arm64; Unity 6000.3.23f1, graphics enabled using Metal. No `-nographics` rendering passes are claimed.
- Package 10.3.0; Test Framework 1.6.0; URP/Core 17.3.0; Cinemachine 3.1.6; UGUI 2.0.0; AI Navigation 2.0.14; Timeline 1.8.13; bundled Asset Store Tools 12.0.1. Full resolved packages are recorded in the manifest/lock evidence.
- Optional Roslyn: CodeAnalysis/Common and CSharp 4.12.0, Immutable/Metadata 8.0.0, Unsafe 6.0.0, installed only in the disposable project.
- Rust 1.96.0; lockfile-backed test, clippy (`-D warnings`), release build and formatting checks.
- Python fallback: Python 3.10.20, FastMCP 3.4.8, MCP SDK 1.30.0, pytest 9.1.1, Pydantic 2.14.0. This fallback run used installed dependencies rather than claiming a frozen `Server/uv.lock` reproduction.
- Live/stress client: Python 3.14.5, official MCP SDK 2.3.0, HTTPX 0.28.1, websockets 17.2, psutil 7.2.2. macOS stress resource sampling now uses psutil; asyncio.timeout requires Python 3.11+.
- Website: Node 24.16.0; Docusaurus updated from 3.10.1 to 3.10.2 through compatible dependency updates. Production build succeeds from the source export.

| Check | Final evidence / result |
|---|---|
| Unity EditMode | `editmode-delivery.xml`: 1,626 discovered/result cases; **1,552 passed, 0 failed, 74 skipped**, 45.015 s. Exit 0. |
| Unity PlayMode | `playmode-final.xml`: **5 passed, 0 failed/skipped**. |
| Domain reload disabled | Actual EditMode UnityTest enters/exits/re-enters Play Mode with DisableDomainReload, retires pending capture and releases a late callback's texture. Passed. It tests cancellation/lifetime, not completion of a valid end-of-frame screenshot in batch mode. |
| Rust unit/binary tests | **134 library + 8 binary tests passed**, including the new output-schema regression. |
| Rust stdio / HTTP contracts | **9 / 7 test groups passed**, with explicit release-binary paths and zero skips. HTTP uses fake Unity WebSocket peers. |
| Rust cleanup stress | **7 groups passed**; 300 create/delete cycles: four samples each **6,272 KiB RSS / 11 descriptors**. |
| Python fallback | **1,239 passed, 18 skipped**, 34.54 s. Separate skip evidence retained in the pytest log. |
| Native ownership | Real URP renderer/Undo, persistence helper and validator fixtures execute in the final suite; targeted intermediate results retained. |
| Live Rust to Unity | Native serverInfo `unity-mcp-light` 0.1.0; exact disposable project verified before mutations. Resources, script creation/editing/compilation, prefab create/read, camera PNG, UXML pixels, intentional compiler errors and recovery, reload/reconnection, test job, real volume and Roslyn custom tool exercised. |

The early EditMode baseline was 1,270 passes / 274 failures / 72 skips. Intermediate failures and compiler errors are retained as evidence, not counted as passes. One full run stalled without a result file after source/harness changes during that run and was terminated; a later traced clean run completed. A separate earlier editor hung in shutdown after writing its complete results and was terminated. Only owned disposable batch editors were terminated. The final delivery run exited normally.

### Discovery and skips

Leaf RunState alone understates exclusion: the final EditMode tree has 15 Explicit leaves, but **59 cases inherit Explicit** from their fixtures. Those 59 were not automatically enabled. They include real config/cache writes, provider/network discovery, process/server actions, focus changes, project-wide saves and reload stress. `skips-final.json` lists every skipped case and reason; no skipped test is credited as a pass.

The remaining **15 runtime ignores** are: five Windows-only shim cases; three bridge-not-running reconnect/resend cases; three tests needing an active render pipeline/settings asset (URP is installed, but the disposable default pipeline is Built-in); two clean/named-scene preconditions; and two no-PanelSettings branches. Cinemachine brings a PanelSettings asset, so the last two cannot truthfully execute in the full optional-package project. They are covered by the separate minimal graphics profile, whose JSON explicitly records zero PanelSettings assets. Live native reconnect/reload smoke provides additional evidence, but it is not a rerun of the three ignored network fixtures.

## UXML profiling evidence

Measurement uses `Resources.FindObjectsOfTypeAll<Texture2D/RenderTexture>`, `Profiler.GetRuntimeMemorySizeLong` totals, `Profiler.GetAllocatedMemoryForGraphicsDriver`, and asset counts. The minimal project has only MCP and Test Framework; it does not delete borrowed settings to meet a precondition.

Before the repairs, 100 path renders produced **0/100 content responses and 0/4 red-pixel samples** in each settings configuration. With an existing PanelSettings asset, one generated RT asset remained before explicit cleanup and native RT count grew from 1 to 2. The cold no-settings Texture2D increase from 112 to 136 includes engine/UI initialization and is not by itself proof of a per-call leak. `profile-before-minimal.json` retains these measurements.

Final workload: 10 warmup renders, 20 settling editor ticks, 100 captures (one per editor tick), samples after 50 and after 100 with another settling interval, a 96×64 resize, a blocked-output-folder failure, explicit cleanup and settling. Test target assets are retained and checked. The unpaced synchronous intermediate profile accumulated outstanding GPU work; it is retained but is not used to claim stable GPU memory.

| Configuration | Warm before → after 50 → after 100 |
|---|---|
| No PanelSettings assets | RT **1 → 1 → 1**; Texture2D **136 → 136 → 136**; RT native bytes **1,188 → 1,188 → 1,188**; texture native bytes **35,264,058 → 35,264,058 → 35,264,058**; driver bytes **34,916,653 → 34,916,653 → 34,916,653**. |
| Existing settings + borrowed user RT | RT **2 → 2 → 2**; Texture2D **136 → 136 → 136**; RT native bytes **3,256 → 3,256 → 3,256**; texture native bytes **35,264,570 → 35,264,058 → 35,264,058**; driver bytes **34,917,537 → 34,917,537 → 34,917,537**. |

Both configurations: **100/100 successful nonblank captures, 4/4 known red-pixel samples, zero generated RT assets**, resize success and expected file-path failure. Borrowed target survives cleanup. After resize/cleanup the driver retains an additional **65,536 bytes** in each case; counts remain stable. This bounded allocator/cache retention is reported rather than asserting all GPU memory returns to the cold baseline. Owner deletion, cache pruning, target restoration and cleanup failures are also covered by the executed lifetime fixtures. Raw JSON, harness, logs and PNGs accompany this report.

## Remaining risks and limits

1. **P1: partial native/disk failures still lack a universal transaction.** Example timeline: adopt/change a native asset, then the owning asset's save fails or a later setter throws. Success/Undo/persistence tests and an output-folder failure do not establish full rollback for disk-full, native importer exceptions, prefab unpack/save, every VolumeProfile remove/Undo path, or multi-step native setters. Do not treat these tools as atomic transactions.
2. **P1: validator scope is tested for normal completion and caller cancellation, not arbitrary hostile scene callbacks.** A user's scene callback can close/mutate an original scene or throw during cleanup; retaining handles cannot reconstruct externally destroyed in-memory state. Handwritten parsers, directory cycles and vendor cache-trust risks remain outside this repair.
3. **P2: graphics compatibility is verified only on this Unity 6/Metal setup.** Runtime panel repaint uses internal Unity APIs with explicit failure when unsupported. Unity 2021.3/2022, other Unity 6 builds, Windows/Linux graphics, HDRP and valid PlayMode screenshot completion remain untested. The package's existing 2021.3 declaration is not newly certified by this run.
4. **P2: filesystem/process identity races remain.** Lexical/symlink validation can be invalidated between check and use; PID revalidation cannot guarantee identity at signal time; skill sync still lacks complete rollback after a disk failure. Synthetic tests do not certify real credential/keychain/provider/process workflows. No real provider transaction or account upload was attempted.
5. **P2: website dependency advisories remain.** Compatible audit updates reduced reported affected-package counts from 61 (19 critical) to **46 (16 critical, 15 high, 15 moderate)**; the final static build passes. Remaining chains include Docusaurus/tinypool, serialize-javascript and dev-server tooling. No forced major overrides or canary migration is presented as a safe fix, and this is not an exploitability assessment of the generated static pages. Both audit JSONs and update/build logs are included.
6. **P2: live Rust verification used stdio/TCP.** HTTP/WebSocket contracts and stress passed against synthetic peers; the actual Unity WebSocket path, remote authentication, other operating systems and paid optional groups remain unverified. Port movement after reload occurred and hash-based selection followed the status files; fixed numeric-port selection is not stable across that fallback.

## Reproduction and setup

`Verification/prepare_disposable.py NEW_DIRECTORY --with-roslyn` creates a fresh review project, refuses to overwrite any existing directory and optionally installs the pinned NuGet DLLs. Use the editor/version and manifest above. Compile without `-runTests` first, then discover with `ReviewDiscovery.Run`, run EditMode/PlayMode and inspect XML skips. Never enable destructive Explicit fixtures merely to improve a count.

The recorded profile used a second minimal project without optional packages and `ReviewProfile.Run` (see the harness's actual entry points/environment variables). Preserve the warmup/settling cadence and check real pixels. `ReviewValidator.Run` requires an already fresh untitled scene and refuses to replace another scene.

Rust tests/build use `--locked`; set **UNITY_MCP_RUST_BIN to the actual built executable** for the black-box/stress suites, otherwise their default debug-path lookup can skip instead of testing. On macOS install psutil for stress sampling. The included binary is macOS arm64 only.

The live harnesses intentionally use owned temporary paths and require explicit environment configuration. The scripts record each response, verify the disposable project before edits and retry only readiness reads across reloads. Set up fresh paths rather than blindly reusing this run's absolute paths or status files. The automatic Unity launcher still selects Python; Rust was started manually and its initialization was verified. Installing the optional example requires separate runtime/Editor assemblies; its corrected namespace is `MCPForUnity.Examples.Editor`.
