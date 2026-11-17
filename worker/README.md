# FSharpened hub (Cloudflare Worker)

CI telemetry hub plus admin console. Design: `docs/adr/0017-cloudflare-hub.md`. Operations: `docs/operations/hub.md`.

```bash
npm ci
npm run typecheck && npm test
npm run db:migrate:local
npx wrangler dev --var ENVIRONMENT:dev     # admin UI at http://localhost:8787 (dev identity, no Access)
```

- `src/logic.ts` pure, unit-tested validation and aggregation. `src/auth.ts` Cloudflare Access JWT (optional) and GitHub session auth. `src/index.ts` routes.
- `admin/` dependency-free UI (no inline script/style; strict CSP in `admin/_headers`).
