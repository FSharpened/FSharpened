// Project storage on IndexedDB (ADR-0024): where projects live, the migration from the localStorage keys of earlier versions
// (which must never lose a project), the fallback when IndexedDB is unavailable, quota failures, writes cut short by a page
// closing, and ordering. Runs against an in-memory fake IndexedDB (fake-indexeddb.mjs) and the compiled F# core.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { createStore } from "../src/store.js";
import { openDb, createIdbPersister, DB_NAME } from "../src/idbstore.js";
import { FakeIndexedDB } from "./fake-indexeddb.mjs";

const entry = new URL("../fable_out/Api.js", import.meta.url);
const built = existsSync(entry);
const Core = built ? await import(entry.href) : null;
const skip = built ? false : "Fable output missing: run `npm run fable` in ide/web first";

/** localStorage as a Map, optionally with a size limit in characters (a browser's few MB), to show what it cannot hold. */
const fakeStorage = (seed = {}, limit = Infinity) => {
  const m = new Map(Object.entries(seed));
  const size = () => [...m].reduce((n, [k, v]) => n + k.length + String(v).length, 0);
  return {
    m,
    getItem: (k) => (m.has(k) ? m.get(k) : null),
    setItem: (k, v) => {
      const had = m.get(k);
      m.set(k, String(v));
      if (size() > limit) { if (had === undefined) m.delete(k); else m.set(k, had); const e = new Error("storage is full"); e.name = "QuotaExceededError"; throw e; }
    },
    removeItem: (k) => void m.delete(k),
  };
};
let tick = 5_000;
const clock = () => ++tick;
const open = async (storage, indexedDB, extra = {}) => { const s = createStore(Core, storage, clock, { indexedDB, openTimeoutMs: 80, ...extra }); const info = await s.load(); return { s, info }; };
const project = (name, files) => Core.projectFromBuffers(name, "python", {}).files.length === 0 && files
  ? files.reduce((p, [path, text]) => Core.projectAddFile(p, path, text).project, Core.projectCreate(name))
  : null;

/** A browser that still has the localStorage projects an earlier version of the page saved. */
async function legacyBrowser(names = ["Alpha", "Beta"]) {
  const storage = fakeStorage();
  const old = await open(storage, undefined);          // localStorage only, as before IndexedDB
  const made = [old.s.current()];
  for (const n of names) made.push(old.s.create(n, project(n, [["a.py", `print('${n}')`], ["src/b.rs", "fn main() {}"]])).rec);
  made.forEach((r) => old.s.save(r));
  return { storage, made, v2keys: [...storage.m.keys()].filter((k) => k.startsWith("fsharpened.ide.v2")) };
}

test("a new browser keeps its projects in IndexedDB, one record each, with the index written beside them", { skip }, async () => {
  const idb = new FakeIndexedDB();
  const storage = fakeStorage();
  const { s, info } = await open(storage, idb);
  assert.equal(info.kind, "indexeddb");
  assert.equal(s.kind, "indexeddb");
  await s.settled();
  const db = idb.peek();
  assert.deepEqual(Object.keys(db.projects), [s.current().id]);
  assert.equal(db.projects[s.current().id].project.name, "Starter examples");
  assert.deepEqual(db.meta.index, { v: 3, current: s.current().id, order: [s.current().id] });
  assert.equal([...storage.m.keys()].filter((k) => k.startsWith("fsharpened.ide.v2")).length, 0, "localStorage is not used for projects any more");
  assert.equal(storage.m.has("fsharpened.ide.v3.pending"), false, "the journal is empty once the write is confirmed");
});

test("projects survive a reload exactly: create, edit, rename, switch, duplicate and delete all reach the database", { skip }, async () => {
  const idb = new FakeIndexedDB();
  const storage = fakeStorage();
  const a = (await open(storage, idb)).s;
  const made = a.create("Chess", null).rec;
  made.project = Core.projectAddFile(made.project, "src/engine.rs", "fn main() {}").project;
  made.open = ["src/engine.rs"]; made.active = "src/engine.rs"; made.collapsed = ["src"]; made.stdin = "e2e4";
  const r = a.save(made);
  assert.equal(r.ok, true);
  assert.equal((await r.done).ok, true);
  const copy = a.duplicate(made.id, "Chess copy").rec;
  a.rename(copy.id, "Chess two");
  const doomed = a.create("Temp", null).rec;
  a.remove(doomed.id);
  a.switchTo(made.id);
  await a.settled();

  const b = (await open(storage, idb)).s;
  assert.deepEqual(b.get(made.id).project, made.project);
  assert.equal(b.get(made.id).stdin, "e2e4");
  assert.deepEqual(b.get(made.id).collapsed, ["src"]);
  assert.equal(b.get(copy.id).project.name, "Chess two");
  assert.equal(b.get(doomed.id), undefined, "a deleted project is gone from the database too");
  assert.equal(b.current().id, made.id);
  assert.equal(b.list().length, 3);
  assert.equal(Object.keys(idb.peek().projects).length, 3);
});

