# FSharpened.Interop.Rust - Rust interop: C ABI, cargo and native library resolution

- **Layer:** core
- **Priority:** P1 | **Phase:** 3.6 | **Stability:** Experimental
- **Depends on:** `FSharpened.Platform`
- **Wraps / composes:** cargo, rustc, cbindgen, csbindgen
- **Gaps addressed:** rust
- **Skill:** [`.agents/skills/add-rust-interop.md`](../../../.agents/skills/add-rust-interop.md)
- **Consumer doc:** [`docs/packages/Interop.Rust.md`](../../packages/Interop.Rust.md)
- **Decision:** [ADR-0019](../../adr/0019-rust-compatibility.md)

## Purpose

F# calls Rust through the C ABI only. RID to Rust target-triple table, native library names and runtimes/<rid>/native layout, a no-#if native library resolver, a cargo build plan and cargo metadata reader that wrap cargo, and the ABI conventions (status codes, UTF-8 ownership, panics never cross) every bound Rust library follows.

## Shape

The project is six small files in compile order, each depending only on the ones above it:

| File | Responsibility | Touches the OS? |
|---|---|---|
| `Targets.fs` | RID to triple table, library file names, `runtimes/<rid>/native` | no |
| `Abi.fs` | status codes, `Abi.check`, UTF-8 ownership helpers | memory marshalling only |
| `Resolver.fs` | search plan (pure), `resolve` (injected `exists`), `install` (`NativeLibrary`) | `install` only |
| `Command.fs` | a command as data, `Command.run` | yes: starts a process |
| `Toolchain.fs` | parses `cargo --version` and `rustc -vV`, `probe` through an injected runner | no |
| `Cargo.fs` | cargo command lines, `cargo metadata` JSON reader, `build` through an injected runner and disk | no |

Everything that can be pure is, and the two effectful seams (running a process, checking a file) are parameters. That is what makes the failure behaviour testable without Rust installed: `tests/FSharpened.Interop.Rust.Tests/PureTests.fs`.

The pillar depends on `FSharpened.Platform` for `RuntimeIdentity` (OS, architecture, RID) and for nothing else. `fsh` (`tools/FSharpened.Cli`) references it for the `rust:` line of `fsh doctor`.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Wraps cargo and rustc (ADR-0006). It does not parse `Cargo.toml`, resolve crates or reimplement a build graph.
- AOT-safe by construction (ADR-0014): `System.Text.Json` document reading rather than reflection serialisation, no `printf`-family formatting, plain `DllImport` for bindings.
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Trust boundary (see `review-threat-model`)

- `cargo build` executes the manifest's build scripts and proc macros. The pillar runs only the manifest path the caller names, with `--locked` by default. `cargo metadata --no-deps` reads manifests and runs no code.
- Loading a native library runs its code. The resolver searches only caller-supplied roots, never the current directory or `PATH`, and in strict mode refuses to fall back to the runtime's default probing.
- The browser IDE never runs cargo and never executes user Rust on our machines: it sends source to Compiler Explorer through the gateway ([ADR-0018](../../adr/0018-browser-ide.md), [ADR-0019](../../adr/0019-rust-compatibility.md)).

## Fallback discipline

Failure behaviour is declared and tested: a missing toolchain, unknown RID, failing cargo, missing library and library-not-found each return a structured `Error` (or a strict `DllNotFoundException` listing every path tried) and never a guess. A Rust panic is caught on the Rust side and returned as `AbiStatus.Panicked`. See the failure table in the [consumer doc](../../packages/Interop.Rust.md).

## Status

Active, Experimental. Contract tests exist and pass on win-x64; the other five RIDs are verified by the CI matrix, not yet by a run. Open follow-ups: binding generation (wrap `csbindgen`, own ADR), WebAssembly as an alternative boundary, `staticlib` linking into NativeAOT output, an MSBuild integration.
