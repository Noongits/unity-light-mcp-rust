# Public source publication

This checkout was prepared from `unity-mcp-light-reviewed.zip` after the completed independent review. Application source and regression tests retain the reviewed contents. The original download and delivered ZIP remain unchanged.

Publication adjustments:

- README identifies native Rust setup and uses this repository's Unity package URL.
- The Rust README points to completed verification instead of the earlier unverified status.
- Verification text replaces the local user's home path with `/Users/reviewer/`.
- Raw runtime logs, generated executables, environment files, and local agent settings are excluded from Git. Structured test/profile results and capture images are retained.
- Upstream workflows are preserved outside GitHub's active workflow directory pending configuration for this repository.
- Original license and attribution notices are preserved.

A Gitleaks 8.30.1 scan of the complete extracted project found 58 candidates: 54 source-file SHA-256 checksums, three WebSocket test nonces, and one synthetic test API key. These were inspected as false positives; no real credential was found. A separate token/private-key/credential-URL scan found only test fixtures and source expressions. This is a scan of these files, not a guarantee against every possible secret format.
