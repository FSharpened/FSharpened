import { describe, expect, it } from "vitest";
import { EMPTY, admit, budgetsFrom, refund, release, reserve, roll, type Decision, type Ledger, type LedgerState, type Limits } from "../src/budget";
import { memoryLedger } from "./helpers";
import { bearer, callerHandle, signToken, verifyToken } from "../src/auth";

const limits: Limits = { requestsPerMinute: 3, requestsPerDay: 5, tokensPerDay: 1000 };
const T0 = Date.UTC(2026, 10, 18, 12, 0, 30);

describe("budget arithmetic", () => {
  it("admits until the per-minute limit, then names it", () => {
    let s: LedgerState = EMPTY;
    for (let i = 0; i < 3; i++) { const d = reserve(s, T0, limits, 10); expect(d.ok).toBe(true); s = d.state; }
    const d = reserve(s, T0, limits, 10);
    expect(d).toMatchObject({ ok: false, reason: "requests-per-minute", retryAfterSeconds: 30 });
  });
  it("starts a new minute window on its own", () => {
    let s: LedgerState = EMPTY;
    for (let i = 0; i < 3; i++) s = reserve(s, T0, limits, 10).state;
    expect(reserve(s, T0 + 60_000, limits, 10).ok).toBe(true);
  });
  it("enforces the daily request limit across minutes", () => {
    let s: LedgerState = EMPTY;
    for (let i = 0; i < 5; i++) { const d = reserve(s, T0 + i * 60_000, limits, 1); expect(d.ok).toBe(true); s = d.state; }
    const d = reserve(s, T0 + 6 * 60_000, limits, 1);
    expect(d).toMatchObject({ ok: false, reason: "requests-per-day" });
    expect((d as Extract<Decision, { ok: false }>).retryAfterSeconds).toBeGreaterThan(60);
  });
  it("enforces the daily token limit and never takes a partial amount", () => {
    const s = reserve(EMPTY, T0, limits, 900).state;
    const d = reserve(s, T0, limits, 101);
    expect(d).toMatchObject({ ok: false, reason: "tokens-per-day" });
    expect(d.state.dayTokens).toBe(900);
    expect(reserve(s, T0, limits, 100).ok).toBe(true);
  });
  it("resets everything at the next UTC day", () => {
    const s = reserve(EMPTY, T0, limits, 1000).state;
    expect(reserve(s, T0, limits, 1).ok).toBe(false);
    const next = Date.UTC(2026, 10, 19, 0, 0, 1);
    expect(roll(s, next)).toMatchObject({ dayTokens: 0, dayRequests: 0, minuteRequests: 0 });
    expect(reserve(s, next, limits, 1000).ok).toBe(true);
  });
  it("refund returns a reservation and never goes below zero", () => {
    const s = reserve(EMPTY, T0, limits, 100).state;
    const back = refund(s, T0, 100);
    expect(back).toMatchObject({ minuteRequests: 0, dayRequests: 0, dayTokens: 0 });
    expect(refund(back, T0, 500).dayTokens).toBe(0);
  });
  it("a refund from an earlier window does not touch the new one", () => {
    const s = reserve(EMPTY, T0, limits, 100).state;
    expect(refund(s, T0 + 86_400_000, 100).dayTokens).toBe(0);
  });
});

describe("budget variables", () => {
  it("uses the small defaults for missing or malformed values, never unlimited", () => {
    const c = budgetsFrom({ CALLER_REQUESTS_PER_MINUTE: "lots", CALLER_TOKENS_PER_DAY: "-1", GLOBAL_TOKENS_PER_DAY: "1.5" });
    expect(c.caller.requestsPerMinute).toBe(6);
    expect(c.caller.tokensPerDay).toBe(40_000);
    expect(c.global.tokensPerDay).toBe(300_000);
  });
  it("caps absurd values", () => expect(budgetsFrom({ GLOBAL_REQUESTS_PER_DAY: "99999999999" }).global.requestsPerDay).toBe(1_000_000));
  it("accepts a zero budget (a way to stop spending without the kill switch)", () => expect(budgetsFrom({ GLOBAL_TOKENS_PER_DAY: "0" }).global.tokensPerDay).toBe(0));
});