test("several quick saves are written in order and the last one wins", { skip }, async () => {
  const idb = new FakeIndexedDB();
  const { s } = await open(fakeStorage(), idb);
  const rec = s.current();
  for (let i = 1; i <= 8; i++) { rec.stdin = "v" + i; s.save(rec); }
  await s.settled();
  assert.equal(idb.peek().projects[rec.id].stdin, "v8");
  const again = (await open(fakeStorage(), idb)).s;
  assert.equal(again.current().stdin, "v8");
});

// ------------------------------------------------------------------------------------------------ migration
test("the localStorage projects are copied into IndexedDB, proven equal, and only then removed", { skip }, async () => {
  const { storage, made, v2keys } = await legacyBrowser();
  assert.ok(v2keys.length >= 4, "the index and three project records were in localStorage");
  const v1 = JSON.stringify({ ws: { active: "python", buffers: { python: "print(1)" }, stdin: "" }, prefs: {} });
  storage.setItem("fsharpened.ide.v1", v1);
  const before = Object.fromEntries(made.map((r) => [r.id, JSON.parse(storage.getItem("fsharpened.ide.v2.p." + r.id))]));

  const idb = new FakeIndexedDB();
  const { s, info } = await open(storage, idb);
  assert.equal(info.kind, "indexeddb");
  assert.deepEqual(info.migration, { from: "localStorage", projects: 3 });
  assert.equal(s.list().length, 3, "every project is still there");
  for (const r of made) {
    assert.deepEqual(idb.peek().projects[r.id], { ...before[r.id], id: r.id }, "stored exactly as it was");
    assert.deepEqual(s.get(r.id).project.files, r.project.files);
  }
  assert.equal(idb.peek().meta.info.migratedFrom, "localStorage v2");
  assert.deepEqual([...storage.m.keys()].filter((k) => k.startsWith("fsharpened.ide.v2")), [], "the old keys are removed after the copy was verified");
  assert.equal(storage.getItem("fsharpened.ide.v1"), v1, "the v1 backup is never touched");
  assert.equal(s.current().id, made.find((r) => r.id === JSON.parse(JSON.stringify(s.current())).id).id);
});

test("if the copy cannot be written (disk full) nothing is removed and the page keeps using localStorage", { skip }, async () => {
  const { storage, made, v2keys } = await legacyBrowser();
  const snapshot = new Map(storage.m);
  const idb = new FakeIndexedDB();
  idb.failWrites = "QuotaExceededError";
  const { s, info } = await open(storage, idb);
  assert.equal(info.kind, "localStorage");
  assert.match(info.fellBackBecause, /full/);
  assert.equal(s.list().length, made.length, "every project is still available");
  for (const k of v2keys) assert.equal(storage.getItem(k), snapshot.get(k), "old key kept as it was: " + k);
  assert.deepEqual(idb.peek()?.projects ?? {}, {}, "nothing half-written is left in the database");
  idb.failWrites = null;
  const next = await open(storage, idb); // a later visit, with room again, migrates
  assert.equal(next.info.kind, "indexeddb");
  assert.equal(next.s.list().length, made.length);
});

test("if the copy does not read back identical it is discarded and the old keys stay", { skip }, async () => {
  const { storage, made, v2keys } = await legacyBrowser(["Only"]);
  const snapshot = new Map(storage.m);
  const idb = new FakeIndexedDB();
  idb.tamper = (rows) => rows.map((r) => ({ ...r, stdin: "tampered" }));
  const { s, info } = await open(storage, idb);
  assert.equal(info.kind, "localStorage");
  assert.match(info.fellBackBecause, /did not match/);
  assert.equal(s.list().length, made.length);
  for (const k of v2keys) assert.equal(storage.getItem(k), snapshot.get(k));
  idb.tamper = null;
  assert.deepEqual(idb.peek().projects, {}, "the unverified copy was cleared so it cannot be trusted later");
  assert.deepEqual(idb.peek().meta, {});
});

test("a damaged localStorage record is left in place, not deleted, when the rest migrate", { skip }, async () => {
  const { storage, made } = await legacyBrowser(["Good"]);
  storage.setItem("fsharpened.ide.v2.p.broken", "{ not json");
  const idx = JSON.parse(storage.getItem("fsharpened.ide.v2"));
  idx.order.push("broken");
  storage.setItem("fsharpened.ide.v2", JSON.stringify(idx));
  const idb = new FakeIndexedDB();
  const { s, info } = await open(storage, idb);
  assert.equal(info.kind, "indexeddb");
  assert.equal(s.list().length, made.length);
  assert.equal(storage.getItem("fsharpened.ide.v2.p.broken"), "{ not json", "what could not be read is never destroyed");
});

