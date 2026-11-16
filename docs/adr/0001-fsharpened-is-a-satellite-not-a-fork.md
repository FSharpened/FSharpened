# ADR-0001: FSharpened is a satellite, not a fork

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

Forking F# would fragment the ecosystem, break NuGet compatibility and create an unsustainable maintenance burden.

## Decision

FSharpened consumes FCS, FSharp.Analyzers.SDK, the .NET SDK, NuGet and LSP as extension surfaces. No compiler patches, no FSharp.Core changes, no HKTs.

## Consequences

Everything installs today on a stock SDK. Upstream-first is mandatory. Some gaps are only partially closable without compiler changes.
