// Pure, dependency-free logic: unit-testable without a Workers runtime.

export const SIX_RIDS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"] as const;
export const PROFILES = ["fast", "full", "release"] as const;
const STAGE_STATUS = new Set(["pass", "fail", "skip", "warn", "blocked", "cached"]);
const RID_RE = /^[a-z0-9][a-z0-9.-]{1,31}$/;

export interface StageIn { name: string; status: string; seconds: number; detail: string }
export interface RunIn {
  id: string; started: string; profile: string; rid: string; ok: boolean; duration_s: number; commit: string;
  branch: string; source: string; dirty: boolean; sdk: string; run_url: string; stages: StageIn[];
}

const str = (v: unknown, max: number): string => (typeof v === "string" ? v.slice(0, max) : "");
const num = (v: unknown): number => (typeof v === "number" && Number.isFinite(v) && v >= 0 ? Math.min(v, 1e6) : 0);

/** Validate and normalise an `fsh_ci` summary.json (plus the publish extras). Never trusts shape or size. */
export function validateIngest(body: unknown): { ok: true; value: RunIn } | { ok: false; error: string } {
  if (typeof body !== "object" || body === null || Array.isArray(body)) return { ok: false, error: "body must be an object" };
  const b = body as Record<string, unknown>;
  const profile = str(b.profile, 16);
  if (!(PROFILES as readonly string[]).includes(profile)) return { ok: false, error: "unknown profile" };
  const rid = str(b.rid, 32);
  if (!RID_RE.test(rid)) return { ok: false, error: "invalid rid" };
  const started = str(b.started, 40);
  if (!started) return { ok: false, error: "missing started" };
  if (typeof b.ok !== "boolean") return { ok: false, error: "ok must be boolean" };
  const rawStages = b.stages;
  if (typeof rawStages !== "object" || rawStages === null || Array.isArray(rawStages)) return { ok: false, error: "stages must be an object" };
  const entries = Object.entries(rawStages as Record<string, unknown>);
  if (entries.length === 0 || entries.length > 40) return { ok: false, error: "stages count out of range" };
  const stages: StageIn[] = [];
  for (const [name, s] of entries) {
    if (typeof s !== "object" || s === null) return { ok: false, error: "bad stage " + name.slice(0, 40) };
    const st = s as Record<string, unknown>;
    const status = str(st.status, 12);
    if (!STAGE_STATUS.has(status)) return { ok: false, error: "bad stage status" };
    stages.push({ name: name.slice(0, 40), status, seconds: num(st.seconds), detail: str(st.detail, 400) });
  }
  const commit = str(b.commit, 40);
  const id = `${rid}:${started}:${commit}:${profile}`;
  return {
    ok: true,
    value: {
      id, started, profile, rid, ok: b.ok, duration_s: num(b.duration_s), commit, branch: str(b.branch, 120),
      source: str(b.source, 24) || "unknown", dirty: b.dirty === true, sdk: str(b.sdk, 24), run_url: safeUrl(b.run_url), stages,
    },
  };
}

/** Only https URLs are kept (rendered as links in the admin UI). */
export function safeUrl(v: unknown): string {
  if (typeof v !== "string" || v.length > 300) return "";
  try { const u = new URL(v); return u.protocol === "https:" ? u.toString() : ""; } catch { return ""; }
}

export async function sha256Hex(s: string): Promise<string> {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(d)].map((x) => x.toString(16).padStart(2, "0")).join("");
}

export function newToken(): string {
  const a = crypto.getRandomValues(new Uint8Array(32));
  return "fsh_" + [...a].map((x) => x.toString(16).padStart(2, "0")).join("");
}

export function constantTimeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let r = 0;
  for (let i = 0; i < a.length; i++) r |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return r === 0;
}

export type RidState = "pass" | "fail" | "none";
/** Latest verdict per RID from newest-first rows (only full/release runs count as matrix evidence). */
export function matrixState(rows: { rid: string; ok: number; profile: string }[]): Record<string, RidState> {
  const out: Record<string, RidState> = Object.fromEntries(SIX_RIDS.map((r) => [r, "none" as RidState]));
  const seen = new Set<string>();
  for (const r of rows) {
    if (r.profile === "fast" || seen.has(r.rid) || !(r.rid in out)) continue;
    seen.add(r.rid);
    out[r.rid] = r.ok ? "pass" : "fail";
  }
  return out;
}

export function percentile(values: number[], p: number): number {
  if (values.length === 0) return 0;
  const s = [...values].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))]!;
}

export const DEFAULT_FLAGS = { ingestEnabled: true, publicStatus: true, retentionDays: 180, regressionPct: 30 } as const;
export type Flags = { ingestEnabled: boolean; publicStatus: boolean; retentionDays: number; regressionPct: number };

export function sanitizeFlags(input: unknown, current: Flags): Flags {
  const o = (typeof input === "object" && input !== null ? input : {}) as Record<string, unknown>;
  const clamp = (v: unknown, lo: number, hi: number, d: number) => (typeof v === "number" && Number.isFinite(v) ? Math.min(hi, Math.max(lo, Math.round(v))) : d);
  return {
    ingestEnabled: typeof o.ingestEnabled === "boolean" ? o.ingestEnabled : current.ingestEnabled,
    publicStatus: typeof o.publicStatus === "boolean" ? o.publicStatus : current.publicStatus,
    retentionDays: clamp(o.retentionDays, 7, 730, current.retentionDays),
    regressionPct: clamp(o.regressionPct, 5, 300, current.regressionPct),
  };
}
