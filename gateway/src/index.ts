import { LIMITS, cacheKey, normalize, normalizeWandbox, originAllowed, validate, type RunRequest, type RunResult, type Upstream, type WandboxUpstream } from "./policy";

interface Env {
  RUN_LIMITER: { limit(o: { key: string }): Promise<{ success: boolean }> };
  ALLOWED_ORIGINS: string;
  UPSTREAM: string;
  WANDBOX: string;
}

const USER_AGENT = "FSharpened-IDE/0.1 (+https://github.com/FSharpened/FSharpened)";

/** A failure the client should see as a status code, not as a generic 502. */
class Busy extends Error {}

async function viaCompilerExplorer(env: Env, r: RunRequest, signal: AbortSignal): Promise<RunResult> {
  const up = await fetch(`${env.UPSTREAM}/api/compiler/${r.compiler}/compile`, {
    method: "POST", signal,
    headers: { "content-type": "application/json", accept: "application/json", "user-agent": USER_AGENT },
    body: JSON.stringify({
      source: r.source, lang: null, allowStoreCodeDebug: false,
      options: { userArguments: r.options, executeParameters: { args: [], stdin: r.stdin }, compilerOptions: { executorRequest: true }, filters: { execute: true }, tools: [], libraries: [] },
    }),
  });
  if (up.status === 429) throw new Busy();
  if (!up.ok) throw new Error("upstream " + up.status);
  return normalize((await up.json()) as Upstream);
}

async function viaWandbox(env: Env, r: RunRequest, signal: AbortSignal): Promise<RunResult> {
  const started = Date.now();
  const up = await fetch(`${env.WANDBOX}/api/compile.json`, {
    method: "POST", signal,
    headers: { "content-type": "application/json", accept: "application/json", "user-agent": USER_AGENT },
    body: JSON.stringify({ compiler: r.compiler, code: r.source, stdin: r.stdin, options: "", save: false }),
  });
  if (up.status === 429) throw new Busy();
  if (!up.ok) throw new Error("upstream " + up.status);
  return normalizeWandbox((await up.json()) as WandboxUpstream, Date.now() - started);
}

const json = (data: unknown, status: number, extra: Record<string, string> = {}) =>
  new Response(JSON.stringify(data), { status, headers: { "content-type": "application/json", "cache-control": "no-store", "x-content-type-options": "nosniff", ...extra } });

export default {
  async fetch(req: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(req.url);
    const origin = req.headers.get("origin");
    const cors: Record<string, string> = originAllowed(origin, env.ALLOWED_ORIGINS)
      ? { "access-control-allow-origin": origin!, vary: "origin", "access-control-allow-methods": "POST, GET, OPTIONS", "access-control-allow-headers": "content-type", "access-control-max-age": "600" }
      : { vary: "origin" };

    if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: cors });
    if (url.pathname === "/api/health") return json({ ok: true, service: "fsharpened-ide-gateway" }, 200, cors);
    if (url.pathname !== "/api/run" || req.method !== "POST") return json({ error: "not found" }, 404, cors);
    if (!originAllowed(origin, env.ALLOWED_ORIGINS)) return json({ error: "origin not allowed" }, 403, cors);

    const len = Number(req.headers.get("content-length") ?? "0");
    if (len > LIMITS.source * 2 + 8192) return json({ error: "payload too large" }, 413, cors);
    let body: unknown;
    try { body = await req.json(); } catch { return json({ error: "invalid json" }, 400, cors); }
    const v = validate(body);
    if (!v.ok) return json({ error: v.error }, 400, cors);
    const r = v.value;

    const key = await cacheKey(r);
    const cacheReq = new Request("https://ide-cache.invalid/" + key);
    const hit = await caches.default.match(cacheReq);
    if (hit) return new Response(hit.body, { status: 200, headers: { ...Object.fromEntries(hit.headers), ...cors, "x-fsh-cache": "hit" } });

    const ip = req.headers.get("cf-connecting-ip") ?? "unknown";
    const limited = await env.RUN_LIMITER.limit({ key: ip });
    if (!limited.success) return json({ error: "rate limited: 12 runs per minute" }, 429, { ...cors, "retry-after": "30" });

    const ac = new AbortController();
    const timer = setTimeout(() => ac.abort(), 28_000);
    try {
      const result = await (r.backend === "wandbox" ? viaWandbox(env, r, ac.signal) : viaCompilerExplorer(env, r, ac.signal));
      const res = json({ ...result, cached: false }, 200, cors);
      if (!result.timedOut) {
        const c = json({ ...result, cached: true }, 200, { "cache-control": "public, max-age=3600" });
        ctx.waitUntil(caches.default.put(cacheReq, c));
      }
      return res;
    } catch (e) {
      if (e instanceof Busy) return json({ error: "the execution service is busy, try again in a few seconds" }, 429, { ...cors, "retry-after": "10" });
      if (e instanceof Error && e.message.startsWith("upstream ")) return json({ error: e.message }, 502, cors);
      return json({ error: (e as Error).name === "AbortError" ? "upstream timeout" : "upstream unreachable" }, 504, cors);
    } finally {
      clearTimeout(timer);
    }
  },
} satisfies ExportedHandler<Env>;
