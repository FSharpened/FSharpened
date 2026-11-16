# Hub operations (ADR-0017)

The hub collects `fsh-ci` run reports and serves an admin console. It is optional; local and GitHub pipelines work without it.

## What it stores
Run summaries (RID, profile, result, durations, stage results, short commit, branch, CI run URL), hashed API tokens, an audit log, and feature flags. No personal data.

## Live deployment
- Site: https://fsharpened.fsharpened.workers.dev (`site/`, `wrangler deploy`)
- Hub: https://fsharpened-hub.fsharpened.workers.dev (D1 `fsharpened-hub`, KV `SETTINGS`; migration 0001 applied)
- Admin sign-in: GitHub OAuth (app "FSharpened Hub" owned by the FSharpened account), allowlist `ADMIN_GITHUB_USERS` in `worker/wrangler.jsonc`; secrets `GITHUB_CLIENT_SECRET` and `SESSION_SECRET` live only in the Worker. Cloudflare Access remains an optional second path (Zero Trust needs a card on file).

## First-time deploy (maintainer)
```bash
cd worker && npm ci
npx wrangler login
npx wrangler d1 create fsharpened-hub           # copy database_id into wrangler.jsonc
npx wrangler kv namespace create SETTINGS       # copy id into wrangler.jsonc
npm run db:migrate:remote
npx wrangler deploy
```
Then in Cloudflare Zero Trust create a **Self-hosted Access application** for the Worker's hostname, allow only the maintainers' identities, and copy its **AUD tag** and team domain into `ACCESS_AUD` / `ACCESS_TEAM_DOMAIN` (`wrangler.jsonc` vars). Until both are set the admin API rejects every request (fail closed). Do not enable `ENVIRONMENT=dev` outside local development.

## Connect publishers
1. Console, API tokens, create a token (shown once).
2. In GitHub: repository variable `HUB` (value: the hub URL) and secret `FSH_HUB_TOKEN`. `ci.yml` forwards them to `fsh_ci.py`, which publishes after each run.
3. Locally: set the same two environment variables.

## Operate
- **Kill switch:** Settings, "Accept CI ingest" off rejects all publishes.
- **Revoke a token:** API tokens, Revoke (publishers then get 401).
- **Retention:** nightly cron deletes runs older than the retention setting.
- **Local development:** `cd worker && npm run db:migrate:local && npx wrangler dev --var ENVIRONMENT:dev`.
