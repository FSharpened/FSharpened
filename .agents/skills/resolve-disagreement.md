---
name: resolve-disagreement
description: Three-stage protocol when agents disagree.
---

# resolve-disagreement

Three-stage protocol when agents disagree.

## Steps

1. Stage 1 invariant check: if the dispute is whether a change violates an invariant, the Reviewer's judgement is final and the change is blocked.
2. Stage 2 ADR escalation: for architectural disputes the Architect drafts an ADR with both positions; the Orchestrator routes it to a human (SLA 5 business days).
3. Stage 3 fork-and-evaluate: otherwise fork the statepoint, implement both, run the frozen judge, merge the better result.
4. Record the outcome in `.agents/memory/decisions.log`.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
