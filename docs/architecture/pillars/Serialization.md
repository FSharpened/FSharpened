# FSharpened.Serialization - AOT-ready, source-generation-free serialization

- **Layer:** core
- **Priority:** P0 | **Phase:** 0.5 | **Stability:** Preview
- **Depends on:** `FSharpened.Analyzers`, `FSharpened.Effects`
- **Wraps / composes:** System.Text.Json, FSharp.SystemTextJson
- **Gaps addressed:** B, K, I
- **Skill:** [`.agents/skills/add-serializer.md`](../../../.agents/skills/add-serializer.md)
- **Consumer doc:** [`docs/packages/Serialization.md`](../../packages/Serialization.md)

## Purpose

System.Text.Json converter factory using analyzer-generated metadata instead of FSharp.Reflection; build-time AOT validation that fails the build with a suggested fix.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
