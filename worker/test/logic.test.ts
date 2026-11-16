import { describe, expect, it } from "vitest";
import { isAllowed, parseAllowlist, parseCookies, signSession, verifySession, constantTimeEqual, matrixState, newToken, percentile, safeUrl, sanitizeFlags, sha256Hex, validateIngest, DEFAULT_FLAGS } from "../src/logic";

const good = () => ({
  ok: true, profile: "full", rid: "win-x64", started: "20261116-130614", commit: "abc1234", duration_s: 41.8, sdk: "9.0.200",
  stages: { build: { status: "pass", seconds: 20, detail: "" }, test: { status: "cached", seconds: 0.1, detail: "9 passed" } },
});

describe("validateIngest", () => {
  it("accepts a real summary and derives a stable id", () => {
    const r = validateIngest(good());
    expect(r.ok).toBe(true);
    if (r.ok) { expect(r.value.id).toBe("win-x64:20261116-130614:abc1234:full"); expect(r.value.stages).toHaveLength(2); }
  });
  it("rejects bad shapes", () => {
    expect(validateIngest(null).ok).toBe(false);
    expect(validateIngest([]).ok).toBe(false);
    expect(validateIngest({ ...good(), profile: "evil" }).ok).toBe(false);
    expect(validateIngest({ ...good(), rid: "../x" }).ok).toBe(false);
    expect(validateIngest({ ...good(), ok: "yes" }).ok).toBe(false);
    expect(validateIngest({ ...good(), stages: {} }).ok).toBe(false);
    expect(validateIngest({ ...good(), stages: { a: { status: "weird", seconds: 1 } } }).ok).toBe(false);
  });
  it("clamps sizes and drops non-https run urls", () => {
    const r = validateIngest({ ...good(), branch: "x".repeat(999), run_url: "javascript:alert(1)" });
    expect(r.ok && r.value.branch.length).toBe(120);
    expect(r.ok && r.value.run_url).toBe("");
  });
  it("caps stage count", () => {
    const stages = Object.fromEntries(Array.from({ length: 41 }, (_, i) => ["s" + i, { status: "pass", seconds: 1 }]));
    expect(validateIngest({ ...good(), stages }).ok).toBe(false);
  });
});

describe("helpers", () => {
  it("safeUrl", () => { expect(safeUrl("https://github.com/x")).toContain("https://"); expect(safeUrl("http://x.dev")).toBe(""); expect(safeUrl(5)).toBe(""); });
  it("hashes and tokens", async () => {
    expect(await sha256Hex("abc")).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    const t = newToken(); expect(t).toMatch(/^fsh_[0-9a-f]{64}$/); expect(newToken()).not.toBe(t);
  });
  it("constantTimeEqual", () => { expect(constantTimeEqual("a", "a")).toBe(true); expect(constantTimeEqual("a", "b")).toBe(false); expect(constantTimeEqual("a", "ab")).toBe(false); });
  it("percentile", () => { expect(percentile([], 50)).toBe(0); expect(percentile([1, 2, 3, 4], 50)).toBe(3); expect(percentile([5], 95)).toBe(5); });
  it("matrixState ignores fast runs and uses newest per rid", () => {
    const m = matrixState([
      { rid: "win-x64", ok: 0, profile: "fast" }, { rid: "win-x64", ok: 1, profile: "full" }, { rid: "win-x64", ok: 0, profile: "full" },
      { rid: "linux-x64", ok: 0, profile: "full" }, { rid: "mars-x1", ok: 1, profile: "full" },
    ]);
    expect(m["win-x64"]).toBe("pass"); expect(m["linux-x64"]).toBe("fail"); expect(m["osx-arm64"]).toBe("none"); expect("mars-x1" in m).toBe(false);
  });
  it("sanitizeFlags clamps and keeps current on junk", () => {
    const cur = { ...DEFAULT_FLAGS };
    expect(sanitizeFlags({ retentionDays: 1, regressionPct: 9999, ingestEnabled: "no" }, cur)).toEqual({ ...cur, retentionDays: 7, regressionPct: 300 });
    expect(sanitizeFlags(null, cur)).toEqual(cur);
  });
});

describe("sessions and allowlist", () => {
  const secret = "x".repeat(40);
  it("signs and verifies, rejects tamper, expiry, wrong secret and short secrets", async () => {
    const tok = await signSession(secret, "FSharpened", 60, 1000);
    expect(await verifySession(secret, tok, 1030)).toBe("FSharpened");
    expect(await verifySession(secret, tok, 1061)).toBeNull();
    expect(await verifySession("y".repeat(40), tok, 1030)).toBeNull();
    expect(await verifySession("short", tok, 1030)).toBeNull();
    const [p, s] = tok.split(".");
    const forged = btoa(JSON.stringify({ u: "mallory", exp: 9e9 })).replace(/=+$/, "");
    expect(await verifySession(secret, forged + "." + s, 1030)).toBeNull();
    expect(await verifySession(secret, p + "." + s + ".x", 1030)).toBeNull();
    expect(await verifySession(secret, "garbage", 1030)).toBeNull();
  });
  it("parses cookies and allowlists case-insensitively", () => {
    expect(parseCookies("a=1; fsh_session=abc.def; b=2")["fsh_session"]).toBe("abc.def");
    expect(parseCookies(undefined)).toEqual({});
    const l = parseAllowlist(" FSharpened , bad name!,Loopy-Luci ");
    expect(l).toEqual(["fsharpened", "loopy-luci"]);
    expect(isAllowed("FSHARPENED", l)).toBe(true);
    expect(isAllowed("eve", l)).toBe(false);
    expect(isAllowed("x", [])).toBe(false);
  });
});
