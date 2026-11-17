# ADR-0019: Rust compatibility: Rust in the browser IDE and a C-ABI interop pillar

- **Status:** Proposed (needs human acceptance; the IDE half is already shipped on the PR branch under the ADR-0018 gateway)
- **Date:** 2026-11-16
- **Deciders:** maintainer (request in chat), Architect, Reviewer

## Context
Rust is the language F# teams most often reach for when they need a native hot path, a memory-safe system library, or a WebAssembly module. Two things were asked for: Rust in the browser IDE, and "proper Rust compatibility" in FSharpened itself.

Constraints that shape the answer:
- Invariant 1 and 4: nothing may be platform-privileged, and the six-RID matrix (win/linux/osx x x64/arm64) must stay green. Rust has a different vocabulary for platforms (target triples) than .NET (RIDs), and the two do not map one-to-one.
- Invariant 11 and ADR-0006: wrap, do not rebuild. `cargo`, `rustc`, `cbindgen` and `csbindgen` already exist.
- Hard rule: never fork the F# compiler. A bridge to Rust must not need one.
- ADR-0014: NativeAOT is validated per RID, so any binding strategy must stay trimming- and AOT-friendly.
- Rust has no stable ABI. The only stable, tool-supported boundary between Rust and .NET is the C ABI.

## Decision
1. **Rust is the fifth language in the browser IDE**, through the same seam as the other four (ADR-0018). The gateway allowlists three Compiler Explorer `rust` compilers (1.99, 1.96, 1.90) and a token-by-token `rustc` flag allowlist: edition, `-O`, `-g`, lint levels, `-C opt-level|debuginfo|overflow-checks|debug-assertions|panic|codegen-units|lto|strip|target-cpu|target-feature`. Anything that reaches the sandbox, the linker, the target or unstable internals (`-Z`, `--emit`, `-o`, `-L`, `-l`, `--extern`, `--sysroot`, `--print`, `--target`, `-Clinker`, `-Clink-arg`, `-Cllvm-args`, `-Cpasses`) is absent from the list, not blacklisted. The F# core parses rustc diagnostics (multi-line `error[E0425]:` plus the `-->` location, lint warnings, runtime panics with exit code 101) into `StructuredDiagnostic`, and a Rust-aware bracket check understands lifetimes, labels, raw strings and nested block comments so local checks never invent errors.
2. **The boundary between F# and Rust is the C ABI, and only the C ABI.** Rust builds a `cdylib` (or `staticlib`); F# binds to it with `DllImport`/`NativeLibrary`. No code generator, no reflection, no custom marshaller: this stays AOT-compatible per ADR-0014.
3. **A new pillar, `FSharpened.Interop.Rust`** (layer core, Experimental), owns the parts that are specific to the F#/Rust seam and that nobody else provides:
   - the **RID to Rust target triple** table for the six Tier 1 RIDs and the reverse lookup, with the **native library file name** per OS (`name.dll`, `libname.so`, `libname.dylib`) and the `runtimes/<rid>/native/` layout NuGet and `dotnet publish` already use;
   - a **native library resolver** (a pure search plan, plus a thin `NativeLibrary` hook) so a bound library is found next to the app, under `runtimes/<rid>/native/`, or in a configured directory, without `#if`;
   - a **cargo build plan** and a **`cargo metadata` reader** that wrap `cargo` rather than reimplement it: given a manifest and a RID, produce the exact command line, find the `cdylib` target and its output path;
   - **ABI conventions** every FSharpened-bound Rust library follows: status-code returns, `fsh_last_error`, UTF-8 strings owned by the library and released with `fsh_free_string`, panics caught at the boundary and never unwound across it;
   - a **toolchain probe** (`cargo`/`rustc` presence and version, host triple) that reports, never installs.
4. **Platform behaviour stays in `FSharpened.Platform`.** The pillar depends only on Platform and reads `RuntimeIdentity`; it contains no `#if`. All OS differences are data in the mapping table.
5. **CI.** One extra stage, `interop-rust`, builds the example crate (`native/rust/fsh-interop-example`) for the host RID and calls it from F#. It is skipped with a warning when `cargo` is absent locally and fails when `--require-rust` is given. The workflow passes `--require-rust` on all six RIDs.
6. **Out of scope for this decision:** Rust code in any FSharpened hot path, building Rust from MSBuild during `dotnet build`, automatic binding generation (wrap `csbindgen` later under its own ADR), exporting F# to Rust (NativeAOT `UnmanagedCallersOnly`), and a Rust analyzer or LSP for F# users (use rust-analyzer).

## Consequences
- The IDE depends on Compiler Explorer for Rust the same way it does for C and C++ (ADR-0018 consequences apply). Rust programs that need crates cannot run in the IDE; only `std` programs do.
- A fifth rustc flag grammar to keep equal in two places (`gateway/src/policy.ts`, `tools/FSharpened.Ide/Engines.fs`); a gateway test pins the compiler ids to `Engines.fs`.
- Adds a pillar (29) and a test project. The mapping table must be revisited when Rust promotes or demotes a Tier 1 target.
- Developers need a Rust toolchain only if they use the pillar's build or interop stage; the rest of the repository does not.
- Threat model (`review-threat-model`): the pillar executes `cargo`, which runs `build.rs` and proc macros from the manifest. It only runs manifests the caller names, passes `--locked`, never runs untrusted manifests, and the IDE never runs cargo at all. Loading a native library executes its code; the resolver therefore searches only caller-supplied roots and never the current directory or `PATH`.
- Dependencies: the example crate has none outside `std`. No new .NET package dependency. (`docs/governance/licensing.md` unchanged.)

## Alternatives considered
- **Bind through WebAssembly (wasmtime) instead of the C ABI.** Safer isolation, but adds a runtime dependency, slower calls, and no AOT story on all six RIDs. Deferred; the table and resolver do not preclude it.
- **Generate bindings with csbindgen inside this repository.** It is the right long-term tool, and it should be wrapped (ADR-0006), but it adds a build-time dependency and needs its own ADR. The conventions in this ADR are what it would target.
- **Use P/Invoke against Rust's unstable "extern Rust" ABI or `repr(Rust)` types.** There is no ABI to rely on; layouts change between compiler versions.
- **Make Rust a core hot path (for example the diagnostics parser).** Violates "an extension, not a rewrite", multiplies the toolchain requirements of every contributor, and has no measured benefit.
- **A separate repository for Rust interop.** Splits the matrix, the docs map and the conventions; the example crate is tiny and belongs beside its tests.
