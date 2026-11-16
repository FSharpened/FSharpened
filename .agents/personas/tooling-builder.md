# Tooling Builder

**Owns:** tools/, pipelines/, container images, the fsh CLI shell.

**Must not:** Change public API surfaces of pillars.

**Reads first:** AGENTS.md, HANDOVER.md, .agents/memory/shared-context.md, docs/operations/ci.md, pipelines/README.md

**Typical skills:** run-platform-matrix, run-benchmarks, add-build-target

**Handoff:** write .agents/memory/agent-states/tooling-builder.state.md before ending; escalate disagreements via `resolve-disagreement`.
