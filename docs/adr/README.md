# Architecture Decision Records

ADRs are the project's institutional memory: they stop future agents from "correcting" intentional design. Format: MADR ([template](adr-template.md)). Read this index before any structural change.

## When to write one
Structural, deployment, dependency-edge, trust-boundary, performance-regression, invariant-related, or documentation-architecture decisions. Write the ADR **first** (`add-pillar` order).

## Lifecycle
Proposed -> Accepted (human-approved) -> Superseded by ADR-N. Never edit an accepted ADR's decision; supersede it.

## Index
| ADR | Title |
|---|---|
| 0001 | FSharpened is a satellite, not a fork |
| 0002 | Documentation architecture: routed progressive disclosure |
| 0003 | No component is platform-privileged |
| 0004 | Statepoints are the single checkpointing substrate |
| 0005 | Target net8.0, central package management, SDK 9+ |
| 0006 | Composition-first: wrap, do not rebuild |
| 0007 | ONNX-first inference, TorchSharp training behind a portability contract |
| 0008 | Hot reload is a statepoint session, Harmony now, FCS-native later |
| 0009 | Debugging wraps netcoredbg with an F# evaluator |
| 0010 | Semantic grounding via MCP is mandatory for agents |
| 0011 | Agents are permanent maintainers; the repo is their operating system |
| 0012 | Five-layer deterministic fallback for all model-backed features |
| 0013 | License: Apache-2.0 |
| 0014 | NativeAOT validated per RID |
| 0015 | Recursive training with frozen judges and recursion budgets |
| 0016 | One local-first CI pipeline, shared by hooks and GitHub Actions |
| 0017 | A Cloudflare Worker hub for CI telemetry and operations |
