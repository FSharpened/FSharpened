# Pipelines

CI/CD definitions as agent feedback loops. Strong tests and fast feedback are the precondition for autonomous change. Every step is a named command.

- `github-actions/` workflows (copy to `.github/workflows/` once hosted)
- `containers/` multi-arch Linux image (a distribution channel, not a platform hierarchy)
- `otel/` OpenTelemetry collector config for compiler, pipeline, analyzer and model telemetry
- `benchmarks/` BenchmarkDotNet harness, per-RID baselines, budget enforcement

Details: `docs/operations/ci.md`.
