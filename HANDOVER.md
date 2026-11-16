# HANDOVER.md - 2026-11-16

*A snapshot, not a transcript. The "Exact Next Action" field is the one that matters.*

## Status Snapshot
The repository skeleton, specification, governance, agent operating system and generated project skeletons exist. **First build done 2026-11-16 on win-x64 (SDK 9.0.321): 0 warnings, 0 errors, 9/9 tests pass, `fsh doctor` runs.** Other RIDs, NativeAOT, Fantomas and the CI matrix are unverified.

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
- Real code (builds, 9 tests pass on win-x64): see "What Was Done". Everything else: marker module (`Status = "Skeleton"`).
- CI workflows exist but have never run. No release, no packages, no baselines.
- `FSharpened.Semantic`, `CompilerBridge`, `Analyzers`, `Debugging` etc. contain no behaviour yet.

## Browser IDE (added 2026-11-16)
Live at https://fsharpened.pages.dev/ide/ (ADR-0018, `docs/operations/ide.md`). F# core in `tools/FSharpened.Ide` compiled by Fable; CodeMirror shell in `ide/web`; gateway Worker in `gateway/`. Rebuild before deploying the site: `cd ide/web && npm run build` (writes `site/dist/ide.js`), then `python tools/site/build.py --require-ide`, `python tools/site/check.py`, then `wrangler pages deploy site/public`. `site/public` and `site/dist` are generated and git-ignored. The site and the hub console are installable PWAs (`docs/operations/site.md`); their service workers are tested in `site/test` but real-browser install and offline behaviour must be confirmed once per release (checklist in that doc).

## Open TODOs
- [x] First build and test (P0.7) - fixed: FSharp.Core must be referenced explicitly under central package management; `RollForward=LatestMajor` so net8.0 tests run on SDK-only 9.x machines.
- [ ] **P0** - Fill `LICENSE` with full Apache-2.0 text (human).
- [ ] **P1** - Validate `ML.Portability` contracts against real TorchSharp / ONNX Runtime versions.

## Gotchas & Context
- `dotnet watch` does not support F#. Use `dotnet fsh watch` (Harmony path). Do not wire `dotnet-watch` until sdk#55128 lands.
- TorchSharp is x64-shaped; ARM training falls back to oneDAL or ONNX training export.
- `.slnx` needs SDK 9.0.200+. `global.json` allows roll-forward.
- `scaffold.py` never overwrites; use `--force` only on files you have not hand-edited.

## CI status
`python tools/ci/fsh_ci.py run --profile release` is green on win-x64 (preflight, docs-drift, selftest, Fantomas, strict build, 9 tests, NativeAOT publish + run, 30 packages). `.github/workflows/ci.yml` runs it on the six-RID matrix (win-arm64 experimental); results are recorded below once the first run completes.

## Exact Next Action
Run `dotnet fantomas --check .` (after `dotnet tool restore`) and fix formatting; then copy `pipelines/github-actions/ci.yml` to `.github/workflows/`, fix whatever the six-RID matrix and per-RID `PublishAot` publish expose (the CLI may need trimming/AOT fixes), and record per-RID results here. Then start P0.5.2 (thread-safe `StatepointGraph`).
