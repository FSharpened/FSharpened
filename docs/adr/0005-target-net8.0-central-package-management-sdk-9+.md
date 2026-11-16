# ADR-0005: Target net8.0, central package management, SDK 9+

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

Need broad reach and reproducibility without ahead-of-time uncertainty.

## Decision

All projects target net8.0 initially; Directory.Packages.props pins versions; global.json pins the SDK (slnx requires 9.0.200+). netstandard2.0 for Platform/analyzers is deferred to a follow-up ADR when the analyzer SDK requires it.

## Consequences

Skeleton has not been compiled in the scaffolding environment; first CI run is the first real build.
