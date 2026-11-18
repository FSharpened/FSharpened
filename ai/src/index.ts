import { ledgerClient, BudgetLedger } from "./ledger";
import { edgeCache } from "./cache";
import { handle, type Deps } from "./handler";

export { BudgetLedger };

interface Env {
  AI?: Ai;
  LEDGER: DurableObjectNamespace<BudgetLedger>;
  // Variables (wrangler.jsonc) and the optional TOKEN_SECRET secret, all strings.
  [key: string]: unknown;
}

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const vars: Record<string, string | undefined> = {};
    for (const [k, v] of Object.entries(env)) if (typeof v === "string") vars[k] = v;
    const deps: Deps = {
      env: vars,
      ai: env.AI as Deps["ai"],
      ledger: ledgerClient(env.LEDGER),
      cache: edgeCache(caches.default),
      now: () => Date.now(),
      // Metadata only (policy: never the prompt or the completion). Workers logs are sampled and retained by Cloudflare.
      log: (meta) => console.log(JSON.stringify(meta)),
    };
    return handle(req, deps);
  },
} satisfies ExportedHandler<Env>;