test("programs from before projects (v1) still become a project, now stored in IndexedDB, and v1 stays", { skip }, async () => {
  const old = { ws: { active: "python", buffers: { python: "print('kept')", rust: "fn main() {}" }, stdin: "42" }, prefs: { compiler: { python: "x" }, options: {} } };
  const storage = fakeStorage({ "fsharpened.ide.v1": JSON.stringify(old) });
  const idb = new FakeIndexedDB();
  const { s, info } = await open(storage, idb);
  assert.equal(info.migrated, true);
  assert.equal(s.current().project.name, "My workspace");
  assert.equal(s.current().stdin, "42");
  assert.equal(idb.peek().projects[s.current().id].project.name, "My workspace");
  assert.equal(storage.getItem("fsharpened.ide.v1"), JSON.stringify(old));
});

// ------------------------------------------------------------------------------------------------ unavailable
for (const mode of ["throws", "error", "blocked", "hang"]) {
  test(`IndexedDB that ${mode === "hang" ? "never answers" : "is " + mode} (a private window) falls back to localStorage and everything works`, { skip }, async () => {
    const idb = new FakeIndexedDB();
    idb.mode = mode;
    const storage = fakeStorage();
    const { s, info } = await open(storage, idb);
    assert.equal(info.kind, "localStorage");
    assert.ok(info.fellBackBecause.length > 0);
    const rec = s.create("Fallback", null).rec;
    assert.equal(s.save(rec).ok, true);
    assert.ok(storage.getItem("fsharpened.ide.v2.p." + rec.id), "saved in localStorage");
    const again = (await open(storage, idb)).s;
    assert.equal(again.get(rec.id).project.name, "Fallback");
  });
}

test("no IndexedDB object at all is the old behaviour exactly", { skip }, async () => {
  const storage = fakeStorage();
  const { s, info } = await open(storage, undefined);
  assert.equal(info.kind, "localStorage");
  assert.equal(info.fellBackBecause, "");
  assert.ok(storage.getItem("fsharpened.ide.v2"));
  assert.equal(s.list().length, 1);
});

test("openDb refuses a missing, throwing or silent IndexedDB with a reason", async () => {
  await assert.rejects(() => openDb(undefined), /not available/);
  const quiet = new FakeIndexedDB(); quiet.mode = "hang";
  await assert.rejects(() => openDb(quiet, 30), /in time/);
  const throwing = new FakeIndexedDB(); throwing.mode = "throws";
  await assert.rejects(() => openDb(throwing), /denied/);
  const ok = await openDb(new FakeIndexedDB());
  assert.equal(ok.name, DB_NAME);
});

// ------------------------------------------------------------------------------------------------ quota and journal
test("a full disk is reported when the write commits, the project stays usable, and the next write works once there is room", { skip }, async () => {
  const idb = new FakeIndexedDB();
  const storage = fakeStorage();
  const { s } = await open(storage, idb);
  const rec = s.current();
  rec.stdin = "keep me";
  idb.failWrites = "QuotaExceededError";
  const r = s.save(rec);
  assert.equal(r.ok, true, "the write was accepted; the verdict comes with `done`");
  const res = await r.done;
  assert.equal(res.ok, false);
  assert.match(res.error, /storage is full/i);
  assert.equal(s.current().stdin, "keep me", "the project is still usable in memory");
  assert.ok(storage.getItem("fsharpened.ide.v3.pending"), "and a copy waits in the journal, so nothing is lost if the page closes now");
  idb.failWrites = null;
  const ok = await s.save(rec).done;
  assert.equal(ok.ok, true);
  assert.equal(idb.peek().projects[rec.id].stdin, "keep me");
  assert.equal(storage.getItem("fsharpened.ide.v3.pending"), null, "journal cleared after the confirmed write");
});

test("a write cut short by the page closing is recovered from the journal on the next visit", { skip }, async () => {
  const idb = new FakeIndexedDB();
  const storage = fakeStorage();
  const { s } = await open(storage, idb);
  await s.settled();
  const rec = s.current();
  rec.project = Core.projectAddFile(rec.project, "last-words.py", "print('typed just before closing')").project;
  idb.dropCommits = true;                       // the page is gone before the transaction commits
  s.save(rec);
  await new Promise((ok) => setTimeout(ok, 20));
  assert.equal(JSON.stringify(idb.peek().projects[rec.id]).includes("last-words"), false, "the database never got it");
  idb.dropCommits = false;
  const next = (await open(storage, idb)).s;    // the next visit
  assert.ok(next.get(rec.id).project.files.some((f) => f.path === "last-words.py"), "recovered from the journal");
  await next.settled();
  assert.ok(JSON.stringify(idb.peek().projects[rec.id]).includes("last-words"), "and now in the database");
  assert.equal(storage.getItem("fsharpened.ide.v3.pending"), null);
});

