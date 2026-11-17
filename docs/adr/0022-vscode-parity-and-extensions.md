# ADR-0022: IDE parity with VS Code, what "works with VS Code extensions and Copilot" can honestly mean

- **Status:** Proposed (requested by the maintainer in chat, 2026-11-16; needs formal acceptance)
- **Date:** 2026-11-16
- **Deciders:** maintainer (request in chat), Architect, Reviewer, Security

## Context
The maintainer asks for the features VS Code and other IDEs have, and for the IDE to "work with Copilot and other VS Code extensions". The first half is ordinary work. The second half runs into facts that decide the design, so they are written down first.

1. **A VS Code extension is a JavaScript program written against the `vscode` API.** It runs in an *extension host* that the VS Code workbench provides (a Node process on the desktop, a web worker in the browser). Our page is a CodeMirror editor around an F# core; it has no workbench and no extension host. Pretending to offer "the vscode API" would mean re-implementing a large, fast-moving surface; most extensions would still fail, and saying "compatible" would be false.
2. **GitHub Copilot and Copilot Chat are proprietary.** They need the `vscode` API, a GitHub sign-in with a Copilot entitlement, and are licensed for use in supported editors. They are not on Open VSX, and nothing allows another product to load them. The same holds for several Microsoft extensions (C# Dev Kit, Pylance, Remote). The Visual Studio Marketplace terms limit it to Microsoft's own products; Open VSX is the open registry.
3. **Some of what extensions ship is plain data** and carries across: colour themes, snippets, language configuration (brackets, comments), and settings and recommendation files. A `.vsix` is a ZIP, which the IDE already reads.
4. **Language intelligence mostly comes from language servers**, which are separate processes speaking LSP over stdio. A web page cannot start a process. A small local bridge could.
5. **The site's Content-Security-Policy is `script-src 'self'`**, deliberately. Loading third-party code into the origin that holds a person's projects (local storage) would let that code read everything. Any code-running extension model needs a sandbox and its own threat model first.

## Decision
Five layers, in this order. Each ends in something usable and a test.

**L1. Native parity (built).** The features people expect from VS Code, in the page, with their logic in the F# core where it can be tested and shared: command palette (`Ctrl+Shift+P`, `F1`), quick open (`Ctrl+P`, with `:line` and `@symbol`), search and replace across the project (`Ctrl+Shift+F`, case, whole word, regex), outline and breadcrumbs, go to line and symbol, multi-cursor (`Alt`+click, `Ctrl+D`, rectangular selection), move and copy line, toggle comment, fold, snippets, settings (theme, font size, tab size, word wrap, whitespace, line numbers), built-in themes including high contrast, a status bar with line, column, selection, language and indentation, and keyboard shortcut help.

**L2. Declarative VS Code content (built).** Import a `.vsix` (or a theme or snippet `.json`) and use what is data: `contributes.themes`, `contributes.snippets`, language configuration. **No extension code runs.** The Extensions panel lists each installed package with what was used and what was left out and why ("adds commands: needs the VS Code extension host"). Copilot is named in the panel, with the reason it cannot load.

**L3. Interop with real VS Code (built).** *Download for VS Code* writes the project plus `.vscode/extensions.json` (recommending the extension for each language the project uses, **and** `GitHub.copilot` and `GitHub.copilot-chat`), `.vscode/settings.json` from the IDE settings, and a `.code-workspace` file. This is how Copilot works with a FSharpened project: open the exported folder in VS Code, vscode.dev or a Codespace, where Copilot is licensed to run. Importing a project honours `.vscode/settings.json` for tab size, font size and word wrap, on request.

**L4. Language servers through a local bridge (designed, not built).** `fsh lsp-bridge` would be a small program on the person's machine: a WebSocket on `127.0.0.1` to the stdio of servers named in a local allowlist file (FsAutoComplete, rust-analyzer, pyright, gopls, typescript-language-server, and so on), guarded by a per-session token and an origin check, never taking a command line from the page. The page gets a minimal LSP client (diagnostics, completion, hover, definition, rename). It needs a CSP change for `ws://127.0.0.1:*` and the person's own installation of each server. Tracked as PLAN P7.4.

**L5. A completion provider interface for AI (designed, not built).** Providers are pluggable: the built-in keyword and snippet provider today; Cloudflare Workers AI through the gateway later (ADR-0021 Phase D, its own ADR, budgets, cache and a static fallback). Off by default, asks before the first request leaves the browser, says which provider answered, and never calls itself Copilot.

**Not doing:** hosting the `vscode` API; loading Copilot or any Marketplace-only extension; running extension code from the network inside the site's origin. A sandboxed web-extension host (iframe or worker, capability API, no access to storage) is the only acceptable shape if code-running extensions are ever wanted, and it needs its own ADR and threat model first.

## Alternatives considered
- **Replace CodeMirror with Monaco plus `monaco-vscode-api`**, which can run web-compatible extensions from Open VSX in a worker. It would give real extension compatibility for the extensions that are web-compatible. It costs a rewrite of the editor shell, a much larger bundle (megabytes), a CSP and PWA rework, and still cannot load Copilot. Kept as the route to take if the L2 to L4 layers prove too thin; nothing here blocks it.
- **Ship FSharpened as a VS Code extension** (a language client for the same F# core). Useful and cheap, and the right home for Fantomas and FsAutoComplete integration; it is a separate deliverable (PLAN P7.5), not a substitute for the web IDE.
- **A browser-hosted extension host with a partial `vscode` API.** Rejected: partial compatibility is worse than none because failures are silent.

## Consequences
- The IDE can say truthfully: it has the VS Code features listed above, reads VS Code themes and snippets, exports to VS Code with Copilot recommended, and (once built) talks to real language servers. It cannot run VS Code extensions or Copilot, and the Extensions panel says so.
- Declarative import is bounded the same way archive import is: a size cap, JSON only, every field validated, nothing evaluated.
- Every layer is optional and works offline except the AI provider.
