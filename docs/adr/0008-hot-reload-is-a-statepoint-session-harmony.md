# ADR-0008: Hot reload is a statepoint session, Harmony now, FCS-native later

- **Status:** Accepted (founding decision; amend only via a superseding ADR)
- **Date:** 2026-11-16
- **Deciders:** project owner, Architect persona

## Context

dotnet watch does not support F#; upstream FCS hot-reload API is still landing.

## Decision

dotnet fsh watch uses statepoints with Harmony patching (~100 ms target), transitioning to the FCS-native session when SDK integration ships, with an equivalence test.

## Consequences

Two code paths until upstream lands; removed within two releases after (upstream policy).
