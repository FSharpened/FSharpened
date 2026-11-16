# FSharpened.CompilerBridge - FCS wrapper, warm compiler, per-file cache

- **Layer:** core
- **Priority:** P0 | **Phase:** 0.0 | **Stability:** Stable
- **Depends on:** `FSharpened.Platform`
- **Wraps / composes:** FSharp.Compiler.Service
- **Gaps addressed:** Y, Z, J
- **Skill:** [`.agents/skills/add-compiler-bridge-feature.md`](../../../.agents/skills/add-compiler-bridge-feature.md)
- **Consumer doc:** [`docs/packages/CompilerBridge.md`](../../packages/CompilerBridge.md)

## Purpose

Warm FSharpChecker host, structured diagnostic projection, diagnostic enrichment, onboarding diagnostics, content-addressed per-file typecheck cache exposed as an MSBuild task wrapping CoreCompile.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
