# ADR-0009: Debugging wraps netcoredbg with an F# evaluator

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

No F#-aware expression evaluator exists; rewriting a debugger engine is unnecessary.

## Decision

DAP proxy over netcoredbg plus an in-process FCS evaluator via ICorDebug and PDB+IL snapshots for hot-reloaded assemblies.

## Consequences

Evaluator is the high-risk component; fallback is raw ToString().
