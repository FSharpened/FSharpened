---
name: validate-hot-reload-fallback
description: Hot-reload paths must be equivalent.
---

# validate-hot-reload-fallback

Hot-reload paths must be equivalent.

## Steps

1. Run the Harmony path and the FCS-native path (when available) on the same edit.
2. Assert identical statepoint transitions.
3. A failed reload must roll back to the prior statepoint with no residual state.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
