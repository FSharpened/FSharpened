# FSharpened Specification v0.1

Derived from the design conversation of 2026-11-16 (30 messages, archived outside the repo). Pillar facts live in `registry/pillars.json`; this document is the narrative contract. Keywords MUST / SHOULD / MAY are used in the RFC 2119 sense.

## 1. Purpose and positioning
FSharpened extends F# without replacing it. It MUST NOT fork the compiler, patch `FSharp.Core`, or introduce a new language. It integrates through FCS, `FSharp.Analyzers.SDK`, the .NET SDK + NuGet, and LSP/MCP/DAP. Tagline: *Sharpen your existing F#.* The founding observation: F# adoption is limited less by the language than by perception, strategic positioning, ecosystem gravity and an incomplete engineering surface (compiler speed, diagnostics, debugging, refactoring, tooling parity, supply chain, ML infrastructure).

## 2. Scope
Nothing is out of scope in principle (owner decision). Sequencing is governed by `PLAN.md`. Windows, Linux and macOS are peers from the first commit; Android and iOS must not be precluded.

## 3. Requirements by gap
Gap IDs follow the conversation. Each maps to a pillar.

| Gap | Problem | Pillar / mechanism |
|---|---|---|
| Compiler opacity | Opaque internal compiler errors | CompilerBridge structured diagnostics; Models CDM; `fsh explain` |
| A | No unified effect composition | Effects: `Effect<'T,'E>` + `effect { }` |
| B, K | STJ cannot source-generate DUs; AOT holes | Serialization: analyzer-generated metadata, build-time AOT validation |
| C, U | No pure-F# TOML 1.0.0 parser | Data: XParsec-based TOML |
| D | Complexity metrics are C#/IL-shaped | Quality: AST-native metrics |
| E, CC | No coherent GUI/mobile story | Desktop: MVU over Avalonia / Fabulous.Avalonia |
| F, N | macOS/Linux tooling gaps; SharpLsp gaps | Tooling (upstream-first) |
| G | Value restriction ergonomics | Analyzers rule with two one-click fixes |
| H | `String` module limits | Analyzers guidance; `FSharpened.Text` helpers |
| I | No Roslyn source generators for F# | Serialization: analyzer-generated metadata |
| J | File-order onboarding | Analyzers + CompilerBridge explanation of dependency chain |
| L, M | F# debugging broken cross-platform | Debugging: DAP proxy over netcoredbg + F# evaluator |
| O | Minimal refactoring | Refactoring (MCP first) |
| P, Q | Paket/NuGet split; no F# supply-chain governance | Security: compose SBOM/SLSA/Sigstore; validate both models |
| R, S, T, DD | PBT, UI, mutation, contract, chaos testing gaps | Testing: compose + AST mutation where needed |
| V | No Arrow/Parquet/CSV story | Data: compose DuckDB/Flowthru/DataFusionSharp |
| W, X | Fragmented observability; no pipeline tracing | Telemetry + Effects OTel context |
| Y, Z | No per-file incremental compilation; ad-hoc cache | CompilerBridge cache as MSBuild task around `CoreCompile` |
| AA | Doc coverage unenforced | Analyzers doc-coverage rules |
| BB | No living architecture | Architecture: typed graph + fitness functions |
| Hot reload | `dotnet watch` does not support F# | DevLoop: statepoint session, Harmony now, FCS-native later |
| ML | No F#-native authoring spine, registry, tracking; CUDA-shaped | ML.* pillars + Hardware + Portability |

Out of reach without a compiler change (documented limits): higher-kinded types, making `Option` a struct globally, removing `Async<T>`, language-level nullability metadata. FSharpened mitigates via libraries/analyzers and upstream contribution.

## 4. Architecture
See `docs/architecture/overview.md`. Eight substrates, 20+ pillars, two tools. Stability tiers: Stable (semver), Preview, Experimental.

### 4.1 Platform
Interfaces: `IFileWatcher`, `ITransport`, `IPathNormalizer`, `IOverlayStore`, `ISandbox`, `ITerminal`, `RuntimeIdentity`. No other project may `#if` on a platform. Capability manifest published once at startup.

### 4.2 Statepoints
API: snapshot, fork, rollback, merge, lineage. Graph, not list. Overlay is in-memory and content-addressed; filesystem CoW is optional. `TrainingStatepoint` specialisation captures weights, optimiser buffers, scheduler, RNG and data cursor atomically.

### 4.3 Semantic
Library + stdio MCP server: `find`, `check`, `types`, `rename`, `dead-code`, plus `model-params`, `model-graph`, `training-lineage`. Agents MUST use it for symbol resolution.

### 4.4 ML stack
Authoring over TorchSharp/DiffSharp/Furnace **only through ML.Portability**. Inference ONNX-first via execution providers chosen by `ML.Hardware.Resolver`. Training chain: TorchSharp (CUDA/ROCm/MPS) then oneDAL then ONNX training then ILGPU. Backend failure at runtime forks the statepoint to the next backend without losing optimiser or RNG state. Every artifact records exact framework versions, is signed, carries a model card, and is validated under NativeAOT per RID before promotion.

### 4.5 Internal models
See `docs/architecture/models.md`. Five tiers, five-layer fallback, recursive training with frozen judges, atomic hot swap.

## 5. Command line
`dotnet tool install -g FSharpened.Cli`; commands: `init`, `watch`, `doctor [--wsl]`, `analyze [--ci]`, `quality`, `explain <code>`, `refactor`, `mutate`, `build --cached`, `sign`, `architecture check`, `new desktop`. Alias: `fsh`.

## 6. Quality, performance, security
Budgets: `docs/performance/budgets.md`. Threat model: `docs/security/threat-model.md`. Supply chain: SBOM every build, locked restore, signed releases, license inventory.

## 7. Agent operating model
Seven personas with hard boundaries, file-based memory, handoff queue, skills library, conflict-resolution protocol, human-gated irreversible acts. See `MAINTAINER.md`, `.agents/`, `docs/governance/`.

## 8. Invariants
The 14 invariants in `MAINTAINER.md` are normative.

## 9. Roadmap
`PLAN.md`. Order: governance + benchmarks, Platform, Statepoints, Semantic, ML.Portability/Hardware, CompilerBridge cache, Diagnostics, Analyzers, hot reload, debugging, refactoring, quality/security/testing, data/effects/desktop, ML platform, hardening.

## 10. Open questions (owner decisions needed)
1. Hosting org and repo URL; package-id reservation on NuGet.
2. Real licenses to verify in `licensing.md` (several marked "verify").
3. netstandard2.0 targeting for analyzer packages (ADR when the analyzer SDK requires it).
4. Whether `FSharpened.Text` (String-module helpers, gap H) is a new project or part of LanguageExtensions.
5. Sub-package split of `LanguageExtensions` (Option, Async, Collections.Immutable, Pipeline) into separate NuGet ids.
6. Verify upstream facts cited in the source conversation (PR #20030, sdk#55128, SharpLsp gaps, TorchSharp platform support); they were not independently checked.
