---
name: sign-model-checkpoint
description: Sigstore-sign weights, judge configuration and training-data hash.
---

# sign-model-checkpoint

Sigstore-sign weights, judge configuration and training-data hash.

## Steps

1. Compute hashes of weights, judge config and training data.
2. Sign with keyless Sigstore; attach the bundle.
3. Update the registry manifest; the runtime refuses mismatches in production mode.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
