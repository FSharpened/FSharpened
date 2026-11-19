// Live checking as you type (ADR-0025): when the person has turned it on, the compiler check runs on the existing gateway
// path after a pause in typing. This module only decides WHEN a check may run; what a check does is the caller's `task`.
//
//   debounce      a check starts `delay` ms after the last request, never while the person is still typing
//   cancellation  a newer request aborts the check in flight (its AbortSignal) and replaces the one waiting
//   budget        at most `budget` checks start in any `windowMs` window, well below the gateway's own limit
//                 (RUN_LIMITER, 12 a minute per client, shared with the Run button), so typing cannot starve Run
//   backoff       a rate-limited answer pauses checking for the time the server asked for, doubling on repeats
//   dedupe        text that was already checked (same key) is never sent again
//
// Timers and the clock are injected so the tests run on a fake clock.

export const LIVE_KEY = "fsharpened.ide.livecheck.v1";

/** Whether live checking is on. Off unless the person turned it on; a blocked or missing store means off. */
export function readLiveEnabled(storage) {
  try { return storage.getItem(LIVE_KEY) === "1"; } catch { return false; }
}
export function writeLiveEnabled(storage, on) {
  try { if (on) storage.setItem(LIVE_KEY, "1"); else storage.removeItem(LIVE_KEY); } catch { /* the setting just does not persist */ }
}

/**
 * @param {object} o
 * @param {(state: {phase: string, message: string, resumeAt?: number}) => void} [o.onState] called whenever the phase changes
 *   phase: "idle" | "waiting" (debouncing or waiting for budget) | "checking" | "paused" | "error"
 */
export function createLiveCheck({ delay = 1500, budget = 4, windowMs = 60000, pauseMs = 60000, maxPauseMs = 300000, now = Date.now, setTimer = setTimeout, clearTimer = clearTimeout, onState = () => {} } = {}) {
  let timer = null, ctrl = null, pending = null, lastKey = null, enabled = false, pausedUntil = 0, strikes = 0, phase = "idle";
  const starts = [];
  let epoch = 0;

  function set(next, message, extra) {
    phase = next;
    onState({ phase: next, message, ...extra });
  }
  const trim = () => { const cut = now() - windowMs; while (starts.length && starts[0] <= cut) starts.shift(); };

  function disarm() { if (timer !== null) { clearTimer(timer); timer = null; } }
  function arm(ms) { disarm(); timer = setTimer(fire, Math.max(0, ms)); }

  function fire() {
    timer = null;
    if (!enabled || !pending) return;
    const t = now();
    if (t < pausedUntil) { arm(pausedUntil - t); return; }
    trim();
    if (starts.length >= budget) {
      // wait for the oldest start to leave the window instead of dropping the request
      const wait = starts[0] + windowMs - t;
      set("waiting", "Live check is waiting its turn so it does not use up your runs", { resumeAt: t + wait });
      arm(wait + 5);
      return;
    }
    const job = pending;
    pending = null;
    starts.push(t);
    const mine = ++epoch;
    ctrl = new AbortController();
    const signal = ctrl.signal;
    set("checking", "Checking…");
    Promise.resolve()
      .then(() => job.task(signal))
      .then((res) => {
        if (mine !== epoch) return;                   // replaced or cancelled meanwhile
        ctrl = null;
        if (res && res.rateLimited) {
          strikes += 1;
          const wait = Math.min(maxPauseMs, Math.max(res.retryAfterMs || 0, pauseMs * 2 ** (strikes - 1)));
          pausedUntil = now() + wait;
          set("paused", `Live check paused for ${Math.ceil(wait / 1000)} s (the service asked for a rest)`, { resumeAt: pausedUntil });
          if (pending) arm(wait);
          return;
        }
        strikes = 0;
        if (res && res.checked !== false) lastKey = job.key;
        set("idle", (res && res.message) || "Checked");
        if (pending) arm(delay);
      })
      .catch((e) => {
        if (mine !== epoch) return;
        ctrl = null;
        if (e && e.name === "AbortError") { set("idle", "Check cancelled"); return; }
        set("error", "Live check could not reach the service: " + (e && e.message ? e.message : "network error"));
      });
  }

  return {
    /** Ask for a check of the text identified by `key`. Replaces any request still waiting and aborts one in flight. */
    request(key, task) {
      if (!enabled) return;
      if (key === lastKey && !ctrl) { pending = null; disarm(); if (phase !== "idle" && phase !== "paused") set("idle", "Checked"); return; }
      pending = { key, task };
      if (ctrl) { epoch += 1; ctrl.abort(); ctrl = null; }   // the answer to the older text would only be thrown away
      set(now() < pausedUntil ? "paused" : "waiting", now() < pausedUntil ? "Live check is paused" : "Waiting for you to stop typing…", now() < pausedUntil ? { resumeAt: pausedUntil } : {});
      arm(Math.max(delay, pausedUntil - now()));
    },
    /** Stop everything: a Run is starting, the project changed, or the check was switched off. Keeps what was already checked. */
    cancel() {
      pending = null; disarm();
      if (ctrl) { epoch += 1; ctrl.abort(); ctrl = null; }
      if (phase !== "idle" && phase !== "paused") set("idle", "");
    },
    /** Forget which text was checked, so the same text may be checked again (for example after the options changed). */
    forget() { lastKey = null; },
    setEnabled(on) {
      enabled = !!on;
      if (!enabled) { this.cancel(); lastKey = null; strikes = 0; pausedUntil = 0; set("idle", ""); }
    },
    get enabled() { return enabled; },
    get phase() { return phase; },
    get inFlight() { return ctrl !== null; },
    get pausedUntil() { return pausedUntil; },
  };
}

/** `Retry-After` (seconds or a date) in milliseconds, or 0 when absent. */
export function retryAfterMs(header, nowMs = Date.now()) {
  if (!header) return 0;
  const secs = Number(header);
  if (Number.isFinite(secs)) return Math.max(0, secs * 1000);
  const at = Date.parse(header);
  return Number.isFinite(at) ? Math.max(0, at - nowMs) : 0;
}
