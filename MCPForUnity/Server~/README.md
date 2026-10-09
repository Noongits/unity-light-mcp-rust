# Packaged native Rust server

Unity's **Start Server** button runs these prebuilt executables directly. Python,
uv, Git, Cargo, and a terminal are not required at runtime. Unity copies the
appropriate binary to a per-user, SHA-256-versioned cache before launching it.
The checked-in manifest records the Rust source revision and each binary's hash.

Included targets: Windows x86-64 (MSVC, static CRT), macOS arm64/x86-64, and Linux
x86-64 (glibc 2.28 or newer). The Windows build imports only Windows system DLLs;
it does not require a separate Visual C++ runtime installation.

The binaries are built from `ServerRust/` with `Cargo.lock` and `--release --locked`.
Native target builds use `cargo build`; the Windows build made on macOS uses
`cargo xwin build --target x86_64-pc-windows-msvc` with
`RUSTFLAGS='-C target-feature=+crt-static -C strip=symbols'`. The Linux build uses
`cargo zigbuild --target x86_64-unknown-linux-gnu.2.28`. Cross-compilation does not
certify the complete Unity interaction on that operating system.

When changing Rust code, rebuild the included binaries and regenerate the hashes
in `manifest.json`; do not update a binary without updating its checksum. Keep
this folder when distributing the Unity package. The trailing `~` prevents Unity
from importing these executables as project assets.

The server license is in the repository's `LICENSE` and `ServerRust/LICENSE`.
Dependency licenses are reproduced in `THIRD_PARTY_NOTICES.md`; versions are pinned
in `Cargo.lock`. See `Verification/NATIVE_LAUNCH.md` for
launch and regression evidence.
