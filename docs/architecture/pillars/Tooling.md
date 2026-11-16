# FSharpened.Tooling - Editor integrations and headless LSP harness

- **Layer:** core
- **Priority:** P1 | **Phase:** 1.2 | **Stability:** Preview
- **Depends on:** `FSharpened.CompilerBridge`, `FSharpened.Statepoints`, `FSharpened.Platform`
- **Wraps / composes:** Ionide, FsAutoComplete, SharpLsp
- **Gaps addressed:** F, N
- **Skill:** [`.agents/skills/add-tooling-feature.md`](../../../.agents/skills/add-tooling-feature.md)
- **Consumer doc:** [`docs/packages/Tooling.md`](../../packages/Tooling.md)

## Purpose

VS Code companion, VSIX, headless language-server harness, SharpLsp gap closure (upstream-first), statepoint-backed hot-reload sessions.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
