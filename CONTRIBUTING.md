# Contributing

FSharpened is built and maintained by humans and agents together. Start with `README.md`, then `docs/README.md` (task-indexed map).

1. **Setup:** `docs/setup/environment.md`.
2. **Pick work:** `PLAN.md` / `TODO.md`; claim an item by editing it.
3. **Branch:** `feat/<pillar>-<short>`; one pillar per PR unless an ADR says otherwise.
4. **Order of work** (`.agents/skills/add-pillar.md`): ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
5. **Rules:** the 14 invariants in `MAINTAINER.md`. Upstream-first (`docs/governance/upstream-policy.md`). New dependency: update `docs/governance/licensing.md`.
6. **Check locally:** build, test, `dotnet fantomas --check .`, `python tools/scripts/check_docs.py`.
7. **PR:** explain what and why, link the ADR/issue, state which RIDs you could not verify. A Reviewer (human or persona) other than the author must approve. Merging is human-gated.
8. **Security issues:** do not open a public issue; see `SECURITY.md`.
