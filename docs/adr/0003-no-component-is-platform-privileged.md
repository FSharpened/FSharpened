# ADR-0003: No component is platform-privileged

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

Linux-first, Windows-first or any hierarchy creates silent second-class paths that agents break.

## Decision

Invariant 1 and a tier policy: Tier 1 Windows/Linux/macOS (six RIDs), Tier 2 WSL/containers/Windows Server, Tier 3 Android/iOS (non-preclusion). All platform behaviour lives in FSharpened.Platform behind a capability manifest.

## Consequences

Six-RID CI with per-RID AOT. Containers are a Linux distribution channel, not a hierarchy.
