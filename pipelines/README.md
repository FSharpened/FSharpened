# Pipelines

CI/CD definitions as agent feedback loops. Strong tests and fast feedback are the precondition for autonomous change. Every step is a named command.

- `github-actions/` reference workflows not yet enabled (agent review/triage/release, benchmarks); live ones are in `.github/workflows/`
- local pipeline: `tools/ci/fsh_ci.py` (see `docs/operations/local-ci.md`)
- `containers/` multi-arch Linux image (a distribution channel, not a platform hierarchy)
- `otel/` OpenTelemetry collector config for compiler, pipeline, analyzer and model telemetry
- `benchmarks/` BenchmarkDotNet harness, per-RID baselines, budget enforcement

Details: `docs/operations/ci.md`.
