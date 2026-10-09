# Final static validation record

Date: 2026-10-09. These checks apply to the full source package after the
file-by-file C# review. They are not Unity compilation or runtime results.

## Source coverage

- 696/696 C# files from the input working tree source-reviewed.
- 32 additional helper/regression C# files source-reviewed.
- 728 C# files in the final package; exact paths in `CSHARP_REVIEW_COVERAGE.md`.
- Final focused integration reviews checked transport/task ownership, new
  fixtures, configuration/key-store contracts, provider results, and the vendor
  upload-generation interface chain. No blocking introduced mismatch was found
  by those source checks; unresolved cases remain in `CSHARP_FULL_REVIEW.md`.

## Executed static checks

- Tree-sitter C# parsing with two representative conditional-compilation symbol
  profiles: **1,454 of 1,456 checks passed** across 728 files.
- Both remaining parser flags are the same file:
  `MCPForUnity/Editor/Services/AssetGen/FalModelSchema.cs`. Its contextual identifier
  `field` triggers the parser in both profiles, including the unmodified Git
  baseline. This is a known parser limitation for this source; do not interpret
  the flag as either a successful compile or a newly introduced compiler error.
- Conditional profiles exercise source branches; they do not emulate complete
  Unity version/package symbol combinations or resolve types/assemblies.
- CRLF-aware `git diff --check`: passed.
- Newly added `.meta` GUID collision check against the package: no collisions.
- Focused source assertions and small fake-state models were executed for several
  fixes. They are not NUnit execution, native API tests, or Unity integration tests.

## Not executed

No C# compiler or Unity Editor was available. The repository's Unity matrix
commands skipped all four configured versions. New and changed C# NUnit fixtures
were authored/reviewed but **not compiled, discovered, or run in Unity**. Graphics,
domain reload, native setters, Undo, provider/account workflows, actual keychains,
process termination, and GPU-memory behavior remain unverified.

Some tests deliberately remain Explicit, environment-dependent, or capable of
global project effects. Run them only after reviewing their prerequisites in a
disposable project. Count skipped/undiscovered tests separately from passes.

## Rust checks

The Rust source was not changed by this C# pass. As a final check,
`cargo test --all-targets --locked` passed again: **142 tests** (134 library,
8 binary). Earlier locked release/lint and 23 stdio/HTTP/cleanup test groups are
recorded in `ServerRust/VALIDATION.md`. They use fake Unity peers and cannot prove
the modified C# package compiles or behaves correctly.

## Next validation step

Use `REVIEW_HANDOFF_PROMPT.md`. Compile first, verify test discovery, then run
targeted timeline regressions and actual Unity memory profiling. Keep the known
remaining risks visible; source coverage is not a leak-free certification.


## Independent Unity verification — 2026-10-09

The archive was independently compiled, tested, profiled and repaired in disposable Unity projects. See [INDEPENDENT_REVIEW.md](INDEPENDENT_REVIEW.md) and `Verification/` for current findings, executed checks, skips and remaining risks. Earlier validation statements in this document describe the historical source-review boundary.
