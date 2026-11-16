---
name: update-docs-map
description: Keep `docs/README.md` the single routing contract.
---

# update-docs-map

Keep `docs/README.md` the single routing contract.

## Steps

1. List new or changed tasks and their canonical documents.
2. Edit `docs/README.md`: one canonical owner per fact; entrypoints may link, never copy.
3. Run `python tools/scripts/check_docs.py` (routing targets exist, no duplicated canonical content, every routed task has a skill).
4. Changes to the documentation architecture itself require an ADR.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
