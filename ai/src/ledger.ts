// The Durable Object that holds one budget (one caller, or "global") and the client the Worker uses to reach it.
// A Durable Object processes one request at a time, so reserve and refund are atomic without any locking here.

import { DurableObject } from "cloudflare:workers";
import type { Decision, Limits, Ledger } from "./budget";
import { LedgerCore } from "./ledger-core";

export class BudgetLedger extends DurableObject {
  private readonly core = new LedgerCore({
    get: <T>(k: string) => this.ctx.storage.get<T>(k),
    put: <T>(k: string, v: T) => this.ctx.storage.put(k, v),
  });
  reserve(limits: Limits, tokens: number, now: number): Promise<Decision> { return this.core.reserve(limits, tokens, now); }
  refund(tokens: number, now: number): Promise<void> { return this.core.refund(tokens, now); }
}

/** Reaches the ledger of a scope. Scope names are hashed by the caller of this function where they hold a person's identity. */
export function ledgerClient(ns: DurableObjectNamespace<BudgetLedger>): Ledger {
  const stub = (scope: string) => ns.get(ns.idFromName(scope));
  return {
    reserve: (scope, limits, tokens, now) => stub(scope).reserve(limits, tokens, now),
    refund: (scope, tokens, now) => stub(scope).refund(tokens, now),
  };
}
