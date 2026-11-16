# FSharpened

*Sharpen your existing F#. Not a fork. An extension.*

**FSharpened is not a fork, not a compiler patch, and not a new language.** It is a suite of libraries, analyzers, tools and pipelines that plug into the standard .NET SDK, FCS, `FSharp.Analyzers.SDK`, NuGet and LSP. Every component installs on top of a stock .NET SDK with zero compiler patches.

**The problem:** F# is a beautiful language with an incomplete engineering surface. Debugging is not F#-aware, `dotnet watch` hot reload does not support F#, refactoring is minimal, ML infrastructure assumes CUDA, supply-chain governance is C#-first, and there is no per-file incremental compilation. FSharpened fills those gaps and keeps filling them, maintained by agents and humans together.

> **Status: skeleton.** The repository structure, specification, governance, agent operating system and project skeletons exist. Almost no product behaviour is implemented yet. See [`docs/STATUS.md`](docs/STATUS.md) and [`PLAN.md`](PLAN.md). Nothing below is installable yet.

## Quick start (target UX)

```bash
dotnet tool install -g FSharpened.Cli
dotnet fsh init          # scaffold with analyzers, telemetry, Fantomas
dotnet fsh watch         # statepoint-backed hot reload + warm compiler
dotnet fsh doctor        # structured toolchain report
```

Working from source today: see [`docs/setup/environment.md`](docs/setup/environment.md).

## Pillars

Substrates are consumed by every pillar; no pillar implements its own version of them.

| Layer | Pillar | Package | Purpose |
|---|---|---|---|
| substrate | Platform | `FSharpened.Platform` | File watcher, transport, path identity, overlay store, sandbox, terminal, runtime identity |
| substrate | Statepoints | `FSharpened.Statepoints` | Snapshot / fork / rollback / merge shared by hot reload, debugging, ML |
| substrate | Semantic | `FSharpened.Semantic` | MCP + library semantic grounding (`find`, `check`, `types`, `rename`) |
| substrate | CompilerBridge | `FSharpened.CompilerBridge` | Warm compiler host, structured diagnostics, per-file incremental cache |
| substrate | Diagnostics | `FSharpened.Diagnostics` | Structured errors, `DiagnosticScope` CE, `Result` pipeline |
| substrate | Telemetry | `FSharpened.Telemetry` | OpenTelemetry-native instrumentation, feedback, ML instruments |
| substrate | Security | `FSharpened.Security` | SBOM, SLSA, Sigstore, lock files, model artifact signing |
| substrate | ML.Hardware | `FSharpened.ML.Hardware` | CUDA, ROCm, DirectML, OpenVINO, CoreML, oneDNN, ILGPU resolution |
| pillar | Analyzers | `FSharpened.Analyzers` | File-order, value restriction, Option/ValueOption, Async/Task, nullability, docs |
| pillar | Tooling | `FSharpened.Tooling` | Ionide, SharpLsp, VS Code, Rider, headless LSP harness |
| pillar | Build | `FSharpened.Build` | FAKE / Fun.Build targets, MSBuild cache, CI primitives |
| pillar | LanguageExtensions | `FSharpened.LanguageExtensions` | Immutable collections, Option, Async bridge, Pipeline |
| pillar | Effects | `FSharpened.Effects` | `effect { }` over async, task, result, option, validation |
| pillar | Serialization | `FSharpened.Serialization` | AOT-ready JSON (no reflection), validation at build time |
| pillar | Quality | `FSharpened.Quality` | AST-native complexity, CRAP, mutation-score gates |
| pillar | Desktop | `FSharpened.Desktop` | MVU wrapper over Avalonia / Fabulous.Avalonia |
| pillar | Debugging | `FSharpened.Debugging` | DAP proxy over netcoredbg, F# evaluator, profiling bridge |
| pillar | Refactoring | `FSharpened.Refactoring` | rename, extract, inline, Async to Task |
| pillar | Testing | `FSharpened.Testing` | Property facade, mutation, contract, UI, chaos |
| pillar | Data | `FSharpened.Data` | DuckDB / Flowthru / DataFusionSharp facade, TOML, CSV |
| pillar | Architecture | `FSharpened.Architecture` | Typed dependency graphs and fitness functions |
| ml | ML.Portability | `FSharpened.ML.Portability` | Capability contracts for fragile ML frameworks |
| ml | ML.Authoring | `FSharpened.ML.Authoring` | `model { }` / `training { }` CEs |
| ml | ML.Data | `FSharpened.ML.Data` | Lineage-tracked data pipelines |
| ml | ML.Experiments | `FSharpened.ML.Experiments` | Statepoint-backed tracking, search, AutoML |
| ml | ML.Registry | `FSharpened.ML.Registry` | Signed artifacts, frozen-judge promotion |
| ml | ML.Serving | `FSharpened.ML.Serving` | ONNX-first inference, drift, shadow deploy |
| models | Models | `FSharpened.Models` | Hyper-specific internal models and the five-layer fallback |

Machine-readable source of truth: [`registry/pillars.json`](registry/pillars.json). Full specification: [`docs/spec/SPEC.md`](docs/spec/SPEC.md).

## Platform support

Tier 1 (validated on every CI run): `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`. Tier 2: WSL, Linux containers, Windows Server. Tier 3: Android and iOS (must not be architecturally precluded; AOT-only lane). No platform is privileged. See [`docs/platform/README.md`](docs/platform/README.md).

## Documentation map

| I need to... | Read first |
|---|---|
| Set up a dev environment | [`docs/setup/environment.md`](docs/setup/environment.md) |
| Understand the architecture | [`docs/architecture/overview.md`](docs/architecture/overview.md) |
| Know why we chose X over Y | [`docs/adr/README.md`](docs/adr/README.md) |
| Add an analyzer rule | [`docs/packages/Analyzers.md`](docs/packages/Analyzers.md) + `.agents/skills/add-analyzer-rule.md` |
| Cut a release | [`docs/operations/release.md`](docs/operations/release.md) + `.agents/skills/cut-release.md` |
| Resolve an agent disagreement | [`docs/governance/conflict-resolution.md`](docs/governance/conflict-resolution.md) |

Everything else routes from [`docs/README.md`](docs/README.md).

## License

Apache 2.0. FSharpened is an independent community project. It is not affiliated with, endorsed by, or a replacement for Microsoft's F# compiler or the F# Software Foundation.
