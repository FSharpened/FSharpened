# FSharpened.LanguageExtensions - Opt-in language-level gap fillers

- **Layer:** core
- **Priority:** P1 | **Phase:** 3.0 | **Stability:** Experimental
- **Depends on:** none (leaf substrate)
- **Wraps / composes:** System.Collections.Immutable, FSharpPlus
- **Gaps addressed:** FS-1094, Option, Async
- **Skill:** [`.agents/skills/add-language-extension.md`](../../../.agents/skills/add-language-extension.md)
- **Consumer doc:** [`docs/packages/LanguageExtensions.md`](../../packages/LanguageExtensions.md)

## Purpose

Collections.Immutable, Option (ValueOption-aware), Async/Task bridge, Pipeline. Library-level only: no compiler patches, no FSharp.Core changes, no HKTs.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
