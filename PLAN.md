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

## Phase 6 - The IDE as a platform (ADR-0021, Proposed)
Order matters: every later item needs the project model of 6.A.
- [x] P6.A1 - Project model in `FSharpened.Ide` (`Project.fs`): files, folders, move and rename with references following, entry, pipeline, import sanitising, 36 tests; exported to JavaScript by `Api.fs`. **Owner:** Pillar Builder *(done 2026-11-16, not yet used by the page)*
- [x] P6.A2 - Save and manage several projects in the browser (new, examples, duplicate, rename, delete, switch; one storage record per project; the pre-project workspace migrates into a project and stays as a backup); the timeline snapshots a whole project (`ProjectTimeline`, `ProjectState.fs`). **Owner:** Pillar Builder *(done and deployed 2026-11-16)*
- [ ] P6.A3 - Copy, download file, download folder and project as `.zip`, import `.zip`, folder, files and a public GitHub repository. **Owner:** Pillar Builder *(done except the GitHub import: copy, download file, folder and project, import of a `.zip`, a picked or dropped folder and loose files with a report of what was left out and why; `archive.js`, `projectio.js`, `transfer.js`)*
- [x] P6.A4 - File tree, tabs and project manager UI: accessible tree (arrow keys, Home/End, Left/Right), open-file tabs, New file, Rename or move, Delete, Set entry, Copy, Download, a Projects list, a drawer on narrow screens. **Owner:** Tooling Builder *(done and deployed 2026-11-16; no empty folders yet, folders exist through the files in them)*
- [x] P6.A5 - Multi-file runs and `fsh-pipe/1` pipelines in the page: per-language bundlers in the F# core (JS, TS, Python, Rust, Go, Java, C, C++; the rest run the entry alone), a pipeline planner and runner (stdout to stdin, first failure stops, per-step results), a pipeline editor, line mapping back to files, 25 Node and 22 xunit tests run against real node, python, go, rustc and javac, and 8 browser tests (ADR-0024). **Owner:** Pillar Builder *(done 2026-11-18)*
- [ ] P6.A5b - A gateway pipeline endpoint and gateway-side multi-file requests (the gateway still takes one source; not probed with Compiler Explorer). **Owner:** Pillar Builder
- [ ] P6.A6 - Default polyglot chess project across all eighteen languages. **Owner:** Pillar Builder
- [ ] P6.A7 - Live code view (structure, output, compiled; resizable, minimise, maximise). **Owner:** Tooling Builder
- [ ] P6.B1 - Cross-language rule engine and project-level rules. **Owner:** Pillar Builder
- [ ] P6.B2 - Pipelines framework (build, test, lint, run) declared in the manifest. **Owner:** Tooling Builder
- [ ] P6.C1 - Community Projects: submission API, D1 schema, scanner, approval queue in the hub console, public gallery (own ADR). **Owner:** Tooling Builder
- [ ] P6.D1 - Workers AI module with an allowlist, budgets, cache and a static fallback (own ADR). **Owner:** Tooling Builder
- [ ] P6.E1 - rust-rtpc wire-protocol adapter *(blocked: licence for rust-rtpc; repository creation needs maintainer approval)*. **Owner:** Pillar Builder
- [ ] P6.E2 - `fsharpened-rtpc` repository: language-neutral pre-compile daemon contract and adapters. **Owner:** Architect
- [ ] P6.F1 - Atomicity contract and test kit; audit all pillars one by one. **Owner:** QA/Release
- [ ] P6.G1 - Agent harness: router, specialist registry, scope guard, evaluation suite, first specialists with symbolic fallbacks (own ADR). **Owner:** Pillar Builder
- [ ] P6.G2 - Training desktop GUI with telemetry and the stream-of-thought trace (own ADR for the UI toolkit). **Owner:** Tooling Builder

## Phase 7 - VS Code parity and extensions (ADR-0022, Proposed)
- [x] P7.1 - Native parity: command palette, quick open, search and replace in files, outline and breadcrumbs, settings, themes, multi-cursor, snippets, status bar, right-click menus. **Owner:** Tooling Builder *(done 2026-11-16 in the working tree, not deployed; verified in the browser pane and by tests, see `docs/STATUS.md`. Not done: type-aware definitions and hover (needs P7.4), a minimap, split editors)*
- [x] P7.2 - Declarative VS Code content: import a `.vsix` or a theme or snippet file, and `settings.json`; no extension code runs. **Owner:** Tooling Builder *(done 2026-11-16, not deployed; themes, snippets and settings only: a `.vsix`'s language configuration is not read)*
- [x] P7.3 - Download for VS Code: `.vscode/extensions.json` (language extensions and Copilot), settings, `.code-workspace`. **Owner:** Tooling Builder *(done 2026-11-16, not deployed; Copilot is recommended to VS Code, it cannot run in the page)*
- [ ] P7.4 - Language servers: `fsh lsp-bridge` (loopback WebSocket to an allowlist of stdio servers) and a minimal LSP client in the page; CSP change. **Owner:** Pillar Builder
- [x] P7.7 - Native desktop IDE (ADR-0023, Proposed): Avalonia + AvaloniaEdit in F#, the website's layout and themes, language packs for 37 languages, trusted local runs, problems from any language's output, a minimal language-server client, Git status. **Owner:** Tooling Builder *(done 2026-11-17 in the working tree, not committed or released; see `docs/operations/desktop.md` for what is and is not in it)*
- [ ] P7.8 - Desktop: a real pseudo-terminal (Rust, behind `FSharpened.Interop.Rust`; the line-based terminal is done), file watching and fast search in Rust, debugging through DAP, rename in the UI (hover and search-and-replace are done). **Owner:** Tooling Builder
- [ ] P7.5 - FSharpened as a VS Code extension (language client for the F# core, Fantomas and FsAutoComplete integration). **Owner:** Pillar Builder
- [ ] P7.6 - AI completion provider interface; Workers AI as the first provider *(depends on P6.D1)*. **Owner:** Tooling Builder

