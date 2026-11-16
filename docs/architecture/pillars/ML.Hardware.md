# FSharpened.ML.Hardware - Hardware abstraction and backend resolver

- **Layer:** ml
- **Priority:** P0 | **Phase:** 0.9 | **Stability:** Preview
- **Depends on:** `FSharpened.Platform`, `FSharpened.ML.Portability`, `FSharpened.Statepoints`
- **Wraps / composes:** ONNX Runtime EPs, TorchSharp, ILGPU, oneDAL
- **Gaps addressed:** hardware
- **Skill:** [`.agents/skills/add-hardware-backend.md`](../../../.agents/skills/add-hardware-backend.md)
- **Consumer doc:** [`docs/packages/ML.Hardware.md`](../../packages/ML.Hardware.md)

## Purpose

Backend registry (Training/Inference/Kernel), capability negotiation, per-task priority-chain resolver and deterministic fallback. CUDA, ROCm/HIP, DirectML, OpenVINO, CoreML, MPS, oneDNN/oneDAL, ILGPU, CPU.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
