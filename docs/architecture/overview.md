# Architecture Overview

FSharpened has **eight substrates** and **twenty-plus pillars**. Substrates are consumed by every pillar; no pillar implements its own version of them.

**Substrates:** `Platform`, `Statepoints`, `Semantic`, `CompilerBridge` (FCS wrapper), `Diagnostics`, `Telemetry`, `Security` (signing/verification primitives), `ML.Hardware` (backend resolution).

**Pillars:** Analyzers, Tooling, Build, LanguageExtensions, Effects, Serialization, Quality, Desktop, Debugging, Refactoring, Testing, Data, Architecture, ML.Portability, ML.Authoring, ML.Data, ML.Experiments, ML.Registry, ML.Serving, Models.

**Tools:** `FSharpened.Cli` (`dotnet fsh`), `FSharpened.DevLoop` (warm compiler + hot reload).

**Integration rule:** a pillar may depend on a substrate. A pillar may depend on another pillar only through a declared contract (ADR). A pillar may never depend on TorchSharp, DiffSharp or Furnace without going through `ML.Portability`. Dependency edges per pillar are in `registry/pillars.json` and each `docs/architecture/pillars/<Id>.md`.

```
              +-------------------- Tools: fsh, DevLoop ---------------------+
              |                                                              |
 Pillars: Analyzers Tooling Build Effects Serialization Quality Debugging ... ML.*  Models
              |                                                              |
 Substrates:  Platform  Statepoints  Semantic  CompilerBridge  Diagnostics  Telemetry  Security  ML.Hardware
              |
 Consumed:    FCS  FSharp.Analyzers.SDK  .NET SDK/NuGet  LSP/MCP/DAP  ONNX Runtime  TorchSharp ...
```

## Three integration surfaces (ADR-0001)
1. **FSharp.Compiler.Service** - parser, type checker, diagnostics, hot-reload session surface.
2. **FSharp.Analyzers.SDK** - the supported plugin boundary for Ionide, FsAutoComplete and Visual Studio.
3. **.NET SDK + NuGet** - everything ships as packages and `dotnet` tools.

## Six-RID CI matrix
Every PR runs build, unit and integration tests, Fantomas check, analyzer gate, **NativeAOT publish + smoke test** and **ONNX load + inference smoke test** on `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`. AOT is RID-specific; a clean `linux-x64` publish says nothing about `win-arm64`.

## Cross-cutting rules
- Fallback discipline per pillar: refactor fails to no-op; security fails closed; debugger evaluation fails to raw `ToString()`; architecture-rule error escalates; model-backed features use the five-layer stack ([models](models.md)).
- Upstream independence: the repo must stay operable from its own contents plus the public SDK and NuGet.
- Hot reload, debugging, canarying, experiments and chaos testing share one consistency model: statepoints.
