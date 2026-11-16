# Containers (Tier 2)

Containers are a **Linux distribution channel**, not a statement of platform preference. Images (`pipelines/containers/`) are multi-arch (`linux/amd64`, `linux/arm64`): .NET SDK, Fantomas, FsAutoComplete, OTLP collector. Agents and CI share one image to remove "works in CI, fails locally" drift. Images are signed and SBOM'd by `FSharpened.Security`. Windows containers are a separate ADR-gated track. The repo must remain operable without any image (upstream-independence).
