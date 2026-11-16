---
name: update-license-inventory
description: Add or change a dependency (Invariant 14).
---

# update-license-inventory

Add or change a dependency (Invariant 14).

## Steps

1. Record license, compatibility with Apache-2.0, and obligations in `docs/governance/licensing.md`.
2. Do an upstream-policy assessment: tier A/B/C and linked upstream issues for any workaround.
3. Confirm the SBOM emits the license.
4. docs-drift fails if a dependency lacks an inventory entry.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
