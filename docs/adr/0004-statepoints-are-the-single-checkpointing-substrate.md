# ADR-0004: Statepoints are the single checkpointing substrate

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

Hot reload, debugging, model canaries, experiments and chaos tests all need snapshot/fork/rollback.

## Decision

A graph-based Statepoints pillar over an in-memory content-addressed overlay (filesystem CoW is an optimisation only). Invariant 5 forbids private checkpointing.

## Consequences

One consistency model across features; millisecond-scale fork depends on overlay design, published per platform.
