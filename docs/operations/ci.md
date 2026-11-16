# CI

Workflows in `pipelines/github-actions/`; every step is a named `dotnet`/`python` command, never an inferred shell incantation.

| Workflow | Purpose |
|---|---|
| `ci.yml` | six-RID build, test, Fantomas, analyzer gate, per-RID AOT smoke |
| `docs-drift.yml` | `check_docs.py`: routing targets exist, skills cover tasks, registry matches projects and invariants |
| `agent-review.yml` | independent reviewer checks invariants; cannot merge |
| `agent-triage.yml` | triage per `.github/repo-policy.md` |
| `agent-release.yml` | runs only from a human-approved tag |
| `benchmarks.yml` | per-RID benchmarks versus baseline and budgets |

Quality-gate hierarchy: tests are the primary defence; automated and adversarial review the secondary; CI is the enforcement surface. An agent may not bypass a red gate. Only `docs-drift.yml` is live in `.github/workflows/`. The rest stay in `pipelines/github-actions/` until the first successful local build (HANDOVER P0.7); then copy them over.

Debugging a failure: reproduce the exact command locally; check the RID; if only one RID fails, suspect `FSharpened.Platform` or a native provider, not product logic.
