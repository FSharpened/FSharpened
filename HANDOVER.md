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
Live at https://fsharpened.pages.dev/ide/ (ADR-0018, `docs/operations/ide.md`). **Eighteen languages, including Rust, through a second backend, Wandbox (ADR-0019, ADR-0020, both Proposed), were deployed on 2026-11-16 from an uncommitted working tree. Verified locally and by two live requests; run the full live check in `docs/operations/ide.md`.** F# core in `tools/FSharpened.Ide` compiled by Fable; CodeMirror shell in `ide/web`; gateway Worker in `gateway/`. New pillar `FSharpened.Interop.Rust` (`docs/operations/rust.md`); `ide/web` has `npm test`. **Next (ADR-0021, `PLAN.md` Phase 6):** projects are wired into the page and **deployed** (2026-11-16; P6.A2, A4 done; A3 done except the GitHub import; the gateway was unchanged). The working tree is still uncommitted. Then pipelines (P6.A5) and the polyglot chess project (P6.A6). Rebuild before deploying the site: `cd ide/web && npm run build` (writes `site/dist/ide.js`), then `python tools/site/build.py --require-ide`, `python tools/site/check.py`, then `wrangler pages deploy site/public`. `site/public` and `site/dist` are generated and git-ignored. The site and the hub console are installable PWAs (`docs/operations/site.md`); their service workers are tested in `site/test` but real-browser install and offline behaviour must be confirmed once per release (checklist in that doc).

## IDE workbench (added 2026-11-16, not deployed, uncommitted)
VS Code-style features in the page (ADR-0022, Proposed; PLAN P7.1 to P7.3): command palette, quick open, search and replace, outline, multi-cursor, snippets, settings, themes, status-bar pickers, right-click menus, import of `.vsix`/theme/snippets/`settings.json` as data, and Download for VS Code. Details and limits: `docs/operations/ide.md`; what was verified and what was not: `docs/STATUS.md`. **GitHub Copilot and extension code cannot run in the page** (proprietary, needs GitHub, CSP forbids foreign code); the export recommends `GitHub.copilot` and `GitHub.copilot-chat` to VS Code instead. Rebuild before deploying: `cd ide/web && npm run build`, `python tools/site/build.py --require-ide`, `python tools/site/check.py`; the gateway is unchanged. Remaining Phase 6 items (pipelines, chess project, live view, linter, Community Projects, Workers AI, rust-rtpc, atomic FSharpened, the agent) are not started. Needs a human: accept ADRs 0019 to 0022 (all still Proposed), the rust-rtpc licence and `fsharpened-rtpc` repository decisions, committing this large working tree, review and merge of PR #9, and the deploy.

## Desktop IDE (added 2026-11-17, uncommitted, not released)
`tools/FSharpened.Ide.Desktop` (Avalonia, F#) over `tools/FSharpened.Ide.Host` (language packs in `languages/`, runner, trust, disk workspace, Git, language-server client) and the shared core; ADR-0023 (Proposed), PLAN P7.7, guide `docs/operations/desktop.md`. Full CI green on win-x64 (316 tests, 0 warnings); the Host tests also pass on Ubuntu (WSL, .NET 9.0.321). Checked in the real window with `--screenshot` (Go ran, a Go compile error became a problem, scripted language-server problems drew as underlines). Also built: hover box, replace in files, a line-based terminal (no pseudo-terminal), grammars for Haskell, OCaml, Elixir, Kotlin, Scala, Zig and TOML. Not built: pseudo-terminal, debugger, Rust natives, split editors; no VS Code extensions or Copilot. Not run on Linux or macOS. Run: `dotnet run --project tools/FSharpened.Ide.Desktop -- <folder>`.

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

Local pipeline (2026-11-17): hardened for a machine under memory pressure (governor, infrastructure retry, process-tree timeouts, lazy build), with `doctor`, `status`, `watch`, a `--profile all` that also runs the web, gateway, hub and site stages, and a `release` bundle with `verify`; see `docs/operations/local-ci.md`. On win-x64 `full` passes in about 70 s cold and 5 s warm, `all` in about 3 min, with 316 tests.

Browser IDE (2026-11-18, ADR-0024): `--profile all` also runs `web-e2e` (87 Playwright and axe tests in Chromium, gateway mocked; skipped with a warning without a browser, `--require-browser` fails). Projects live in IndexedDB; multi-file bundling and the pipeline are in `tools/FSharpened.Ide/Bundle*.fs` and `Pipeline.fs`. Open: a gateway pipeline endpoint, and whether Compiler Explorer's multi-file mode is usable (never probed).

## Exact Next Action
Run `dotnet fantomas --check .` (after `dotnet tool restore`) and fix formatting; then copy `pipelines/github-actions/ci.yml` to `.github/workflows/`, fix whatever the six-RID matrix and per-RID `PublishAot` publish expose (the CLI may need trimming/AOT fixes), and record per-RID results here. Then start P0.5.2 (thread-safe `StatepointGraph`).
