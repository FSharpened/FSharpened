// The logic of the budget Durable Object, free of the Workers runtime so tests can run it with any storage.
import { EMPTY, refund, reserve, type Decision, type LedgerState, type Limits } from "./budget";

const KEY = "state";

/** The storage this class needs; the test double implements the same two calls. */
export interface Storage {
  get<T>(key: string): Promise<T | undefined>;
  put<T>(key: string, value: T): Promise<void>;
}

/** The logic of the Durable Object, free of the runtime so it can be tested. */
export class LedgerCore {
  constructor(private readonly storage: Storage) {}
  async reserve(limits: Limits, tokens: number, now: number): Promise<Decision> {
    const state = (await this.storage.get<LedgerState>(KEY)) ?? EMPTY;
    const d = reserve(state, now, limits, tokens);
    await this.storage.put(KEY, d.state);
    return d;
  }
  async refund(tokens: number, now: number): Promise<void> {
    const state = (await this.storage.get<LedgerState>(KEY)) ?? EMPTY;
    await this.storage.put(KEY, refund(state, now, tokens));
  }
}
