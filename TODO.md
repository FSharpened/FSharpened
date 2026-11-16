# TODO.md - Current Sprint

Pulled from `PLAN.md` and `HANDOVER.md`.

## P0 - Blocking
- [x] **P0.7** - (done 2026-11-16, win-x64 only) On a machine with .NET SDK 9.0.200+: `dotnet build FSharpened.slnx`, fix any skeleton compile errors, run `dotnet test`. Record results in `HANDOVER.md`.
- [ ] **P0.5.2** - Make `StatepointGraph` thread-safe; add concurrent fork/merge tests. `src/FSharpened.Statepoints/Statepoints.fs`.
- [ ] **P0.5.4** - Replace the static availability probe in `Resolver.resolve` with real ONNX Runtime provider discovery per RID. `src/FSharpened.ML.Hardware/Hardware.fs`.
- [ ] **P1.4** - Harmony-based method patching in `dotnet fsh watch`; target <500 ms round-trip on `linux-x64` and `win-x64`.

## P1 - This Sprint
- [ ] **P1.1** - Per-file incremental cache as an MSBuild task wrapping `CoreCompile`; benchmark against `MSBuild.CompilerCache`.
- [ ] **P1.3** - Value-restriction diagnostic with two one-click fixes (`src/FSharpened.Analyzers/Rules/ValueRestriction.fs`).
- [ ] **P1.5** - F#-aware expression evaluator in the debug adapter (embed FCS, compile to delegates).
- [ ] **P2.1** - AST-native complexity: `match` cases are decision points, not IL branches.
- [ ] Write real `.github/repo-policy.md` and fill `pipelines/benchmarks/` baselines.

## P2 - Backlog
- [ ] `FSharpened.ML.TorchSharp-rocm-windows` HIP SDK detection.
- [ ] Extend `check_docs.py` with a semantic API-signature check (needs `FSharpened.Semantic`).
- [ ] `FSharpened.Telemetry.Aot` source-generated instrumentation.
- [ ] Replace `LICENSE` placeholder with the full Apache-2.0 text (human).

## Blocked
- [ ] **P1.4 (FCS-native path)** - Blocked on dotnet/sdk#55128. The Harmony path is unblocked.
- [ ] **P0.5.4 (ROCm on Windows)** - Blocked on AMD HIP SDK availability on CI runners. DirectML fallback is active by design.
