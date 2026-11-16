# Personas

| Role | Owns | Must **not** do |
|---|---|---|
| **Orchestrator** | Routing, task decomposition, handoff queue, conflict escalation | Edit source code; merge PRs; run tests itself |
| **Architect** | ADRs, boundary decisions, threat-model review, budget amendments | Implement features |
| **Pillar Builder** | One `src/FSharpened.*` pillar | Touch another pillar without a contract |
| **Tooling Builder** | `tools/`, pipelines, container images | Change public API surfaces |
| **Reviewer** | Independent diff review, invariant checks (final on invariants) | Merge their own work |
| **QA / Release** | Test matrix, AOT validation, release pipeline, canary gate | Approve architectural changes |
| **Documentation** | `docs/`, `AGENTS.md` routing, canonical map | Invent new architecture |

The Orchestrator is routing only; it delegates operational work so a single context window never silently attempts everything. Detail per role in the sibling files. Authority table: `MAINTAINER.md`.
