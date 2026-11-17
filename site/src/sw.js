/* FSharpened service worker. tools/site/build.py injects the version and file lists and writes this to /sw.js.

   Model
   - Pages (navigations): network-first with a short timeout, so they are fresh when online and the last copy you
     saw is shown when offline or on a bad connection. Unknown offline pages fall back to /offline.
   - Fingerprinted files under /assets/h/ never change (the name carries a content hash): cache-first.
   - Other same-origin files (icons, manifest): stale-while-revalidate.
   - Other origins (the IDE gateway) are never intercepted. A new version activates immediately; open pages keep
     the files they already loaded, and site.js offers a reload.
   The core site is precached at install. The IDE bundle is cached the first time the IDE is opened, so reading
   the docs never downloads half a megabyte of editor. */
"use strict";

const VERSION = "__VERSION__";
const PRECACHE = __PRECACHE__;
const KNOWN_ASSETS = __KNOWN__;
const SHELL = "fsh-shell-" + VERSION;
const RUNTIME = "fsh-assets";
const NAV_TIMEOUT_MS = 3500;

/** A response that went through a redirect cannot be used to answer a navigation; copy it clean. */
async function clean(res) {
  if (!res.redirected) return res;
  return new Response(await res.blob(), { status: res.status, statusText: res.statusText, headers: res.headers });
}

self.addEventListener("install", (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(SHELL);
      await Promise.all(
        PRECACHE.map(async (url) => {
          const res = await fetch(new Request(url, { cache: "reload" }));
          if (!res.ok) throw new Error("precache " + url + " -> " + res.status);
          await cache.put(url, await clean(res));
        }),
      );
      await self.skipWaiting();
    })(),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    (async () => {
      for (const key of await caches.keys()) if (key.startsWith("fsh-shell-") && key !== SHELL) await caches.delete(key);
      const runtime = await caches.open(RUNTIME);
      for (const req of await runtime.keys()) {
        if (!KNOWN_ASSETS.includes(new URL(req.url).pathname)) await runtime.delete(req);
      }
      await self.clients.claim();
    })(),
  );
});

async function page(event) {
  const req = event.request;
  const cache = await caches.open(SHELL);
  const key = new URL(req.url).pathname;
  const network = fetch(req).then(async (res) => {
    if (res.ok && res.type === "basic" && !res.redirected) await cache.put(key, res.clone());
    return res;
  });
  try {
    return await Promise.race([network, new Promise((_, reject) => setTimeout(() => reject(new Error("slow")), NAV_TIMEOUT_MS))]);
  } catch (_) {
    event.waitUntil(network.catch(() => undefined)); // a slow request still refreshes the cache when it lands
    return (await cache.match(key)) || (await cache.match("/offline")) || Response.error();
  }
}

async function immutable(req) {
  const hit = await caches.match(req);
  if (hit) return hit;
  const res = await fetch(req);
  if (res.ok) await (await caches.open(RUNTIME)).put(req, res.clone());
  return res;
}

async function revalidate(event) {
  const cache = await caches.open(SHELL);
  const hit = await cache.match(event.request);
  const network = fetch(event.request)
    .then(async (res) => {
      if (res.ok) await cache.put(event.request, res.clone());
      return res;
    })
    .catch(() => null);
  if (hit) {
    event.waitUntil(network);
    return hit;
  }
  return (await network) || Response.error();
}

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin || url.pathname === "/sw.js") return;
  if (req.mode === "navigate") event.respondWith(page(event));
  else if (url.pathname.startsWith("/assets/h/")) event.respondWith(immutable(req));
  else event.respondWith(revalidate(event));
});
