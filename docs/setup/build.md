# Build

| Goal | Command |
|---|---|
| Restore + build everything | `dotnet build FSharpened.slnx` |
| Build one pillar | `dotnet build src/FSharpened.<Id>/FSharpened.<Id>.fsproj` |
| Release build, CI-strict (warnings are errors) | `dotnet build FSharpened.slnx -c Release -p:CI=true` |
| Format check | `dotnet fantomas --check .` |
| Regenerate skeleton from registry | `python tools/scripts/scaffold.py` |
| Docs drift check | `python tools/scripts/check_docs.py` |
| NativeAOT publish smoke (per RID) | `dotnet publish tools/FSharpened.Cli -c Release -r <rid> -p:PublishAot=true` |

Versions are pinned centrally in `Directory.Packages.props`; never put a version in a `.fsproj`. Project order inside a project is dependency order (F# is file-ordered).

Planned: `dotnet fsh build --cached` (per-file incremental cache). Not implemented.
