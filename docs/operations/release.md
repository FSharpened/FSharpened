# Release

Executed by `.agents/skills/cut-release.md`. **Merging to `main` and publishing packages/models are human-gated.**

Pre-flight: six-RID matrix green; per-RID NativeAOT publish + ONNX smoke green; benchmarks inside budgets; docs-drift green; license inventory current.
Artifacts: NuGet packages + `fsh` tool, SBOM (CycloneDX), SLSA provenance, Sigstore signatures. Release is triggered only by a human-approved tag via `agent-release.yml`. Stability tiers: Stable (semver, back-compat), Preview, Experimental (breaking allowed with migration note).
