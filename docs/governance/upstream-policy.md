# Upstream Policy

**Upstream-first.** A fix that belongs in the F# compiler, FCS, Ionide or a wrapped tool is contributed upstream before any downstream workaround is accepted. FSharpened carries a fix downstream only when upstream adoption is blocked by release timelines.

## Tiers
**Tier A - Funded.** F# compiler, FCS, SharpLsp. Load-bearing substrates; FSharpened contributes maintainers or funding.

**Tier B - Partnered.** TorchSharp, DiffSharp, Furnace, Ionide, FsLangMCP, SageFs. Coordinate releases and contribute PRs; no direct funding.

**Tier C - Consumed.** dep-scan, SOVA, ShieldedStack, Expecto, FsCheck, Hegel, Hedgehog, AltCover, Stryker.NET, DuckDB, Flowthru, DataFusionSharp, ILGPU, ONNX Runtime, ML.NET, Sigstore. Wrapped and tracked; FSharpened releases do not depend on their roadmaps.

## Rules
1. Every downstream workaround links an upstream issue in code and in the PR.
2. The workaround is removed within **two releases** of the upstream fix landing.
3. Every PR that wraps an external tool includes an upstream-impact assessment (`wrap-not-rebuild`).
4. Pending upstream transitions are tracked in `TODO.md` "Blocked" (e.g. dotnet/sdk#55128 for FCS-native hot reload).
