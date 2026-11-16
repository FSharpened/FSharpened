# FSharpened.Refactoring - Type-directed refactorings (MCP first, LSP second)

- **Layer:** core
- **Priority:** P1 | **Phase:** 3.3 | **Stability:** Preview
- **Depends on:** `FSharpened.Semantic`, `FSharpened.CompilerBridge`
- **Wraps / composes:** FsAutoComplete
- **Gaps addressed:** O
- **Skill:** [`.agents/skills/add-refactoring-rule.md`](../../../.agents/skills/add-refactoring-rule.md)
- **Consumer doc:** [`docs/packages/Refactoring.md`](../../packages/Refactoring.md)

## Purpose

rename, extract, inline, move-type, Async to Task, Option to ValueOption; MCP and LSP code actions with identical semantics and test suites.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
