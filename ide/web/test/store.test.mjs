// The project store: what the page keeps in the browser, and what it makes of what an older version left there.
// Needs the compiled F# core (`npm run fable`); skipped, with the reason, when it is missing.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { createStore } from "../src/store.js";
import { buildProject } from "../src/projectio.js";

const entry = new URL("../fable_out/Api.js", import.meta.url);
const built = existsSync(entry);
const Core = built ? await import(entry.href) : null;
const skip = built ? false : "Fable output missing: run `npm run fable` in ide/web first";

const fakeStorage = (seed = {}) => {
  const m = new Map(Object.entries(seed));
  return { m, getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => void m.set(k, String(v)), removeItem: (k) => void m.delete(k) };
};
let tick = 1_000;
const clock = () => ++tick;
const open = (storage) => { const s = createStore(Core, storage, clock); const info = s.load(); return { s, info }; };

test("a new browser gets the starter project, one file per language", { skip }, () => {
  const { s, info } = open(fakeStorage());
  assert.equal(info.migrated, false);
  const rec = s.current();
  assert.equal(rec.project.name, "Starter examples");
  assert.equal(rec.project.files.length, Core.languages().length);
  assert.ok(rec.active && rec.open.includes(rec.active));
});

test("programs from before projects become a project, and the old data is left in place", { skip }, () => {
  const old = { ws: { active: "python", buffers: { python: "print('kept')", rust: "fn main() {}", nonsense: 5 }, stdin: "42" }, prefs: { compiler: { python: "x" }, options: { c: "-O2" } } };
  const storage = fakeStorage({ "fsharpened.ide.v1": JSON.stringify(old) });
  const { s, info } = open(storage);
  assert.equal(info.migrated, true);
  const rec = s.current();
  assert.equal(rec.project.name, "My workspace");
  assert.deepEqual(rec.project.files.map((f) => f.path).sort(), ["main.py", "main.rs"]);
  assert.equal(rec.project.files.find((f) => f.path === "main.py").text, "print('kept')");
  assert.equal(rec.active, "main.py");
  assert.equal(rec.stdin, "42");
  assert.deepEqual(rec.prefs.options, { c: "-O2" });
  assert.equal(storage.getItem("fsharpened.ide.v1"), JSON.stringify(old), "the old workspace is a backup: never rewritten or removed");
});

test("projects survive a reload exactly", { skip }, () => {
  const storage = fakeStorage();
  const a = open(storage).s;
  const made = a.create("Chess", null).rec;
  made.project = Core.projectAddFile(made.project, "src/engine.rs", "fn main() {}").project;
  made.open = ["src/engine.rs"];
  made.active = "src/engine.rs";
  made.collapsed = ["src"];
  made.stdin = "e2e4";
  assert.ok(a.save(made).ok);
  const b = open(storage).s;
  const back = b.get(made.id);
  assert.deepEqual(back.project, made.project);
  assert.equal(back.active, "src/engine.rs");
  assert.deepEqual(back.collapsed, ["src"]);
  assert.equal(back.stdin, "e2e4");
  assert.equal(b.current().id, made.id, "the project that was open is open again");
  assert.equal(b.list().length, 2);
});

test("damaged or hostile records are repaired or dropped, never trusted", { skip }, () => {
  const storage = fakeStorage();
  const a = open(storage).s;
  const good = a.create("Good", null).rec;
  good.project = Core.projectAddFile(good.project, "a.py", "1").project;
  a.save(good);
  storage.setItem(`fsharpened.ide.v2.p.${good.id}`, JSON.stringify({ ...JSON.parse(storage.getItem(`fsharpened.ide.v2.p.${good.id}`)), active: "ghost.py", open: ["ghost.py", "a.py", 7], project: { name: "Good", files: [{ path: "a.py", text: "1" }, { path: 5, text: "x" }, "junk"], entry: "ghost.py", pipeline: ["a.py", "nope.py"] } }));
  storage.setItem("fsharpened.ide.v2.p.broken", "{ not json");
  const idx = JSON.parse(storage.getItem("fsharpened.ide.v2"));
  idx.order.push("broken", 12);
  storage.setItem("fsharpened.ide.v2", JSON.stringify(idx));
  const b = open(storage).s;
  assert.equal(b.get("broken"), undefined);
  const rec = b.get(good.id);
  assert.deepEqual(rec.project.files, [{ path: "a.py", text: "1" }]);
  assert.equal(rec.project.entry, null);
  assert.deepEqual(rec.project.pipeline, ["a.py"]);
  assert.deepEqual(rec.open, ["a.py"]);
  assert.equal(rec.active, "a.py");
});

test("garbage in the index falls back to a working starter instead of a blank page", { skip }, () => {
  const { s } = open(fakeStorage({ "fsharpened.ide.v2": "nonsense", "fsharpened.ide.v1": "also nonsense" }));
  assert.equal(s.list().length, 1);
  assert.ok(s.current().project.files.length > 1);
});

