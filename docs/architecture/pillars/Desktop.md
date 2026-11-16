# FSharpened.Desktop - Functional-first desktop / mobile UI

- **Layer:** core
- **Priority:** P2 | **Phase:** 3.5 | **Stability:** Experimental
- **Depends on:** `FSharpened.Effects`, `FSharpened.Telemetry`
- **Wraps / composes:** Avalonia, Fabulous.Avalonia
- **Gaps addressed:** E, CC
- **Skill:** [`.agents/skills/add-desktop-template.md`](../../../.agents/skills/add-desktop-template.md)
- **Consumer doc:** [`docs/packages/Desktop.md`](../../packages/Desktop.md)

## Purpose

MVU wrapper over Avalonia and Fabulous.Avalonia, `dotnet fsh new desktop` template, AOT lane for mobile.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
