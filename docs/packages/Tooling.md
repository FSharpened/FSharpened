# FSharpened.Tooling

VS Code companion, VSIX, headless language-server harness, SharpLsp gap closure (upstream-first), statepoint-backed hot-reload sessions.

| | |
|---|---|
| NuGet id | `FSharpened.Tooling` |
| Stability tier | Preview |
| Depends on | `FSharpened.CompilerBridge`, `FSharpened.Statepoints`, `FSharpened.Platform` |
| Priority / phase | P1 / 1.2 |
| Source | `src/FSharpened.Tooling/` |

## Public API

_The pillar marker is still a skeleton._ The first real code is `FSharpened.Tooling.Completion` (ADR-0026): `CompletionRequest`, `Candidate`, `CompletionResult`, `ICompletionProvider`, `Context` (windowing, secret and sensitive-file filter), `StaticProvider`, `Chain`, `MiniJson` and `WorkersAi` (a provider that calls the `ai/` Worker through an injected transport). Pure F#, tested through `tests/FSharpened.Ide.Tests` because the browser IDE compiles the same files with Fable; see `docs/operations/ai.md`. The rest of the API surface is not yet defined. When it is, list every public module and type here. The docs-drift check compares this section to the compiled assembly (see `docs/operations/ci.md`).

## Usage

```fsharp
// Example appears here once the pillar is Active.
```

## What it does not do

- It is not a fork, a compiler patch, or a change to `FSharp.Core`.
- It does not reimplement an existing tool it can wrap (Invariant 11).
- It does not contain `#if` on a platform (Invariant 1); platform behaviour lives in `FSharpened.Platform`.

> FSharpened is an independent community project. It is not affiliated with, endorsed by, or a replacement for Microsoft's F# compiler or the F# Software Foundation.
