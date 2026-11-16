# HANDOVER.md - 2026-11-16

*A snapshot, not a transcript. The "Exact Next Action" field is the one that matters.*

## Status Snapshot
The repository skeleton, specification, governance, agent operating system and generated project skeletons exist. **Nothing has been compiled or run**: the scaffolding environment had no .NET SDK. All status claims below mean "files written", not "verified".

## What Was Done
- Read the 30-message design conversation (DeepSeek share) and distilled it into `registry/pillars.json` (28 pillars, 2 tools, 14 invariants).
- Generated `src/` projects, `tools/`, `tests/FSharpened.Skeleton.Tests`, `FSharpened.slnx` with `tools/scripts/scaffold.py`.
- Wrote minimal real code for: `Platform` (interfaces, `RuntimeIdentity`, in-memory overlay), `Statepoints` (graph), `Diagnostics` (`StructuredDiagnostic`), `Effects` (`Effect` type), `ML.Portability` (contracts), `ML.Hardware` (resolver + inference chain), `Models` (five-layer fallback runner). Every other pillar is a marker module.
- Wrote root docs, 15 founding ADRs, governance, threat model, budgets, platform docs, personas, memory, ~50 skills, pipelines, `check_docs.py`.

## Decisions Made
| Decision | Rationale | Alternatives Considered |
|---|---|---|
| In-memory overlay for statepoints, not filesystem CoW | CoW is not portable (btrfs/XFS/APFS only; NTFS none). In-memory works identically on all RIDs | Reflinks (Linux-only); APFS clonefile (macOS-only) |
| ONNX Runtime as primary inference backbone | Broadest provider matrix (CUDA, DirectML, ROCm, OpenVINO, CoreML, oneDNN, QNN, CPU) | TorchSharp-only (x64-shaped, no mobile) |
| Target net8.0, SDK 9.0.200+, `.slnx` | Reach + reproducibility (ADR-0005) | netstandard2.0 for Platform (deferred) |
| Registry JSON as source of truth | Docs, projects, skills are generated/validated from it | Hand-maintained lists (drift) |

## Current State
- Real code (uncompiled): see "What Was Done". Everything else: marker module (`Status = "Skeleton"`).
- CI workflows exist but have never run. No release, no packages, no baselines.
- `FSharpened.Semantic`, `CompilerBridge`, `Analyzers`, `Debugging` etc. contain no behaviour yet.

## Open TODOs
- [ ] **P0** - First build and test on a machine with the SDK (P0.7).
- [ ] **P0** - Fill `LICENSE` with full Apache-2.0 text (human).
- [ ] **P1** - Validate `ML.Portability` contracts against real TorchSharp / ONNX Runtime versions.

## Gotchas & Context
- `dotnet watch` does not support F#. Use `dotnet fsh watch` (Harmony path). Do not wire `dotnet-watch` until sdk#55128 lands.
- TorchSharp is x64-shaped; ARM training falls back to oneDAL or ONNX training export.
- `.slnx` needs SDK 9.0.200+. `global.json` allows roll-forward.
- `scaffold.py` never overwrites; use `--force` only on files you have not hand-edited.

## Exact Next Action
On a machine with .NET SDK 9.0.200+, run `dotnet build FSharpened.slnx` from the repo root. Fix compile errors in the order they appear (most likely in `src/FSharpened.Platform/Platform.fs` and `tests/FSharpened.Skeleton.Tests/SkeletonTests.fs`), then `dotnet test FSharpened.slnx`. Do not change pillar structure to make it compile; if a design rule seems wrong, write an ADR. Record the outcome here and tick P0.7 in `PLAN.md`.
