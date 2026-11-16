# ADR-0002: Documentation architecture: routed progressive disclosure

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

Duplicated handbooks drift and measurably hurt agent performance.

## Decision

Thin entrypoints (AGENTS.md 20-30 lines, README, CONTRIBUTING, MAINTAINER) route to one canonical document per fact via docs/README.md. Procedures live only in skills.

## Consequences

docs-drift CI enforces routing. Changes to the doc architecture need an ADR.
