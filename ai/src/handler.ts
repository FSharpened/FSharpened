// The request flow, free of Cloudflare bindings: everything it needs arrives in `Deps`, so tests drive it with fakes.
//
//   origin -> token -> size -> parse -> validate -> kill switch -> cache -> budget -> model (with timeout) -> sanitize
//
// Any step after validation that cannot give a model answer returns the static fallback (fallback.ts) with
// `degraded` saying why. Invalid or oversized requests are rejected, not degraded. Prompt and completion text are never
// logged: `log` receives metadata only. The model's text is returned as data in `candidates`; nothing here executes it.

import { admit, budgetsFrom, release, type Ledger } from "./budget";
import { bearer, callerHandle, verifyToken } from "./auth";
import { cacheable, ttlFrom, type Candidate, type ResponseCache } from "./cache";
import { staticCandidates } from "./fallback";
import { estimateTokens } from "./models";
import { LIMITS, buildMessages, cacheKey, estimateRequestTokens, extractText, originAllowed, sanitizeOutput, validate } from "./policy";

export interface Ai { run(model: string, inputs: unknown): Promise<unknown> }

export type Meta = Record<string, string | number | boolean | null>;

export interface Deps {
  /** Worker variables and secrets, as strings. */
  env: Record<string, string | undefined>;
  ai: Ai | undefined;
  ledger: Ledger;
  cache: ResponseCache;
  now(): number;
  log(meta: Meta): void;
}

export type Degraded = "disabled" | "budget" | "error" | "timeout" | "empty" | "unavailable";

export interface CompleteResponse {
  ok: true;
  source: "workers-ai" | "static";
  degraded: Degraded | null;
  model: string | null;
  cached: boolean;
  retryAfterSeconds?: number;
  candidates: Candidate[];
}

const DEFAULT_TIMEOUT_MS = 8_000;
const MAX_TIMEOUT_MS = 25_000;

const timeoutFrom = (v: string | undefined): number => {
  const n = v === undefined ? NaN : Number(v);
  return Number.isInteger(n) && n >= 100 ? Math.min(n, MAX_TIMEOUT_MS) : DEFAULT_TIMEOUT_MS;
};

const json = (data: unknown, status: number, extra: Record<string, string> = {}) =>
  new Response(JSON.stringify(data), { status, headers: { "content-type": "application/json", "cache-control": "no-store", "x-content-type-options": "nosniff", ...extra } });

class TimedOut extends Error {}

function withTimeout<T>(p: Promise<T>, ms: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  const t = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new TimedOut()), ms); });
  return Promise.race([p, t]).finally(() => clearTimeout(timer));
}

export const isEnabled = (env: Deps["env"]): boolean => env.AI_ENABLED === "true";

export async function handle(req: Request, d: Deps): Promise<Response> {
  const started = d.now();
  const url = new URL(req.url);
  const origin = req.headers.get("origin");
  const allowed = d.env.ALLOWED_ORIGINS ?? "";
  const cors: Record<string, string> = originAllowed(origin, allowed)
    ? { "access-control-allow-origin": origin!, vary: "origin", "access-control-allow-methods": "POST, GET, OPTIONS", "access-control-allow-headers": "content-type, authorization", "access-control-max-age": "600" }
    : { vary: "origin" };

  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: cors });
  if (url.pathname === "/api/ai/health" && req.method === "GET") return json({ ok: true, service: "fsharpened-ai", enabled: isEnabled(d.env) }, 200, cors);
  if (url.pathname !== "/api/ai/complete" || req.method !== "POST") return json({ error: "not found" }, 404, cors);
  if (!originAllowed(origin, allowed)) return json({ error: "origin not allowed" }, 403, cors);

  let identity = req.headers.get("cf-connecting-ip") ?? "unknown";
  const secret = d.env.TOKEN_SECRET;
  if (secret) {
    const tok = bearer(req.headers.get("authorization"));
    const id = tok ? await verifyToken(secret, tok, Math.floor(d.now() / 1000)) : null;
    if (!id) return json({ error: "token required" }, 401, cors);
    identity = "token:" + id;
  }

  if (Number(req.headers.get("content-length") ?? "0") > LIMITS.body) return json({ error: "payload too large" }, 413, cors);
  let text: string;
  try { text = await req.text(); } catch { return json({ error: "invalid body" }, 400, cors); }
  if (text.length > LIMITS.body) return json({ error: "payload too large" }, 413, cors);
  let body: unknown;
  try { body = JSON.parse(text); } catch { return json({ error: "invalid json" }, 400, cors); }
  const v = validate(body, d.env.DEFAULT_MODEL);
  if (!v.ok) return json({ error: v.error }, v.status, cors);
  const r = v.value;
  const caller = await callerHandle(identity);

  const done = (outcome: Omit<CompleteResponse, "ok">, inTok: number, outTok: number, extra: Record<string, string> = {}): Response => {
    d.log({ event: "complete", source: outcome.source, degraded: outcome.degraded, model: outcome.model, cached: outcome.cached, inputTokens: inTok, outputTokens: outTok, language: r.language, ms: d.now() - started, caller });
    return json({ ok: true, ...outcome }, 200, { ...cors, ...extra });
  };
  const fallback = (degraded: Degraded, extra: { retryAfterSeconds?: number } = {}, headers: Record<string, string> = {}): Response =>
    done({ source: "static", degraded, model: null, cached: false, ...extra, candidates: staticCandidates(r.language, r.prefix) }, 0, 0, headers);

  // The kill switch comes before everything that could touch a model or cached model output.
  if (!isEnabled(d.env)) return fallback("disabled");
  if (!d.ai) return fallback("unavailable");

  const key = await cacheKey(r);
  try {
    const hit = await d.cache.get(key);
    if (hit) return done({ source: "workers-ai", degraded: null, model: hit.model, cached: true, candidates: hit.candidates }, hit.inputTokens, hit.outputTokens, { "x-fsh-cache": "hit" });
  } catch { /* a broken cache is a miss */ }

  const reserved = estimateRequestTokens(r);
  const cfg = budgetsFrom(d.env);
  let admission;
  try {
    admission = await admit(d.ledger, caller, cfg, reserved, d.now());
  } catch {
    return fallback("error"); // no budget, no model: fail closed
  }
  if (!admission.ok) return fallback("budget", { retryAfterSeconds: admission.retryAfterSeconds }, { "retry-after": String(admission.retryAfterSeconds) });

  let raw: unknown;
  try {
    raw = await withTimeout(d.ai.run(r.model.id, { messages: buildMessages(r), max_tokens: r.maxTokens, temperature: 0, stream: false }), timeoutFrom(d.env.AI_TIMEOUT_MS));
  } catch (e) {
    try { await release(d.ledger, caller, reserved, d.now()); } catch { /* the budget stays charged: safe */ }
    return fallback(e instanceof TimedOut ? "timeout" : "error");
  }

  const out = sanitizeOutput(extractText(raw));
  if (out === "") return fallback("empty");
  const candidates: Candidate[] = [{ text: out, kind: "inline", confidence: 0.5 }];
  const outTokens = estimateTokens(out.length);
  const entry = { model: r.model.id, candidates, inputTokens: reserved - r.maxTokens, outputTokens: outTokens };
  if (cacheable(entry)) { try { await d.cache.put(key, entry, ttlFrom(d.env.CACHE_TTL_SECONDS)); } catch { /* not cached, still answered */ } }
  return done({ source: "workers-ai", degraded: null, model: r.model.id, cached: false, candidates }, entry.inputTokens, outTokens);
}
