// Shared fixtures: the static server (one per worker), and a page that
//   - blocks every request that is not the local server or the (mocked) gateway, and records what it blocked,
//   - collects console errors, page errors and Content-Security-Policy violations,
//   - answers the gateway from a function the test can replace.
import { test as base, expect } from "@playwright/test";
import { startServer } from "./server.mjs";

export const GATEWAY = "https://ide.fsharpened.workers.dev";

export const okRun = (stdout = "Hello from the mocked gateway\n", extra = {}) => ({
  buildFailed: false, buildOutput: "", stdout, stderr: "", exitCode: 0, timedOut: false, durationMs: 7, cached: false, ...extra,
});

export const test = base.extend({
  server: [async ({}, use) => { const s = await startServer(); await use(s); await s.close(); }, { scope: "worker" }],

  ide: async ({ page, context, server }, use) => {
    const gateway = { calls: [], reply: () => ({ status: 200, body: okRun() }) };
    const blocked = [];
    const consoleErrors = [];
    const pageErrors = [];

    // Anything outside the local server and the gateway is refused and remembered: a test run never reaches the network.
    await context.route("**/*", (route) => {
      const u = route.request().url();
      if (u.startsWith(server.origin) || u.startsWith("data:") || u.startsWith("blob:")) return route.fallback();
      blocked.push(u);
      return route.abort("blockedbyclient");
    });
    await context.route(GATEWAY + "/**", async (route) => {
      const req = route.request();
      const cors = { "access-control-allow-origin": server.origin, "access-control-allow-headers": "content-type", "access-control-allow-methods": "POST, GET, OPTIONS", vary: "origin" };
      if (req.method() === "OPTIONS") return route.fulfill({ status: 204, headers: cors });
      let body = {};
      try { body = JSON.parse(req.postData() ?? "{}"); } catch { /* keep {} */ }
      gateway.calls.push(body);
      const { status, body: out, headers } = await gateway.reply(body, gateway.calls.length);
      return route.fulfill({ status, headers: { ...cors, "content-type": "application/json", ...(headers ?? {}) }, body: JSON.stringify(out) });
    });

    page.on("console", (m) => { if (m.type() === "error") consoleErrors.push(m.text()); });
    page.on("pageerror", (e) => pageErrors.push(process.env.E2E_STACK ? String(e.stack) : String(e)));
    await page.addInitScript(() => {
      window.__csp = [];
      document.addEventListener("securitypolicyviolation", (e) => window.__csp.push(`${e.violatedDirective} ${e.blockedURI} ${e.sourceFile ?? ""}:${e.lineNumber ?? ""}`));
    });

    const ide = {
      page, gateway, blocked, consoleErrors, pageErrors, origin: server.origin,
      url: server.origin + "/ide/",
      async open(hash = "") {
        await page.goto(this.url + hash);
        await expect(page.locator("#ide.ready")).toBeVisible();
        return this;
      },
      /** Clears this origin's storage, then loads the page fresh (a first visit). */
      async fresh() {
        await page.goto(this.url);
        await page.evaluate(async () => {
          localStorage.clear();
          const dbs = (await indexedDB.databases?.()) ?? [];
          await Promise.all(dbs.map((d) => new Promise((ok) => { const r = indexedDB.deleteDatabase(d.name); r.onsuccess = r.onerror = r.onblocked = () => ok(); })));
        });
        return this.open();
      },
      editor: page.locator("#editor .cm-content"),
      /** Replaces the open file's text by typing into the editor. */
      async setText(text) {
        await this.editor.click();
        await page.keyboard.press("Control+A");
        await page.keyboard.press("Delete");
        await page.keyboard.type(text);
      },
      tab: (name) => page.locator('#ftabs [role="tab"]').filter({ hasText: name }),
      treeItem: (name) => page.locator('#tree [role="treeitem"]').filter({ has: page.locator(".tname").getByText(name, { exact: true }) }),
      /** The small inline form (new file, rename, project name…): fill it and press its OK button. */
      async answer(value) {
        const input = page.locator("#ask-input");
        await expect(page.locator("#askbar")).toBeVisible();
        if (value !== undefined) await input.fill(value);
        await page.locator("#ask-ok").click();
      },
      async showSide(name) {
        await page.locator(`.stabs [data-s="${name}"]`).click();
        await expect(page.locator(`.spanel[data-s="${name}"]`)).toBeVisible();
      },
      async palette(text) {
        await page.keyboard.press("F1");
        await expect(page.locator("#palette")).toBeVisible();
        if (text) await page.locator("#pal-input").fill(">" + text);
      },
      async storedProjects() {
        return page.evaluate(async () => (window.__ideDebug ? window.__ideDebug.projects() : null));
      },
    };
    await use(ide);
    // Every test also proves the page stayed quiet and on its own origin.
    expect(blocked, "requests that left the test server and the mocked gateway").toEqual([]);
    expect(pageErrors, "uncaught page errors").toEqual([]);
    const csp = await page.evaluate(() => window.__csp ?? []).catch(() => []);
    expect(csp, "Content-Security-Policy violations").toEqual([]);
  },
});

export { expect };
