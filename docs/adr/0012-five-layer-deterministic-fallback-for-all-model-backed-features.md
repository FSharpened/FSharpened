# ADR-0012: Five-layer deterministic fallback for all model-backed features

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

Models fail, drift or crash; the task must still complete.

## Decision

Specialised model, sibling model, symbolic rules (pure F#), warm checkpoint, structured escalation with resume token. Failure classifier output is a closed set: retry, fix, escalate, rollback.

## Consequences

Symbolic layer ships first and is the guaranteed path on any platform.
