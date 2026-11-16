import type { Context } from "hono";

export interface Env {
  DB: D1Database;
  SETTINGS: KVNamespace;
  ASSETS: Fetcher;
  ENVIRONMENT: string;
  ACCESS_TEAM_DOMAIN: string;
  ACCESS_AUD: string;
  RETENTION_DAYS: string;
}
export type AppCtx = Context<{ Bindings: Env; Variables: { actor: string } }>;

interface Jwk { kid: string; kty: string; n: string; e: string; alg?: string }
let jwksCache: { at: number; keys: Jwk[]; team: string } | null = null;

const b64urlToBytes = (s: string): Uint8Array => {
  const p = s.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(s.length / 4) * 4, "=");
  return Uint8Array.from(atob(p), (c) => c.charCodeAt(0));
};
const b64urlJson = (s: string): Record<string, unknown> => JSON.parse(new TextDecoder().decode(b64urlToBytes(s)));

async function getKeys(team: string): Promise<Jwk[]> {
  if (jwksCache && jwksCache.team === team && Date.now() - jwksCache.at < 600_000) return jwksCache.keys;
  const res = await fetch(`https://${team}/cdn-cgi/access/certs`);
  if (!res.ok) throw new Error("jwks fetch failed");
  const keys = ((await res.json()) as { keys: Jwk[] }).keys;
  jwksCache = { at: Date.now(), keys, team };
  return keys;
}

/** Verify a Cloudflare Access JWT (RS256). Returns the user email or null. Fails closed. */
export async function verifyAccessJwt(token: string, env: Env): Promise<string | null> {
  const team = env.ACCESS_TEAM_DOMAIN, aud = env.ACCESS_AUD;
  if (!team || !aud || !/^[a-z0-9.-]+$/i.test(team)) return null;
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  try {
    const header = b64urlJson(parts[0]!), payload = b64urlJson(parts[1]!);
    if (header.alg !== "RS256" || typeof header.kid !== "string") return null;
    const jwk = (await getKeys(team)).find((k) => k.kid === header.kid);
    if (!jwk) return null;
    const key = await crypto.subtle.importKey("jwk", { kty: jwk.kty, n: jwk.n, e: jwk.e, alg: "RS256", ext: true }, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
    const ok = await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, b64urlToBytes(parts[2]!), new TextEncoder().encode(parts[0] + "." + parts[1]));
    if (!ok) return null;
    const now = Math.floor(Date.now() / 1000);
    const audOk = Array.isArray(payload.aud) ? payload.aud.includes(aud) : payload.aud === aud;
    if (!audOk || payload.iss !== `https://${team}` || typeof payload.exp !== "number" || payload.exp < now) return null;
    if (typeof payload.nbf === "number" && payload.nbf > now + 30) return null;
    return typeof payload.email === "string" ? payload.email : "unknown";
  } catch {
    return null;
  }
}

/** Admin authentication: Cloudflare Access JWT in production; a fixed dev identity only when ENVIRONMENT=dev. */
export async function authenticateAdmin(c: AppCtx): Promise<string | null> {
  if (c.env.ENVIRONMENT === "dev") return "dev@localhost";
  const jwt = c.req.header("cf-access-jwt-assertion");
  return jwt ? verifyAccessJwt(jwt, c.env) : null;
}
