# PLAN.md - FSharpened Roadmap

## How to use this file
- Each item has an ID (`P<n>.<n>`), an owner and a status.
- Status: `todo` / `in progress` / `blocked (<why>)` / `done`.
- Before starting: add `- claimed: <agent>, branch <branch>` to the item.
- When done: tick the item, link the PR, update `HANDOVER.md`, add a line to `docs/STATUS.md`.
- Pillar order and dependencies come from `registry/pillars.json`; this file sequences them.

## Phase 0 - Foundation (Weeks 1-4)
- [x] P0.1 - Scaffold repo, solution, tool packaging. **Owner:** Tooling Builder. *(skeleton generated; CI not yet run)*
- [x] P0.2 - Write `README.md`, `AGENTS.md`, `MAINTAINER.md`, `HANDOVER.md`. **Owner:** Documentation
- [x] P0.3 - Write ADR-0001 and ADR-0002 (and founding ADRs 0003-0015). **Owner:** Architect
- [x] P0.4 - Scaffold `.agents/personas/`, `memory/`, `skills/`. **Owner:** Orchestrator
- [x] P0.5 - Write governance, threat model, performance budgets. **Owner:** Architect + Documentation
- [ ] P0.6 - Scaffold `pipelines/benchmarks/` with BenchmarkDotNet and per-RID baselines. **Owner:** QA/Release *(stub only)*
- [x] P0.7 - First compile on a machine with the SDK; fix skeleton build errors. **Owner:** Tooling Builder *(2026-11-16: SDK 9.0.321, win-x64; 0 warnings, 9/9 tests; fixes: explicit FSharp.Core under CPM, RollForward)*

## Phase 0.5 - Statepoint and Semantic Substrates (Weeks 4-8)
- [ ] P0.5.1 - Harden `FSharpened.Platform`: `IFileWatcher` (overflow, polling fallback), `ITransport`, `IPathNormalizer`, `IOverlayStore`, `ISandbox`. Three-OS tests. **Owner:** Pillar Builder *(interfaces + in-memory overlay exist)*
- [ ] P0.5.2 - Harden `FSharpened.Statepoints` (graph, fork/merge provenance, concurrency). **Owner:** Pillar Builder *(single-threaded graph exists)*
- [ ] P0.5.3 - Ship `FSharpened.Semantic` wrapping FsLangMCP as library + MCP server. **Owner:** Pillar Builder
- [ ] P0.5.4 - Ship `FSharpened.ML.Hardware` with ONNX-first inference and TorchSharp-first training chains. **Owner:** Pillar Builder *(inference chain + resolver exist)*
- [ ] P0.5.5 - Ship `FSharpened.ML.Portability` contracts for TorchSharp, DiffSharp, Furnace, ONNX Runtime, ML.NET, ILGPU. **Owner:** Pillar Builder *(TorchSharp, ONNX Runtime drafted)*

## Phase 1 - Compiler Tooling and Diagnostics (Weeks 8-16)
- [ ] P1.1 - Per-file incremental MSBuild cache wrapping `CoreCompile`. **Owner:** Pillar Builder
- [ ] P1.2 - `FSharpened.Diagnostics` with `DiagnosticScope` CE and `Result` pipeline. **Owner:** Pillar Builder
- [ ] P1.3 - `FSharpened.Analyzers` v1: file-order, value-restriction, `Async`/`Task`, nullability. **Owner:** Pillar Builder
- [ ] P1.4 - `dotnet fsh watch` with Harmony-backed statepoint hot reload (~100 ms feedback). **Owner:** Tooling Builder
- [ ] P1.5 - `FSharpened.Debugging` DAP proxy over `netcoredbg` + FCS evaluator + hot-reload symbol snapshot. **Owner:** Pillar Builder
- [ ] P1.6 - `FSharpened.Refactoring` engine with MCP first, LSP second. **Owner:** Pillar Builder

## Phase 2 - Quality, Security, Testing (Weeks 16-24)
- [ ] P2.1 - `FSharpened.Quality` AST-native complexity and CRAP. **Owner:** Pillar Builder
- [ ] P2.2 - `FSharpened.Security` composing dep-scan/SOVA/ShieldedStack/Sigstore + model signing. **Owner:** Pillar Builder
- [ ] P2.3 - `FSharpened.Testing` composing Expecto, FsCheck, Hegel, Hedgehog, AltCover, Stryker.NET + AST mutation. **Owner:** Pillar Builder
- [ ] P2.4 - `FSharpened.Architecture` fitness functions and graphs. **Owner:** Pillar Builder
- [ ] P2.5 - `FSharpened.Telemetry.Feedback` opt-in, privacy-preserving. **Owner:** Pillar Builder

## Phase 3 - Data, Effects, Desktop (Weeks 20-30)
- [ ] P3.1 - `FSharpened.Effects` with `effect { }` CE and OTel context. **Owner:** Pillar Builder *(Effect type exists; CE does not)*
- [ ] P3.2 - `FSharpened.Serialization` AOT-ready JSON/TOML/CSV/Arrow. **Owner:** Pillar Builder
- [ ] P3.3 - `FSharpened.Data` DuckDB/Flowthru/DataFusionSharp + XParsec TOML. **Owner:** Pillar Builder
- [ ] P3.4 - `FSharpened.Desktop` MVU wrapper + `dotnet fsh new desktop`. **Owner:** Pillar Builder
- [ ] P3.5 - `LanguageExtensions`: Collections.Immutable, Option, Async, Pipeline. **Owner:** Pillar Builder

## Phase 4 - ML Platform (Weeks 24-40)
- [ ] P4.1 - `FSharpened.ML.Authoring` `model { }` / `training { }`. **Owner:** Pillar Builder
- [ ] P4.2 - `FSharpened.ML.Data` Frame, Validation, Split, Streaming. **Owner:** Pillar Builder
- [ ] P4.3 - `FSharpened.ML.Experiments` tracking, search, AutoML. **Owner:** Pillar Builder
- [ ] P4.4 - `FSharpened.ML.Registry` signed artifacts, frozen-judge promotion, ONNX export. **Owner:** Pillar Builder
- [ ] P4.5 - `FSharpened.ML.Serving` ONNX AOT, Giraffe, drift, shadow. **Owner:** Pillar Builder
- [ ] P4.6 - `FSharpened.Models`: symbolic fallback first, then APM, first CDM, recursive loop. **Owner:** Pillar Builder *(fallback runner exists)*
- [ ] P4.7 - `FSharpened.ML.TorchSharp-rocm-linux` / `-windows` community packages. **Owner:** Pillar Builder
- [ ] P4.8 - ILGPU kernel backend: custom kernels, CPU simulation, OpenCL fallback. **Owner:** Pillar Builder

## Phase 5 - Hardening and Governance (Weeks 40+)
- [ ] P5.1 - Compiler regression suite against `FSharpened.Analyzers`. **Owner:** QA/Release
- [ ] P5.2 - Publish governance charter; first community RFC for analyzer rules. **Owner:** Orchestrator
- [ ] P5.3 - AOT-only lane for `android-arm64` and `ios-arm64`. **Owner:** QA/Release
- [ ] P5.4 - License inventory and SBOM coverage for every dependency. **Owner:** Documentation + Security
