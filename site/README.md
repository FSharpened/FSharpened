# Website (Cloudflare Pages: https://fsharpened.pages.dev)

An installable, offline-capable static site (no framework, no runtime dependencies). Pages are generated from `registry/pillars.json` so the pillar list cannot drift. Full guide: `docs/operations/site.md`.

- `src/` hand-written CSS, JavaScript and the service-worker template. `static/` is copied as is (`_headers`, favicon, images).
- `public/` and `dist/` are generated and git-ignored. Never edit them.

```bash
(cd ide/web && npm ci && npm run build)            # IDE bundle -> site/dist/ide.js
python tools/site/build.py --require-ide           # -> site/public
python tools/site/check.py                         # verify links, metadata, manifest, service worker, CSP-safe markup
node --test "site/test/*.test.mjs"                 # service worker tests
npx --prefix gateway wrangler pages deploy site/public --project-name fsharpened --branch main   # deploy (needs `wrangler login`)
```

The Pages project was created with `wrangler pages project create fsharpened --production-branch main --force` (`--force` is needed once so Wrangler creates a classic Pages project instead of delegating to Workers). A legacy Worker named `fsharpened` (workers.dev) also exists and can be deleted.
