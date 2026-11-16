# FSharpened.Models - Internal specialised models and resilience envelope

- **Layer:** core
- **Priority:** P1 | **Phase:** 0.0 | **Stability:** Experimental
- **Depends on:** `FSharpened.Statepoints`, `FSharpened.Telemetry`, `FSharpened.Security`, `FSharpened.Platform`
- **Wraps / composes:** ONNX Runtime, ML.NET
- **Gaps addressed:** models
- **Skill:** [`.agents/skills/train-model.md`](../../../.agents/skills/train-model.md)
- **Consumer doc:** [`docs/packages/Models.md`](../../packages/Models.md)

## Purpose

Hyper-specific CDM/APM/ECM/SSM/ATRM models, recursive training pipeline with frozen judges, five-layer deterministic fallback stack, atomic hot-swap via statepoints. The symbolic fallback ships first.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
