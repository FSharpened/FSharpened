import { LIMITS, cacheKey, normalize, originAllowed, validate, type Upstream } from "./policy";

interface Env {
  RUN_LIMITER: { limit(o: { key: string }): Promise<{ success: boolean }> };
  ALLOWED_ORIGINS: string;
  UPSTREAM: string;
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
      const up = await fetch(`${env.UPSTREAM}/api/compiler/${r.compiler}/compile`, {
        method: "POST", signal: ac.signal,
        headers: { "content-type": "application/json", accept: "application/json", "user-agent": "FSharpened-IDE/0.1 (+https://github.com/FSharpened/FSharpened)" },
        body: JSON.stringify({
          source: r.source, lang: null, allowStoreCodeDebug: false,
          options: { userArguments: r.options, executeParameters: { args: [], stdin: r.stdin }, compilerOptions: { executorRequest: true }, filters: { execute: true }, tools: [], libraries: [] },
        }),
      });
      if (!up.ok) return json({ error: "upstream " + up.status }, 502, cors);
      const result = normalize((await up.json()) as Upstream);
      const res = json({ ...result, cached: false }, 200, cors);
      if (!result.timedOut) {
        const c = json({ ...result, cached: true }, 200, { "cache-control": "public, max-age=3600" });
        ctx.waitUntil(caches.default.put(cacheReq, c));
      }
      return res;
    } catch (e) {
      return json({ error: (e as Error).name === "AbortError" ? "upstream timeout" : "upstream unreachable" }, 504, cors);
    } finally {
      clearTimeout(timer);
    }
  },
} satisfies ExportedHandler<Env>;
