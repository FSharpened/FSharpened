# Hub operations (ADR-0017)

The hub collects `fsh-ci` run reports and serves an admin console. It is optional; local and GitHub pipelines work without it.

## What it stores
Run summaries (RID, profile, result, durations, stage results, short commit, branch, CI run URL), hashed API tokens, an audit log, and feature flags. No personal data.

## Live deployment
- Site: https://fsharpened.pages.dev (Cloudflare Pages; `docs/operations/site.md`)
- Hub: https://hub.fsharpened.workers.dev (D1 `fsharpened-hub`, KV `SETTINGS`; migration 0001 applied)
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

## Install as an app (PWA)
The console is installable on desktop and mobile (Chrome, Edge, Safari "Add to Home Screen"). `worker/admin/manifest.webmanifest`, `sw.js` and `pwa.js` provide it; the layout adapts from phones (sidebar becomes a top bar with scrolling tabs) to ultrawide screens.
- The service worker caches **only the app shell**. It never touches `/api/*`: run history, tokens and the audit log are private, and the GitHub sign-in is a redirect it must not intercept. Signing out therefore leaves no data in Cache Storage.
- Offline, the console opens and says "You are offline"; it reloads by itself when the connection returns.
- Adding a file the shell needs? List it in `PRECACHE` in `worker/admin/sw.js` (tests and `tools/site/check.py` verify every listed file exists). Deploys need no version bump: shell files are fetched network-first.
- Icons are generated: `python tools/site/icons.py` (it also refreshes `worker/admin/`).

## Connect publishers
1. Console, API tokens, create a token (shown once).
2. In GitHub: repository variable `HUB` (value: the hub URL) and secret `FSH_HUB_TOKEN`. `ci.yml` forwards them to `fsh_ci.py`, which publishes after each run.
3. Locally: set the same two environment variables.

## Operate
- **Kill switch:** Settings, "Accept CI ingest" off rejects all publishes.
- **Revoke a token:** API tokens, Revoke (publishers then get 401).
- **Retention:** nightly cron deletes runs older than the retention setting.
- **Local development:** `cd worker && npm run db:migrate:local && npx wrangler dev --var ENVIRONMENT:dev`.
