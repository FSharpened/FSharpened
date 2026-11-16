# Performance Budgets

Budgets are a **ratchet**, not a drift detector: a component may not creep 9% per release. Enforced by `pipelines/benchmarks/`; amended only by ADR (Invariant 13). Relative gate: more than **10%** regression versus `main` for the same RID fails CI.

| Component | Budget | Measured | Notes |
|---|---|---|---|
| Statepoint snapshot | < 15 ms | unmeasured | in-memory overlay |
| Statepoint fork | target < 15 ms | unmeasured | per-platform numbers in `docs/platform/performance.md` |
| Hot reload round trip | < 500 ms (target ~100 ms) | unmeasured | Harmony path, `linux-x64` and `win-x64` first |
| Type-check throughput | no worse than baseline per RID | unmeasured | with and without per-file cache |
| Analyzer execution | per-rule baseline | unmeasured | |
| ONNX inference latency | within 20% of raw ONNX Runtime, per provider | unmeasured | |
| Training-step throughput | within 15% of raw TorchSharp, per backend | unmeasured | |

Baseline reference points from upstream (informational): F# parallel type-checking for signature-backed files improved Fantomas 17.49 s to 14.28 s and the F# codebase 112 s to 92 s.
