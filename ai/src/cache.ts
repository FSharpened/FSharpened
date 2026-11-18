// Response cache: completions keyed by a hash of (model, prompt, parameters). Entries expire (TTL) and are not stored
// when larger than LIMITS.cacheEntry. The Cloudflare Cache API evicts on its own; the entry cap and the TTL are ours.

import { LIMITS } from "./policy";

export interface Candidate { text: string; kind: "inline" | "keyword" | "snippet"; confidence: number }
export interface CacheEntry { model: string; candidates: Candidate[]; inputTokens: number; outputTokens: number }

export interface ResponseCache {
  get(key: string): Promise<CacheEntry | null>;
  put(key: string, entry: CacheEntry, ttlSeconds: number): Promise<boolean>;
}

/** True when the entry would be stored: small enough. */
export function cacheable(entry: CacheEntry): boolean {
  return new TextEncoder().encode(JSON.stringify(entry)).length <= LIMITS.cacheEntry;
}

export const DEFAULT_TTL_SECONDS = 3600;
export const MAX_TTL_SECONDS = 86_400;

export function ttlFrom(v: string | undefined): number {
  const n = v === undefined ? NaN : Number(v);
  return Number.isInteger(n) && n >= 0 ? Math.min(n, MAX_TTL_SECONDS) : DEFAULT_TTL_SECONDS;
}

/** The Workers Cache API, on a synthetic URL that is never fetched. */
export function edgeCache(cache: Cache): ResponseCache {
  const req = (key: string) => new Request("https://ai-cache.invalid/" + key);
  return {
    async get(key) {
      const hit = await cache.match(req(key));
      if (!hit) return null;
      try { return (await hit.json()) as CacheEntry; } catch { return null; }
    },
    async put(key, entry, ttl) {
      if (ttl <= 0 || !cacheable(entry)) return false;
      await cache.put(req(key), new Response(JSON.stringify(entry), { headers: { "content-type": "application/json", "cache-control": `public, max-age=${ttl}` } }));
      return true;
    },
  };
}
