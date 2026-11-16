# ADR-0017: A Cloudflare Worker hub for CI telemetry and operations

- Status: Proposed (human approval required)
- Date: 2026-11-16

## Context
The local-first pipeline (ADR-0016) already emits a structured `summary.json` per run. Nothing aggregates it: there is no view of per-RID health over time, stage regressions, or infrastructure-vs-real failures (for example GitHub macOS arm64 runner capacity). The project also needs a human-gated console for operational settings and API tokens.

## Decision
Add `worker/` (the **hub**): one Cloudflare Worker with static admin assets, D1 (runs, stages, tokens, audit) and KV (feature flags).
- **Ingest:** `POST /api/ingest/ci`, bearer token, validated and size-capped. `fsh_ci.py` publishes best-effort when `FSH_HUB_URL` and `FSH_HUB_TOKEN` are set and can never change the pipeline's exit code.
- **Admin:** `/api/admin/*` authenticated by a verified Cloudflare Access JWT (fails closed; no passwords stored), mutating calls require a custom header and same-origin.
- **Public:** only `/api/status.json` (matrix verdicts, no commits/URLs), behind a flag.
- **Data:** telemetry only, no personal data; retention sweep by cron; every admin change is audit-logged.

## Consequences
- Adds a deployable service; it is optional, the pipeline works without it (Invariant 11: compose, don't rebuild; the hub only consumes existing reports).
- Tokens are stored as SHA-256 hashes and are revocable from the console.
- Future modules (release feed, approval inbox, model registry, MCP knowledge server) attach to the same Worker.
