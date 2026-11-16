---
name: run-benchmarks
description: Reproduce the benchmark environment and compare to per-RID baselines.
---

# run-benchmarks

Reproduce the benchmark environment and compare to per-RID baselines.

## Steps

1. Run `pipelines/benchmarks/run.ps1` or `run.sh` (identical output).
2. Compare against `main` baseline for this RID; fail on more than 10% regression.
3. Compare against absolute budgets in `docs/performance/budgets.md`.
4. Regressions need an ADR that explicitly accepts them (Invariant 13).

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
