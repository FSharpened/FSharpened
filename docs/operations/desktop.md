# Desktop IDE (ADR-0023)

A native window written in F# (Avalonia 12, AvaloniaEdit, TextMateSharp). It looks and behaves like the website IDE and uses the same F# core (`tools/FSharpened.Ide`), but it runs **your own toolchain** on **your own folders**. Status: working tree, not released (ADR-0023 is Proposed; installers and signing are a human gate).

## Run it
```bash
dotnet run --project tools/FSharpened.Ide.Desktop -- path/to/folder
```
Switches: `--folder DIR`, `--file REL`, `--theme ID`, `--command ID` (a palette command, repeatable), `--palette TEXT`, `--search TEXT` and `--replace-with TEXT` (fill the search panel and run it), `--late-command ID` (a command run just before the screenshot, once language servers are up), `--terminal-line TEXT` (a line sent to the terminal), `--selftest` (no window: loads the shipped packs and grammars, lists a temporary folder, loads the native libraries shipped for this RID, prints one JSON line, exits 0 or 1; the release pipeline uses it to prove a packaged copy is whole), `--screenshot FILE.png [--delay SECONDS] [--size WxH]` (draws the real window to a PNG and quits; used to check the app without a person). The environment variable `FSHARPENED_DATA` moves the settings, trust list and your language packs to another folder (portable use, clean tests).

## What is in it
| Area | Web IDE | Desktop IDE |
|---|---|---|
| Layout, colour roles, nine themes (two high-contrast), settings | yes | yes (same F# `Themes` module; syntax colours come from the nearest bundled TextMate theme) |
| Command palette, quick open, go to line and symbol, ranking | yes | yes (same `Fuzzy` and `Symbols` modules) |
| Search in files, outline, breadcrumbs, status bar | yes | yes, and **replace in files** (include and exclude patterns, case, whole word, regex; a file open in the editor is searched and changed as it is there, so Undo works) |
| Find and replace in the open file | yes | yes (Ctrl+F, Ctrl+H) |
| Right-click menus (editor, tree, tabs) | yes | yes |
| Files | browser storage | **real folders**: open with the native dialog, save, create, rename, delete (permanent, asks first), file watching |
| Run | remote compilers (18 languages) | **local toolchain**, any language with a pack and an installed tool |
| Problems | from compiler output | from compiler output (core parsers for the 18, generic `file:line:col` parsers for the rest) **and** from language servers |
| Git | no | branch, changed-file count, coloured tree |
| Language servers | no (PLAN P7.4) | minimal client: problems as underlines, completion (Ctrl+Space), go to definition (F12); hover box: rest the pointer on code (or *Editor: Show Hover*) and it shows the problems at that spot and what the server says |
| Multi-cursor, snippets, import of VS Code themes/snippets/settings/.vsix, Download for VS Code | yes | **not yet** |
| Terminal | no | **line-based**: your own shell on pipes in the opened folder (Ctrl+`), history with Up and Down. **Not a pseudo-terminal**: full-screen programs (vim, top), colours and Ctrl+C do not work |
| Debugger, split editors, minimap | no | **not yet** |
| VS Code extensions, GitHub Copilot | no | **no** (ADR-0022 stands: extension code is not hosted) |

## Trust and limits
- Opening a folder runs nothing. The first **Run**, the first language server start and the first **terminal** in a folder ask for trust (the terminal runs whatever is typed into it). The answer is remembered per folder in the data folder (`trust.json`). *Run → Revoke Trust for This Folder* withdraws it.
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

## Grammars
Highlighting uses TextMate grammars. TextMateSharp brings most; the app adds the ones it lacks from `grammars/*.tmLanguage.json` (shipped: Haskell, OCaml, Elixir, Kotlin, Scala, Zig, TOML). A grammar's `scopeName` is what a language pack's `scope` names. Put a `.tmLanguage.json` file in `FSharpened/grammars` under the data folder to add a language or replace a shipped grammar (yours wins); files starting with `_`, files that are not JSON and files with no `scopeName` are ignored. Restart the app after adding one.

## Packaging
`python tools/ci/fsh_ci.py run --profile release` publishes the app (`desktop-package` stage) and archives it as `fsharpened-desktop-<version>-<rid>.zip` (Windows) or `.tar.gz` (Linux, macOS) inside the release bundle ([Release](release.md) lists what is and is not an artefact). Unpack it anywhere and run `FSharpened.Ide.Desktop`; `languages/`, `grammars/` and `runtimes/<rid>/native` sit next to it.

- **Self-contained, plain.** `dotnet publish -c Release -r <rid> --self-contained`, a folder (not single-file), no debug symbols. About 74 MB as a zip on win-x64. **Not trimmed and not NativeAOT**, as a decision rather than a measurement: the app uses `printf` and other reflection in F#, loads `AvaloniaEdit.xaml` styles by URI at run time, and TextMateSharp reads grammars through reflection-based JSON, and none of this was tried under the trimmer. Trimming Avalonia needs compiled bindings and trim annotations in this code first; that is future work, not a switch.
- **Native libraries.** Every `cdylib` crate under `native/rust` is built for the host RID with `cargo build --release --locked` and copied to `runtimes/<rid>/native`, where `NativeResolver` looks. `packaging/desktop.json` excludes the reference crate `fsh-interop-example`, so at this commit **no Rust library is shipped** (the app loads none yet); the code path was checked once by building and loading that crate. Without `cargo` the stage warns and the package lacks the libraries (`--require-rust` makes it fail).
- **Smoke.** The packaged program runs `--selftest` (required) and, where a display exists, a `--screenshot` window (best effort; headless Linux skips it and the report says so).
- **Installers are inputs only.** `packaging/` has a WiX file, an MSIX manifest, a Linux `.desktop` and AppStream file with an AppImage script, and a macOS `Info.plist`, with a README of the human steps. None was built or run here, and code signing, notarisation and store accounts are human gates.

## Known limits (checked, not guessed)
- Haskell, OCaml, Elixir, Kotlin, Scala, Zig and TOML have no grammar in TextMateSharp, so the app ships its own small ones (`grammars/`). They colour comments, strings, numbers, keywords and types, not every construct of the language; a better grammar dropped into `FSharpened/grammars` replaces them.
- The terminal has no pseudo-terminal, so a program that needs a real terminal behaves badly in it, and Ctrl+C cannot interrupt a running command (use *Terminal: Restart*). That needs the Rust work in PLAN P7.8.
- On Windows a language server installed as a `.cmd` shim (typical for npm packages) cannot be started; it needs an `.exe`.
- The language-server client is checked against a scripted server and a fake in tests, and against a scripted Python server in the real window. It has **not** been run against gopls, rust-analyzer or others on this machine (none was installed).
- The packaged archive was built and smoke-run on win-x64 only; Linux and macOS archives come from the same code but were not run here (the `release-dry-run.yml` workflow is the way to try them). Only a Windows build was run here; the six-RID matrix (CI) is the check for Linux and macOS. The window has no automated UI test; `--screenshot` was used by hand.
- Syntax colours are the bundled TextMate theme nearest to the chosen theme, not the exact website palette.

## Polyglot design
F# for the UI, core, host, language-pack engine and language-server client. Rust (through `FSharpened.Interop.Rust`) is the choice for a pseudo-terminal, file watching and fast search, not built yet. Language servers, compilers, formatters and debuggers are each language's own tools. Go is supported as a language through its pack; a Go agent for remote or container hosts is a candidate, not part of v1. See ADR-0023 for the reasoning.
