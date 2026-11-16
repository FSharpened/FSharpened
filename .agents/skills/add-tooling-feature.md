---
name: add-tooling-feature
description: Procedure for changing the Editor integrations and headless LSP harness pillar (FSharpened.Tooling).
---

# add-tooling-feature

Procedure for changing the Editor integrations and headless LSP harness pillar (FSharpened.Tooling).

## Steps

1. Read `docs/packages/Tooling.md` and `docs/architecture/pillars/Tooling.md`; read the ADR index for related decisions.
2. Write or update the ADR first if the change is structural, adds a dependency edge, or touches a trust boundary (run `review-threat-model` if so).
3. Update the canonical doc second; add or change the contract tests third.
4. Implement in `src/FSharpened.Tooling/`. Use `FSharpened.Semantic` tools for symbol resolution, never text search.
5. Declare the failure behaviour (no-op, fail closed, fallback layer, or escalate) and add a test that forces it.
6. Run `run-platform-matrix`; no `#if` on a platform may appear in product code.
7. Run `run-benchmarks` if the change is on a hot path; stay inside `docs/performance/budgets.md`.
8. Run `update-docs-map` and `python tools/scripts/check_docs.py`.
9. Open the PR; request the Reviewer persona. Do not merge your own work.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
