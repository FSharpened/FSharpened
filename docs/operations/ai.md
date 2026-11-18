# Workers AI module and the completion provider interface (ADR-0026)

Two things, built together:

1. **`ai/`**, a Cloudflare Worker that calls Workers AI for code completion, with a model allowlist, budgets, a cache, a kill switch and a static fallback. *(PLAN P6.D1)*
2. **`FSharpened.Tooling.Completion`**, a small F# library that defines a provider-agnostic completion interface, a fallback chain, context trimming and a Workers AI provider; the browser IDE has an opt-in command that uses it. *(PLAN P7.6)*

**State today:** both exist, are tested against mocks, and are **not deployed**. No real Workers AI call, Durable Object, Cache API or CORS exchange of a deployed Worker has ever been made. **AI is off by default** at both ends.

## The Worker (`ai/`)

```
POST /api/ai/complete   { language, prefix, suffix?, path?, model?, maxTokens? }
GET  /api/ai/health     { ok, enabled }          (never calls a model)
```

Answer (always `200` for a valid request, so a client can rely on getting candidates):

```json
{ "ok": true, "source": "workers-ai" | "static", "degraded": null | "disabled" | "unavailable" | "budget" | "error" | "timeout" | "empty",
  "model": "...", "cached": false, "retryAfterSeconds": 30,
  "candidates": [ { "text": "...", "kind": "inline" | "keyword" | "snippet", "confidence": 0.5 } ] }
```

