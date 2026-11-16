---
name: bootstrap
description: Build the repository environment all future agents inhabit, in strict order.
---

# bootstrap

Build the repository environment all future agents inhabit, in strict order.

## Steps

1. Create the directory tree, `global.json`, `.editorconfig`, `.gitattributes`.
2. Write root entrypoints: README, CONTRIBUTING, MAINTAINER; write `AGENTS.md` last so it only holds verified links.
3. Write `docs/README.md`, `docs/adr/README.md`, the ADR template, ADR-0001 and ADR-0002.
4. Create `.agents/` personas, memory and the first skills.
5. Build `FSharpened.Platform` and its three-OS test suite; make the six-RID matrix green before any product package.
6. Implement the first vertical slice (`FSharpened.Analyzers`: file-order + value-restriction) end to end, including one human-approved release.
7. Expand pillar by pillar with `add-pillar`.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
