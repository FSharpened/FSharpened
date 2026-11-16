# FSharpened.Effects - Unified effect pipeline

- **Layer:** core
- **Priority:** P0 | **Phase:** 0.5 | **Stability:** Preview
- **Depends on:** `FSharpened.Diagnostics`
- **Wraps / composes:** FsToolkit.ErrorHandling
- **Gaps addressed:** A, X
- **Skill:** [`.agents/skills/add-effect-combinator.md`](../../../.agents/skills/add-effect-combinator.md)
- **Consumer doc:** [`docs/packages/Effects.md`](../../packages/Effects.md)

## Purpose

Effect<'T,'E> struct and a single effect { } CE covering async, task, result, option and validation with OTel context. Not algebraic effects at runtime.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
