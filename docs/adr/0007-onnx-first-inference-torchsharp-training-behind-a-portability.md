# ADR-0007: ONNX-first inference, TorchSharp training behind a portability contract

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

TorchSharp is a CUDA/x64-shaped wrapper; DiffSharp and Furnace are in revision; ONNX Runtime has the broadest provider matrix.

## Decision

Inference defaults to ONNX Runtime execution providers resolved by ML.Hardware; training prefers TorchSharp where its contract allows, then oneDAL, then ONNX training export; ILGPU for custom kernels. Invariants 8 and 10.

## Consequences

No CUDA assumption. Backends need per-RID smoke and fallback tests.
