---
name: add-rust-interop
description: Procedure for changing the Rust interop pillar (FSharpened.Interop.Rust): the C-ABI conventions, the RID to Rust target table, the native resolver, the cargo wrapper, or the reference Rust crate.
---

# add-rust-interop

Procedure for changing the Rust interop pillar (`FSharpened.Interop.Rust`) and its reference crate. Two kinds of change share this skill; do the one that applies, then the common steps.

## A. Change the ABI (add or change an export, a status code, a convention)

1. Read `docs/packages/Interop.Rust.md` (the conventions) and [ADR-0019](../../docs/adr/0019-rust-compatibility.md). A new status code or a changed signature changes the ABI: write or update the ADR first.
2. Change the Rust crate `native/rust/fsh-interop-example/src/lib.rs` first, with a Rust unit test. Every export goes through `guard` so a panic cannot cross; strings are NUL-terminated UTF-8 and freed with `fsh_free_string`.
3. Bump `FSH_ABI_VERSION` (and the expectation in `InteropTests.fs`) if a function was removed or a signature changed. Status codes only ever grow; mirror new ones in `AbiStatus` in `Abi.fs`.
4. Add the `extern` declaration and a test in `tests/FSharpened.Interop.Rust.Tests/InteropTests.fs` that calls the new export through `DllImport`, covering the failure path and the last-error message.
5. Update the ABI tables in `docs/packages/Interop.Rust.md`.

## B. Add a platform, or change how libraries are found or built

1. Add a row to `Targets.all` in `src/FSharpened.Interop.Rust/Targets.fs` (RID, Rust triple, OS, architecture, whether it is in the CI matrix) and a test in `PureTests.fs`. Never add `#if`: platform behaviour is data (Invariant 1).
2. Changes to `Cargo.fs`, `Resolver.fs` or `Command.fs` keep the effectful seams injectable. A new failure mode gets a pure test with a fake runner before any real-cargo test.
3. A change that runs more code (a new cargo subcommand, `build.rs` handling, a wider search path) is a trust-boundary change: run `review-threat-model`.

## Common steps

1. Run `cargo fmt`, `cargo clippy --locked --all-targets -- -D warnings` and `cargo test --locked` in `native/rust/fsh-interop-example`.
2. Run `FSH_REQUIRE_RUST=1 dotnet test tests/FSharpened.Interop.Rust.Tests`. Without that variable the integration tests pass quietly when cargo is absent; with it a missing toolchain fails.
3. Run `python tools/ci/fsh_ci.py run --only rust --require-rust`, then `python tools/ci/fsh_ci.py run --profile fast`.
4. Run `run-platform-matrix`; no `#if` on a platform may appear in product code.
5. Run `update-docs-map` and `python tools/scripts/check_docs.py`.
6. Open the PR and request the Reviewer persona. Do not merge your own work, and do not flip ADR-0019 to Accepted yourself.

## Done when

CI is green on all six RIDs (the `rust` stage and the Interop.Rust tests ran, not skipped), docs-drift passes, and the Reviewer persona has signed off.
