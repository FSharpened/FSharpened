# Rust in FSharpened (ADR-0019)

Two separate things share the name, and they fail independently:

| | What it is | Where it runs | Needs on your machine |
|---|---|---|---|
| **Rust in the IDE** | A tab in the browser IDE that compiles and runs `std` Rust programs | A remote Compiler Explorer sandbox, through the gateway | Nothing |
| **`FSharpened.Interop.Rust`** | The pillar that lets F# call a Rust `cdylib` through the C ABI | Your machine, your CI | Rust (`cargo`, `rustc`) when you build a crate; nothing at run time |

Both are Proposed in [ADR-0019](../adr/0019-rust-compatibility.md) and flip to Accepted only when a human accepts them. The pillar's API and the ABI rules are in [Interop.Rust](../packages/Interop.Rust.md); this page is the operator's view.

## Rust in the IDE

- **Compilers:** Rust 1.99, 1.96 and 1.90 (`r1990`, `r1960`, `r1900`), default flags `-Copt-level=2 --edition=2024`. The first is tried first; the other two are the fallback siblings (ADR-0012).
- **Flags** are allowlisted token by token in `gateway/src/policy.ts` (`RUST_FLAG`): edition, optimisation level, lint levels, a few `-C` codegen options. Anything that could read or write the sandbox, pick a linker, change the target or reach unstable internals (`-Z`, `--emit`, `-o`, `-L`, `-l`, `--extern`, `--sysroot`, `-Clinker`, `-Clink-arg`, `--print`, `--target`) is simply not in the list. C flags are rejected for Rust and the reverse.
- **Diagnostics:** rustc's multi-line errors are folded into one editor marker with the error code, the position and the `doc.rust-lang.org/error_codes` link. A run-time panic becomes an error pinned to the panicking line. The trailing `aborting due to` and `warning emitted` summary lines are dropped.
- **Offline checks** (layer 3 of the fallback chain) understand Rust lifetimes, raw strings, nested block comments and char literals, and warn when there is no `fn main`.
- **Starters:** hello, enums and iterators, and `rust-ffi`, which shows the C-ABI export shape F# binds to.
- **Limits:** one file, `std` only (no crates), no network, a short time limit, output cached for an hour.

Run a Rust program from a script (this is what the live check does; it needs an allowed `Origin`):

```bash
curl -s https://ide.fsharpened.workers.dev/api/run \
  -H 'origin: https://fsharpened.pages.dev' -H 'content-type: application/json' \
  -d '{"compiler":"r1990","language":"rust","options":"-Copt-level=2 --edition=2024","source":"fn main() { println!(\"hi\"); }","stdin":""}'
```

## Rust interop (`FSharpened.Interop.Rust`)

### Prerequisites
Install Rust with [rustup](https://rustup.rs). `cargo --version` and `rustc --version` must work from the shell that runs the build. Edition 2024 needs Rust 1.85 or newer. `fsh doctor` prints a `rust:` line that tells you what it found, and says plainly when Rust is absent. The pillar never installs a toolchain or a target.

### Build and run the reference library
```bash
cargo test --locked --manifest-path native/rust/fsh-interop-example/Cargo.toml   # the Rust side: 9 tests
dotnet test tests/FSharpened.Interop.Rust.Tests                                   # the F# side: 55 tests, real calls into the library
```
Without `cargo` the 12 tests that need the built library pass without calling it (the pure contract tests still run); with `FSH_REQUIRE_RUST=1` they fail instead, so a CI job cannot go green on a runner that has no Rust.

### In CI
- The `rust` stage of `python tools/ci/fsh_ci.py run` runs `cargo test --locked` for the reference crate, then `cargo fmt --check` and `cargo clippy -D warnings` when those components are installed (it notes it when they are not). A run in which zero Rust tests executed fails.
- `--require-rust` turns a missing `cargo` from a skip into a failure and sets `FSH_REQUIRE_RUST=1` for the test stage. GitHub CI passes it; a laptop without Rust does not need to.
- Rust files and `Cargo.lock` are part of the cache key of the build, test and AOT stages, so editing the crate re-runs them.

### Shipping a library
Put the file at `runtimes/<rid>/native/<file>` in the NuGet package and resolve it with `NativeResolver.planFor identity [ AppContext.BaseDirectory ] "<crate lib name>"`. The RID to target-triple table (`Targets.all`) covers the six CI-matrix RIDs plus `linux-musl-x64` and `linux-musl-arm64`. `dotnet publish` for a RID copies the layout.

### Troubleshooting
| Symptom | Cause and fix |
|---|---|
| `fsh doctor` says `rust: not installed` | Rust is not on `PATH` for that shell. Open a new shell after installing, or add `~/.cargo/bin`. |
| `DllNotFoundException` listing every path tried | The strict resolver found no file. The message names the search plan; build the crate for this RID or copy the file to the first listed path. |
| `can't find crate` or target errors on a cross build | `rustup target add <triple>` (see `Targets.all`). The pillar reports cargo's error and does not install targets. |
| Process aborts when Rust panics | The release profile has `panic = "abort"`. Keep `panic = "unwind"`; every export runs in `catch_unwind` and returns status 4. |
| `error: edition 2024 is unstable` | Rust older than 1.85. `rustup update`. |
| `Cargo.lock` out of date under `--locked` | Run `cargo update -p <crate>` or `cargo generate-lockfile` in the crate and commit the lock file. |

### What is verified, and what is not
Verified on Windows x64 with a real toolchain (cargo 1.99): the crate builds and its tests pass, the F# tests call into the built library (arithmetic, status codes, last error, UTF-8 ownership, caught panics, resolver failures), and the toolchain probe parses real `cargo --version` and `rustc -vV` output. **Not yet verified:** the other five matrix RIDs (the GitHub matrix is the first place that happens), musl, NativeAOT publish of a program that binds the library, and callbacks from Rust into .NET (out of scope, see ADR-0019).
