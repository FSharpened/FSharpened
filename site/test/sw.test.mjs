// Runs the real service worker source (site/src/sw.js) against a mocked Cache API and fetch. No dependencies:
//   node --test "site/test/*.test.mjs"
import { test } from "node:test";
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { boot as bootWorker, ok, ORIGIN } from "./harness.mjs";

const FILE = fileURLToPath(new URL("../src/sw.js", import.meta.url));
const PRECACHE = ["/", "/why", "/offline", "/assets/h/site.aaa.css"];
const KNOWN = ["/assets/h/site.aaa.css", "/assets/h/ide.bbb.js"];

function boot({ routes = {}, version = "t1", timeoutMs = 1500 } = {}) {
  const patch = (s) => s.replace('"__VERSION__"', JSON.stringify(version)).replace("__PRECACHE__", JSON.stringify(PRECACHE)).replace("__KNOWN__", JSON.stringify(KNOWN)).replace("NAV_TIMEOUT_MS = 3500", `NAV_TIMEOUT_MS = ${timeoutMs}`);
  return bootWorker({ file: FILE, patch, routes });
}

const ROUTES = { "/": ok("home"), "/why": ok("why"), "/offline": ok("offline"), "/assets/h/site.aaa.css": ok("css", "text/css"), "/assets/h/ide.bbb.js": ok("ide", "text/javascript") };

test("install precaches every listed URL into a versioned cache and takes over immediately", async () => {
  const sw = boot({ routes: ROUTES });
  await sw.lifecycle("install");
  const cache = sw.caches.stores.get("fsh-shell-t1");
  assert.deepEqual([...cache.entries.keys()].sort(), PRECACHE.map((p) => ORIGIN + p).sort());
  assert.equal(sw.env.skipped, true);
});

test("install fails (and is retried by the browser) if any precached file is missing", async () => {
  const sw = boot({ routes: { ...ROUTES, "/why": undefined } });
  await assert.rejects(sw.lifecycle("install"), /precache \/why -> 404/);
  assert.equal(sw.env.skipped, undefined);
});

test("install stores a clean copy of a redirected response", async () => {
  const redirected = () => { const r = new Response("home", { status: 200 }); Object.defineProperty(r, "redirected", { value: true }); return r; };
  const sw = boot({ routes: { ...ROUTES, "/": redirected } });
  await sw.lifecycle("install");
  const stored = await sw.caches.stores.get("fsh-shell-t1").match("/");
  assert.equal(await stored.text(), "home");
  assert.equal(stored.redirected, false);
});

test("activate removes old shells, prunes unknown fingerprinted files, keeps current ones, and claims clients", async () => {
  const sw = boot({ routes: ROUTES });
  await sw.caches.open("fsh-shell-old");
  await sw.caches.open("unrelated-cache");
  const runtime = await sw.caches.open("fsh-assets");
  await runtime.put("/assets/h/ide.bbb.js", new Response("keep"));
  await runtime.put("/assets/h/ide.zzz.js", new Response("stale"));
  await sw.lifecycle("install");
  await sw.lifecycle("activate");
  assert.deepEqual((await sw.caches.keys()).sort(), ["fsh-assets", "fsh-shell-t1", "unrelated-cache"]);
  assert.deepEqual([...runtime.entries.keys()], [ORIGIN + "/assets/h/ide.bbb.js"]);
  assert.equal(sw.env.claimed, true);
});

test("pages are network-first and refresh the cached copy", async () => {
  const routes = { ...ROUTES };
  const sw = boot({ routes });
  await sw.lifecycle("install");
  routes["/why"] = ok("why v2"); // the site was redeployed after install
  const { res } = await sw.fire("/why?utm=1", { mode: "navigate" });
  assert.equal(await res.text(), "why v2");
  assert.equal(await (await sw.caches.stores.get("fsh-shell-t1").match("/why")).text(), "why v2");
});

test("offline: a visited page comes from the cache, an unknown page shows /offline", async () => {
  const sw = boot({ routes: ROUTES });
  await sw.lifecycle("install");
  sw.env.online = false;
  assert.equal(await (await sw.fire("/why", { mode: "navigate" })).res.text(), "why");
  assert.equal(await (await sw.fire("/never-seen", { mode: "navigate" })).res.text(), "offline");
});

test("a slow network falls back to the cached page instead of hanging", async () => {
  const sw = boot({ routes: ROUTES, timeoutMs: 10 });
  await sw.lifecycle("install");
  sw.env.delay = 150;
  assert.equal(await (await sw.fire("/", { mode: "navigate" })).res.text(), "home");
});

test("fingerprinted files are cache-first: one network fetch, then served from cache", async () => {
  const sw = boot({ routes: ROUTES });
  await sw.lifecycle("install");
  const first = await sw.fire("/assets/h/ide.bbb.js");
  assert.equal(await first.res.text(), "ide");
  sw.env.online = false;
  const second = await sw.fire("/assets/h/ide.bbb.js");
  assert.equal(await second.res.text(), "ide");
  assert.equal(sw.calls.filter((c) => c === "/assets/h/ide.bbb.js").length, 1);
});

test("never touches other origins, non-GET requests, or the worker script itself", async () => {
  const sw = boot({ routes: ROUTES });
  await sw.lifecycle("install");
  assert.equal((await sw.fire("https://ide.example.workers.dev/api/run")).handled, false);
  assert.equal((await sw.fire("/api/anything", { method: "POST" })).handled, false);
  assert.equal((await sw.fire("/sw.js")).handled, false);
});
