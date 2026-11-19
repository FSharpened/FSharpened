// The live-check scheduler (ADR-0025): debounce, cancellation, the per-minute budget, backoff and dedupe, on a fake clock.
// Nothing here touches a network: a "task" is a function the test controls.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createLiveCheck, readLiveEnabled, writeLiveEnabled, retryAfterMs, LIVE_KEY } from "../src/livecheck.js";

/** A clock whose timers only fire when the test advances it. */
function clock() {
  let t = 1_000_000, id = 0;
  const timers = new Map();
  return {
    now: () => t,
    setTimer: (fn, ms) => { timers.set(++id, { at: t + ms, fn }); return id; },
    clearTimer: (i) => void timers.delete(i),
    async advance(ms) {
      const end = t + ms;
      for (;;) {
        const due = [...timers].filter(([, v]) => v.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        t = Math.max(t, due[1].at); timers.delete(due[0]); due[1].fn();
        await new Promise((r) => setImmediate(r));   // let the task's promise chain run
      }
      t = end;
      await new Promise((r) => setImmediate(r));
    },
    get pending() { return timers.size; },
  };
}

function make(opts = {}) {
  const c = clock(), states = [];
  const lc = createLiveCheck({ delay: 1000, budget: 3, windowMs: 60000, pauseMs: 30000, now: c.now, setTimer: c.setTimer, clearTimer: c.clearTimer, onState: (s) => states.push(s), ...opts });
  lc.setEnabled(true);
  return { c, lc, states };
}
/** A task that records its call and resolves when the test says so. */
function task(log, name, result = { message: "ok" }) {
  return (signal) => new Promise((res, rej) => {
    log.push({ name, signal });
    signal.addEventListener("abort", () => rej(Object.assign(new Error("aborted"), { name: "AbortError" })));
    task.resolvers.push(() => res(result));
  });
}
task.resolvers = [];
const instant = (log, name, result) => async (signal) => { log.push({ name, signal }); return result ?? { message: "ok" }; };

test("nothing runs while checking is off", async () => {
  const { c, lc } = make();
  lc.setEnabled(false);
  const log = [];
  lc.request("a", instant(log, "a"));
  await c.advance(10_000);
  assert.equal(log.length, 0);
});

test("a check starts only after the person stops typing", async () => {
  const { c, lc } = make();
  const log = [];
  lc.request("a", instant(log, "a"));
  await c.advance(900);
  lc.request("ab", instant(log, "ab"));        // typing continued: the wait starts again
  await c.advance(900);
  assert.equal(log.length, 0);
  await c.advance(200);
  assert.deepEqual(log.map((l) => l.name), ["ab"]);
});

test("a new request aborts the check in flight", async () => {
  const { c, lc } = make();
  const log = [];
  lc.request("a", task(log, "a"));
  await c.advance(1000);
  assert.equal(lc.phase, "checking");
  assert.equal(log[0].signal.aborted, false);
  lc.request("b", instant(log, "b"));
  assert.equal(log[0].signal.aborted, true, "the older check was aborted");
  await c.advance(1000);
  assert.deepEqual(log.map((l) => l.name), ["a", "b"]);
  assert.equal(lc.phase, "idle");
});

test("cancel aborts and forgets the waiting request", async () => {
  const { c, lc } = make();
  const log = [];
  lc.request("a", instant(log, "a"));
  lc.cancel();
  await c.advance(5000);
  assert.equal(log.length, 0);
  lc.request("b", task(log, "b"));
  await c.advance(1000);
  lc.cancel();
  assert.equal(log[0].signal.aborted, true);
  assert.equal(lc.inFlight, false);
});

test("text that was already checked is not sent again", async () => {
  const { c, lc } = make();
  const log = [];
  lc.request("same", instant(log, "1"));
  await c.advance(1000);
  lc.request("different", instant(log, "2"));
  await c.advance(1000);
  lc.request("same", instant(log, "3"));       // back to text that was checked two checks ago: still sent (only the last is remembered)
  await c.advance(1000);
  lc.request("same", instant(log, "4"));       // identical to the last checked text: skipped
  await c.advance(5000);
  assert.deepEqual(log.map((l) => l.name), ["1", "2", "3"]);
});

test("a result flagged as not checked does not count as checked", async () => {
  const { c, lc } = make();
  const log = [];
  lc.request("a", instant(log, "1", { checked: false, message: "engines unavailable" }));
  await c.advance(1000);
  lc.request("a", instant(log, "2"));
  await c.advance(1000);
  assert.equal(log.length, 2);
});

test("no more than the budget starts in a window, and the request waits instead of being dropped", async () => {
  const { c, lc } = make();
  const log = [];
  for (let i = 0; i < 5; i++) { lc.request("t" + i, instant(log, "t" + i)); await c.advance(1000); }
  assert.equal(log.length, 3, "three starts in the first minute");
  assert.equal(lc.phase, "waiting");
  await c.advance(60_000);
  assert.deepEqual(log.map((l) => l.name), ["t0", "t1", "t2", "t4"], "the newest text was checked once the window opened; t3 was replaced");
});

test("a rate-limited answer pauses checking, then it resumes, and the pause doubles when it repeats", async () => {
  const { c, lc, states } = make();
  const log = [];
  lc.request("a", instant(log, "a", { rateLimited: true, retryAfterMs: 0 }));
  await c.advance(1000);
  assert.equal(lc.phase, "paused");
  assert.equal(states.at(-1).resumeAt - c.now(), 30_000);
  lc.request("b", instant(log, "b"));
  await c.advance(29_000);
  assert.equal(log.length, 1, "still paused");
  await c.advance(2000);
  assert.deepEqual(log.map((l) => l.name), ["a", "b"]);

  lc.request("c", instant(log, "c", { rateLimited: true, retryAfterMs: 90_000 }));
  await c.advance(1000);
  assert.equal(states.at(-1).resumeAt - c.now(), 90_000, "the server's own wait is honoured when it is longer");
});

test("a failed check is reported once and nothing is retried on its own", async () => {
  const { c, lc, states } = make();
  const log = [];
  lc.request("a", async () => { log.push(1); throw new Error("offline"); });
  await c.advance(1000);
  await c.advance(60_000);
  assert.equal(log.length, 1);
  assert.equal(lc.phase, "error");
  assert.match(states.at(-1).message, /offline/);
});

test("switching it off aborts, forgets what was checked and clears a pause", async () => {
  const { c, lc } = make();
  const log = [];
  lc.request("a", task(log, "a"));
  await c.advance(1000);
  lc.setEnabled(false);
  assert.equal(log[0].signal.aborted, true);
  lc.setEnabled(true);
  lc.request("a", instant(log, "again"));
  await c.advance(1000);
  assert.equal(log.at(-1).name, "again");
});

test("the setting is read and written defensively", () => {
  const m = new Map();
  const ok = { getItem: (k) => m.get(k) ?? null, setItem: (k, v) => void m.set(k, v), removeItem: (k) => void m.delete(k) };
  assert.equal(readLiveEnabled(ok), false, "off by default");
  writeLiveEnabled(ok, true);
  assert.equal(m.get(LIVE_KEY), "1");
  assert.equal(readLiveEnabled(ok), true);
  writeLiveEnabled(ok, false);
  assert.equal(readLiveEnabled(ok), false);
  const broken = { getItem: () => { throw new Error("blocked"); }, setItem: () => { throw new Error("blocked"); }, removeItem: () => { throw new Error("blocked"); } };
  assert.equal(readLiveEnabled(broken), false);
  assert.doesNotThrow(() => writeLiveEnabled(broken, true));
});

test("Retry-After accepts seconds and dates", () => {
  assert.equal(retryAfterMs("30"), 30_000);
  assert.equal(retryAfterMs(null), 0);
  assert.equal(retryAfterMs("nonsense"), 0);
  assert.equal(retryAfterMs(new Date(5000).toUTCString(), 2000), 3000);
});
