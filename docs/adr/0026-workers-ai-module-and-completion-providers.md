# ADR-0026: A Workers AI Worker with budgets and a static fallback, and a provider-agnostic completion interface

- **Status:** Proposed (implements PLAN.md P6.D1 and P7.6 of ADR-0021 phase D; needs human acceptance and a human deploy)
- **Date:** 2026-11-18
- **Deciders:** Tooling Builder, Architect, Reviewer (maintainer approval pending)

## Context
ADR-0021 phase D asks for "a Workers AI module with a fixed allowlist of models, per-day budgets under the free allocation, caching by content hash and a hard fallback", and ADR-0012 requires every model-backed feature to have a deterministic path first. P7.6 asks for an AI completion provider interface with Workers AI as the first provider. Constraints that shape the design:

- Source code leaves the browser to a third party. Nothing may be on by default (ADR-0021, "limits"), nothing may be logged, and a model's output is data, never an instruction.
- The free Workers AI allocation is small and shared across the account. A leaked or abused endpoint must fail towards "static answer", not towards "bill or exhausted quota".
- The project has no way to call Cloudflare from CI. Everything must be testable with a mocked `AI` binding.
- AGENTS.md: never deploy, never publish; a human approves.

## Decision

### 1. A separate Worker, `ai/` (not a module inside `gateway/` or the hub)
- **Not `gateway/`.** The gateway is public, anonymous and rate-limited per IP; it runs other people's code through third-party services. The AI Worker holds an `AI` binding (a billable, quota-limited resource) and Durable Object budgets. Separate Workers mean separate deploys, separate kill switches, separate bindings and a smaller blast radius for each.
- **Not the hub.** The hub holds D1 data and admin sessions (ADR-0017). Giving an anonymous completion endpoint a binding in the same Worker would put CI telemetry and the admin console next to it. The hub can mint tokens for the AI Worker later; it does not host it.
- `ai/` has its own `package.json`, `tsconfig.json`, `wrangler.jsonc` and vitest, and one CI stage (`ai`) and workflow (`ai.yml`) in the same style as `gateway` and `hub`.

### 2. What the Worker does
Endpoint `POST /api/ai/complete` (and `GET /api/ai/health`, which never calls a model). The request is `{ language, prefix, suffix?, path?, model?, maxTokens? }`. The Worker builds the prompt itself from a fixed template; a caller cannot send a free-form prompt, so the endpoint is a code-completion endpoint, not a general chat proxy (ADR-0021 scope: code only).

Flow: origin, then token (when configured), then size, JSON, validation, the kill switch, the cache, the budget, the model with a timeout, output sanitising.

- **Allowlist.** `ai/src/models.ts` names the models and gives each a maximum input and output in tokens. A request may name one of them or none; anything else is `400`. The `DEFAULT_MODEL` variable can only choose among them. Model ids and their free availability are Cloudflare's to change; the list is short and reviewed. *The two ids in the allowlist were taken from Cloudflare's published catalogue and have not been called from this project (no credentials were used).*
- **Validation and limits.** Prefix at most 6,000 characters, suffix 2,000, path 200, body 24 KiB, input within the model's token limit, `maxTokens` within the model's limit. Over-limit requests are rejected (`413`/`400`), never truncated silently, and never reach the budget or the model.
- **Budgets.** Requests per minute, requests per day and tokens per day, for each caller and for everyone together. The arithmetic is a pure function (`budget.ts`); a **Durable Object** per scope (`ledger.ts`, SQLite-backed so it works on the free plan) holds the counters and runs that function, so a reservation is atomic (a Durable Object handles one call at a time). Reservation is worst case (estimated prompt tokens plus `maxTokens`, at about three characters per token, rounded up) and is given back if the model call fails. If the global budget refuses, the caller's reservation is returned. If the ledger cannot be reached the request is **not** sent to the model (fail closed). Budget numbers are Worker variables with small defaults; a malformed value falls back to the small default, never to "unlimited"; `0` is a valid way to stop spending without the kill switch. KV was rejected for counters (no atomic increment); D1 was rejected (the hub's database; a counter per request is write-heavy; ledger state does not belong beside CI data).
- **Cache.** The Workers Cache API, keyed by SHA-256 of (prompt version, model, language, path, prefix, suffix, maxTokens), with a TTL (default one hour, at most a day) and a per-entry size cap (4 KiB; larger answers are returned but not stored). Cache hits do not call the model and do not spend budget. The cache stores the sanitised completion text for the TTL at Cloudflare's edge in the data centre that served it; the key is a hash. A hit is only returned to a request with the same hash input, so it reveals nothing the requester did not already send. The total size is bounded by the TTL and the Cache API's own eviction, not by a counter of ours.
- **Static fallback.** `ai/src/fallback.ts`: deterministic keyword completions for the word at the cursor. It answers with `source: "static"` and `degraded` set to why: `disabled` (kill switch), `unavailable` (no binding), `budget`, `error`, `timeout`, `empty`. Validation failures are rejections, not fallbacks.
- **Kill switch.** The variable `AI_ENABLED` must be exactly `"true"`. `wrangler.jsonc` ships `"false"`: **Workers AI is off by default**. The check comes before the cache and the budget, so switching it off also stops serving model output from the cache.
- **Authentication.** Two layers. (1) An allowed `Origin` (browsers cannot forge it; scripts can, so it is a guard rail, not a secret), the same approach as `gateway/`. (2) When the `TOKEN_SECRET` Worker secret is set, a signed bearer token `id.expiry.signature` (HMAC-SHA256, constant-time verify) is required, and budgets are per token id instead of per address. The hub or another trusted service can mint tokens with the same secret; **minting is not built here**. No secret is in the repository: `TOKEN_SECRET` is set with `wrangler secret put` by a human. Whatever the layer, the budgets bound the damage.
- **Logging.** One metadata line per request: source, degraded reason, model id, cached, token estimates, language id, duration and a 24-hex hash of the caller. Never the prompt, the path, the completion or the address. A test asserts it. No telemetry is added anywhere else.
- **Prompt-injection posture.** The code goes into the user message inside `<fsh-prefix>`/`<fsh-suffix>` tags that are neutralised if the code contains them; the system message says the content is untrusted data. The model has no tools. Its answer is reduced to plain text (fences removed, control and bidirectional-override characters removed, 2,000 characters at most) and returned as `candidates[].text` for a person to accept or ignore. Nothing in the Worker or in the clients executes it, parses it as a command, or feeds it to another model call. This lowers the risk; it does not prove a model never emits something misleading, so clients show it as a suggestion, labelled with its source.

