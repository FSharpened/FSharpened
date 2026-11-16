// A tiny, dependency-free harness that runs a real service worker script (no copy of its logic) against a mocked
// Cache API and fetch, in a Node vm context. Shared by the site and hub worker tests.
import fs from "node:fs";
import vm from "node:vm";

export const ORIGIN = "https://site.test";

export class FakeCache {
  constructor() { this.entries = new Map(); }
  static key(r) { return new URL(typeof r === "string" ? r : r.url, ORIGIN).href; }
  async put(r, res) { this.entries.set(FakeCache.key(r), { status: res.status, headers: [...res.headers], body: await res.arrayBuffer() }); }
  async match(r) { const e = this.entries.get(FakeCache.key(r)); return e ? new Response(e.body, { status: e.status, headers: e.headers }) : undefined; }
  async keys() { return [...this.entries.keys()].map((u) => new Request(u)); }
  async delete(r) { return this.entries.delete(FakeCache.key(r)); }
}
export class FakeCaches {
  constructor() { this.stores = new Map(); }
  async open(n) { if (!this.stores.has(n)) this.stores.set(n, new FakeCache()); return this.stores.get(n); }
  async keys() { return [...this.stores.keys()]; }
  async delete(n) { return this.stores.delete(n); }
  async match(r) { for (const c of this.stores.values()) { const m = await c.match(r); if (m) return m; } return undefined; }
}

/** In a real worker `new Request("/x")` resolves against the worker's own URL; Node needs that spelled out. */
export class SwRequest extends Request {
  constructor(input, init) { super(typeof input === "string" ? new URL(input, ORIGIN).href : input, init); }
}

export const ok = (body, type = "text/html") => () => new Response(body, { status: 200, headers: { "content-type": type } });

/**
 * Boots the worker in `file`. `patch(src)` can substitute build-time placeholders. `routes` maps pathname -> Response
 * factory; `env.online` / `env.delay` can be changed by a test while it runs.
 */
export function boot({ file, patch = (s) => s, routes = {} }) {
  const handlers = {};
  const calls = [];
  const env = { online: true, delay: 0 };
  const caches = new FakeCaches();
  const self = { location: new URL(ORIGIN), addEventListener: (t, f) => (handlers[t] = f), skipWaiting: () => { env.skipped = true; return Promise.resolve(); }, clients: { claim: async () => { env.claimed = true; } } };
  const fetchMock = async (input) => {
    const req = input instanceof Request ? input : new Request(new URL(input, ORIGIN));
    calls.push(new URL(req.url).pathname);
    if (env.delay) await new Promise((r) => setTimeout(r, env.delay));
    if (!env.online) throw new TypeError("Failed to fetch");
    const make = routes[new URL(req.url).pathname];
    const res = make ? make() : new Response("missing", { status: 404 });
    // A real same-origin fetch returns a "basic" response; the Response constructor makes "default" ones.
    return Object.defineProperty(res, "type", { value: "basic" });
  };
  const src = patch(fs.readFileSync(file, "utf8"));
  vm.runInNewContext(src, { self, caches, fetch: fetchMock, Response, Request: SwRequest, URL, setTimeout, clearTimeout, Promise, Error, console });
  const lifecycle = async (type) => { const waits = []; await handlers[type]({ waitUntil: (p) => waits.push(p) }); await Promise.all(waits); };
  const fire = async (url, { method = "GET", mode = "cors" } = {}) => {
    const request = new Request(new URL(url, url.startsWith("http") ? undefined : ORIGIN), { method });
    Object.defineProperty(request, "mode", { value: mode });
    let responded; const waits = [];
    handlers.fetch({ request, respondWith: (p) => (responded = p), waitUntil: (p) => waits.push(p) });
    const res = responded ? await responded : undefined;
    await Promise.all(waits);
    return { handled: responded !== undefined, res };
  };
  return { env, caches, calls, lifecycle, fire };
}