describe("ledger core", () => {
  it("never over-admits when many requests arrive at once (the double serialises per scope like a Durable Object)", async () => {
    const ledger = memoryLedger();
    const results = await Promise.all(Array.from({ length: 10 }, () => ledger.reserve("global", { requestsPerMinute: 4, requestsPerDay: 100, tokensPerDay: 1000 }, 1, T0)));
    expect(results.filter((r) => r.ok).length).toBe(4);
  });
});

describe("admit", () => {
  const cfg = { caller: { requestsPerMinute: 2, requestsPerDay: 10, tokensPerDay: 500 }, global: { requestsPerMinute: 3, requestsPerDay: 10, tokensPerDay: 500 } };
  it("charges the caller and the global budget", async () => {
    const l = memoryLedger();
    expect((await admit(l, "a", cfg, 100, T0)).ok).toBe(true);
    expect((l.state.get("caller:a/state") as LedgerState).dayTokens).toBe(100);
    expect((l.state.get("global/state") as LedgerState).dayTokens).toBe(100);
  });
  it("stops one caller without touching the global budget", async () => {
    const l = memoryLedger();
    await admit(l, "a", cfg, 10, T0); await admit(l, "a", cfg, 10, T0);
    const r = await admit(l, "a", cfg, 10, T0);
    expect(r).toMatchObject({ ok: false, reason: "requests-per-minute" });
    expect((l.state.get("global/state") as LedgerState).dayRequests).toBe(2);
  });
  it("gives the caller's reservation back when the global budget refuses", async () => {
    const l = memoryLedger();
    await admit(l, "a", cfg, 10, T0); await admit(l, "b", cfg, 10, T0); await admit(l, "c", cfg, 10, T0);
    const r = await admit(l, "d", cfg, 10, T0);
    expect(r).toMatchObject({ ok: false, reason: "global-requests-per-minute" });
    expect((l.state.get("caller:d/state") as LedgerState).dayRequests).toBe(0);
  });
  it("release returns both reservations", async () => {
    const l = memoryLedger();
    await admit(l, "a", cfg, 100, T0);
    await release(l, "a", 100, T0);
    expect((l.state.get("caller:a/state") as LedgerState).dayTokens).toBe(0);
    expect((l.state.get("global/state") as LedgerState).dayTokens).toBe(0);
  });
});

describe("tokens", () => {
  const secret = "test-secret-not-real";
  const now = 1_800_000_000;
  it("verifies a token it signed", async () => {
    expect(await verifyToken(secret, await signToken(secret, "user_1", now + 60), now)).toBe("user_1");
  });
  it("rejects expired, tampered, wrong-secret and malformed tokens", async () => {
    const t = await signToken(secret, "user_1", now + 60);
    expect(await verifyToken(secret, t, now + 61)).toBeNull();
    expect(await verifyToken(secret, t.replace("user_1", "user_2"), now)).toBeNull();
    expect(await verifyToken("other", t, now)).toBeNull();
    expect(await verifyToken(secret, "a.b", now)).toBeNull();
    expect(await verifyToken(secret, "bad id.99999999999.AAAA", now)).toBeNull();
    expect(await verifyToken(secret, t + "x".repeat(400), now)).toBeNull();
    expect(await verifyToken("", t, now)).toBeNull();
  });
  it("reads a bearer header strictly", () => {
    expect(bearer("Bearer abc.def-1_2")).toBe("abc.def-1_2");
    expect(bearer("Basic abc")).toBeNull();
    expect(bearer("Bearer a b")).toBeNull();
    expect(bearer(null)).toBeNull();
  });
  it("hands out a stable handle that does not contain the identity", async () => {
    const h = await callerHandle("203.0.113.9");
    expect(h).toMatch(/^[0-9a-f]{24}$/);
    expect(h).toBe(await callerHandle("203.0.113.9"));
    expect(h).not.toBe(await callerHandle("203.0.113.10"));
  });
});
