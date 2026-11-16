# Boundaries: consume, own, upstream

| Category | Meaning | Examples |
|---|---|---|
| **Consume** | Used as-is through a stable surface | FCS, FSharp.Analyzers.SDK, ONNX Runtime, netcoredbg, Sigstore |
| **Wrap / compose** | Facade over an existing tool; Invariant 11 default | FsLangMCP, dep-scan, SOVA, Stryker.NET, DuckDB |
| **Own** | Greenfield because nothing fills the gap | Statepoints, TOML parser (XParsec), model registry, experiment tracking, ML.Portability |
| **Upstream** | Fix belongs elsewhere; contribute first | compiler diagnostics, FCS hot-reload session, SharpLsp gaps |

## F# file-order dependence
F# is single-pass and file-ordered: a type or function must be declared earlier in the same file or in an earlier `<Compile Include>`. In this repo:
- Within each project, `Compile` order is dependency order; the generator emits a single file for skeleton projects.
- Across projects, edges follow `registry/pillars.json`; cycles are impossible by construction and rejected by `check_docs.py`.
- The `Analyzers` file-order rule explains these errors to users (gap J).

## Rules
- A new dependency edge between pillars needs an ADR.
- No `#if` on a platform outside `FSharpened.Platform`.
- Downstream workarounds link an upstream issue (see upstream policy).
