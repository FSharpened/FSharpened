# FSharpened.Security

Composes SBOM (dep-scan, SOVA, ShieldedStack), SLSA/Sigstore, lock-file validation (NuGet, CPM and Paket); extends signing and SBOM to model artifacts. Continuous, not release-gate.

| | |
|---|---|
| NuGet id | `FSharpened.Security` |
| Stability tier | Preview |
| Depends on | `FSharpened.Build` |
| Priority / phase | P1 / 2.6 |
| Source | `src/FSharpened.Security/` |

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
