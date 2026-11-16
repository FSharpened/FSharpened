# Website (Cloudflare Pages: https://fsharpened.pages.dev)

Static site (no framework, no runtime dependencies). Pages are generated from `registry/pillars.json` so the pillar list cannot drift.

```bash
python tools/site/build.py                                    # regenerate site/public
npx wrangler pages deploy site/public --project-name fsharpened --branch main   # deploy (needs `wrangler login`)
```

The Pages project was created with `wrangler pages project create fsharpened --production-branch main --force` (`--force` is needed once so Wrangler creates a classic Pages project instead of delegating to Workers). A legacy Worker named `fsharpened` (workers.dev) also exists and can be deleted.
