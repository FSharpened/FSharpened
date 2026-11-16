# STATUS.md - What Works Today (2026-11-16)

"Written" means files exist. **Verified so far:** `dotnet build FSharpened.slnx` (0 warnings, 0 errors) and `dotnet test` (9/9 pass) on **win-x64 only**, SDK 9.0.321, 2026-11-16. Not verified: other five RIDs, NativeAOT, Fantomas, CI matrix. Update this file when a status is actually verified, and say how.

## Written, minimal real code (builds and tested on win-x64)
- `FSharpened.Platform` - interfaces, `RuntimeIdentity.detect`, in-memory overlay. No OS implementations of watcher/transport/sandbox yet.
- `FSharpened.Statepoints` - single-threaded graph: snapshot, fork, rollback, merge, lineage.
- `FSharpened.Diagnostics` - `StructuredDiagnostic` model only.
- `FSharpened.Effects` - `Effect<'T,'E>` struct and map/bind; no `effect { }` CE.
- `FSharpened.ML.Portability` - contracts for TorchSharp and ONNX Runtime (draft).
- `FSharpened.ML.Hardware` - resolver and default inference chain; availability probe is injected, not real.
- `FSharpened.Models` - five-layer fallback runner; no models.
- `fsh` CLI - help text and `doctor` (runtime identity only).

## Skeleton only (marker module, no behaviour)
CompilerBridge, Semantic, Telemetry, Analyzers, Tooling, Build, LanguageExtensions, Serialization, Quality, Desktop, Debugging, Refactoring, Testing, Data, Security, Architecture, ML.Authoring, ML.Data, ML.Experiments, ML.Registry, ML.Serving, DevLoop.

## Process and docs
Specification, 15 ADRs, governance, threat model, budgets, personas, memory, skills, pipelines and `check_docs.py` are written. CI has never run.
