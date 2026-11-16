# FSharpened.Quality - F#-aware quality gates

- **Layer:** core
- **Priority:** P1 | **Phase:** 2.5 | **Stability:** Preview
- **Depends on:** `FSharpened.CompilerBridge`, `FSharpened.Analyzers`
- **Wraps / composes:** Lizard, dotCover, AltCover, energy-state-analyzer
- **Gaps addressed:** D
- **Skill:** [`.agents/skills/add-quality-metric.md`](../../../.agents/skills/add-quality-metric.md)
- **Consumer doc:** [`docs/packages/Quality.md`](../../packages/Quality.md)

## Purpose

Complexity computed from the typed AST (match case = decision point), coverage, CRAP, mutation-score gates, baseline-comparison mode.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
