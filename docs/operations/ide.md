# Browser IDE operations (ADR-0018)

Live: https://fsharpened.pages.dev/ide/ (gateway: https://ide.fsharpened.workers.dev).

## Pieces
| Part | Path | Role |
|---|---|---|
| F# core | `tools/FSharpened.Ide`, tests in `tests/FSharpened.Ide.Tests` | workspace, timeline, diagnostics, protocol, fallback chain |
| Fable entry | `tools/FSharpened.Ide.Web` | exports the core to JavaScript |
| UI | `ide/web` | CodeMirror 6 shell, bundled to `site/dist/ide.js` (generated, git-ignored); the site build fingerprints it into `site/public/assets/h/` |
| Page | `tools/site/build.py` | generates `site/public/ide/index.html`; `site/src/ide.css` is hand-written (see `docs/operations/site.md`) |
| Gateway | `gateway/` | Worker: validates, rate-limits and proxies to Compiler Explorer |

## Build the front end
```bash
dotnet tool restore
cd ide/web && npm ci && npm run build      # Fable, then esbuild -> site/dist/ide.js
python tools/site/build.py --require-ide   # regenerate pages and fingerprint the bundle into site/public
```
`IDE_GATEWAY=https://your-gateway node build.mjs` points a local build at another gateway.

## Deploy (maintainer)
```bash
cd gateway && npm ci && npm test && npx wrangler deploy
npx wrangler pages deploy site/public --project-name fsharpened --branch main
```
Deploy the gateway first when compiler ids or limits change; `gateway/test/policy.test.ts` fails if its allowlist and `tools/FSharpened.Ide/Engines.fs` disagree.

## Operate
- **Rate limit:** 12 runs per minute per client (`RUN_LIMITER` in `gateway/wrangler.jsonc`).
- **Origins:** `ALLOWED_ORIGINS` must list every site origin that hosts the IDE; others get 403.
- **Cache:** successful results are cached for one hour under a hash of the request. Nothing else is stored; Worker logs hold request metadata only.
- **Upstream down:** the IDE walks the fallback chain (siblings, warm result, offline checks, escalation). Change `UPSTREAM` to move to a self-hosted Compiler Explorer.
- **Adding a compiler:** add it to `Engines.fs` and `COMPILERS` in `gateway/src/policy.ts` together; the test enforces this.
- **CSP:** the `/ide/*` block in `site/static/_headers` detaches the site-wide policy and relaxes only `style-src-elem` and `connect-src`. If the gateway URL changes, update it there.

## Privacy
Code is sent to Compiler Explorer when the user presses Run (disclosed on the page). Buffers and snapshots live in the browser's local storage. Share links keep code in the URL fragment, which is never sent to a server.
