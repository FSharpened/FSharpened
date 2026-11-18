# Desktop IDE (ADR-0023)

A native window written in F# (Avalonia 12, AvaloniaEdit, TextMateSharp). It looks and behaves like the website IDE and uses the same F# core (`tools/FSharpened.Ide`), but it runs **your own toolchain** on **your own folders**. Status: working tree, not released (ADR-0023 is Proposed; installers and signing are a human gate).

## Run it
```bash
dotnet run --project tools/FSharpened.Ide.Desktop -- path/to/folder
```
Switches: `--folder DIR`, `--file REL`, `--breakpoint REL:LINE` (repeatable), `--caret LINE:COL`, `--rename-to NAME` (what *Rename Symbol* uses without asking), `--theme ID`, `--command ID` (a palette command, repeatable), `--palette TEXT`, `--search TEXT` and `--replace-with TEXT` (fill the search panel and run it), `--late-command ID` (a command run just before the screenshot, once language servers are up), `--terminal-line TEXT` (a line sent to the terminal), `--screenshot FILE.png [--delay SECONDS] [--size WxH]` (draws the real window to a PNG and quits; used to check the app without a person). The environment variable `FSHARPENED_DATA` moves the settings, trust list and your language packs to another folder (portable use, clean tests).

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
| Rename symbol | no | **F2** through the language server: new name, preview of the files and lines, confirm (see *Rename and debugging*) |
| Debugger | no | **DAP**: breakpoints (F9 or a click in the gutter), F5, F10, F11, Shift+F11, Shift+F5, call stack, variables, debug console (see *Rename and debugging*) |
| Split editors, minimap | no | **not yet** |
| VS Code extensions, GitHub Copilot | no | **no** (ADR-0022 stands: extension code is not hosted) |

## Rename and debugging
**Rename.** Put the caret on a name and press F2 (or *Rename Symbol* in the palette or the editor's menu). The language server is asked whether the name can be renamed (`prepareRename`), you type the new name, the server returns the edits, and a preview lists every file with the number of changes and the lines before and after. *Rename* applies it: files open in the editor change there (one Undo, left unsaved), the others are written to disk. If any edit is invalid or two overlap, nothing is changed. A server that cannot rename says so; a server that asks for file operations (create, rename, delete) is refused for now. Files outside the opened folder are never touched.

**Debugging.** Needs a debug adapter for the language (a `debug` block in its pack) and that adapter on the PATH; if it is missing the DEBUG panel and a message say how to get it, before anything is built.

| Key | Command |
|---|---|
| F9, or click left of the line numbers | Toggle breakpoint (kept with its line while you edit; kept when the file is closed) |
| F5 | Start debugging; when paused, continue. Asks for trust in the folder first, like Run |
| F10, F11, Shift+F11 | Step over, step into, step out |
| Shift+F5 | Stop (the program is ended) |
| Ctrl+F5 | Run the file without a debugger (this was F5 before the debugger) |

While paused, the stopped line is highlighted with an arrow in the gutter, the DEBUG panel shows the call stack (click a frame to go there), the variables of that frame (open a row for its children) and a console: the program's output, and a line at the bottom that evaluates an expression in the selected frame. Breakpoints can be added and removed while the program runs. Stopping, or the program ending, clears the highlight and removes the build output.

| Language | Adapter | How it is started | Tried here |
|---|---|---|---|
| Python | `python -m debugpy.adapter` | stdio, no build | **yes, real debugpy**: stop at a breakpoint, locals, evaluate, step, continue |
| F#, C# | `netcoredbg --interpreter=vscode` | stdio; `dotnet build` of the nearest project, then the newest `bin/Debug/*/<project>.dll` | no (netcoredbg not installed) |
| Go | `dlv dap --listen=127.0.0.1:{port}` | TCP; delve builds the package of the open file | no (delve not installed) |
| C, C++, Rust | `lldb-dap` | stdio; `cc`, `c++` or `rustc` with `-g` builds the open file | no (lldb-dap not installed); Rust with Cargo projects debugs the single file, not the crate; Windows MSVC builds have PDB symbols that LLDB may not read |
| JavaScript, TypeScript | needs js-debug | not shipped: js-debug starts child sessions with `startDebugging`, which the client does not support yet | no |

The protocol client itself is tested against a scripted adapter that behaves like debugpy (`launch` answered after `configurationDone`, stop at the first breakpoint, step, continue), over stdio and over a local port, and against real debugpy when it can be found (set `FSH_DEBUGPY_LIBS` to a folder holding it; the VS Code Python extension ships one in `bundled/libs`).

A pack's debug block:
```json
"debug": { "adapter": "lldb-dap", "args": [], "install": "install LLVM: https://github.com/llvm/llvm-project/releases",
           "transport": "stdio", "adapterId": "lldb-dap", "request": "launch",
           "build": { "phase": "build", "tool": "cc", "args": ["-g", "{entry}", "-o", "{out}", "{flags}"] },
           "launch": { "program": "{out}", "cwd": "{root}", "args": [], "stopOnEntry": false } }
```
`transport` is `stdio` (default) or `tcp` (the adapter listens on `{port}`, which may appear in `args`). `build` is optional (a tool the pack declares); `launch` is the JSON sent with the `launch` (or `attach`, via `request`) request. Placeholders in `launch` and `build`: `{file}` (absolute path of the open file), `{entry}` (relative), `{root}`, `{dir}` (the file's folder), `{name}`, `{out}` (the build output path), `{flags}`, `{project}`, `{projectfile}`, `{projectdir}` (the nearest `.fsproj`, `.csproj` or `.vbproj`), and `{newest:bin/Debug/*/{project}.dll}` (the most recently written file matching the glob under the project folder).

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

## Known limits (checked, not guessed)
- Haskell, OCaml, Elixir, Kotlin, Scala, Zig and TOML have no grammar in TextMateSharp, so the app ships its own small ones (`grammars/`). They colour comments, strings, numbers, keywords and types, not every construct of the language; a better grammar dropped into `FSharpened/grammars` replaces them.
- Debugging: a stopped program is shown by file and line only; no conditional breakpoints, watch expressions, data breakpoints or attach (the client can send `attach`, the window has no way to pick a process); several threads are shown as the stopped one only. Dialogs (the rename preview, questions) are separate windows, so `--screenshot` writes each beside the main picture as `NAME.dialogN.png`.
- The terminal has no pseudo-terminal, so a program that needs a real terminal behaves badly in it, and Ctrl+C cannot interrupt a running command (use *Terminal: Restart*). That needs the Rust work in PLAN P7.8.
- On Windows a language server installed as a `.cmd` shim (typical for npm packages) cannot be started; it needs an `.exe`.
- The language-server client (including rename) is checked against a scripted server and a fake in tests, and against a scripted Python server in the real window. It has **not** been run against gopls, rust-analyzer or others on this machine (none was installed; `rust-analyzer` here is only a rustup shim).
- Only a Windows build was run here; the six-RID matrix (CI) is the check for Linux and macOS. The window has no automated UI test; `--screenshot` was used by hand.
- Syntax colours are the bundled TextMate theme nearest to the chosen theme, not the exact website palette.

## Polyglot design
F# for the UI, core, host, language-pack engine and language-server client. Rust (through `FSharpened.Interop.Rust`) is the choice for a pseudo-terminal, file watching and fast search, not built yet. Language servers, compilers, formatters and debuggers are each language's own tools. Go is supported as a language through its pack; a Go agent for remote or container hosts is a candidate, not part of v1. See ADR-0023 for the reasoning.
