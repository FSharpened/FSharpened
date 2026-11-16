# Benchmarks

BenchmarkDotNet harness (not yet implemented). Required coverage: type-check throughput (with/without per-file cache); statepoint snapshot/fork/rollback; hot-reload round trip; analyzer time per rule; ONNX inference per execution provider; training-step throughput per backend. Results are emitted as OpenTelemetry metrics, compared with per-RID baselines from `main`, and judged against `docs/performance/budgets.md`. Skill: `.agents/skills/run-benchmarks.md`. `run.sh` and `run.ps1` must produce identical output.