test("a full browser store is reported, and the project stays usable in memory", { skip }, () => {
  const storage = fakeStorage();
  const { s } = open(storage);
  storage.setItem = () => { const e = new Error("full"); e.name = "QuotaExceededError"; throw e; };
  const r = s.save(s.current());
  assert.equal(r.ok, false);
  assert.match(r.error, /full/);
  assert.ok(s.current().project.files.length > 0);
});

test("creating, renaming, duplicating, switching and deleting projects", { skip }, () => {
  const { s } = open(fakeStorage());
  const first = s.current();
  const { rec: second } = s.create("  Second / one  ", null);
  assert.ok(!/[\/]/.test(second.project.name) && second.project.name === second.project.name.trim() && second.project.name.startsWith("Second"), "names are cleaned by the core");
  assert.equal(s.current().id, second.id);
  assert.equal(s.switchTo(first.id).id, first.id);
  assert.equal(s.switchTo("nope"), null);

  s.rename(second.id, "Renamed");
  assert.equal(s.get(second.id).project.name, "Renamed");

  const copy = s.duplicate(first.id, "Copy of first").rec;
  assert.notEqual(copy.id, first.id);
  assert.deepEqual(copy.project.files, first.project.files);
  copy.project.files[0].text = "changed";
  assert.notEqual(first.project.files[0].text, "changed", "a copy shares nothing with its source");

  assert.equal(s.list().length, 3);
  const next = s.remove(copy.id);
  assert.equal(s.list().length, 2);
  assert.notEqual(next.id, copy.id);
  assert.equal(s.get(copy.id), undefined);
});

test("deleting the last project leaves a fresh starter, never an empty browser", { skip }, () => {
  const { s } = open(fakeStorage());
  const only = s.current();
  const next = s.remove(only.id);
  assert.notEqual(next.id, only.id);
  assert.equal(s.list().length, 1);
  assert.equal(next.project.name, "Starter examples");
});

test("a project made from a share link is found again by its origin", { skip }, () => {
  const { s } = open(fakeStorage());
  const { project } = buildProject(Core, "Shared", [{ path: "a.py", text: "1" }], "a.py", []);
  const { rec } = s.create(project.name, project, { origin: "abc123", stdin: "in" });
  assert.equal(s.findByOrigin("abc123").id, rec.id);
  assert.equal(s.findByOrigin("other"), undefined);
  assert.equal(s.findByOrigin(""), undefined);
  assert.equal(rec.stdin, "in");
});

test("buildProject keeps paths as given and reports what it refused", { skip }, () => {
  const files = [{ path: "app/main.py", text: "1" }, { path: "app/lib.rs", text: "2" }, { path: "../evil.py", text: "x" }, { path: "con.py", text: "x" }];
  const { project, skipped } = buildProject(Core, "App", files, "app/lib.rs", ["app/main.py", "app/lib.rs"]);
  assert.deepEqual(project.files.map((f) => f.path), ["app/lib.rs", "app/main.py"], "a single top-level folder is not stripped here");
  assert.equal(project.entry, "app/lib.rs");
  assert.deepEqual(project.pipeline, ["app/main.py", "app/lib.rs"]);
  assert.deepEqual(skipped.map((s) => s.path).sort(), ["../evil.py", "con.py"]);
  const bad = buildProject(Core, "App", [{ path: "notes.md", text: "" }], "notes.md", []);
  assert.equal(bad.project.entry, null);
  assert.match(bad.skipped[0].reason, /entry file/);
});

test("the core exports the limits the page warns about", { skip }, () => {
  const l = Core.projectLimits();
  assert.equal(l.fileChars, 65536);
  assert.equal(l.files, 200);
  assert.equal(Core.projectRename(Core.projectCreate("x"), "  a/b  ").name, "ab");
});

test("a statepoint of a whole project restores files, entry and open file", { skip }, () => {
  const { project } = buildProject(Core, "P", [{ path: "a.py", text: "1" }, { path: "src/b.rs", text: "2" }], "src/b.rs", ["a.py", "src/b.rs"]);
  const session = new Core.ProjectSession({ project, active: "a.py", stdin: "" });
  const start = session.headId;
  const edited = Core.projectRemoveFile(project, "a.py").project;
  const id = session.snapshot("after delete", { project: edited, active: "src/b.rs", stdin: "x" });
  assert.deepEqual(session.read(id).project.files.map((f) => f.path), ["src/b.rs"]);
  const back = session.restore(start);
  assert.deepEqual(back.project.files.map((f) => f.path), ["a.py", "src/b.rs"]);
  assert.equal(back.project.entry, "src/b.rs");
  assert.deepEqual(back.project.pipeline, ["a.py", "src/b.rs"]);
  assert.equal(back.active, "a.py");
  assert.equal(session.entries().length, 2);
});
