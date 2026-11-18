// Who may call. Two layers, both optional to configure but never silently open:
//  1. an allowed Origin (browsers send it and cannot forge it; a script can, so this alone is not a secret);
//  2. when the TOKEN_SECRET secret is set, a signed bearer token `<id>.<expiry>.<signature>` (HMAC-SHA256) that the
//     hub (or any trusted service holding the same secret) mints. The secret is a Worker secret, never in this repository.
// Abuse is bounded either way by the budgets (budget.ts), which are per caller and global.

const ID = /^[A-Za-z0-9_-]{1,64}$/;
const enc = new TextEncoder();

const b64url = (bytes: ArrayBuffer): string =>
  btoa(String.fromCharCode(...new Uint8Array(bytes))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const fromB64url = (s: string): Uint8Array<ArrayBuffer> => {
  const p = s.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(s.length / 4) * 4, "=");
  return Uint8Array.from(atob(p), (c) => c.charCodeAt(0));
};

const hmacKey = (secret: string, usage: "sign" | "verify") =>
  crypto.subtle.importKey("raw", enc.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, [usage]);

/** Mints a token. Used by tests and by whatever trusted service issues tokens; the Worker itself only verifies. */
export async function signToken(secret: string, id: string, expiresAtSeconds: number): Promise<string> {
  const body = `${id}.${expiresAtSeconds}`;
  return `${body}.${b64url(await crypto.subtle.sign("HMAC", await hmacKey(secret, "sign"), enc.encode(body)))}`;
}

/** The caller id of a valid, unexpired token, or null. Constant-time signature check; fails closed on any malformed input. */
export async function verifyToken(secret: string, token: string, nowSeconds: number): Promise<string | null> {
  if (!secret || token.length > 300) return null;
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  const [id, exp, sig] = parts as [string, string, string];
  if (!ID.test(id) || !/^[0-9]{1,12}$/.test(exp) || Number(exp) < nowSeconds) return null;
  try {
    const ok = await crypto.subtle.verify("HMAC", await hmacKey(secret, "verify"), fromB64url(sig), enc.encode(`${id}.${exp}`));
    return ok ? id : null;
  } catch {
    return null;
  }
}

export function bearer(header: string | null): string | null {
  const m = header ? /^Bearer ([A-Za-z0-9._-]+)$/.exec(header) : null;
  return m ? m[1]! : null;
}

/** A short, stable, non-reversible handle for a caller: used as the budget scope and in logs instead of an address or an id. */
export async function callerHandle(identity: string): Promise<string> {
  const d = await crypto.subtle.digest("SHA-256", enc.encode("fsh-ai-caller:" + identity));
  return [...new Uint8Array(d)].slice(0, 12).map((x) => x.toString(16).padStart(2, "0")).join("");
}
