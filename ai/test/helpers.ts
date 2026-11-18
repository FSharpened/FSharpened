import type { Ledger } from "../src/budget";
import { LedgerCore } from "../src/ledger-core";

/**
 * The real ledger logic over in-memory storage, one instance per scope. Like a Durable Object it runs one call at a
 * time per scope (the queue below stands in for the runtime's input gate), so concurrent requests cannot over-admit.
 */
export function memoryLedger(): Ledger & { state: Map<string, unknown>; calls: string[] } {
  const state = new Map<string, unknown>();
  const cores = new Map<string, LedgerCore>();
  const queues = new Map<string, Promise<unknown>>();
  const calls: string[] = [];
  const core = (scope: string) => {
    let c = cores.get(scope);
    if (!c) {
      c = new LedgerCore({ get: async <T>(k: string) => state.get(scope + "/" + k) as T | undefined, put: async <T>(k: string, v: T) => { state.set(scope + "/" + k, v); } });
      cores.set(scope, c);
    }
    return c;
  };
  const serial = <T>(scope: string, f: () => Promise<T>): Promise<T> => {
    const next = (queues.get(scope) ?? Promise.resolve()).then(f, f);
    queues.set(scope, next.catch(() => undefined));
    return next;
  };
  return {
    state, calls,
    reserve: (scope, l, tokens, now) => { calls.push("reserve:" + scope); return serial(scope, () => core(scope).reserve(l, tokens, now)); },
    refund: (scope, tokens, now) => { calls.push("refund:" + scope); return serial(scope, () => core(scope).refund(tokens, now)); },
  };
}
