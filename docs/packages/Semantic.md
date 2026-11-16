# FSharpened.Semantic

find / check / types / rename / dead-code plus model-aware queries, exposed as a library and a stdio MCP server. Agents must use it for symbol resolution.

| | |
|---|---|
| NuGet id | `FSharpened.Semantic` |
| Stability tier | Preview |
| Depends on | `FSharpened.Platform`, `FSharpened.CompilerBridge` |
| Priority / phase | P1 / 0.8 |
| Source | `src/FSharpened.Semantic/` |

## Public API

_Skeleton: API surface is not yet defined._ When it is, list every public module and type here. The docs-drift check compares this section to the compiled assembly (see `docs/operations/ci.md`).

## Usage

```fsharp
// Example appears here once the pillar is Active.
```

## What it does not do

- It is not a fork, a compiler patch, or a change to `FSharp.Core`.
- It does not reimplement an existing tool it can wrap (Invariant 11).
- It does not contain `#if` on a platform (Invariant 1); platform behaviour lives in `FSharpened.Platform`.

> FSharpened is an independent community project. It is not affiliated with, endorsed by, or a replacement for Microsoft's F# compiler or the F# Software Foundation.
