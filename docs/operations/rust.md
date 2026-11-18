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
- The `rust` stage of `python tools/ci/fsh_ci.py run` runs `cargo test --locked` for **every crate under `native/rust`** (each needs a committed `Cargo.lock`), then `cargo fmt --check` and `cargo clippy -D warnings` for each when those components are installed (it notes it when they are not). A crate in which zero Rust tests executed fails the stage.
- `--require-rust` turns a missing `cargo` from a skip into a failure and sets `FSH_REQUIRE_RUST=1` for the test stage. GitHub CI passes it; a laptop without Rust does not need to.
- Rust files, `Cargo.toml` and `Cargo.lock` of every crate are part of the cache key of the build, test and AOT stages, so editing a crate re-runs them.
- The Host project builds `fsh-watch` itself (see below), so `dotnet build` needs no separate step; with `--require-rust` the Host tests fail, rather than pass quietly, when that library is missing.

### The second crate: `fsh-watch` (PLAN P7.8a)
`native/rust/fsh-watch` is the first library built for a product, not as an example: file watching and fast search for the desktop IDE ([desktop.md](desktop.md)). It follows the same ABI conventions (status codes, thread-local last error, UTF-8 ownership, panics caught). Results are JSON documents, which the F# side reads with `JsonDocument` (no reflection).

| Function | What it does |
|---|---|
| `fsh_watch_abi_version() -> u32` | ABI version, 1; checked once when the library is loaded |
| `fsh_watch_start(root, ignore_csv, debounce_ms, &handle)` | starts a recursive watch; `ignore_csv` null means `.git,node_modules,bin,obj,target` |
| `fsh_watch_poll(handle, timeout_ms, &json)` | waits for the next batch: `{"overflow":bool,"events":[{"kind":"created\|changed\|removed\|renamed","path","from"?,"dir"}]}`, or null when nothing happened. Paths are relative with `/` |
| `fsh_watch_stop(handle)` | ends the watch; a poll waiting on it returns |
| `fsh_search(root, query, flags, include, exclude, ignore_csv, max_hits, max_per_file, max_file_bytes, &json)` | `flags`: 1 case-sensitive, 2 whole word, 4 regex, 8 honour `.gitignore`. Result: the managed `SearchResult` shape (`files[].hits[]` with 1-based line, UTF-16 column and length, preview) |
| `fsh_watch_last_error()`, `fsh_watch_free_string(p)` | the usual pair |

How it behaves, and what it does not promise:
- The watcher uses the `notify` crate and does its own batching: events collect until `debounce_ms` of quiet (or until a burst has lasted ten debounces, one second at least), then each path is reported once, **judged by what is on disk when the batch closes**, not by the order of the raw events (the three systems disagree on that order). Create then remove vanishes; remove then create is *changed*; a folder whose contents changed is not reported. If more than 5000 paths changed, or the system dropped events, the batch says `overflow` and carries no list; the caller looks at the folder again. Ignored folders are still watched by the operating system (inotify and ReadDirectoryChangesW have no exclusion), their events are dropped afterwards.
- Search reads files on up to 8 threads in batches and takes the results in explorer order (files before folders, case-insensitive), so a result cap cuts at the same place every time. It uses the `regex` crate (linear time, no look-around or back-references) and the `ignore` crate for `.gitignore`. A pattern that does not compile is reported in the JSON `error`; the F# side then asks the managed engine.
- Columns and lengths count UTF-16 code units, like .NET strings, so a hit after an emoji lines up in the editor.

Build and test:
```bash
cargo test --locked --manifest-path native/rust/fsh-watch/Cargo.toml                       # the Rust side: 57 tests, incl. a real watch of a temp folder
dotnet test tests/FSharpened.Ide.Host.Tests --filter FullyQualifiedName~FolderTests         # the F# side: pure parts, fallbacks, native engines called for real
```
The Host project's build runs `cargo build --release --locked` for this crate when `cargo` is on the PATH and copies the library to `runtimes/<rid>/native/` next to the program (`FshSkipRust=true` skips it). Without cargo nothing is built, the IDE uses its managed search and polling watcher, and `FolderTests` pass without calling the library unless `FSH_REQUIRE_RUST=1`.

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
Verified on Windows x64 with a real toolchain (cargo 1.99): the crate builds and its tests pass, the F# tests call into the built library (arithmetic, status codes, last error, UTF-8 ownership, caught panics, resolver failures), and the toolchain probe parses real `cargo --version` and `rustc -vV` output. **Verified for `fsh-watch` on Windows x64 only:** the 57 Rust tests, the F# tests including native-versus-managed search parity, and the desktop app running with the library (an outside change reached the explorer and an open tab; a native search listed results). **Not yet verified:** the other five matrix RIDs (the GitHub matrix is the first place that happens), musl, NativeAOT publish of a program that binds the library, and callbacks from Rust into .NET (out of scope, see ADR-0019).
