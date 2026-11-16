# FSharpened.Analyzers - Curated FSharp.Analyzers.SDK rule set

- **Layer:** core
- **Priority:** P0 | **Phase:** 0.5 | **Stability:** Preview
- **Depends on:** `FSharpened.CompilerBridge`, `FSharpened.Diagnostics`
- **Wraps / composes:** FSharp.Analyzers.SDK, FSharpLint
- **Gaps addressed:** G, H, J, AA
- **Skill:** [`.agents/skills/add-analyzer-rule.md`](../../../.agents/skills/add-analyzer-rule.md)
- **Consumer doc:** [`docs/packages/Analyzers.md`](../../packages/Analyzers.md)

## Purpose

File-order, value-restriction, Async/Task, Option/ValueOption, nullability, performance, documentation-coverage, testing-gap and supply-chain rules. Every rule is opt-in and has an RFC with false-positive analysis.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
