import { Hono } from "hono";
import { authenticateAdmin, type AppCtx, type Env } from "./auth";
import {
  DEFAULT_FLAGS, SIX_RIDS, matrixState, newToken, percentile, sanitizeFlags, sha256Hex, validateIngest, type Flags,
} from "./logic";

const app = new Hono<{ Bindings: Env; Variables: { actor: string } }>();

const json = (c: AppCtx, data: unknown, status = 200) => c.json(data as object, status as 200);
const now = () => Math.floor(Date.now() / 1000);

async function getFlags(env: Env): Promise<Flags> {
  const raw = await env.SETTINGS.get("flags", "json");
  return sanitizeFlags(raw, { ...DEFAULT_FLAGS, retentionDays: Number(env.RETENTION_DAYS) || DEFAULT_FLAGS.retentionDays });
}
async function audit(env: Env, actor: string, action: string, detail: string) {
  await env.DB.prepare("INSERT INTO audit (ts, actor, action, detail) VALUES (?,?,?,?)").bind(now(), actor, action, detail.slice(0, 500)).run();
}

// Security headers on everything the Worker serves.
app.use("*", async (c, next) => {
  await next();
  c.header("x-content-type-options", "nosniff");
  c.header("referrer-policy", "no-referrer");
  c.header("x-frame-options", "DENY");
  if (c.req.path.startsWith("/api/")) c.header("cache-control", "no-store");
});

// ---------------------------------------------------------------- public
app.get("/api/health", (c) => c.json({ ok: true, service: "fsharpened-hub" }));

/** Public, safe-to-expose status: matrix verdicts only (no commits, branches, or URLs). Gated by the publicStatus flag. */
app.get("/api/status.json", async (c) => {
  const flags = await getFlags(c.env);
  if (!flags.publicStatus) return c.json({ error: "disabled" }, 404);
  const { results } = await c.env.DB.prepare("SELECT rid, ok, profile FROM runs WHERE branch IN ('main','master') ORDER BY received_at DESC LIMIT 60").all<{ rid: string; ok: number; profile: string }>();
  c.header("cache-control", "public, max-age=60");
  return c.json({ updated: now(), matrix: matrixState(results) });
});

// ---------------------------------------------------------------- ingest (bearer token)
app.post("/api/ingest/ci", async (c) => {
  const auth = c.req.header("authorization") ?? "";
  const token = auth.startsWith("Bearer ") ? auth.slice(7).trim() : "";
  if (!token) return c.json({ error: "unauthorized" }, 401);
  const hash = await sha256Hex(token);
  const tok = await c.env.DB.prepare("SELECT id FROM tokens WHERE hash = ? AND revoked_at IS NULL").bind(hash).first<{ id: string }>();
  if (!tok) return c.json({ error: "unauthorized" }, 401);
  const flags = await getFlags(c.env);
  if (!flags.ingestEnabled) return c.json({ error: "ingest disabled" }, 503);
  const len = Number(c.req.header("content-length") ?? "0");
  if (len > 256_000) return c.json({ error: "payload too large" }, 413);
  let body: unknown;
  try { body = await c.req.json(); } catch { return c.json({ error: "invalid json" }, 400); }
  const v = validateIngest(body);
  if (!v.ok) return c.json({ error: v.error }, 400);
  const r = v.value;
  const stmts = [
    c.env.DB.prepare("DELETE FROM runs WHERE id = ?").bind(r.id),
    c.env.DB.prepare("INSERT INTO runs (id, received_at, started, profile, rid, ok, duration_s, commit_sha, branch, source, dirty, sdk, run_url) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)")
      .bind(r.id, now(), r.started, r.profile, r.rid, r.ok ? 1 : 0, r.duration_s, r.commit, r.branch, r.source, r.dirty ? 1 : 0, r.sdk, r.run_url),
    ...r.stages.map((s) => c.env.DB.prepare("INSERT INTO stages (run_id, name, status, seconds, detail) VALUES (?,?,?,?,?)").bind(r.id, s.name, s.status, s.seconds, s.detail)),
    c.env.DB.prepare("UPDATE tokens SET last_used = ? WHERE id = ?").bind(now(), tok.id),
  ];
  await c.env.DB.batch(stmts);
  return c.json({ ok: true, id: r.id }, 201);
});

