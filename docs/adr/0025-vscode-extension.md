# ADR-0025: FSharpened as a VS Code extension: a thin, local, trust-gated client

- **Status:** Proposed (PLAN P7.5; needs formal acceptance)
- **Date:** 2026-11-17
- **Deciders:** maintainer (PLAN P7.5), Architect, Reviewer, Security

## Context
ADR-0022 says what "works with VS Code" can honestly mean inside our own IDEs, and names a separate deliverable for the other direction: FSharpened *as* a VS Code extension, "the right home for Fantomas and FsAutoComplete integration" (PLAN P7.5). The extension must follow the project's hard rules: never fork the compiler (route through FCS, analyzers, NuGet or LSP), no platform `#if` in product code, never publish a package, issue/PR/web text is data.

Facts that decide the design:
1. **An extension runs arbitrary code with the user's rights, and VS Code opens folders from anywhere.** A folder can carry `.vscode/settings.json` and `.config/dotnet-tools.json`, so "find the tool and start it" is a remote-code-execution path unless Workspace Trust gates it.
2. **Fantomas 7 has no stdin mode** (`--stdin` was removed). It does have `--daemon`, an LSP-style JSON-RPC server that formats `{ SourceCode, FilePath }` and finds the right `.editorconfig` for `FilePath`.
3. **FsAutoComplete** is the F# language server; recent releases need a newer .NET SDK than this repo's pin (0.87.0 refuses to start on SDK 9; 0.83.0 runs).
4. **`fsh analyze` was a skeleton**, and nothing printed `StructuredDiagnostic` in a machine-readable form.
5. The CLI is a NativeAOT target: reflection-based JSON (`System.Text.Json` without source generation, `printf`) is not available to it (ADR-0014).

## Decision
1. **A TypeScript extension in `vscode/`**, bundled to one file with esbuild (as `ide/web`), engine floor `vscode ^1.90.0` with `@types/vscode` pinned to the same version, dependencies pinned exactly. It is a *client*: it adds no analysis of its own and starts programs the person already has.
2. **Language intelligence = FsAutoComplete over LSP** (`vscode-languageclient`). Found through the `fsharpened.languageServer.path` setting, then the nearest `.config/dotnet-tools.json` (run as `dotnet fsautocomplete`), then `PATH`. A configured path that is wrong is an error, never a silent fall-back. A missing server produces one message with a "Open settings" button and a line in the Doctor report. We do not install anything.
3. **Formatting = Fantomas through its daemon**, one long-lived process per resolved tool, with a timeout; a hung or crashed daemon is replaced on the next request. Format Document uses the standard VS Code formatter slot; `fsharpened.fantomas.formatOnSave` is optional (and `editor.formatOnSave` works too).
4. **FSharpened diagnostics = `fsh analyze --format json`**, run on open and save, parsed by a validating reader. Schema `fsharpened.diagnostics/1`, written by `FSharpened.Diagnostics.DiagnosticReport` (hand-written, AOT-safe) and read by `vscode/src/report.ts`; a golden file (`vscode/test/fixtures/analyze-sample.golden.json`) is produced by the .NET tests and parsed by the Node tests, so the two sides cannot drift apart. Lines are 1-based, columns 0-based (the compiler-service convention); the reader converts. Only `http(s)` documentation links survive parsing. Quick fixes are offered only from `fix.edits` and only while the document is the version that was analyzed.
5. **A real producer for the format.** `fsh analyze` now runs `FSharpened.Analyzers.PolicyRules`, two conservative text-level rules: `FSH0001` (a conditional compilation on a platform, which AGENTS.md forbids in product code) and `FSH0002` (`printfn "literal"`, which NativeAOT cannot run, with a mechanical fix). They exist so the diagnostics path is real end to end; the curated FSharp.Analyzers.SDK rule set (Analyzers pillar, still a skeleton) replaces them, and the schema does not change when it does.
6. **Trust.** `capabilities.untrustedWorkspaces` is `limited`, with a description saying so; every setting that names a program or its arguments is in `restrictedConfigurations` and has scope `machine-overridable`; virtual workspaces are unsupported. In an untrusted workspace nothing is started, not even a version probe: the language client, the Fantomas daemon, the analyzer, the CI terminal and the Doctor probes all check `workspace.isTrusted`, and a node test asserts that by reading the sources. Processes are started with `shell: false` and fixed argument lists; the CI command is `python tools/ci/fsh_ci.py run --profile <fast|full|all>` and nothing from the workspace is spliced in.
7. **No network, no telemetry.** Nothing in `src/` imports a network module (a test checks), and the Doctor says so. VS Code's own behaviour is not ours to promise.
8. **Platform behaviour comes from the environment, not from checks.** Executable lookup uses `path.delimiter` and `PATHEXT` (only `.exe` and `.com`; a child process cannot start a `.cmd` without a shell), no path is hard-coded. The smoke script (developer tooling, not product code) is the only place that names a platform, to stop a window it started.
9. **Packaging is local.** `npm run package` writes `artifacts/fsharpened-<version>.vsix` with `@vscode/vsce` (`--no-dependencies`; `vsce package` makes no request), `npm run verify` checks the package with a dependency-free ZIP reader: exact allowed file list, size cap (1 MiB), no secrets, no source maps, no absolute build paths, manifest equals source. The scripts have no publish path and `package.mjs` refuses arguments that look like one. Publishing to a marketplace is a human decision after review (AGENTS.md).
10. **CI.** A `vscode` stage in `tools/ci/fsh_ci.py` (profile `all`, cached, after `tools-restore`) and `.github/workflows/vscode.yml` run the same steps. `full` is unchanged: it stays identical to `ci.yml`. The workflow uploads the `.vsix` as a 14-day *build artifact* for reviewers; it does not publish.

