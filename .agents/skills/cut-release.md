---
name: cut-release
description: Cut a release. Merging to main and publishing packages are human-gated.
---

# cut-release

Cut a release. Merging to main and publishing packages are human-gated.

## Steps

1. Confirm CI is green on all six RIDs and benchmarks are inside budget.
2. Update CHANGELOG; run `update-license-inventory` if dependencies changed.
3. Generate SBOM and SLSA provenance; sign artifacts with Sigstore.
4. Open a release PR; a human approves the tag.
5. `agent-release.yml` publishes only from a human-approved tag.
6. Record the release in `.agents/memory/decisions.log`.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