// ---------------------------------------------------------------- admin (Cloudflare Access)
const admin = new Hono<{ Bindings: Env; Variables: { actor: string } }>();
admin.use("*", async (c, next) => {
  const who = await authenticateAdmin(c as unknown as AppCtx);
  if (!who) return c.json({ error: "unauthorized" }, 401);
  if (c.req.method !== "GET") {
    // CSRF defence in depth: custom header plus same-origin check.
    const origin = c.req.header("origin");
    if (c.req.header("x-fsh-admin") !== "1" || (origin && new URL(origin).host !== new URL(c.req.url).host)) return c.json({ error: "forbidden" }, 403);
  }
  c.set("actor", who);
  await next();
});

admin.get("/me", (c) => c.json({ email: c.get("actor"), environment: c.env.ENVIRONMENT }));

admin.get("/overview", async (c) => {
  const day = 86400, t = now();
  const [recent, daily, matrixRows, totals] = await Promise.all([
    c.env.DB.prepare("SELECT id, received_at, profile, rid, ok, duration_s, commit_sha, branch, source, run_url FROM runs ORDER BY received_at DESC LIMIT 12").all(),
    c.env.DB.prepare("SELECT CAST(received_at / 86400 AS INTEGER) AS d, SUM(ok) AS pass, COUNT(*) - SUM(ok) AS fail, AVG(duration_s) AS avg_s FROM runs WHERE received_at > ? GROUP BY d ORDER BY d").bind(t - 14 * day).all(),
    c.env.DB.prepare("SELECT rid, ok, profile FROM runs ORDER BY received_at DESC LIMIT 120").all<{ rid: string; ok: number; profile: string }>(),
    c.env.DB.prepare("SELECT COUNT(*) AS n, COALESCE(SUM(ok),0) AS pass FROM runs WHERE received_at > ?").bind(t - 30 * day).first<{ n: number; pass: number }>(),
  ]);
  const durs = (await c.env.DB.prepare("SELECT duration_s FROM runs WHERE received_at > ? AND profile != 'fast'").bind(t - 30 * day).all<{ duration_s: number }>()).results.map((r) => r.duration_s);
  return c.json({
    rids: SIX_RIDS, matrix: matrixState(matrixRows.results), recent: recent.results, daily: daily.results,
    totals: { runs30d: totals?.n ?? 0, passRate: totals && totals.n ? totals.pass / totals.n : null, p50: percentile(durs, 50), p95: percentile(durs, 95) },
  });
});

admin.get("/runs", async (c) => {
  const rid = c.req.query("rid"), status = c.req.query("status");
  const limit = Math.min(100, Math.max(1, Number(c.req.query("limit")) || 40)), offset = Math.max(0, Number(c.req.query("offset")) || 0);
  const where: string[] = [], args: (string | number)[] = [];
  if (rid) { where.push("rid = ?"); args.push(rid); }
  if (status === "pass" || status === "fail") { where.push("ok = ?"); args.push(status === "pass" ? 1 : 0); }
  const sql = `SELECT id, received_at, profile, rid, ok, duration_s, commit_sha, branch, source, run_url FROM runs ${where.length ? "WHERE " + where.join(" AND ") : ""} ORDER BY received_at DESC LIMIT ? OFFSET ?`;
  const { results } = await c.env.DB.prepare(sql).bind(...args, limit, offset).all();
  return c.json({ runs: results, limit, offset });
});

admin.get("/runs/:id", async (c) => {
  const id = c.req.param("id");
  const run = await c.env.DB.prepare("SELECT * FROM runs WHERE id = ?").bind(id).first();
  if (!run) return c.json({ error: "not found" }, 404);
  const { results } = await c.env.DB.prepare("SELECT name, status, seconds, detail FROM stages WHERE run_id = ? ORDER BY seconds DESC").bind(id).all();
  return c.json({ run, stages: results });
});

