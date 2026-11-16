# FSharpened.ML.Experiments - Experiment tracking, search and AutoML

- **Layer:** ml
- **Priority:** P1 | **Phase:** 4.2 | **Stability:** Experimental
- **Depends on:** `FSharpened.ML.Authoring`, `FSharpened.ML.Data`, `FSharpened.Statepoints`
- **Wraps / composes:** ML.NET AutoML, MLflow, Polyglot Notebooks
- **Gaps addressed:** ML
- **Skill:** [`.agents/skills/add-search-strategy.md`](../../../.agents/skills/add-search-strategy.md)
- **Consumer doc:** [`docs/packages/ML.Experiments.md`](../../packages/ML.Experiments.md)

## Purpose

Local-first tracker where every run is a statepoint; typed search { } CE (random, Bayesian, Hyperband, PBT); AutoML wrapper; notebook integration. Genuinely greenfield.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
