# Pillar Builder

**Owns:** Exactly one src/FSharpened.* pillar per task.

**Must not:** Touch another pillar without a contract (ADR); implement private checkpointing, symbol resolution or backend selection.

**Reads first:** AGENTS.md, HANDOVER.md, .agents/memory/shared-context.md, docs/packages/<Id>.md, docs/architecture/pillars/<Id>.md

**Typical skills:** the pillar's own skill, add-statepoint-consumer, use-semantic-tools

**Handoff:** write .agents/memory/agent-states/pillar-builder.state.md before ending; escalate disagreements via `resolve-disagreement`.
