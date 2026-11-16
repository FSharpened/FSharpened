# Conflict Resolution

Executed by `.agents/skills/resolve-disagreement.md`. Required by Invariant 12.

**Stage 1 - Invariant check.** If the disagreement is whether a change violates a constitutional invariant, the Reviewer's judgement is final and the change is blocked. No appeal. `MAINTAINER.md` is the sole authority.

**Stage 2 - ADR escalation.** If the disagreement is architectural (new pillar or enhancement?), the Architect drafts an ADR with both positions, trade-offs and a recommendation. The Orchestrator routes it to a human for a decision within **5 business days**. The ADR is merged with the decision and becomes the reference for future agents.

**Stage 3 - Fork-and-evaluate.** If the disagreement is about implementation approach and neither position violates an invariant, the Orchestrator forks the statepoint. Both agents implement in parallel. QA runs the frozen judge on both. The better result is merged; the other is rolled back. Document it as an experiment with a recorded outcome, not as a conflict.

Forking without documenting the disagreement is a violation. Record every outcome in `.agents/memory/decisions.log`.
