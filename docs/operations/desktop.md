# Desktop IDE (ADR-0023)

A native window written in F# (Avalonia 12, AvaloniaEdit, TextMateSharp). It looks and behaves like the website IDE and uses the same F# core (`tools/FSharpened.Ide`), but it runs **your own toolchain** on **your own folders**. Status: working tree, not released (ADR-0023 is Proposed; installers and signing are a human gate).

## Run it
```bash
dotnet run --project tools/FSharpened.Ide.Desktop -- path/to/folder
```
Switches: `--folder DIR`, `--file REL`, `--theme ID`, `--command ID` (a palette command, repeatable), `--palette TEXT`, `--screenshot FILE.png [--delay SECONDS] [--size WxH]` (draws the real window to a PNG and quits; used to check the app without a person). The environment variable `FSHARPENED_DATA` moves the settings, trust list and your language packs to another folder (portable use, clean tests).

## What is in it
| Area | Web IDE | Desktop IDE |
|---|---|---|
| Layout, colour roles, nine themes (two high-contrast), settings | yes | yes (same F# `Themes` module; syntax colours come from the nearest bundled TextMate theme) |
| Command palette, quick open, go to line and symbol, ranking | yes | yes (same `Fuzzy` and `Symbols` modules) |
| Search in files, outline, breadcrumbs, status bar | yes | yes (search finds, it does not replace yet) |
| Right-click menus (editor, tree, tabs) | yes | yes |
| Files | browser storage | **real folders**: open with the native dialog, save, create, rename, delete (permanent, asks first), file watching |
| Run | remote compilers (18 languages) | **local toolchain**, any language with a pack and an installed tool |
| Problems | from compiler output | from compiler output (core parsers for the 18, generic `file:line:col` parsers for the rest) **and** from language servers |
| Git | no | branch, changed-file count, coloured tree |
| Language servers | no (PLAN P7.4) | minimal client: problems as underlines, completion (Ctrl+Space), go to definition (F12); hover exists in the client library, not yet in the UI |
| Multi-cursor, snippets, import of VS Code themes/snippets/settings/.vsix, Download for VS Code | yes | **not yet** |
| Terminal, debugger, split editors, minimap, search and replace | no / no / no / no / yes | **not yet** |
| VS Code extensions, GitHub Copilot | no | **no** (ADR-0022 stands: extension code is not hosted) |

## Trust and limits
- Opening a folder runs nothing. The first **Run** (and the first language server start) in a folder asks for trust; the answer is remembered per folder in the data folder (`trust.json`). *Run → Revoke Trust for This Folder* withdraws it.
- Programs start as an argument list, never through a shell. Time limit 30 s (palette: 120 s), 1 MiB of output per stream, the whole process tree is killed on timeout, build output goes to a fresh temporary folder that is removed afterwards.
- **Local runs are not sandboxed.** A trusted folder's program has your permissions. Only trust code you would run anyway.
- The host refuses paths that leave the opened folder (`..`, absolute paths, links, reserved names), reads text files up to 5 MB and refuses binary or non-UTF-8 files.

## Adding a language
Put a `.json` file in `languages/` (shipped) or in `FSharpened/languages` under the data folder (yours; wins over a shipped pack with the same id). No code changes. Files starting with `_` are ignored; a bad pack is reported in *View → Tools and Languages* and skipped. Shape (`fsharpened.language/1`):
```json
{ "format": "fsharpened.language/1", "id": "go", "name": "Go", "extensions": [".go"], "scope": "source.go",
  "comments": { "line": "//", "block": ["/*", "*/"] }, "indent": { "tabs": true, "size": 4 },
  "server": { "command": "gopls", "args": [], "install": "go install golang.org/x/tools/gopls@latest" },
  "tools": [ { "id": "go", "label": "Go", "candidates": [["go"]], "version": ["version"], "install": "https://go.dev/dl/" } ],
  "run": [ { "files": ["go.mod"], "steps": [ { "phase": "run", "tool": "go", "args": ["run", "."] } ] },
           { "steps": [ { "phase": "run", "tool": "go", "args": ["run", "{entry}"] } ] } ],
  "format": { "tool": "gofmt", "args": ["-w", "{entry}"] } }
```
Placeholders: `{entry}` `{out}` `{dir}` `{root}` `{name}` and `{flags}` (an argument that is exactly `{flags}` becomes the flags box). Programs must be bare names declared under `tools`; `"tool": "built"` runs the output of an earlier `build` step. The first plan whose `files` patterns match and whose `entryExtensions` fit wins.

## Known limits (checked, not guessed)
- **No bundled highlighting grammar** for Haskell, OCaml, Elixir, Kotlin, Scala, Zig and TOML in TextMateSharp: those files open and run without colour. (A pack's own `grammar` file is read and validated but not loaded yet.)
- On Windows a language server installed as a `.cmd` shim (typical for npm packages) cannot be started; it needs an `.exe`.
- The language-server client is checked against a scripted server and a fake in tests, and against a scripted Python server in the real window. It has **not** been run against gopls, rust-analyzer or others on this machine (none was installed).
- Only a Windows build was run here; the six-RID matrix (CI) is the check for Linux and macOS. The window has no automated UI test; `--screenshot` was used by hand.
- Syntax colours are the bundled TextMate theme nearest to the chosen theme, not the exact website palette.

## Polyglot design
F# for the UI, core, host, language-pack engine and language-server client. Rust (through `FSharpened.Interop.Rust`) is the choice for a pseudo-terminal, file watching and fast search, not built yet. Language servers, compilers, formatters and debuggers are each language's own tools. Go is supported as a language through its pack; a Go agent for remote or container hosts is a candidate, not part of v1. See ADR-0023 for the reasoning.
