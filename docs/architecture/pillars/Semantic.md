# FSharpened.Semantic - Semantic grounding for agents (MCP + library)

- **Layer:** substrate
- **Priority:** P1 | **Phase:** 0.8 | **Stability:** Preview
- **Depends on:** `FSharpened.Platform`, `FSharpened.CompilerBridge`
- **Wraps / composes:** FsLangMCP, SageFs, FsAutoComplete
- **Gaps addressed:** Cross-cutting 3
- **Skill:** [`.agents/skills/use-semantic-tools.md`](../../../.agents/skills/use-semantic-tools.md)
- **Consumer doc:** [`docs/packages/Semantic.md`](../../packages/Semantic.md)

## Purpose

find / check / types / rename / dead-code plus model-aware queries, exposed as a library and a stdio MCP server. Agents must use it for symbol resolution.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
