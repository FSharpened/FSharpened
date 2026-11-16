# FSharpened.Debugging - F#-aware debugging and profiling

- **Layer:** core
- **Priority:** P0 | **Phase:** 1.9 | **Stability:** Preview
- **Depends on:** `FSharpened.Statepoints`, `FSharpened.CompilerBridge`, `FSharpened.Platform`
- **Wraps / composes:** netcoredbg
- **Gaps addressed:** L, M
- **Skill:** [`.agents/skills/add-debugger-feature.md`](../../../.agents/skills/add-debugger-feature.md)
- **Consumer doc:** [`docs/packages/Debugging.md`](../../packages/Debugging.md)

## Purpose

DAP proxy over netcoredbg, in-process FCS expression evaluator via ICorDebug, PDB+IL symbol snapshots for hot-reloaded assemblies, profiling bridge.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
