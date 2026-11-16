# FSharpened.ML.Serving - Inference runtime, drift response, shadow deploy

- **Layer:** ml
- **Priority:** P1 | **Phase:** 4.6 | **Stability:** Experimental
- **Depends on:** `FSharpened.ML.Registry`, `FSharpened.ML.Hardware`, `FSharpened.Telemetry`
- **Wraps / composes:** ONNX Runtime, Giraffe, Saturn
- **Gaps addressed:** ML
- **Skill:** [`.agents/skills/deploy-model-endpoint.md`](../../../.agents/skills/deploy-model-endpoint.md)
- **Consumer doc:** [`docs/packages/ML.Serving.md`](../../packages/ML.Serving.md)

## Purpose

ONNX-first runtime with batching and warm-up, Giraffe integration, drift detection, shadow traffic, automatic rollback to prior artifact.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
