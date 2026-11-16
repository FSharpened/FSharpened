---
name: promote-model
description: Promote a checkpoint (Invariants 3 and 6).
---

# promote-model

Promote a checkpoint (Invariants 3 and 6).

## Steps

1. Fork a statepoint and load the candidate into the fork.
2. Run the frozen judge and canary on a 5% slice with automatic rollback thresholds.
3. Validate export under PublishAot on all six RIDs.
4. Sign the checkpoint (`sign-model-checkpoint`); update the registry manifest.
5. Atomically switch the routing table; write an ADR with data version, judge config and canary metrics.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
