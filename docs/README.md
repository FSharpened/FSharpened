# FSharpened Engineering Documentation Map

This is a **routing contract**, not a table of contents: given a task, which document is canonical and which skill executes it. One fact has one canonical owner. Entrypoints (`README.md`, `AGENTS.md`, `CONTRIBUTING.md`) may link, never copy. Changes to this architecture require an ADR (ADR-0002).

## I need to...
| Task | Read first | Then |
|---|---|---|
| Set up a dev environment (Windows/Linux/macOS/WSL) | docs/setup/environment.md | docs/platform/README.md |
| Build, test, run one test | docs/setup/build.md | docs/setup/test.md |
| Understand the architecture | docs/architecture/overview.md | docs/architecture/boundaries.md |
| Read the full specification | docs/spec/SPEC.md | registry/pillars.json |
| Understand why we chose X over Y | docs/adr/README.md | the specific ADR |
| Add a pillar | docs/architecture/overview.md | .agents/skills/add-pillar.md |
| Add an analyzer rule | docs/packages/Analyzers.md | .agents/skills/add-analyzer-rule.md |
| Add platform-specific behaviour | docs/platform/README.md | .agents/skills/add-platform-abstraction.md |
| Add a statepoint consumer | docs/packages/Statepoints.md | .agents/skills/add-statepoint-consumer.md |
| Resolve symbols as an agent | docs/packages/Semantic.md | .agents/skills/use-semantic-tools.md |
| Add an ML backend | docs/packages/ML.Hardware.md | .agents/skills/add-hardware-backend.md |
| Add an ML framework dependency | docs/packages/ML.Portability.md | .agents/skills/add-portability-contract.md |
| Train / promote / roll back / sign a model | docs/architecture/models.md | .agents/skills/train-model.md |
| Wrap an existing tool instead of building | docs/governance/upstream-policy.md | .agents/skills/wrap-not-rebuild.md |
| Add a dependency | docs/governance/licensing.md | .agents/skills/update-license-inventory.md |
| Resolve an agent disagreement | docs/governance/conflict-resolution.md | .agents/skills/resolve-disagreement.md |
| Review a threat-model-relevant change | docs/security/threat-model.md | .agents/skills/review-threat-model.md |
| Run benchmarks / check budgets | docs/performance/budgets.md | .agents/skills/run-benchmarks.md |
| Run the platform matrix | docs/platform/README.md | .agents/skills/run-platform-matrix.md |
| Run the full pipeline locally / in hooks | docs/operations/local-ci.md | .agents/skills/run-local-ci.md |
| Debug a CI failure | docs/operations/ci.md | pipelines/README.md |
| Run or deploy the telemetry hub | docs/operations/hub.md | worker/README.md |
| Cut a release | docs/operations/release.md | .agents/skills/cut-release.md |
| Triage an issue | docs/operations/triage.md | .agents/skills/triage-issue.md |
| Handle a security report | docs/operations/security.md | .agents/skills/handle-security-report.md |
| Update this map | this file | .agents/skills/update-docs-map.md |
| Know what works today | docs/STATUS.md | HANDOVER.md |
| Look up a term | docs/glossary.md | |

## Canonical ownership
| Topic | Canonical source | Summary allowed elsewhere? |
|---|---|---|
| Pillar list, deps, stability | registry/pillars.json | tables generated or checked against it |
| Constitutional invariants | MAINTAINER.md (mirrored in registry) | AGENTS.md links only |
| Build commands | docs/setup/build.md | AGENTS.md may list the five core commands |
| Platform tiers and RIDs | docs/platform/README.md | no |
| ADR process | docs/adr/README.md | CONTRIBUTING.md links only |
| Agent roles | .agents/personas/README.md | AGENTS.md links only |
| Procedures | .agents/skills/ | never inlined elsewhere |
| Performance budgets | docs/performance/budgets.md | no |
| License inventory | docs/governance/licensing.md | SBOM output only |

## Document set
- `docs/setup/` environment, build, test
- `docs/architecture/` overview, boundaries, models, `pillars/<Id>.md`
- `docs/adr/` MADR records (institutional memory)
- `docs/platform/` tier policy and per-platform notes
- `docs/packages/<Id>.md` one consumer doc per package, with a "what it does not do" section
- `docs/governance/` conflict resolution, upstream policy, licensing, agent authority
- `docs/security/`, `docs/performance/`, `docs/operations/`
- `docs/spec/SPEC.md` the complete specification
