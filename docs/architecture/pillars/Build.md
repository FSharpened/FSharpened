# FSharpened.Build - Build, CI primitives and compiler cache integration

- **Layer:** core
- **Priority:** P1 | **Phase:** 1.0 | **Stability:** Preview
- **Depends on:** `FSharpened.CompilerBridge`, `FSharpened.Platform`
- **Wraps / composes:** FAKE, Fun.Build, MSBuild.CompilerCache
- **Gaps addressed:** Y, Z
- **Skill:** [`.agents/skills/add-build-target.md`](../../../.agents/skills/add-build-target.md)
- **Consumer doc:** [`docs/packages/Build.md`](../../packages/Build.md)

## Purpose

FAKE / Fun.Build targets, MSBuild cache task integration, `dotnet fsh build --cached`, reference pipelines.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
