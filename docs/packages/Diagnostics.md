# FSharpened.Diagnostics

DiagnosticScope CE, Result-centric error pipeline, ILogger integration, Sentry/OTLP bridges. Every error has a typed shape.

| | |
|---|---|
| NuGet id | `FSharpened.Diagnostics` |
| Stability tier | Stable |
| Depends on | `FSharpened.Platform` |
| Priority / phase | P0 / 1.0 |
| Source | `src/FSharpened.Diagnostics/` |

## Public API

`FSharpened.Diagnostics` has the typed shapes (`Severity`, `SourceRange`, `StructuredDiagnostic` and its `create`, `withContext`, `withFix`) and the machine-readable report that editors read (ADR-0025):

- `TextEdit` and `Finding` (a diagnostic plus the edits that fix it).
- `DiagnosticReport.toJson` (schema `fsharpened.diagnostics/1`, no reflection, NativeAOT-safe), `DiagnosticReport.toText`, `DiagnosticReport.Schema`.

The format is documented in [docs/operations/vscode.md](../operations/vscode.md); `vscode/test/fixtures/analyze-sample.golden.json` is the golden output the .NET and the editor tests share. Everything else in the pillar is still a skeleton.

## Diagnostic codes
Printed by `fsh analyze` (rules in `FSharpened.Analyzers.PolicyRules`, text-level stand-ins for the curated analyzer set).

### FSH0001
Warning. Conditional compilation on a platform (`#if WINDOWS`, `LINUX`, `OSX`, `MACOS`, `MACOSX`, `FREEBSD`, also in `#elif`). Product code must not branch on a platform (AGENTS.md hard rules, invariant 1); platform behaviour lives in `FSharpened.Platform`. No mechanical fix.

### FSH0002
Info. `printfn`, `printf`, `eprintfn` or `eprintf` applied to a string literal with no format specifier. printf-family functions use reflection that NativeAOT cannot compile (ADR-0014, gap K). Fix: `System.Console.Out.WriteLine` (or `Write`, `Error`). Not reported in `//` comment lines, for longer identifiers, or when the literal contains `%` or a backslash.

Limits of both rules: block comments and multi-line strings are not understood.

## Status of the rest of the API

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
