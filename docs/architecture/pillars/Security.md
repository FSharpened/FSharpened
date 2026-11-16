# FSharpened.Security - Supply-chain and artifact governance

- **Layer:** core
- **Priority:** P1 | **Phase:** 2.6 | **Stability:** Preview
- **Depends on:** `FSharpened.Build`
- **Wraps / composes:** OWASP dep-scan, cdxgen, SOVA, ShieldedStack, Sigstore
- **Gaps addressed:** P, Q
- **Skill:** [`.agents/skills/add-security-check.md`](../../../.agents/skills/add-security-check.md)
- **Consumer doc:** [`docs/packages/Security.md`](../../packages/Security.md)

## Purpose

Composes SBOM (dep-scan, SOVA, ShieldedStack), SLSA/Sigstore, lock-file validation (NuGet, CPM and Paket); extends signing and SBOM to model artifacts. Continuous, not release-gate.

## Boundaries

- Consumes only the pillars listed under *Depends on*. A new dependency edge needs an ADR (see [boundaries](../boundaries.md)).
- Upstream-first: any fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before a downstream workaround is accepted ([upstream policy](../../governance/upstream-policy.md)).

## Fallback discipline

Failure behaviour is declared per pillar and tested. Default rule set: refactoring fails to a no-op; security checks fail closed; debugger evaluation fails to raw `ToString()`; architecture-rule engine errors escalate to the Orchestrator with a structured diagnostic; model-backed features fall through the five-layer stack ([models](models.md)).

## Status

Skeleton. The project compiles and has a marker module. Real work follows `add-pillar` order: ADR, canonical doc, contract tests, implementation, platform matrix, docs map.
