# MAINTAINER.md - Delegated Authority and Invariants

## Human-gated acts (agents may not perform)
- Merge to `main`.
- Publish a NuGet package or model artifact to a public registry.
- Post a public security advisory.
- Delete a release or a model checkpoint from the registry.
- Amend a constitutional invariant (needs a human-signed ADR).

Reading is cheap and safe; writing to a public surface is the irreversible act.

## Constitutional Invariants (agents may not weaken)
1. **No platform is privileged.** Every component is platform-neutral or first-class on every Tier 1 RID; platform behaviour lives only in `FSharpened.Platform`.
2. **Every maintainable action is a numbered procedure in `.agents/skills/`.** No skill, not maintainable.
3. **No model is promoted without a frozen judge, a canary statepoint and automatic rollback.**
4. **No pillar may degrade another pillar's platform support.**
5. **No component may implement its own checkpointing, symbol resolution or model artifact loading.** Use `Statepoints`, `Semantic`, `Models`.
6. **No model artifact may be promoted without a signed model card, a frozen-judge report and a canary statepoint.**
7. **No ML component may bypass the statepoint runtime, Telemetry or Security.**
8. **No ML component may hard-code a backend.** Route through `ML.Hardware.Resolver`.
9. **No backend is promoted without a per-RID initialisation smoke test and a deterministic fallback test.**
10. **No ML component may depend directly on TorchSharp, DiffSharp or Furnace** without going through `ML.Portability`.
11. **No security or testing component may reimplement what an existing .NET/F# tool provides.** Composition is the default.
12. **No agent may proceed past a disagreement without invoking the conflict-resolution protocol.**
13. **No component may regress beyond its performance budget** without an ADR accepting the regression.
14. **No dependency may be added without a license entry and an upstream-policy assessment.**

Machine-readable copy: `registry/pillars.json` (`invariants`). They must stay identical; `check_docs.py` enforces it.

## Delegation Matrix
| Role | Write Authority | Merge Authority | Model Promotion Authority |
|---|---|---|---|
| Orchestrator | Routing, handoffs | No | No |
| Architect | ADRs, contracts | No | No |
| Pillar Builder | One `src/FSharpened.*` pillar | No | No |
| Tooling Builder | `tools/`, pipelines | No | No |
| Reviewer | Diff review, invariant checks | No (recommend only) | No |
| QA / Release | Test matrix, AOT validation | No (human-gated) | Canary gate only |
| Documentation | `docs/`, `AGENTS.md` routing | No | No |

Persona detail: `.agents/personas/`. Structured form: `docs/governance/agent-authority.md`.

## Upstream independence
The repository must remain fully operable from its own contents plus the public .NET SDK and NuGet. No core feature may depend on a FSharpened-hosted service.
