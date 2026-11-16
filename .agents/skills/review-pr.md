---
name: review-pr
description: Independent review of a diff; the Reviewer never merges its own work.
---

# review-pr

Independent review of a diff; the Reviewer never merges its own work.

## Steps

1. Check all 14 invariants in `MAINTAINER.md` against the diff.
2. Check docs/ADR/skill/test updates are present for the change type.
3. Check no `#if` on platform, no own checkpointing, no hard-coded backend, no reimplemented tool.
4. Check new dependencies have a license entry and an upstream-policy assessment.
5. Block on any invariant violation; the Reviewer's invariant judgement is final.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
