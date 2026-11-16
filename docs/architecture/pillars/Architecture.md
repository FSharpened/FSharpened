# FSharpened.Architecture - Living architecture and fitness functions

- **Layer:** core
- **Priority:** P1 | **Phase:** 1.5 | **Stability:** Preview
- **Depends on:** `FSharpened.CompilerBridge`, `FSharpened.Semantic`
- **Wraps / composes:** FSharp.Compiler.Service, fsdocs
- **Gaps addressed:** BB
- **Skill:** [`.agents/skills/add-architecture-rule.md`](../../../.agents/skills/add-architecture-rule.md)
- **Consumer doc:** [`docs/packages/Architecture.md`](../../packages/Architecture.md)

## Purpose

Typed dependency graph from FCS symbol resolution, type-directed fitness-function DSL, generated architecture docs.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
