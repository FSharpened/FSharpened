# Agent Authority (structured)

Human-readable source: `MAINTAINER.md`. This table is the machine-friendly form; keep them equal.

```yaml
human_gated:
  - merge_to_main
  - publish_package_or_model
  - public_security_advisory
  - delete_release_or_checkpoint
  - amend_invariant
roles:
  Orchestrator:   {writes: [routing, handoffs], merge: false, promote_model: false}
  Architect:      {writes: [adr, contracts, threat_model, budgets], merge: false, promote_model: false}
  PillarBuilder:  {writes: ["src/FSharpened.<one pillar>"], merge: false, promote_model: false}
  ToolingBuilder: {writes: [tools, pipelines, containers], merge: false, promote_model: false}
  Reviewer:       {writes: [review], merge: false, promote_model: false}
  QaRelease:      {writes: [test_matrix, aot, release_pipeline], merge: false, promote_model: canary_gate_only}
  Documentation:  {writes: [docs, agents_routing], merge: false, promote_model: false}
```
