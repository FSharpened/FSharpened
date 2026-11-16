---
name: add-pillar
description: Add a pillar in the non-negotiable order that prevents architectural drift.
---

# add-pillar

Add a pillar in the non-negotiable order that prevents architectural drift.

## Steps

1. Add the entry to `registry/pillars.json` (id, layer, depends, wraps, skill, stability).
2. Write the ADR first (status Proposed).
3. Run `python tools/scripts/scaffold.py` to create the project, docs and skill stubs.
4. Write the canonical doc `docs/packages/<Id>.md`, including a 'what it does not do' section.
5. Write contract tests before implementation.
6. Implement; wire into the six-RID CI matrix and the AOT lane.
7. Run `update-docs-map`; flip the ADR to Accepted via human approval.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