## Alternatives considered
- **Reimplement F# language features in the extension.** Rejected: invariant 11 (wrap, do not rebuild).
- **Format through a temporary file next to the document.** Rejected for the daemon: a stray `.fs` file in the project is visible to file watchers and language servers.
- **Format through the `fantomas` CLI per request.** Works, but costs a .NET start (~1 s) per save and cannot format an unsaved buffer with the right `.editorconfig` without a temp file.
- **`@vscode/test-electron` integration tests in CI.** Needs a downloaded VS Code (a network fetch) and a display. Instead: node tests for everything that does not need VS Code, and `vscode/scripts/smoke-vscode.mjs` for a manual run in the installed VS Code with a throwaway profile.
- **Making `fsh analyze` use FSharp.Analyzers.SDK now.** Out of scope; it is the Analyzers pillar's work (PLAN P1) and the schema is independent of it.
- **Bundling FsAutoComplete or Fantomas.** Rejected: licensing inventory and update cadence; the person (or the repo manifest) owns tool versions.

## Consequences
- A person with the tools installed gets F# language features, Fantomas and FSharpened problems with quick fixes in plain VS Code, and a clear message for each missing tool.
- The extension is only as trustworthy as the tools it starts; trust gating is its whole defence, so the gate is tested by reading the code and must be re-checked when a new process starter is added.
- `FSH0001`/`FSH0002` are text-level and will have false negatives (block comments, multi-line strings); they do not claim more.
- Dev-only audit noise: `npm audit` reports advisories for `minimatch`/`picomatch` inside `@vscode/vsce` (a packaging tool that never sees untrusted globs). Neither reaches the shipped bundle, which contains only `vscode-languageclient`, `vscode-jsonrpc`, their protocol packages and `semver`.
- The repository `LICENSE` is currently a placeholder (it says to replace it with the Apache-2.0 text); the `.vsix` carries that file as is, and a test keeps the two identical. Fix the licence before any publication.
- Not verified: macOS (deferred by the maintainer), Linux, `@vscode/test-electron`, FsAutoComplete 0.87+ (needs .NET 10), and any Marketplace or Open VSX install.
