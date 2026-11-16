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

## Browser IDE (ADR-0018) - live, verified in a browser on 2026-11-16
- https://fsharpened.pages.dev/ide/ runs F#, C, C# and C++ (10 compilers) through the gateway Worker; verified by running every language, a compiler error in F# and C++ (Problems panel, editor marker, location), snapshot/diff/restore/fork, share link round-trip, and a phone-width layout.
- F# core (`tools/FSharpened.Ide`, 14 xunit tests) is compiled by Fable and reused by the page; gateway has 10 tests. Verified on win-x64 only; the `ide` workflow builds and tests it on ubuntu.
- Limits: depends on Compiler Explorer's public API (see ADR-0018); 12 runs/min/client; 20 s execution cap. Not yet done: in-browser F# language service (hover/completion beyond keywords), multi-file projects.
