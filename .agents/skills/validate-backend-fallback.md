---
name: validate-backend-fallback
description: Every backend addition proves it can fail safely.
---

# validate-backend-fallback

Every backend addition proves it can fail safely.

## Steps

1. Add a test that deliberately fails the backend at init and at run time.
2. Assert the resolver falls through to the next backend without losing state.
3. Assert a structured diagnostic and telemetry span are emitted.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
