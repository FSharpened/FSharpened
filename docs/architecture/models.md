# Internal Models and the Resilience Envelope

The unit of reliability is the **runtime envelope**, not the model. Models are small, narrow, and always wrapped by deterministic fallback. Package: `FSharpened.Models`. Skills: `train-model`, `promote-model`, `rollback-model`, `sign-model-checkpoint`.

## Tiers (grow horizontally to hundreds of models)
| Tier | Model | Base | Frozen judge |
|---|---|---|---|
| 1 | **CDM** Compiler Diagnostics: raw FCS diagnostic to `StructuredDiagnostic` + ranked fixes | 0.5B-1.5B SLM, QLoRA | the compiler (apply fix in sandbox, build) |
| 2 | **APM** Analyzer Prioritisation: rank/dedupe findings | ML.NET gradient-boosted trees | PR action history |
| 3 | **ECM** Effect Composition: suggest `effect { }` structure | 1B-3B code model | type checker |
| 4 | **SSM** Serialization Shape: predict AOT-safe shapes | small ONNX classifier over type graph | AOT analyzer + round-trip |
| 5 | **ATRM** Agent Task Routing: route to persona/skill/model | small classifier | handoff outcomes |

## Five-layer fallback (ADR-0012)
1. Specialised model. 2. Sibling model. 3. **Symbolic rules (pure F#, always available)**. 4. Warm checkpoint (staleness marked). 5. Structured escalation: deferred with a resume token, never "failed".
The failure classifier's output is a closed set: retry, fix, escalate, rollback.

## Recursive training (ADR-0015)
DAPT, DSFT, DPO, checkpointing on frozen versioned data. Self-improvement loop: generate on held-out set, frozen judge scores each step, keep only step-verified outputs, fine-tune a candidate, canary on 5% with automatic rollback. Guards: frozen judges, multi-metric evaluation, rollback to last known-good, per-model recursion budget requiring Architect/human review.

## Atomic hot swap
Promotion forks a statepoint, loads weights into the fork, runs the canary judge, then atomically switches the routing table. Failure discards the fork; the original statepoint is untouched.

## Bootstrap order
Symbolic fallback first, then statepoint runtime, then APM (ML.NET), then first CDM, then the recursive loop, then horizontal scaling.