test("a stale journal never overwrites a newer record, and a deleted project does not come back", { skip }, async () => {
  const idb = new FakeIndexedDB();
  const storage = fakeStorage();
  const { s } = await open(storage, idb);
  const rec = s.current();
  rec.stdin = "newer";
  await s.save(rec).done;
  storage.setItem("fsharpened.ide.v3.pending", JSON.stringify({ [rec.id]: { ...JSON.parse(JSON.stringify(rec)), stdin: "older", updated: 1 } }));
  const again = (await open(storage, idb)).s;
  assert.equal(again.get(rec.id).stdin, "newer");

  const gone = again.create("Gone", null).rec;
  await again.save(gone).done;
  again.remove(gone.id);
  await again.settled();
  const third = (await open(storage, idb)).s;
  assert.equal(third.get(gone.id), undefined);
});

test("a project too big for localStorage still saves in IndexedDB", { skip }, async () => {
  const chunk = "x".repeat(60_000);                              // the core caps one file at 65,536 characters, so many files
  const mk = (s) => { const rec = s.create("Big", null).rec; for (let i = 0; i < 8; i++) rec.project = Core.projectAddFile(rec.project, `big${i}.txt`, chunk).project; return rec; };
  const small = fakeStorage({}, 300_000);                         // a localStorage with room for 300 KB
  const local = (await open(small, undefined)).s;
  const failed = local.save(mk(local));
  assert.equal(failed.ok, false);
  assert.match(failed.error, /full/);

  const roomy = fakeStorage({}, 300_000);
  const idb = new FakeIndexedDB();
  const { s } = await open(roomy, idb);
  const rec = mk(s);
  const res = await s.save(rec).done;
  assert.equal(res.ok, true);
  assert.equal(idb.peek().projects[rec.id].project.files.length, 8);
  assert.equal(idb.peek().projects[rec.id].project.files[0].text.length, chunk.length);
});

test("a damaged record in the database is repaired or dropped like one in localStorage", { skip }, async () => {
  const idb = new FakeIndexedDB();
  const storage = fakeStorage();
  const { s } = await open(storage, idb);
  const good = s.create("Good", null).rec;
  good.project = Core.projectAddFile(good.project, "a.py", "1").project;
  await s.save(good).done;
  const db = idb.dbs.get(DB_NAME);
  const row = db.stores.get("projects").data.get(good.id);
  db.stores.get("projects").data.set(good.id, { ...row, active: "ghost.py", open: ["ghost.py", "a.py", 7], project: { name: "Good", files: [{ path: "a.py", text: "1" }, { path: 5, text: "x" }], entry: "ghost.py", pipeline: [] } });
  db.stores.get("projects").data.set("junk", { id: "junk", project: "nonsense" });
  const idx = db.stores.get("meta").data.get("index");
  idx.order.push("junk");
  const b = (await open(storage, idb)).s;
  assert.equal(b.get("junk"), undefined);
  assert.deepEqual(b.get(good.id).project.files, [{ path: "a.py", text: "1" }]);
  assert.deepEqual(b.get(good.id).open, ["a.py"]);
});

test("the quota the browser reports, and asking it to keep the data, pass through", { skip }, async () => {
  const idb = new FakeIndexedDB();
  let asked = 0;
  const { s } = await open(fakeStorage(), idb, { estimate: async () => ({ usage: 1_500_000, quota: 3_000_000_000 }), persist: async () => { asked++; return true; } });
  assert.deepEqual(await s.quota(), { usage: 1_500_000, quota: 3_000_000_000 });
  assert.equal(await s.keepPersistent(), true);
  assert.equal(asked, 1);
  const bare = (await open(fakeStorage(), idb)).s;
  assert.equal(await bare.quota(), null);
  assert.equal(await bare.keepPersistent(), false);
  const broken = (await open(fakeStorage(), idb, { estimate: async () => { throw new Error("no"); } })).s;
  assert.equal(await broken.quota(), null);
});

test("the persister commits a project and the index in one transaction: a failed write changes neither", { skip }, async () => {
  const idb = new FakeIndexedDB();
  const p = createIdbPersister(await openDb(idb));
  const index = { v: 3, current: "a", order: ["a"] };
  assert.equal((await p.putRecord({ id: "a", project: { name: "A", files: [] } }, index).done).ok, true);
  idb.failWrites = "QuotaExceededError";
  assert.equal((await p.putRecord({ id: "b", project: { name: "B", files: [] } }, { v: 3, current: "b", order: ["a", "b"] }).done).ok, false);
  idb.failWrites = null;
  assert.deepEqual(Object.keys(idb.peek().projects), ["a"]);
  assert.deepEqual(idb.peek().meta.index, index);
});
