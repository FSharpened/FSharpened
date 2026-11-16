# ADR-0006: Composition-first: wrap, do not rebuild

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

Many gaps already have tools (dep-scan, SOVA, Sigstore, Expecto, FsCheck, Stryker, FsLangMCP, DuckDB).

## Decision

Invariant 11. Wrapping is the default; building requires an ADR. Greenfield exceptions: TOML parser, model registry, experiment tracking, statepoints.

## Consequences

Dependency surface grows; mitigated by upstream policy tiers and license inventory.
