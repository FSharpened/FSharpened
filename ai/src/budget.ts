// Budgets: requests per minute and requests and tokens per day, for one caller and for everyone together.
// The arithmetic is a pure function over a small state value. A Durable Object (ledger.ts) holds the state and runs
// the function, so a reservation is atomic: a Durable Object handles one request at a time.

export interface Limits {
  requestsPerMinute: number;
  requestsPerDay: number;
  tokensPerDay: number;
}

export interface LedgerState {
  minute: number;          // window id: floor(now / 60 s)
  minuteRequests: number;
  day: number;             // window id: floor(now / 24 h), UTC
  dayRequests: number;
  dayTokens: number;
}

export const EMPTY: LedgerState = { minute: -1, minuteRequests: 0, day: -1, dayRequests: 0, dayTokens: 0 };

export type Denied = "requests-per-minute" | "requests-per-day" | "tokens-per-day";

export type Decision =
  | { ok: true; state: LedgerState }
  | { ok: false; state: LedgerState; reason: Denied; retryAfterSeconds: number };

const MINUTE = 60_000;
const DAY = 86_400_000;

/** Moves the state into the windows of `now`; counters of a finished window start again at zero. */
export function roll(s: LedgerState, now: number): LedgerState {
  const minute = Math.floor(now / MINUTE), day = Math.floor(now / DAY);
  return {
    minute, minuteRequests: s.minute === minute ? s.minuteRequests : 0,
    day, dayRequests: s.day === day ? s.dayRequests : 0, dayTokens: s.day === day ? s.dayTokens : 0,
  };
}

/** Takes one request and `tokens` from the budget, or says which limit stopped it and when to try again. Never takes part of it. */
export function reserve(prev: LedgerState, now: number, limits: Limits, tokens: number): Decision {
  const s = roll(prev, now);
  const toNextMinute = Math.max(1, Math.ceil(((s.minute + 1) * MINUTE - now) / 1000));
  const toNextDay = Math.max(1, Math.ceil(((s.day + 1) * DAY - now) / 1000));
  if (s.minuteRequests + 1 > limits.requestsPerMinute) return { ok: false, state: s, reason: "requests-per-minute", retryAfterSeconds: toNextMinute };
  if (s.dayRequests + 1 > limits.requestsPerDay) return { ok: false, state: s, reason: "requests-per-day", retryAfterSeconds: toNextDay };
  if (s.dayTokens + tokens > limits.tokensPerDay) return { ok: false, state: s, reason: "tokens-per-day", retryAfterSeconds: toNextDay };
  return { ok: true, state: { ...s, minuteRequests: s.minuteRequests + 1, dayRequests: s.dayRequests + 1, dayTokens: s.dayTokens + tokens } };
}

/** Gives a reservation back (the model was never reached). Only counters of the current windows go down, never below zero. */
export function refund(prev: LedgerState, now: number, tokens: number): LedgerState {
  const s = roll(prev, now);
  return {
    ...s,
    minuteRequests: Math.max(0, s.minuteRequests - 1),
    dayRequests: Math.max(0, s.dayRequests - 1),
    dayTokens: Math.max(0, s.dayTokens - tokens),
  };
}

const int = (v: string | undefined, fallback: number, max: number): number => {
  const n = v === undefined ? NaN : Number(v);
  return Number.isInteger(n) && n >= 0 ? Math.min(n, max) : fallback;
};

export interface BudgetConfig { caller: Limits; global: Limits }

/** Budgets come from Worker variables. A missing or malformed number falls back to the small default, never to "unlimited". */
export function budgetsFrom(env: Record<string, string | undefined>): BudgetConfig {
  return {
    caller: {
      requestsPerMinute: int(env.CALLER_REQUESTS_PER_MINUTE, 6, 600),
      requestsPerDay: int(env.CALLER_REQUESTS_PER_DAY, 200, 100_000),
      tokensPerDay: int(env.CALLER_TOKENS_PER_DAY, 40_000, 10_000_000),
    },
    global: {
      requestsPerMinute: int(env.GLOBAL_REQUESTS_PER_MINUTE, 60, 10_000),
      requestsPerDay: int(env.GLOBAL_REQUESTS_PER_DAY, 2_000, 1_000_000),
      tokensPerDay: int(env.GLOBAL_TOKENS_PER_DAY, 300_000, 100_000_000),
    },
  };
}

/** What the Worker needs from a ledger; the Durable Object client and the test double both provide it. */
export interface Ledger {
  reserve(scope: string, limits: Limits, tokens: number, now: number): Promise<Decision>;
  refund(scope: string, tokens: number, now: number): Promise<void>;
}

export type Admission = { ok: true } | { ok: false; reason: string; retryAfterSeconds: number };

/**
 * Admits a request against the caller's budget and then the global one. If the global budget refuses, the caller's
 * reservation is given back, so a refused request costs nobody anything.
 */
export async function admit(ledger: Ledger, callerScope: string, cfg: BudgetConfig, tokens: number, now: number): Promise<Admission> {
  const c = await ledger.reserve("caller:" + callerScope, cfg.caller, tokens, now);
  if (!c.ok) return { ok: false, reason: c.reason, retryAfterSeconds: c.retryAfterSeconds };
  const g = await ledger.reserve("global", cfg.global, tokens, now);
  if (!g.ok) {
    await ledger.refund("caller:" + callerScope, tokens, now);
    return { ok: false, reason: "global-" + g.reason, retryAfterSeconds: g.retryAfterSeconds };
  }
  return { ok: true };
}

export async function release(ledger: Ledger, callerScope: string, tokens: number, now: number): Promise<void> {
  await ledger.refund("caller:" + callerScope, tokens, now);
  await ledger.refund("global", tokens, now);
}
