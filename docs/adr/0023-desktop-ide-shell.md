# ADR-0023: The desktop IDE is a native F# application, polyglot by fitness, with languages as data

- **Status:** Proposed (requested by the maintainer in chat, 2026-11-16; needs formal acceptance)
- **Date:** 2026-11-16
- **Deciders:** maintainer (request in chat), Architect, Reviewer, Security

## Context
The maintainer asks for a FSharpened **desktop IDE GUI** that looks and behaves like the website IDE, does everything they want, and then grows the rest of the VS Code-like features (ADR-0022). Asked whether that means a real native application or the website in a window, they answered: not a shell, a desktop GUI; "F# polyglot, optimally and seamlessly utilizing the languages that make the most sense", able to code in "basically any programming language now and in the future, including Go and all others".

So three requirements decide the design:
1. **A native GUI**, not the website inside a web view. (An earlier plan to embed `site/public` in Tauri was dropped on this answer.)
2. **F#-first and polyglot by fitness.** Each part is written in the language that suits it, not in every language for show.
3. **Any language, now and later**, without a code change per language.

## Decision
1. **UI: Avalonia 12 with AvaloniaEdit and TextMateSharp, written in F#.** Native controls on the six RIDs, one native code editor, and TextMate grammars, which cover most languages (Go included) and let a user add another by dropping in a grammar file. The layout, colour roles, themes, command palette, menus and shortcuts follow the website IDE; the colours come from the same F# `Themes` module, so a theme means the same in both.
2. **Reuse the F# core directly.** `tools/FSharpened.Ide` is an ordinary .NET library (the website uses it through Fable). The desktop app references it: project model, fuzzy ranking, search, symbols, snippets, themes, settings, VS Code conversions, problem parsing. Nothing is re-implemented in the desktop app, and a fix to the core fixes both IDEs. (Invariant 5 in spirit.)
3. **A UI-free host library, `tools/FSharpened.Ide.Host`** (F#): the workspace on disk, language packs, run plans and the local runner, project trust, tool probes; a minimal language-server client and Git status; the terminal is not built yet. It has no Avalonia reference, so everything that can hurt the machine is tested without a window.
4. **Languages are data: language packs** (`fsharpened.language/1`, JSON). A pack names a language's file extensions and file names, its TextMate scope, comment and bracket tokens, indentation, the language server to start (command, arguments, install hint), the build and run steps with `{entry}`, `{out}`, `{dir}` placeholders, a test command, a formatter and a debug adapter. Packs ship in `languages/` next to the app and are read from the user's folder (`FSharpened/languages`) too, so **adding a language is adding a file**. The shipped set covers the eighteen website languages plus Go, Kotlin, Swift, Zig, Scala, Elixir, Julia, Perl, shell and the common data and markup formats. A pack is a declaration; it runs nothing by itself.
5. **Polyglot, where each language earns its place.**

| Part | Language | Why this one |
|---|---|---|
| UI, core, host, language-pack engine, language-server client | **F#** | one typed core shared with the website; the project's purpose |
| File watching, fast project search | **Rust**, behind the C ABI through `FSharpened.Interop.Rust` (ADR-0019) | `notify` and the `ignore` walker are the best portable implementations; the interop pillar exists to be used |
| Terminal (pseudo-terminal) | **F#**, P/Invoke in `FSharpened.Platform` (amended 2026-11-17) | ConPTY (`CreatePseudoConsole`) on Windows and `posix_openpt` plus `posix_spawn` on Linux and macOS are a few dozen calls each; no native build step, and the platform split stays in the one project the hard rules allow it in. `portable-pty` stays an option if a case needs it |
| Any language server, compiler, debugger, formatter | **whatever the language ships** (gopls, rust-analyzer, clangd, pyright, ...) | started as processes from the pack; this is how "all languages" works |
| Website only | TypeScript or JavaScript (Fable output, CodeMirror) | not part of the desktop app |
| A remote or container agent (not built) | **Go** is the candidate | a static single binary that runs anywhere; plugs in over the same JSON-RPC protocol, so it needs no change to the app |

   *Status:* the F# parts and the language packs are built; the Rust parts (terminal, file watching, fast search) are **not built yet** — the host uses .NET's `FileSystemWatcher` and the F# core's search until they are.

   **There is no Go code in version 1, on purpose:** nothing in the host benefits from Go over F# or Rust today. **Go as a language the IDE can code in is fully supported through its pack** (gopls, `go run`, `go test`, `gofmt`, Delve, the TextMate grammar). The same holds for every other language.
6. **Trust.**
   - *Folders* are opened through the native folder dialog; the host works inside the opened folder only and refuses paths that leave it (no `..`, no links followed out, no reserved names).
   - *Running programs* needs trust: the first run in a folder asks "Run programs from this folder on this computer?" and remembers "Trust" per folder in the app's data folder. A folder that is not trusted runs nothing and starts no language server.
   - *Tools* come from language packs and are started as an argument vector, never through a shell. A pack from the user's folder is shown before its first use.
   - *Limits.* Each run has a time limit (default 30 s, at most 120 s), caps captured output (1 MiB per stream) and kills the whole process tree on timeout.
7. **What this does not do, said plainly.** Local runs are **not sandboxed**: a trusted folder's program has the user's permissions. The app does not run VS Code extensions or GitHub Copilot (ADR-0022 stands); language servers cover what most extensions are used for. The desktop app starts language servers itself (a minimal client: problems, completion, go to definition, hover in the client library), because only a desktop host can start servers; the browser's bridge is still PLAN P7.4.
8. **The platform rule.** No `#if` on a platform in product code. Platform differences go through `FSharpened.Platform` (the executable suffix and the case rules of the trust list use `RuntimeIdentity.detect()`) or through .NET abstractions that already hide them (`Environment.SpecialFolder`, `Path`, `ProcessStartInfo`).
9. **Distribution is a human gate.** The app builds and runs locally and in CI. Installers, signing, notarisation and any release are the maintainer's decision (AGENTS.md: never publish).

## Consequences
- Two GUIs (web and native) share one core, the same colour roles and the same behaviour spec, but not one UI codebase; they must be checked against each other. This is the price of "native". `docs/operations/desktop.md` keeps a parity table.
- New NuGet dependencies (Avalonia, AvaloniaEdit, TextMateSharp, Skia assets) enter the build; licences are inventoried before any release (PLAN P5.4).
- Threat model: new trust boundary "app to local toolchain". Residual risk: a trusted folder's program runs with user permissions. `review-threat-model` should run before acceptance.
- Language support now scales with data: a new language is a pack, tested by one generic test (shape, tools in the allowlist, placeholders valid).

## Alternatives considered
- **The website's IDE inside a web view (Tauri, Electron, WebView2).** Identical by construction and the cheapest, and rejected by the maintainer: it is not a native GUI.
- **A shell or terminal-only tool.** Not a GUI.
- **Avalonia with an embedded browser editor (Monaco in a web view).** Hybrid; the native editor is simpler and keeps the process model clean.
- **A Go or Rust host for everything.** Loses the shared typed core and the purpose of the project; used only where a library is clearly better (Rust, item 5).
- **Hard-coding each language in F#.** The website does this for its 18 and it does not scale to "all languages"; the desktop reads packs instead.

## Amendment 2026-11-18: module layout, tests and accessibility
- **Layout.** The desktop app's code is not one file. `Program.fs` starts Avalonia; `MainWindow.fs` holds `Shell`, which only joins the parts. Compile order (F# is order dependent): `Ext`, `Access`, `Palette`, `Session`, `Hover`, `DebugView`, `Grammars`, `TerminalView`, `Dialogs`, `Gutter`, `Model`, `Decisions`, `Ui` (the widgets and the state every part shares), `Appearance`, `StatusBar`, `Problems`, `TerminalPanel`, `LanguageServers`, `HoverPanel`, `EditorTabs`, `RunPanel`, `RenameFeature`, `DebugPanel`, `Explorer`, `SidebarViews`, `FolderSession`, `Commands`, `CommandPalette`, `Menus`, `Layout`, `Wiring`, `MainWindow`, `Program`. A part is a class that takes the earlier parts it needs in its constructor; none reaches back to a later one (late links go through function slots on `Ui`, e.g. `OpenFileFn`). The split changed no behaviour.
- **Decisions are pure.** What does not need a control (path rules, the key map, bracket pairing, disk-sync policy, status text) lives in `Decisions.fs` with unit tests, so a change to it is tested without a window.
- **The window is tested headless** (Avalonia.Headless, no display): the real `Shell` on a temp folder. See `docs/operations/desktop.md`, "Tests and accessibility".
- **Accessibility is a rule of the build**: a control a person can operate without an accessible name fails `AccessibilityTests`. This adds no dependency.
- **Programs are started through `Launch`** (`Proc.fs`): PATH and PATHEXT lookup, and on Windows a `.cmd` or `.bat` through `cmd.exe /d /v:off /s /c` with strict quoting; an argument that quotes cannot hold is refused. This refines item 6 ("never through a shell"): the only shell is `cmd.exe` for a Windows script, with arguments that cannot act as commands.