/** Per-stage duration stats plus regressions: latest successful run vs the median of the 10 before it, per RID. */
admin.get("/stages", async (c) => {
  const flags = await getFlags(c.env);
  const { results } = await c.env.DB.prepare(
    "SELECT s.name, s.seconds, r.rid, r.received_at FROM stages s JOIN runs r ON r.id = s.run_id WHERE s.status = 'pass' AND r.profile != 'fast' ORDER BY r.received_at DESC LIMIT 1500").all<{ name: string; seconds: number; rid: string; received_at: number }>();
  const by = new Map<string, number[]>(), perRid = new Map<string, number[]>();
  for (const r of results) {
    (by.get(r.name) ?? by.set(r.name, []).get(r.name)!).push(r.seconds);
    const k = r.name + "|" + r.rid;
    (perRid.get(k) ?? perRid.set(k, []).get(k)!).push(r.seconds);
  }
  const stats = [...by].map(([name, v]) => ({ name, n: v.length, p50: percentile(v, 50), p95: percentile(v, 95), max: Math.max(...v) })).sort((a, b) => b.p50 - a.p50);
  const regressions = [...perRid].flatMap(([k, v]) => {
    if (v.length < 4) return [];
    const [name, rid] = k.split("|") as [string, string];
    const latest = v[0]!, base = percentile(v.slice(1, 11), 50);
    return base > 1 && latest > base * (1 + flags.regressionPct / 100) ? [{ name, rid, latest, baseline: base, pct: Math.round((latest / base - 1) * 100) }] : [];
  }).sort((a, b) => b.pct - a.pct);
  return c.json({ stats, regressions, thresholdPct: flags.regressionPct });
});

admin.get("/flags", async (c) => c.json(await getFlags(c.env)));
admin.put("/flags", async (c) => {
  const cur = await getFlags(c.env);
  const next = sanitizeFlags(await c.req.json().catch(() => ({})), cur);
  await c.env.SETTINGS.put("flags", JSON.stringify(next));
  await audit(c.env, c.get("actor"), "flags.update", JSON.stringify(next));
  return c.json(next);
});

admin.get("/tokens", async (c) => {
  const { results } = await c.env.DB.prepare("SELECT id, name, created_at, created_by, last_used, revoked_at FROM tokens ORDER BY created_at DESC").all();
  return c.json({ tokens: results });
});
admin.post("/tokens", async (c) => {
  const body = (await c.req.json().catch(() => ({}))) as { name?: unknown };
  const name = typeof body.name === "string" ? body.name.trim().slice(0, 60) : "";
  if (!name) return c.json({ error: "name required" }, 400);
  const token = newToken(), id = crypto.randomUUID();
  await c.env.DB.prepare("INSERT INTO tokens (id, name, hash, created_at, created_by) VALUES (?,?,?,?,?)").bind(id, name, await sha256Hex(token), now(), c.get("actor")).run();
  await audit(c.env, c.get("actor"), "token.create", name);
  return c.json({ id, name, token, note: "Shown once. Store it as the FSH_HUB_TOKEN secret." }, 201);
});
admin.delete("/tokens/:id", async (c) => {
  const id = c.req.param("id");
  const r = await c.env.DB.prepare("UPDATE tokens SET revoked_at = ? WHERE id = ? AND revoked_at IS NULL").bind(now(), id).run();
  if (!r.meta.changes) return c.json({ error: "not found" }, 404);
  await audit(c.env, c.get("actor"), "token.revoke", id);
  return c.json({ ok: true });
});

admin.get("/audit", async (c) => {
  const { results } = await c.env.DB.prepare("SELECT ts, actor, action, detail FROM audit ORDER BY ts DESC, id DESC LIMIT 100").all();
  return c.json({ audit: results });
});

app.route("/api/admin", admin);
app.notFound((c) => (c.req.path.startsWith("/api/") ? c.json({ error: "not found" }, 404) : c.env.ASSETS.fetch(c.req.raw)));

export default {
  fetch: app.fetch,
  /** Nightly retention sweep. */
  async scheduled(_e: ScheduledController, env: Env) {
    const flags = await getFlags(env);
    const cutoff = now() - flags.retentionDays * 86400;
    await env.DB.prepare("DELETE FROM runs WHERE received_at < ?").bind(cutoff).run();
    await env.DB.prepare("DELETE FROM audit WHERE ts < ?").bind(now() - 400 * 86400).run();
  },
} satisfies ExportedHandler<Env>;
