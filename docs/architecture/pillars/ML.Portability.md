# FSharpened.ML.Portability - Capability contracts for fragile ML frameworks

- **Layer:** ml
- **Priority:** P0 | **Phase:** 0.9 | **Stability:** Preview
- **Depends on:** `FSharpened.Platform`
- **Wraps / composes:** TorchSharp, DiffSharp, Furnace, ONNX Runtime, ML.NET, ILGPU
- **Gaps addressed:** audit-2 F1
- **Skill:** [`.agents/skills/add-portability-contract.md`](../../../.agents/skills/add-portability-contract.md)
- **Consumer doc:** [`docs/packages/ML.Portability.md`](../../packages/ML.Portability.md)

## Purpose

Every ML framework declares RIDs, dtypes, ops, training/inference support and a stability tier; fallback to ONNX export or ILGPU; version pinning recorded in artifacts.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
