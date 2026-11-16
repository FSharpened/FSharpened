# Editors

| Editor | Platforms | F# backend | Notes |
|---|---|---|---|
| Visual Studio | Windows | built-in | Fantomas needs an extra extension; thinner than C# |
| JetBrains Rider | Win/Linux/macOS | own engine | Fantomas integrated |
| VS Code + Ionide | all | FsAutoComplete | Debugging depends on netcoredbg / C# extension today |
| VS Code / any LSP + SharpLsp | all | FCS sidecar (.NET) | Fast; documented gaps below |
| Agents / CI | all | FsAutoComplete (`dotnet tool`), FsLangMCP via `FSharpened.Semantic` | headless |

SharpLsp documented gaps (closed upstream-first by `FSharpened.Tooling`): unopened-namespace completion and auto-open; host-routed Fantomas formatting (sidecar has it, host does not route it); FSharpLint diagnostics; full `.fsx` semantic parity and documentation endpoints; cross-language hierarchies. Verify against the current SharpLsp release before relying on this list.
