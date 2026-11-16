# Glossary

- **Substrate** - a component every pillar consumes (Platform, Statepoints, Semantic, CompilerBridge, Diagnostics, Telemetry, Security, ML.Hardware).
- **Pillar** - a product package built on substrates.
- **Statepoint** - a consistent, restorable point-in-time version of environment state; nodes in a graph supporting snapshot, fork, rollback, merge.
- **Overlay store** - in-memory, content-addressed writable layer behind statepoints.
- **RID** - .NET runtime identifier (`linux-x64`, ...).
- **Tier 1/2/3** - platform support tiers.
- **Capability manifest** - startup-published record of platform and hardware capabilities.
- **Frozen judge** - verifier never trained in the same loop as the generator (compiler, PR history, symbolic checker, held-out suite).
- **Canary** - forked statepoint serving a small traffic slice with automatic rollback.
- **CDM / APM / ECM / SSM / ATRM** - the five internal model tiers.
- **Fallback stack** - five deterministic layers ending in structured escalation.
- **Skill** - a numbered repeatable procedure in `.agents/skills/`.
- **ADR** - architecture decision record.
- **FCS** - FSharp.Compiler.Service.
- **EP** - ONNX Runtime execution provider.
- **Satellite** - consumes upstream surfaces; not a fork.
- **Wrap-not-rebuild** - compose existing tools by default (Invariant 11).
- **Orchestrator / Architect / Pillar Builder / Tooling Builder / Reviewer / QA-Release / Documentation** - agent personas.
