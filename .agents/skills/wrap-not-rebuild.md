---
name: wrap-not-rebuild
description: Default to composing existing tools; rebuilding needs an ADR (Invariant 11).
---

# wrap-not-rebuild

Default to composing existing tools; rebuilding needs an ADR (Invariant 11).

## Steps

1. Search for an existing .NET/F# tool covering the gap; record candidates.
2. Prefer wrapping; contribute upstream where the surface is missing.
3. Include an upstream-impact assessment in the PR.
4. Only if no tool can be extended, write an ADR proving it, then build.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
