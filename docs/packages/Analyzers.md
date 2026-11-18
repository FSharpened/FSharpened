# FSharpened.Analyzers

File-order, value-restriction, Async/Task, Option/ValueOption, nullability, performance, documentation-coverage, testing-gap and supply-chain rules. Every rule is opt-in and has an RFC with false-positive analysis.

| | |
|---|---|
| NuGet id | `FSharpened.Analyzers` |
| Stability tier | Preview |
| Depends on | `FSharpened.CompilerBridge`, `FSharpened.Diagnostics` |
| Priority / phase | P0 / 0.5 |
| Source | `src/FSharpened.Analyzers/` |

## Public API

`FSharpened.Analyzers.PolicyRules.check file text` returns the `Finding`s of two text-level repository-policy rules, `FSH0001` (platform `#if`) and `FSH0002` (`printfn` of a literal); see [Diagnostics](Diagnostics.md#diagnostic-codes). They exist so `fsh analyze` has real output for editors (ADR-0025) and are replaced by the FSharp.Analyzers.SDK rule set; they are not that rule set and have no RFC.

_Skeleton: the rest of the API surface is not yet defined._ When it is, list every public module and type here. The docs-drift check compares this section to the compiled assembly (see `docs/operations/ci.md`).

## Usage

```fsharp
// Example appears here once the pillar is Active.
```

## What it does not do

- It is not a fork, a compiler patch, or a change to `FSharp.Core`.
- It does not reimplement an existing tool it can wrap (Invariant 11).
- It does not contain `#if` on a platform (Invariant 1); platform behaviour lives in `FSharpened.Platform`.

> FSharpened is an independent community project. It is not affiliated with, endorsed by, or a replacement for Microsoft's F# compiler or the F# Software Foundation.