### 3. The completion provider interface (P7.6)
Pure F# in the **Tooling** pillar (`src/FSharpened.Tooling/Completion/`), compiled in place into the IDE core (as `Overlay.fs` and Statepoints already are) so the browser IDE gets it through Fable, and tested natively in `tests/FSharpened.Ide.Tests`. No new pillar is registered: editor integrations are what `Tooling` is for, and the files depend on nothing but FSharp.Core.

- `CompletionRequest` (path, language, prefix, suffix, cursor, context cap), `CompletionResult` (candidates with text, kind, confidence, source; a degraded note), and `ICompletionProvider` (`Name`, `Complete: request -> CancellationToken -> Async<CompletionResult>`).
- **Context trimming** (`Context.fs`): keeps a window around the cursor within the cap, cuts at a line boundary where it can, uses only the buffer the person has open, and **withholds the request entirely** when the text near the cursor looks like a secret (private-key blocks, `ghp_`/`AKIA`/`xox` tokens, long `password = "..."`-shaped assignments; the same idea as the `secrets` check in `fsh_ci.py`). Files the person did not open are never added: the type has no field for them.
- **Fallback chain** (`Chain.fs`): providers in order; the first one that returns candidates wins; a provider that fails, times out or returns nothing is skipped; the last is the static provider, which cannot fail. Cancellation stops the chain. The result says which provider answered and why earlier ones did not.
- **Workers AI provider** (`WorkersAi.fs`): encodes the request for the module above, decodes the answer, validates it, and degrades to the static provider's answer. The transport (a function from request text to an outcome) is injected by the host, which enforces the timeout (browser `fetch` with `AbortController`; a .NET host with `HttpClient`); the F# code therefore stays pure and identical under Fable and .NET.
- **Consumer.** The browser IDE gets an opt-in, default-off setting (`aiCompletion`, plus the endpoint URL) and one command, "Suggest Inline Completion", that shows the suggestion as ghost text and accepts it on Tab. The desktop IDE (ADR-0023) and the VS Code extension (ADR-0025) are **not** wired here; `docs/operations/ai.md` documents the interface so they can adopt it.

### 4. What this ADR does not decide
Training or hosting our own models (ADR-0021 phase G), AI scanning of community submissions (phase C; it can reuse the Worker), a streaming API, chat, or sending any file other than the buffer being edited. Deploying the Worker is a human step: create the Cloudflare resources, set `TOKEN_SECRET` if wanted, review the budgets, set `AI_ENABLED` to `"true"`.

## Consequences
- A new Worker and CI stage/workflow to maintain; a second place that must be deployed by a person; a new Durable Object class (migration `v1`).
- Source text (a window of at most a few KB around the cursor, never secrets by the filter's best effort) goes to Cloudflare Workers AI only when a person turns the setting on and a maintainer has enabled the Worker. The on-page disclosure and `docs/governance/licensing.md` must say so before the Worker is enabled for the public (follow-up; the setting's description says it).
- The secret filter is a best-effort pattern list, not a guarantee. It can miss a secret and can withhold harmless code.
- Free-plan quotas and model availability can change under us; the budgets are numbers to tune after the first real week, not facts.
- Everything here was verified against mocks. No real Workers AI call, Durable Object, Cache API or CORS behaviour of a deployed Worker was exercised.

## Alternatives considered
- **A module in `gateway/`.** Rejected: different binding, different trust, and the kill switch of one would affect the other.
- **A module in the hub.** Rejected: puts an anonymous endpoint beside admin sessions and D1 telemetry.
- **KV or D1 for counters.** Rejected as above; Durable Objects are the primitive that makes read-modify-write atomic.
- **Cloudflare's built-in rate limiting only (as the gateway does).** Rejected: it limits requests, not tokens, and not a global daily total.
- **Letting the client send a prompt.** Rejected: makes the endpoint a free LLM proxy and widens injection and abuse; a fixed completion template keeps it a code tool.
- **A new `Completion` pillar.** Rejected for now: it would add a registry entry, a project and docs for four small files; `Tooling` already owns editor integrations. Revisit if it grows.
- **HTTP inside the F# library.** Rejected: `HttpClient` and `fetch` differ between .NET and Fable, and the timeout belongs to the host's I/O; an injected transport keeps the library pure and testable.
