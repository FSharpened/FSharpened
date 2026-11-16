# ADR-0011: Agents are permanent maintainers; the repo is their operating system

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

The project will be built and maintained by agents indefinitely.

## Decision

.agents/ holds personas with hard boundaries, file-based memory, handoff queue and skills. Irreversible acts (merge to main, publish, public advisory, delete release) are human-gated.

## Consequences

Skill coverage and docs-drift checks are CI gates.
