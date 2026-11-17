# FSharpened.Interop.Rust

F# calls Rust through the C ABI, and only the C ABI. This pillar owns the parts of that seam nobody else provides: the RID to Rust target-triple table, native library names and the `runtimes/<rid>/native` layout, a native library resolver with no `#if`, a cargo build plan and `cargo metadata` reader that wrap cargo, and the ABI conventions every bound Rust library follows. Decision record: [ADR-0019](../adr/0019-rust-compatibility.md) (Proposed).

| | |
|---|---|
| NuGet id | `FSharpened.Interop.Rust` |
| Stability tier | Experimental |
| Depends on | `FSharpened.Platform` |
| Priority / phase | P1 / 3.6 |
| Source | `src/FSharpened.Interop.Rust/` |
| Reference library | `native/rust/fsh-interop-example/` (Rust, `cdylib`, `std` only) |
| Tests | `tests/FSharpened.Interop.Rust.Tests/` (pure contract tests plus real calls into the reference library) |
| Skill | [`add-rust-interop`](../../.agents/skills/add-rust-interop.md) |

## Public API

All in namespace `FSharpened.Interop.Rust`.

| Module / type | What it gives you |
|---|---|
| `RustTarget`, `Targets` | The eight known RIDs (six CI-matrix RIDs plus `linux-musl-x64` and `linux-musl-arm64`) with their Rust triples, `tryOfRid`, `tryOfTriple`, `tryOfIdentity`; `libraryStem` (cargo's `-` to `_`), `libraryFileName os kind stem`, `nativeDirectory`, `nativeRelativePath` |
| `NativeResolver`, `ResolveOutcome` | `searchPlan` and `planFor` (pure ordered candidate paths), `resolve` (existence check injected), `install` (one `DllImportResolver` per assembly, strict by default) |
| `AbiStatus`, `AbiError`, `Abi`, `Utf8` | The status-code table, `Abi.check` (code to `Result`, fetching the library's message only on failure), `Utf8.withBorrowed` and `Utf8.takeOwned` for strings across the boundary |
| `CommandPlan`, `RunResult`, `Command` | An external command as data (program plus argument vector, never a shell string) and `Command.run`, which reports a program that cannot start as a result, not an exception |
| `RustToolchain`, `Toolchain` | `probe` (cargo and rustc versions and host triple through an injected runner), version and edition checks. It reports; it never installs |
| `BuildOptions`, `BuiltLibrary`, `CargoMetadata`, `Cargo` | `metadataPlan`, `buildPlan`, `parseMetadata`, `cdylibTargets`, `libraryPath`, and `build` (metadata, then build, then the library path, with the runner and the disk injected) |
| `Pillar` | `Id`, `Stability`, `Status` literals |

## Usage

Bind with plain `DllImport`. The library name is the crate's library target name (`fsh_interop_example`), not a file name:

```fsharp
open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open FSharpened.Platform
open FSharpened.Interop.Rust

module Native =
    [<Literal>]
    let Library = "fsh_interop_example"

    [<DllImport(Library)>]
    extern int fsh_add(int a, int b)

    [<DllImport(Library)>]
    extern int fsh_divide(int a, int b, int& quotient)

    [<DllImport(Library)>]
    extern nativeint fsh_last_error()

    [<DllImport(Library)>]
    extern void fsh_free_string(nativeint text)

let lastError () =
    Utf8.takeOwned Native.fsh_free_string (Native.fsh_last_error ())

// 1. Development: build the crate for this machine (wraps cargo; --locked by default in the options below).
let identity = RuntimeIdentity.detect ()

let options =
    { Manifest = "native/rust/fsh-interop-example/Cargo.toml"
      Rid = identity.Rid
      Profile = Release
      Locked = true
      TargetDir = None }

match Cargo.build (Command.run 600000) File.Exists options None with
| Error message -> failwith message
| Ok library ->
    // 2. Tell the runtime where the library is. Once per assembly; strict means a miss throws and lists every path tried.
    let plan = NativeResolver.planFor identity [ Path.GetDirectoryName library.Path ] library.Stem
    NativeResolver.install (Assembly.GetExecutingAssembly()) (Map [ library.Stem, plan.Value ]) true

    // 3. Call it. A status code becomes a Result with the library's own message.
    let mutable quotient = 0

    match Abi.check lastError (Native.fsh_divide (7, 0, &quotient)) with
    | Ok () -> printfn "%d" quotient
    | Error e -> printfn "%s: %s" (AbiStatus.name e.Status) e.Message   // invalid argument: cannot divide 7 by 0: division by zero
```

**Shipping a library** is the same minus step 1: place the file at `runtimes/<rid>/native/<file>` inside the NuGet package (or beside the app), and resolve with `NativeResolver.planFor identity [ AppContext.BaseDirectory ] "fsh_interop_example"`. `dotnet publish` copies that layout for the target RID.

**Command line.** `fsh doctor` prints a `rust:` line (cargo and rustc versions, host triple, the FSharpened RID it maps to) and says plainly when Rust is not installed.

## ABI conventions for Rust libraries

The reference library `native/rust/fsh-interop-example` is the executable form of these rules; copy it.

1. **C ABI only.** `extern "C"`, `#[unsafe(no_mangle)]` (edition 2024), crate type `cdylib` (or `staticlib` for NativeAOT linking). Rust has no stable ABI: never expose `repr(Rust)` layouts, generics, trait objects, `String`, `Vec` or `Option` directly.
2. **Status codes.** A fallible function returns `i32` and writes results through out-pointers. The table only grows:

   | Code | `AbiStatus` | Meaning |
   |---|---|---|
   | 0 | `Success` | |
   | 1 | `NullPointer` | a required pointer was null |
   | 2 | `InvalidUtf8` | a string was not valid UTF-8 |
   | 3 | `InvalidArgument` | a well-formed call with a value the function rejects |
   | 4 | `Panicked` | a Rust panic was caught at the boundary |
   | 5 | `Failed` | any other failure |

3. **Last error.** After a failure, `fsh_last_error()` returns a newly allocated message for the calling thread, or null if the last call succeeded. It is thread-local: read it on the same thread, immediately after the failing call. Every export clears it first.
4. **Strings.** NUL-terminated UTF-8. A string the library returns is owned by the library and must go back to `fsh_free_string` (null is allowed); a string the caller passes in is only borrowed for the call. Never free Rust memory with the .NET allocator or the reverse.
5. **Panics never cross.** Unwinding out of an `extern "C"` function aborts the process. Every export runs inside `catch_unwind` and reports `Panicked`. Therefore keep `panic = "unwind"` in the release profile: `panic = "abort"` makes every panic a process abort.
6. **Version handshake.** `fsh_abi_version() -> u32`, bumped when a function is removed or a signature changes. Check it once after loading.
7. **No global state without a lock; no callbacks into .NET on foreign threads** in this version. Callbacks need their own ADR.

## Layout and platforms

- RID to triple is data in `Targets.all`; there is no `#if` anywhere in the pillar (Invariant 1). Adding a platform is one row plus a test.
- Cargo writes `target/<triple>/<debug|release>/` when given `--target`; the pillar always passes `--target`, so the output path is predictable and a cross build never mixes with host build scripts.
- File names: `name.dll` on Windows, `libname.so` on Linux and Android, `libname.dylib` on macOS, `libname.a` for static archives (`name.lib` on Windows). Cargo replaces `-` with `_` in the stem.
- Cross builds need the target installed (`rustup target add <triple>`); the pillar reports cargo's error, it does not install targets.

## Failure behaviour

| Situation | Behaviour |
|---|---|
| Rust not installed | `Toolchain.probe` returns `Error` naming rustup; `fsh doctor` prints "not found (optional)"; the CI stage skips, or fails with `--require-rust` |
| Unknown RID | `Cargo.buildPlan` returns `Error` listing the known RIDs; nothing is run |
| cargo fails | `Cargo.build` returns `Error` with the command line and the last 15 lines of output |
| cargo succeeds but the library is missing | `Error` naming the expected path |
| Library not found at load time | with `install ... true` (strict) a `DllNotFoundException` lists every path tried; the current directory and `PATH` are never searched |
| A Rust panic | `AbiStatus.Panicked` plus the panic message; the host process survives |

## Verified

On win-x64 (rustc 1.99.0, cargo 1.99.0, .NET SDK 9): 55 xunit tests in `tests/FSharpened.Interop.Rust.Tests` pass, including 12 that build the reference crate with real cargo and call it through `DllImport`; 9 Rust unit tests pass; clippy and rustfmt are clean. **Not yet verified:** the other five RIDs, `linux-musl-*`, NativeAOT publish of `fsh` with the Interop.Rust reference (the `aot` stage checks it in CI), cross-compilation. The CI workflow runs the stage on all six RIDs; `win-arm64` is the experimental runner.

## What it does not do

- It is not a fork, a compiler patch, or a change to `FSharp.Core`.
- It does not reimplement an existing tool it can wrap (Invariant 11): cargo builds, rustup installs, `cbindgen` and `csbindgen` generate headers and bindings. This pillar wraps cargo and does not parse `Cargo.toml` or resolve dependencies.
- It does not contain `#if` on a platform (Invariant 1); platform behaviour is data in the target table.
- It does not install Rust, targets or components, and it never runs `rustup`.
- It does not build Rust during `dotnet build`, and it adds no MSBuild target. Build the library first, or ship it prebuilt.
- It does not generate bindings. The `extern` block is written by hand; automatic generation (`csbindgen`) needs its own ADR.
- It does not export F# to Rust (NativeAOT `UnmanagedCallersOnly`), call into .NET from foreign threads, or run Rust in the browser IDE's sandbox beyond `std` programs (see [Rust in the IDE](../operations/rust.md)).
- It does not isolate the Rust code: a loaded native library runs with the process's full privileges. Load only libraries you built or trust.

> FSharpened is an independent community project. It is not affiliated with, endorsed by, or a replacement for Microsoft's F# compiler or the F# Software Foundation.
