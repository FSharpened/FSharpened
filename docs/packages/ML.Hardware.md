# FSharpened.ML.Hardware

Backend registry (Training/Inference/Kernel), capability negotiation, per-task priority-chain resolver and deterministic fallback. CUDA, ROCm/HIP, DirectML, OpenVINO, CoreML, MPS, oneDNN/oneDAL, ILGPU, CPU.

| | |
|---|---|
| NuGet id | `FSharpened.ML.Hardware` |
| Stability tier | Preview |
| Depends on | `FSharpened.Platform`, `FSharpened.ML.Portability`, `FSharpened.Statepoints` |
| Priority / phase | P0 / 0.9 |
| Source | `src/FSharpened.ML.Hardware/` |

## Public API

_Skeleton: API surface is not yet defined._ When it is, list every public module and type here. The docs-drift check compares this section to the compiled assembly (see `docs/operations/ci.md`).

## Usage

```fsharp
// Example appears here once the pillar is Active.
```

## What it does not do

- It is not a fork, a compiler patch, or a change to `FSharp.Core`.
- It does not reimplement an existing tool it can wrap (Invariant 11).
- It does not contain `#if` on a platform (Invariant 1); platform behaviour lives in `FSharpened.Platform`.

> FSharpened is an independent community project. It is not affiliated with, endorsed by, or a replacement for Microsoft's F# compiler or the F# Software Foundation.
