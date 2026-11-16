/* FSharpened Hub service worker.

   The console is a single-page app, so "offline" means: the app shell (HTML, CSS, JS, icons) still opens and says
   plainly that live data needs a connection. It deliberately never stores API responses: they are private
   (run history, tokens, the audit log) and must not sit in Cache Storage after someone signs out.

   - /api/* is never intercepted. That keeps sign-in (a redirect to GitHub and back), sign-out and every data call
     exactly as the server answers them.
   - Everything else on this origin is network-first with a short timeout, so a deploy shows up on the next visit and
     a bad connection falls back to the last copy instead of hanging. Navigations fall back to the cached shell ("/").
   - New versions activate immediately (skipWaiting + clients.claim); pwa.js offers a reload. */
"use strict";

const SHELL = "fsh-hub-shell";
const PRECACHE = ["/", "/admin.css", "/admin.js", "/pwa.js", "/mark.svg", "/favicon.svg", "/icon-192.png", "/manifest.webmanifest"];
const NETWORK_TIMEOUT_MS = 3500;

/** A response that went through a redirect cannot answer a navigation; store a clean copy. */
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
      for (const key of await caches.keys()) if (key.startsWith("fsh-hub-") && key !== SHELL) await caches.delete(key);
      const cache = await caches.open(SHELL);
      for (const req of await cache.keys()) if (!PRECACHE.includes(new URL(req.url).pathname)) await cache.delete(req);
      await self.clients.claim();
    })(),
  );
});

function withTimeout(promise, ms) {
  return Promise.race([promise, new Promise((_, reject) => setTimeout(() => reject(new Error("slow")), ms))]);
}

async function networkFirst(event, key, fallback) {
  const cache = await caches.open(SHELL);
  const network = fetch(event.request).then(async (res) => {
    if (res.ok && res.type === "basic" && !res.redirected) await cache.put(key, res.clone());
    return res;
  });
  try {
    return await withTimeout(network, NETWORK_TIMEOUT_MS);
  } catch (_) {
    event.waitUntil(network.catch(() => undefined)); // a slow request still refreshes the cache when it lands
    return (await cache.match(key)) || (fallback && (await cache.match(fallback))) || Response.error();
  }
}

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin || url.pathname === "/sw.js" || url.pathname.startsWith("/api/")) return;
  if (req.mode === "navigate") event.respondWith(networkFirst(event, "/", "/"));
  else event.respondWith(networkFirst(event, url.pathname, null));
});
