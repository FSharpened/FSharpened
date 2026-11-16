---
name: handle-security-report
description: Handle a private vulnerability report.
---

# handle-security-report

Handle a private vulnerability report.

## Steps

1. Do not discuss publicly; acknowledge privately.
2. Reproduce in a sandbox; classify with `docs/security/threat-model.md`.
3. Prepare a fix on a private branch; human approves any public advisory.
4. Public advisories and package yanks are human-gated acts.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
