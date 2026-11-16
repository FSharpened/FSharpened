# FSharpened.Diagnostics - Structured error and debug logging

- **Layer:** core
- **Priority:** P0 | **Phase:** 1.0 | **Stability:** Stable
- **Depends on:** `FSharpened.Platform`
- **Wraps / composes:** Microsoft.Extensions.Logging, FSharp.Logf
- **Gaps addressed:** opaque errors
- **Skill:** [`.agents/skills/add-diagnostic.md`](../../../.agents/skills/add-diagnostic.md)
- **Consumer doc:** [`docs/packages/Diagnostics.md`](../../packages/Diagnostics.md)

## Purpose

DiagnosticScope CE, Result-centric error pipeline, ILogger integration, Sentry/OTLP bridges. Every error has a typed shape.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
