# CI

The pipeline is **one program**, `tools/ci/fsh_ci.py`, used identically locally, in git hooks and in GitHub Actions ([local-ci.md](local-ci.md)). Workflows only install toolchains and call it.

| Workflow | Purpose |
|---|---|
| `ci.yml` (live) | six-RID matrix running `fsh_ci.py run --profile full --ci --require-aot` (win-arm64 is `experimental`) |
| `docs-drift.yml` (live) | `check_docs.py`: routing targets exist, skills cover tasks, registry matches projects and invariants |
| `agent-review.yml` | independent reviewer checks invariants; cannot merge |
| `agent-triage.yml` | triage per `.github/repo-policy.md` |
| `agent-release.yml` | runs only from a human-approved tag |
| `benchmarks.yml` | per-RID benchmarks versus baseline and budgets |

Quality-gate hierarchy: tests are the primary defence; automated and adversarial review the secondary; CI is the enforcement surface. An agent may not bypass a red gate. `ci.yml` and `docs-drift.yml` are live in `.github/workflows/`; agent review/triage/release and benchmarks stay in `pipelines/github-actions/` until their runner integrations exist.

Debugging a failure: reproduce the exact command locally; check the RID; if only one RID fails, suspect `FSharpened.Platform` or a native provider, not product logic.
