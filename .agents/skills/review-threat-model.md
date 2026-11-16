---
name: review-threat-model
description: Architect checklist for ADRs touching a trust boundary.
---

# review-threat-model

Architect checklist for ADRs touching a trust boundary.

## Steps

1. Identify the boundary crossed (compiler bridge, statepoints, ML pipeline, agent OS, supply chain).
2. Walk STRIDE in `docs/security/threat-model.md` for the change.
3. Update mitigation status and residual risk.
4. Reviewer re-checks on every PR touching security-sensitive components.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
