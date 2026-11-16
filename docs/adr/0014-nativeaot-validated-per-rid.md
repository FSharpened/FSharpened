# ADR-0014: NativeAOT validated per RID

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

NativeAOT output is RID-specific; one RID's success says nothing about another.

## Decision

CI publishes with PublishAot and smoke-tests per RID, plus an AOT-only lane for mobile non-preclusion.

## Consequences

Longer CI; mitigated by caching.
