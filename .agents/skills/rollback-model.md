---
name: rollback-model
description: Restore a known-good checkpoint.
---

# rollback-model

Restore a known-good checkpoint.

## Steps

1. Identify the failing statepoint and the last known-good one.
2. Roll back the routing table; replay compensating actions for remote state.
3. Preserve the failed candidate for forensics; analyse drift logs.
4. Record in `decisions.log`.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
