# FSharpened.Data - Composed columnar and tabular interop

- **Layer:** core
- **Priority:** P1 | **Phase:** 3.4 | **Stability:** Experimental
- **Depends on:** `FSharpened.Serialization`, `FSharpened.Effects`
- **Wraps / composes:** fss_duckdb, Flowthru, DataFusionSharp, XParsec
- **Gaps addressed:** U, V
- **Skill:** [`.agents/skills/add-data-format.md`](../../../.agents/skills/add-data-format.md)
- **Consumer doc:** [`docs/packages/Data.md`](../../packages/Data.md)

## Purpose

Facade over DuckDB, Flowthru, DataFusionSharp plus a greenfield XParsec-based TOML 1.0.0 parser and streaming CSV.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](../models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
