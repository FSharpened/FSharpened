# FSharpened.Platform - Cross-platform abstraction

- **Layer:** substrate
- **Priority:** P0 | **Phase:** 0.0 | **Stability:** Stable
- **Depends on:** none (leaf substrate)
- **Wraps / composes:** nothing; this is original FSharpened code
- **Gaps addressed:** Invariant 1
- **Skill:** [`.agents/skills/add-platform-abstraction.md`](../../../.agents/skills/add-platform-abstraction.md)
- **Consumer doc:** [`docs/packages/Platform.md`](../../packages/Platform.md)

## Purpose

Owns every OS-specific behaviour behind interfaces and a capability manifest: file watching, IPC transport, path identity, overlay store, sandboxing, terminal, runtime identity. Nothing else may #if on a platform.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
