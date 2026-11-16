# Website

Static site (no framework, no runtime dependencies). Pages are generated from `registry/pillars.json` so the pillar list cannot drift.

```bash
python tools/site/build.py                 # regenerate site/public
cd site && npx wrangler dev                # preview locally
cd site && npx wrangler deploy             # deploy to Cloudflare (needs `wrangler login`)
```
