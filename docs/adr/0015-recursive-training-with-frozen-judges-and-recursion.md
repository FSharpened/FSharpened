# ADR-0015: Recursive training with frozen judges and recursion budgets

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

Recursive self-improvement fails through drift and mode collapse.

## Decision

Frozen judges, step-level verification, multi-metric evaluation, canary with automatic rollback, per-model recursion budget needing human/Architect review.

## Consequences

Models grow horizontally without architecture change.
