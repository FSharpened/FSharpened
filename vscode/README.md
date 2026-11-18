# FSharpened for VS Code

F# language server (FsAutoComplete), Fantomas formatting and FSharpened diagnostics, for people who already have the
tools installed. **Local only: no network calls, no telemetry.** In an untrusted workspace it starts nothing.

| | |
|---|---|
| Language server | Starts FsAutoComplete for F# files (`fsharpened.languageServer.*`). |
| Formatting | Fantomas as the F# document formatter, using the workspace `.editorconfig`; optional format on save. |
| Diagnostics | `fsh analyze --format json` on open and save, as problems with codes, docs links and quick fixes. |
| Commands | `FSharpened: Run local CI`, `Doctor`, `Show last CI report`, `Analyze current file`, `Restart language server`. |
| Status bar | Language server and diagnostics state; click it for the Doctor report. |

It needs the tools it drives: `dotnet tool install fsautocomplete`, `dotnet tool install fantomas` (or a
`.config/dotnet-tools.json` that lists them, then `dotnet tool restore`), and a built `fsh`. Run **FSharpened: Doctor**
to see what was found and what to do about what was not.

Install from a local file (this package is not published): `code --install-extension fsharpened-0.0.1.vsix`.

Documentation: <https://github.com/FSharpened/FSharpened/blob/main/docs/operations/vscode.md>.
