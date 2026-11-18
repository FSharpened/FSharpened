# FSharpened for VS Code

A VS Code extension (`vscode/`, ADR-0025, PLAN P7.5) that connects VS Code to the tools an F# developer already has, plus FSharpened's own diagnostics. It is **local only**: it starts programs on your machine, makes no network request and sends no telemetry. In a workspace you have not trusted it starts nothing.

This is the extension *for* VS Code. For FSharpened's own browser and desktop IDEs and what they can do with VS Code content, see [ide.md](ide.md), [desktop.md](desktop.md) and ADR-0022.

## Install (from a local file)
The extension is **not published** to any marketplace. Build the package and install it:

```bash
cd vscode
npm ci --ignore-scripts
npm run package          # writes ../artifacts/fsharpened-0.0.1.vsix
npm run verify           # checks what is inside it
code --install-extension ../artifacts/fsharpened-0.0.1.vsix
```

To try it without touching your own VS Code profile, use a throwaway profile: `node scripts/smoke-vscode.mjs` installs the package into a temporary `--user-data-dir` and `--extensions-dir`, opens a sample workspace and checks the extension's log lines (see "What was checked").

## What you need installed
| Feature | Needs | Found through |
|---|---|---|
| Language server | FsAutoComplete (`dotnet tool install fsautocomplete`) | `fsharpened.languageServer.path`, then `fsautocomplete` in the nearest `.config/dotnet-tools.json` (run as `dotnet fsautocomplete`, after `dotnet tool restore`), then `PATH` |
| Formatting | Fantomas (`dotnet tool install fantomas`; this repo's manifest already lists it) | `fsharpened.fantomas.path`, then the manifest (`dotnet fantomas`), then `PATH` |
| FSharpened diagnostics | the `fsh` CLI: `dotnet build tools/FSharpened.Cli -c Release` | `fsharpened.fsh.path` (an executable or `fsh.dll`), then the manifest, then `PATH` |
| Run local CI | Python 3 and a checkout of this repository | `fsharpened.python.path`, then `python3`, `python`, `py` on `PATH` |

A configured path that does not exist is reported as an error; it is never silently replaced by another tool. **FSharpened: Doctor** lists what was found, how, which versions, and what was tried for each tool that is missing.

FsAutoComplete releases from 0.87 need the .NET 10 SDK; with SDK 9 install an earlier one (`dotnet tool install fsautocomplete --version 0.83.0`).

## Commands
| Command | What it does |
|---|---|
| `FSharpened: Run local CI` | Opens a terminal running `python tools/ci/fsh_ci.py run --profile <fast\|full\|all>` in the workspace folder (setting `fsharpened.ci.profile`, default `fast`). Needs this repository as the workspace |
| `FSharpened: Show last CI report` | Opens the newest `.fsharpened/ci/runs/<timestamp>/report.md` |
| `FSharpened: Doctor` | Workspace trust, language server state, each tool's location and version, `fsh doctor`, in the FSharpened output channel |
| `FSharpened: Analyze current file` | Saves and analyzes the file now |
| `FSharpened: Format document with Fantomas` | The same formatter as Format Document, with an error message when it fails |
| `FSharpened: Restart language server` | After installing or changing the server |
| `FSharpened: Show output` | The FSharpened output channel (client log, analysis lines, Doctor) |

The **status bar item** shows the language server state (starting, running, missing, failed, restricted), the number of FSharpened findings in the active file, and "no fsh" when diagnostics cannot run. Click it for the Doctor report.

## Settings
| Setting | Default | Meaning |
|---|---|---|
| `fsharpened.languageServer.enabled` | `true` | start FsAutoComplete for F# files |
| `fsharpened.languageServer.path` / `.args` | empty | the executable and extra arguments |
| `fsharpened.languageServer.trace` | `off` | `messages` or `verbose` LSP tracing into the output channel |
| `fsharpened.fantomas.path` | empty | Fantomas executable |
| `fsharpened.fantomas.formatOnSave` | `false` | format on save (`editor.formatOnSave` also works) |
| `fsharpened.fsh.path` | empty | `fsh` executable or `fsh.dll` |
| `fsharpened.diagnostics.enabled` / `.runOnSave` / `.runOnOpen` | `true` | FSharpened problems |
| `fsharpened.python.path` | empty | Python 3 for the CI command |
| `fsharpened.ci.profile` | `fast` | `fast`, `full` or `all` |
| `fsharpened.toolTimeoutSeconds` | `60` | how long a formatting or analysis run may take |

`${workspaceFolder}` works in path settings. The path and argument settings have scope `machine-overridable` and are **restricted in untrusted workspaces**.

## Diagnostics format (`fsharpened.diagnostics/1`)
`fsh analyze --format json <file-or-folder>...` prints one JSON document on stdout:

```json
{"schema":"fsharpened.diagnostics/1","tool":"fsh 0.0.1.0","diagnostics":[
  {"code":"FSH0002","severity":"info","file":"Sample.fs",
   "range":{"startLine":4,"startColumn":4,"endLine":4,"endColumn":11},
   "message":"printfn uses reflection that NativeAOT cannot compile; ...",
   "construct":"printfn","docs":"https://.../Diagnostics.md#fsh0002",
   "fix":{"title":"Replace printfn with System.Console.Out.WriteLine",
          "edits":[{"range":{"startLine":4,"startColumn":4,"endLine":4,"endColumn":11},"newText":"System.Console.Out.WriteLine"}]},
   "context":{}}]}
```
Severities are `error`, `warning`, `info`, `hidden`. **Lines are 1-based, columns 0-based UTF-16 units, the end is exclusive.** `file`, `range`, `construct`, `docs` and `fix` may be `null`. A fix with an empty `edits` list is advice only. The exit code is 0 when the report was produced (1 with `--ci` when there is a warning or error, 2 for bad usage or a missing path). `--format text` prints `file(line,col): severity CODE: message`. A new major schema number is refused by the extension with a message; minor additions are ignored. The codes are listed in [Diagnostics](../packages/Diagnostics.md).

## What works, and what does not
- **Works:** everything in the tables above, in a trusted workspace, with the tools installed; the unit-testable logic (report parsing, tool resolution, the Fantomas daemon, the Doctor text, the status text, the package) is covered by 57 Node tests and 10 .NET tests.
- **Is a stand-in:** `FSH0001` and `FSH0002` are two text-level rules, not the curated analyzer set. They do not see block comments or multi-line strings.
- **Does not exist:** range formatting (only whole documents), debugging, test explorer, project creation, F# syntax highlighting (VS Code needs a grammar; install a F# extension for that, or use the one your setup already has; this extension contributes only the `fsharp` language id, file extensions and bracket rules), Copilot integration (ADR-0022), a web extension (`vscode.dev`: virtual workspaces are unsupported), installing the tools for you, publishing.
- **Windows `.cmd` shims** cannot be started (no shell is used): point the settings at the real `.exe` (a dotnet tool's `.exe` works).

## Security
- Workspace Trust: `capabilities.untrustedWorkspaces` = `limited`. Untrusted: no language server, no Fantomas, no analysis, no CI terminal, no version probes; the status bar says "restricted"; Doctor still works and says why. Trusting the workspace starts everything without a reload.
- Programs are started with no shell and fixed argument lists. The tools are chosen from settings and a `.config/dotnet-tools.json` in the folder you trusted: trust only folders you would run a build in.
- No network access and no telemetry in the extension. A test fails if `src/` imports a network module.
- The `.vsix` is checked for: unexpected files, source maps, absolute paths of the build machine, secret patterns, size.

## What was checked, honestly
See the status section in [STATUS](../STATUS.md) for the dated result. In short: the logic is unit-tested without VS Code; the packaged extension was installed into a throwaway profile of a real VS Code and shown to activate on a `.fs` file, start a language server, and analyze with the built `fsh`, once against a **fake** language server and once against real FsAutoComplete 0.83.0 (which started and answered, but without a project it only produced "not in LoadedProjects" errors, so no F# intelligence was exercised). `@vscode/test-electron`, macOS and Linux were not run.

## Developing
```bash
cd vscode
npm ci --ignore-scripts
npm run typecheck && npm test      # 57 node tests; the real-Fantomas test needs `dotnet tool restore`
npm run build                      # dist/extension.js
```
`python tools/ci/fsh_ci.py run --profile all` runs the `vscode` stage (type-check, build, tests, package, verify); `.github/workflows/vscode.yml` runs the same steps. After changing the report format run the .NET tests with `FSH_UPDATE_GOLDEN=1` to rewrite `vscode/test/fixtures/analyze-sample.golden.json`, and review the diff: the Node tests parse that file.
