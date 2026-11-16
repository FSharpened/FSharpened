# FSharpened.Testing - Composed testing stack

- **Layer:** core
- **Priority:** P1 | **Phase:** 2.6 | **Stability:** Preview
- **Depends on:** `FSharpened.Statepoints`, `FSharpened.CompilerBridge`
- **Wraps / composes:** Expecto, FsCheck, Hedgehog, Hegel, AltCover, Stryker.NET
- **Gaps addressed:** R, S, T, DD
- **Skill:** [`.agents/skills/add-test-backend.md`](../../../.agents/skills/add-test-backend.md)
- **Consumer doc:** [`docs/packages/Testing.md`](../../packages/Testing.md)

## Purpose

Unified property facade, AST-native mutation layer where Stryker fails, contract tests from DUs, MVU UI harness, chaos harness with statepoint recovery verification, coverage wrapping.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
