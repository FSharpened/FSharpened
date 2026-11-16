// The hub console's service worker (worker/admin/sw.js), run the same way as the site's. The important property
// is negative: private API traffic and the sign-in redirect must never be intercepted or stored.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import { fileURLToPath } from "node:url";
import { boot as bootWorker, ok, ORIGIN } from "./harness.mjs";

const FILE = fileURLToPath(new URL("../../worker/admin/sw.js", import.meta.url));
const SOURCE = fs.readFileSync(FILE, "utf8");
const PRECACHE = JSON.parse(SOURCE.match(/const PRECACHE = (\[[^\]]*\]);/)[1]);

function boot({ routes = {}, timeoutMs = 1500 } = {}) {
  return bootWorker({ file: FILE, patch: (s) => s.replace("NETWORK_TIMEOUT_MS = 3500", `NETWORK_TIMEOUT_MS = ${timeoutMs}`), routes });
}
const SHELL = { "/": ok("shell"), "/admin.css": ok("css", "text/css"), "/admin.js": ok("js", "text/javascript"), "/pwa.js": ok("pwa", "text/javascript"), "/mark.svg": ok("svg", "image/svg+xml"), "/favicon.svg": ok("svg", "image/svg+xml"), "/icon-192.png": ok("png", "image/png"), "/manifest.webmanifest": ok("{}", "application/manifest+json") };

test("the precache list is the whole app shell, and each file exists in worker/admin", () => {
  assert.ok(PRECACHE.includes("/") && PRECACHE.includes("/admin.js") && PRECACHE.includes("/admin.css"));
  for (const url of PRECACHE) {
    if (url === "/") continue;
    assert.ok(fs.existsSync(fileURLToPath(new URL("../../worker/admin" + url, import.meta.url))), url + " is listed but missing");
  }
});

test("install caches the shell and takes over immediately", async () => {
  const sw = boot({ routes: SHELL });
  await sw.lifecycle("install");
  assert.deepEqual([...sw.caches.stores.get("fsh-hub-shell").entries.keys()].sort(), PRECACHE.map((p) => ORIGIN + p).sort());
  assert.equal(sw.env.skipped, true);
});

test("install fails if a shell file is missing, so a broken deploy never activates", async () => {
  const sw = boot({ routes: { ...SHELL, "/pwa.js": undefined } });
  await assert.rejects(sw.lifecycle("install"), /precache \/pwa\.js -> 404/);
});

test("activate drops older hub caches and files no longer in the shell, but leaves other caches alone", async () => {
  const sw = boot({ routes: SHELL });
  await sw.caches.open("fsh-hub-shell-v0");
  await sw.caches.open("someone-elses-cache");
  await sw.lifecycle("install");
  await (await sw.caches.open("fsh-hub-shell")).put("/old-chunk.js", new Response("x"));
  await sw.lifecycle("activate");
  assert.deepEqual((await sw.caches.keys()).sort(), ["fsh-hub-shell", "someone-elses-cache"]);
  assert.equal(await (await sw.caches.open("fsh-hub-shell")).match("/old-chunk.js"), undefined);
  assert.equal(sw.env.claimed, true);
});

test("navigations are network-first; offline, any route opens the cached shell", async () => {
  const routes = { ...SHELL };
  const sw = boot({ routes });
  await sw.lifecycle("install");
  routes["/"] = ok("shell v2"); // redeployed
  assert.equal(await (await sw.fire("/?source=pwa", { mode: "navigate" })).res.text(), "shell v2");
  sw.env.online = false;
  assert.equal(await (await sw.fire("/", { mode: "navigate" })).res.text(), "shell v2");
  assert.equal(await (await sw.fire("/runs/anything", { mode: "navigate" })).res.text(), "shell v2");
});

test("a slow hub shows the cached shell instead of a blank screen", async () => {
  const sw = boot({ routes: SHELL, timeoutMs: 10 });
  await sw.lifecycle("install");
  sw.env.delay = 150;
  assert.equal(await (await sw.fire("/admin.js")).res.text(), "js");
});

test("API calls and the GitHub sign-in redirect are never intercepted or stored", async () => {
  const sw = boot({ routes: { ...SHELL, "/api/admin/overview": ok("{\"secret\":1}", "application/json") } });
  await sw.lifecycle("install");
  for (const path of ["/api/admin/overview", "/api/auth/login", "/api/auth/callback?code=x", "/api/status.json"]) {
    for (const mode of ["cors", "navigate"]) assert.equal((await sw.fire(path, { mode })).handled, false, path + " (" + mode + ")");
  }
  assert.equal((await sw.fire("/api/admin/tokens", { method: "POST" })).handled, false);
  assert.equal((await sw.fire("https://github.com/login/oauth/authorize", { mode: "navigate" })).handled, false);
  assert.equal((await sw.fire("/sw.js")).handled, false);
  for (const cache of sw.caches.stores.values()) for (const key of cache.entries.keys()) assert.ok(!key.includes("/api/"), "cached " + key);
});

test("static files come from the network when online and from the cache when offline", async () => {
  const sw = boot({ routes: SHELL });
  await sw.lifecycle("install");
  sw.env.online = false;
  assert.equal(await (await sw.fire("/admin.css")).res.text(), "css");
  const missing = await sw.fire("/never-cached.js");
  assert.equal(missing.res.type, "error");
});
