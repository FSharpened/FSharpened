# FSharpened.ML.Authoring - F#-native model definition and training

- **Layer:** ml
- **Priority:** P1 | **Phase:** 4.1 | **Stability:** Experimental
- **Depends on:** `FSharpened.ML.Hardware`, `FSharpened.ML.Portability`, `FSharpened.Statepoints`, `FSharpened.Telemetry`
- **Wraps / composes:** TorchSharp, DiffSharp, Furnace, Toro
- **Gaps addressed:** ML
- **Skill:** [`.agents/skills/add-ml-backend.md`](../../../.agents/skills/add-ml-backend.md)
- **Consumer doc:** [`docs/packages/ML.Authoring.md`](../../packages/ML.Authoring.md)

## Purpose

model { } and training { } CEs, DU architectures, nested-AD combinators, elastic distributed training via statepoint rollback.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
