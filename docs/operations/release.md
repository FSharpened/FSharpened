# Release

Executed by `.agents/skills/cut-release.md`. **Merging to `main` and publishing packages/models are human-gated.**

Pre-flight: six-RID matrix green; per-RID NativeAOT publish + ONNX smoke green; benchmarks inside budgets; docs-drift green; license inventory current.
Artifacts: NuGet packages + `fsh` tool, SBOM (CycloneDX), SLSA provenance, Sigstore signatures. Release is triggered only by a human-approved tag via `agent-release.yml`. Stability tiers: Stable (semver, back-compat), Preview, Experimental (breaking allowed with migration note).

## `--profile release`: what is and is not a release artefact
`python tools/ci/fsh_ci.py run --profile release` builds, tests, packs, publishes the NativeAOT `fsh`, publishes the desktop IDE, and writes `artifacts/release/<commit>-<rid>/` for the **host RID**. `python tools/ci/fsh_ci.py verify` re-hashes it. A person reviews it; nothing is uploaded by the pipeline.

| In the bundle (an artefact) | Notes |
|---|---|
| `packages/*.nupkg` | every packable project; not pushed to any feed |
| `<rid>/fsh[.exe]` | the NativeAOT CLI, run once by the `aot` stage |
| `desktop/fsharpened-desktop-<version>-<rid>.zip` or `.tar.gz` | the desktop IDE: self-contained .NET (not trimmed, not NativeAOT), `languages/`, `grammars/`, Rust libraries for this RID, smoke-run with `--selftest`. **Unsigned**, no installer. `.tar.gz` keeps the executable bit |
| `SHA256SUMS`, `provenance.json`, `RELEASE_NOTES.md` | provenance says `published: false`, and for the desktop archive `signed/notarised/installer: false` |

| Not a release artefact | Why |
|---|---|
| Signed or notarised installers (MSI, MSIX, AppImage, `.app`, `.dmg`) | Signing certificates, Apple notarisation and store accounts are human gates. `packaging/` holds the inputs and a README of the human steps; nothing there is built by CI |
| Other RIDs' binaries from a local run | Only the host RID is built locally. `release-dry-run.yml` builds the six on GitHub for review, as workflow artifacts that expire after 14 days |
| Anything published: NuGet feeds, GitHub Releases, model artifacts, Pages | Human approval (AGENTS.md); there is no publish command |
| SBOM, SLSA provenance, Sigstore signatures (listed above under "Artifacts") | Not produced by the local pipeline yet; the bundle's own `provenance.json` is a local record, not a signed attestation |
| Test logs, `.fsharpened/ci/runs`, the stage's temporary folders | Evidence, not artefacts |

**Reproducibility.** The desktop archive is byte-for-byte reproducible on one machine and toolchain: two builds of the same commit, including one after deleting the project's `bin` and `obj`, produced the same SHA-256 on win-x64 (checked 2026-11-18). It is **not** promised across machines: the archive embeds compressed .NET runtime files and NuGet assets that do not change, but compression uses the zlib of the Python that ran the build, and the .NET compiler's determinism is only as good as the pinned SDK (`global.json` pins the minimum, not the exact version). The `.nupkg` files and the `fsh` binary are not claimed reproducible.