Invalid or over-limit requests are rejected, not degraded: `400` (shape, model not on the allowlist, bad language), `413` (prefix over 6,000 characters, suffix over 2,000, path over 200, body over 24 KiB, `maxTokens` or input over the model's limit), `403` (origin not allowed), `401` (token required and missing or bad).

| Concern | Where | What it does |
|---|---|---|
| Allowlist | `src/models.ts` | Named models only, each with max input and output tokens. `DEFAULT_MODEL` can only choose among them. |
| Validation, prompt, output | `src/policy.ts` | Limits above; the Worker builds the prompt (the client cannot send one); the answer is reduced to plain text (fences, control and bidirectional-override characters removed, 2,000 characters at most). |
| Budgets | `src/budget.ts`, `src/ledger.ts` | Requests per minute, requests per day and tokens per day, per caller and global. A SQLite-backed Durable Object per scope runs the pure arithmetic, so reservations are atomic. Worst-case reservation (estimated prompt plus `maxTokens`), given back if the model call fails or the global budget refuses. Ledger unreachable means no model call (fail closed). |
| Cache | `src/cache.ts` | Cache API, key SHA-256 of (prompt version, model, language, path, prefix, suffix, maxTokens), TTL (default 3600 s, max 86400), entries over 4 KiB are not stored. A hit calls no model and spends no budget. |
| Fallback | `src/fallback.ts` | Deterministic keyword completions for the word at the cursor. Used for `disabled`, `unavailable`, `budget`, `error`, `timeout`, `empty`. |
| Auth | `src/auth.ts` | Allowed `Origin` (guard rail, not a secret) and, when the `TOKEN_SECRET` secret is set, a signed bearer token `id.expiry.signature` (HMAC-SHA256). Budgets are per token id, else per hashed address. Minting tokens is not built. |
| Logging | `src/handler.ts` | One metadata line per request (source, degraded reason, model id, cached, token estimates, language id, duration, 24-hex caller hash). Never prompt, path, completion or address. A test asserts it. |
| Kill switch | `AI_ENABLED` | Must be exactly `"true"`. Ships `"false"`. Checked before the cache and the budget. |

### Variables (`ai/wrangler.jsonc`)
`AI_ENABLED` (`"false"`), `ALLOWED_ORIGINS`, `DEFAULT_MODEL`, `AI_TIMEOUT_MS` (8000, at most 25000), `CACHE_TTL_SECONDS`, `CALLER_REQUESTS_PER_MINUTE` (6), `CALLER_REQUESTS_PER_DAY` (200), `CALLER_TOKENS_PER_DAY` (40000), `GLOBAL_REQUESTS_PER_MINUTE` (60), `GLOBAL_REQUESTS_PER_DAY` (2000), `GLOBAL_TOKENS_PER_DAY` (300000). A malformed number falls back to the small default; `0` is a valid budget and stops spending. The budget numbers are guesses to tune after real use, not measured against the free allocation.

### Develop and test (no Cloudflare account)
```bash
cd ai && npm ci && npm run typecheck && npm test
```
The `AI` binding, the Durable Object ledger and the cache are test doubles (`test/handler.test.ts`, `test/helpers.ts`). The tests prove: fallback on budget exhaustion (caller and global), model error, timeout, empty answer, missing binding, ledger failure and kill switch; a cache hit without a model call or budget; rejection of an over-limit prefix, body, `maxTokens` and non-allowlisted model; origin and token checks; no prompt, completion or address in logs; concurrent requests never over-admit.

### Deploy (maintainer only, never done by an agent)
```bash
cd ai && npm ci
npx wrangler login
npx wrangler deploy                 # creates the Worker "ai" and the BudgetLedger Durable Object (migration v1)
# optional, recommended before the endpoint is public:
npx wrangler secret put TOKEN_SECRET
# then, after reviewing the budgets, set AI_ENABLED to "true" in wrangler.jsonc (or the dashboard) and deploy again
```
`site/static/_headers` already allows `connect-src https://ai.fsharpened.workers.dev` for `/ide/*` (the Worker name `ai` mirrors the gateway's `ide`); if the Worker is deployed under another name, change both that and `DEFAULT_ENDPOINT` in `ide/web/src/aicomplete.js`. Before enabling it for the public, update the page's disclosure and `docs/governance/licensing.md`: source text goes to Cloudflare Workers AI. Check the model ids in `src/models.ts` against Cloudflare's current catalogue and free allocation first; they could not be called from this project.

### Operate
- **Stop it now:** set `AI_ENABLED` to `"false"` and deploy, or set a budget to `0`. Everything still answers, from the static fallback.
- **Why a request got no model answer:** the `degraded` field and the `degraded` key of the log line.

## The completion interface (`src/FSharpened.Tooling/Completion/`)

Pure F#, depends only on FSharp.Core; compiled in place into the IDE core (`tools/FSharpened.Ide`) so Fable can reach it. Tests: `tests/FSharpened.Ide.Tests/CompletionTests.fs` (xunit, .NET) and `ide/web/test/completion.test.mjs`, `aicomplete.test.mjs` (the same code compiled by Fable, with a mocked network).

| File | What |
|---|---|
| `Types.fs` | `CompletionRequest` (path, language, prefix, suffix, cursor, max context; no field for other files), `Candidate` (text, kind, confidence), `CompletionResult` (candidates, source, why providers were skipped, why a request was withheld), `ICompletionProvider` (`Name`, `IsRemote`, `Complete(request, cancellationToken)`). |
| `Context.fs` | Windowing (three quarters before the cursor, a quarter after, cut at line boundaries where close enough, never more than the Worker accepts), sensitive-file check (`.env`, key and certificate stores, credentials files) and a secret scan (private-key blocks, GitHub, AWS, Slack tokens, JWTs, `sk-` keys, `password = "..."`-shaped assignments). A flagged request is **withheld** from remote providers. Best effort: it can miss a secret and can withhold harmless code. |
| `StaticProvider.fs` | Keywords and the host's snippets; local, cannot fail. |
| `Chain.fs` | Providers in order; first non-empty answer wins; failures are skipped and recorded; remote providers are never asked for a withheld request; cancellation stops the chain; the chain never throws. |
| `WorkersAi.fs`, `MiniJson.fs` | Encodes the request, decodes and validates the answer (candidate count and length, kinds, control characters), maps HTTP outcomes and the Worker's `degraded` reasons. The host passes a `Transport` and enforces the timeout it is given; the library does no I/O. Endpoint must be https (http only on localhost, no credentials in the URL). |

### Adopting it from another host (desktop IDE, VS Code extension)
1. Build a `CompletionRequest` from the open buffer: `Context.ofBuffer path language text cursor Context.DefaultMaxContext`.
2. Write a `Transport` (`endpoint -> json -> timeoutMs -> CancellationToken -> Async<TransportOutcome>`) with your HTTP client; map a timeout to `TransportTimedOut`.
3. `Chain.run [ WorkersAi.create (WorkersAi.defaultSettings endpoint) transport; StaticProvider.create snippets ] request ct`.
4. Show `result.Candidates.Head` as a suggestion, labelled with `result.Source` ("a model's suggestion, check it" for `workers-ai`); never run it.
5. Keep it off by default and show what is sent and where before turning it on.

Any other provider (a local ONNX model through `FSharpened.ML.*`, another service) is one more `ICompletionProvider` in the list.

## Browser IDE: the opt-in command
- Off by default. Nothing is sent, and no request is made, until the person runs **AI: Enable AI Completion…** (command palette, `F1`), which states what is sent and where and asks for the endpoint. The setting is stored in this browser only (`fsharpened.ide.ai.v1`).
- **AI: Suggest Inline Completion** (`Alt+\`) asks for a suggestion for the caret of the open file. It shows as ghost text; `Tab` accepts, `Escape` or any edit dismisses. The status message says whether it came from Workers AI or the built-in keyword list, and why Workers AI was not used (off, over budget, timed out, withheld as a secret).
- **AI: Disable AI Completion** and **AI: Set AI Completion Endpoint…** complete the set. The page's request omits credentials and the referrer; the page's own timeout is 9 s.
- Code: `ide/web/src/aicomplete.js` (controller), `ide/web/src/editor.js` (ghost text), `tools/FSharpened.Ide.Web/CompletionApi.fs` (the chain for the page).

## Limits, honestly
- Everything here was verified against mocks, plus the built page against a local mock endpoint (see `docs/STATUS.md`). The first real call will find things mocks cannot (model ids, response shapes, quotas, cold-start latency, CORS).
- A model can still produce wrong or misleading code. The suggestion is data for a person to accept or ignore; nothing executes it.
- Cache hits are not metered; they cost no model time, and only reveal text to a caller who already sent the same hash input.
- The origin check does not stop a script from calling the endpoint; the budgets and, once configured, the signed token do.
