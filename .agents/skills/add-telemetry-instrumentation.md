---
name: add-telemetry-instrumentation
description: Procedure for changing the OpenTelemetry-native observability pillar (FSharpened.Telemetry).
---

# add-telemetry-instrumentation

Procedure for changing the OpenTelemetry-native observability pillar (FSharpened.Telemetry).

## Steps

1. Read `docs/packages/Telemetry.md` and `docs/architecture/pillars/Telemetry.md`; read the ADR index for related decisions.
2. Write or update the ADR first if the change is structural, adds a dependency edge, or touches a trust boundary (run `review-threat-model` if so).
3. Update the canonical doc second; add or change the contract tests third.
4. Implement in `src/FSharpened.Telemetry/`. Use `FSharpened.Semantic` tools for symbol resolution, never text search.
5. Declare the failure behaviour (no-op, fail closed, fallback layer, or escalate) and add a test that forces it.
6. Run `run-platform-matrix`; no `#if` on a platform may appear in product code.
7. Run `run-benchmarks` if the change is on a hot path; stay inside `docs/performance/budgets.md`.
8. Run `update-docs-map` and `python tools/scripts/check_docs.py`.
9. Open the PR; request the Reviewer persona. Do not merge your own work.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
