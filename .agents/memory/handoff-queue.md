# Handoff Queue

Format: `- [ ] <id> | owner persona | acceptance criteria | source`

- [x] H1 | Tooling Builder | build + test green on win-x64 (done 2026-11-16)
- [ ] H0 | Tooling Builder | fantomas clean; ci.yml enabled and green on six RIDs incl. per-RID PublishAot | HANDOVER
- [ ] H2 | Pillar Builder | `StatepointGraph` thread-safe with concurrent fork/merge tests | PLAN P0.5.2
- [ ] H3 | Pillar Builder | Resolver uses real ONNX Runtime provider discovery per RID | PLAN P0.5.4
- [ ] H4 | Documentation | `.github/repo-policy.md` finalised; workflows copied to `.github/workflows/` after first green build | docs/operations/ci.md
