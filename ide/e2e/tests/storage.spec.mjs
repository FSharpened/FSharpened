// Project storage in a real browser (ADR-0024): IndexedDB by default, the migration from the localStorage keys of earlier
// versions, the fallback when IndexedDB is not available, and the storage note in the Projects tab.
import { test, expect } from "../support/fixtures.mjs";

const dbRows = (page) => page.evaluate(() => new Promise((ok, fail) => {
  const req = indexedDB.open("fsharpened-ide");
  req.onerror = () => fail(req.error);
  req.onsuccess = () => {
    const db = req.result;
    const tx = db.transaction(["projects", "meta"], "readonly");
    const a = tx.objectStore("projects").getAll(), b = tx.objectStore("meta").get("index"), c = tx.objectStore("meta").get("info");
    tx.oncomplete = () => { db.close(); ok({ projects: a.result, index: b.result, info: c.result }); };
    tx.onerror = () => fail(tx.error);
  };
}));
const v2Keys = (page) => page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith("fsharpened.ide.v2")));

test("projects are kept in IndexedDB, not in localStorage", async ({ ide }) => {
  await ide.fresh();
  await expect.poll(async () => (await dbRows(ide.page)).projects.length).toBe(1);
  const rows = await dbRows(ide.page);
  expect(rows.projects[0].project.name).toBe("Starter examples");
  expect(rows.index).toMatchObject({ v: 3, order: [rows.projects[0].id] });
  expect(await v2Keys(ide.page)).toEqual([]);
  await ide.showSide("projects");
  await expect(ide.page.locator("#p-storage")).toContainText("IndexedDB");
});

test("an edit is committed to IndexedDB, and one made just before the page closes is not lost", async ({ ide }) => {
  await ide.fresh();
  await ide.page.locator("#f-new").click();
  await ide.answer("late.txt");
  await ide.setText("typed right before closing");
  await ide.open(); // reloads at once, before the 300 ms pause that normally saves
  await expect(ide.tab("late.txt")).toBeVisible();
  await expect.poll(() => ide.editor.innerText()).toContain("typed right before closing");
});

test("projects an earlier version saved in localStorage are moved into IndexedDB with nothing lost", async ({ ide }) => {
  // make this browser look like one from before IndexedDB: the projects only in localStorage, no database. The IDE page is not
  // open while this is done (it would write its own starter project back on the way out), so use a script-free same-origin file.
  await ide.page.goto(ide.origin + "/favicon.svg");
  await ide.page.evaluate(async () => {
    const project = (name, files) => ({ id: "legacy-" + name.toLowerCase(), project: { name, files, entry: files[0].path, pipeline: [] }, active: files[0].path, open: [files[0].path], collapsed: [], stdin: "in-" + name, prefs: { compiler: {}, options: {} }, origin: "", updated: 1000 });
    const a = project("Legacy one", [{ path: "one.py", text: "print('legacy one')" }, { path: "lib/two.rs", text: "fn main() {}" }]);
    const b = project("Legacy two", [{ path: "two.js", text: "console.log('legacy two')" }]);
    localStorage.clear();
    localStorage.setItem("fsharpened.ide.v2", JSON.stringify({ v: 2, current: a.id, order: [a.id, b.id] }));
    localStorage.setItem("fsharpened.ide.v2.p." + a.id, JSON.stringify(a));
    localStorage.setItem("fsharpened.ide.v2.p." + b.id, JSON.stringify(b));
    localStorage.setItem("fsharpened.ide.v1", JSON.stringify({ ws: { active: "python", buffers: { python: "print('v1')" }, stdin: "" }, prefs: {} }));
    await new Promise((ok) => { const r = indexedDB.deleteDatabase("fsharpened-ide"); r.onsuccess = r.onerror = r.onblocked = () => ok(); });
  });
  await ide.open();
  await expect(ide.page.locator("#proj-name")).toHaveText("Legacy one");
  await expect(ide.tab("one.py")).toBeVisible();
  await expect.poll(() => ide.editor.innerText()).toContain("print('legacy one')");
  await ide.showSide("projects");
  await expect(ide.page.locator("#plist li")).toHaveCount(2);
  await expect.poll(async () => (await dbRows(ide.page)).projects.length).toBe(2);
  const rows = await dbRows(ide.page);
  expect(rows.info.migratedFrom).toBe("localStorage v2");
  expect(rows.projects.map((p) => p.project.name).sort()).toEqual(["Legacy one", "Legacy two"]);
  expect(rows.projects.find((p) => p.project.name === "Legacy two").stdin).toBe("in-Legacy two");
  expect(await v2Keys(ide.page), "old keys are removed only after the copy was verified").toEqual([]);
  expect(await ide.page.evaluate(() => localStorage.getItem("fsharpened.ide.v1")), "the v1 backup is never touched").toContain("print('v1')");
});

test("without IndexedDB (a private window) the page works and keeps projects in localStorage", async ({ ide }) => {
  await ide.page.addInitScript(() => { Object.defineProperty(window, "indexedDB", { value: undefined, configurable: true }); });
  await ide.page.goto(ide.url);
  await ide.page.evaluate(() => localStorage.clear());
  await ide.open();
  await ide.page.locator("#f-new").click();
  await ide.answer("fallback.txt");
  await ide.setText("kept in localStorage");
  await expect.poll(async () => (await v2Keys(ide.page)).length).toBeGreaterThan(1);
  await ide.open();
  await expect(ide.tab("fallback.txt")).toBeVisible();
  await expect.poll(() => ide.editor.innerText()).toContain("kept in localStorage");
  await ide.showSide("projects");
  await expect(ide.page.locator("#p-storage")).toContainText("local storage");
});
