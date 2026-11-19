// The projects a person keeps in this browser (ADR-0021, ADR-0024). Pure logic over an injected persister (IndexedDB, or
// localStorage as the fallback) and the compiled F# core, so it runs and is tested in Node against fakes.
//
// Where the data lives, in order of preference:
//   IndexedDB  database fsharpened-ide (idbstore.js): one record per project, the index beside them, written together.
//              Far larger quota than localStorage's few MB, and a failed write is reported, not silently dropped.
//   localStorage (the fallback: private windows, blocked or missing IndexedDB, or a migration that could not be verified)
//     fsharpened.ide.v2          { v: 2, current: id, order: [id, ...] }
//     fsharpened.ide.v2.p.<id>   one record per project, so one big project cannot make the others unsaveable
//     fsharpened.ide.v1          the single-language workspace of earlier versions. Read once, never written or deleted:
//                                it stays as a backup of what the migration started from.
//
// Migration to IndexedDB never loses a project: the localStorage records are copied in one transaction, read back and compared
// field for field, and only then are the old v2 keys removed. A failure at any step leaves them where they were and the page
// keeps using localStorage. While an IndexedDB write is in flight the record is also held in a small localStorage journal
// (fsharpened.ide.v3.pending), because a page being closed can cut a transaction short; the journal is applied on the next
// load and removed when the write is confirmed.
//
// `save` and friends return { ok, error?, done }: `ok` is immediate (false only when the write could not even be started),
// `done` is a promise of the final { ok, error? } once the data is really stored.
import { createIdbPersister, openDb, explain } from "./idbstore.js";

const INDEX = "fsharpened.ide.v2";
const OLD = "fsharpened.ide.v1";
const PENDING = "fsharpened.ide.v3.pending";
const KEY = (id) => `${INDEX}.p.${id}`;

const isText = (v) => typeof v === "string";
const plain = (o) => (o && typeof o === "object" && !Array.isArray(o) ? o : {});
const clone = (o) => (typeof structuredClone === "function" ? structuredClone(o) : JSON.parse(JSON.stringify(o)));

/** Accepts only what a record must look like; anything else is dropped rather than trusted. */
export function sanitize(id, raw) {
  const r = plain(raw), p = plain(r.project);
  if (!isText(p.name) || !Array.isArray(p.files)) return null;
  const files = p.files.filter((f) => f && isText(f.path) && isText(f.text)).map((f) => ({ path: f.path, text: f.text }));
  const paths = new Set(files.map((f) => f.path));
  const entry = isText(p.entry) && paths.has(p.entry) ? p.entry : null;
  const open = (Array.isArray(r.open) ? r.open : []).filter((x) => isText(x) && paths.has(x));
  const active = isText(r.active) && paths.has(r.active) ? r.active : open[0] ?? "";
  if (active && !open.includes(active)) open.push(active);
  const prefs = plain(r.prefs);
  return {
    id,
    project: { name: p.name, files, entry, pipeline: (Array.isArray(p.pipeline) ? p.pipeline : []).filter((s) => isText(s) && paths.has(s)) },
    active,
    open,
    collapsed: (Array.isArray(r.collapsed) ? r.collapsed : []).filter(isText),
    stdin: isText(r.stdin) ? r.stdin : "",
    prefs: { compiler: { ...plain(prefs.compiler) }, options: { ...plain(prefs.options) } },
    origin: isText(r.origin) ? r.origin : "",
    updated: Number.isFinite(r.updated) ? r.updated : 0,
  };
}

/**
 * options: { indexedDB, estimate, persist, openTimeoutMs }. Without `indexedDB` the store uses `storage` alone.
 * `estimate()` answers { usage, quota } (navigator.storage.estimate); `persist()` asks the browser to keep the data.
 */
export function createStore(Core, storage, clock = Date.now, options = {}) {
  const get = (k) => { try { return storage.getItem(k); } catch { return null; } };
  const put = (k, v) => { try { storage.setItem(k, v); return { ok: true }; } catch (e) { return { ok: false, error: explain(e) }; } };
  const del = (k) => { try { storage.removeItem(k); } catch { /* nothing to remove */ } };
  const json = (k) => { const t = get(k); if (!t) return null; try { return JSON.parse(t); } catch { return null; } };
  const done = (r) => ({ ...r, done: Promise.resolve({ ok: r.ok, error: r.error }) });

  const records = new Map();
  let order = [];
  let current = "";
  let persister = null;
  let migration = null;
  let why = "";                                   // why IndexedDB was not used, when it was not

  const newId = () => {
    let id;
    do id = "p" + clock().toString(36) + Math.random().toString(36).slice(2, 6); while (records.has(id));
    return id;
  };

  // ---- the localStorage persister (the fallback, and what the store used before IndexedDB)
  function localPersister() {
    const index = () => ({ v: 2, current, order });
    const writeIndex = () => done(put(INDEX, JSON.stringify(index())));
    return {
      kind: "localStorage",
      async readAll() {
        const idx = json(INDEX);
        const map = new Map();
        if (idx && Array.isArray(idx.order)) for (const id of idx.order) if (isText(id)) { const raw = json(KEY(id)); if (raw) map.set(id, raw); }
        return { index: idx, records: map };
      },
      putRecord(record) {
        const r = put(KEY(record.id), JSON.stringify(record));
        return r.ok ? writeIndex() : done(r);
      },
      putIndex: writeIndex,
      removeRecord(id) { del(KEY(id)); return writeIndex(); },
      settled: () => Promise.resolve(),
      close() {},
    };
  }

  // ---- the journal: records whose IndexedDB write is not confirmed yet
  const journal = () => plain(json(PENDING));
  const journalSet = (id, rec) => { const j = journal(); j[id] = rec; put(PENDING, JSON.stringify(j)); };
  const journalClear = (id) => { const j = journal(); if (!(id in j)) return; delete j[id]; if (Object.keys(j).length) put(PENDING, JSON.stringify(j)); else del(PENDING); };

  const indexObject = () => ({ v: persister?.kind === "indexeddb" ? 3 : 2, current, order });

  function make(project, extra = {}) {
    const id = newId();
    const paths = project.files.map((f) => f.path);
    const entry = Core.projectEntry(project);
    const first = extra.active ?? (paths.includes(entry) ? entry : paths[0] ?? "");
    const rec = sanitize(id, { project, active: first, open: first ? [first] : [], collapsed: [], stdin: "", prefs: { compiler: {}, options: {} }, origin: "", updated: clock(), ...extra });
    records.set(id, rec);
    order.push(id);
    return rec;
  }

  /** The starter every new browser gets, until the chess project replaces it (PLAN P6.A6). */
  function starterProject(name = "Starter examples") {
    return Core.projectFromBuffers(name, "fsharp", Core.initialWorkspace().buffers);
  }

  function migrateV1() {
    const d = json(OLD);
    if (!d || !d.ws || typeof d.ws !== "object") return null;
    const buffers = plain(d.ws.buffers);
    const known = Object.fromEntries(Object.entries(buffers).filter(([, v]) => isText(v)));
    if (!Object.keys(known).length) return null;
    const lang = isText(d.ws.active) ? d.ws.active : "fsharp";
    return make(Core.projectFromBuffers("My workspace", lang, known), { stdin: isText(d.ws.stdin) ? d.ws.stdin : "", prefs: plain(d.prefs) });
  }

  /** Copies the localStorage (v2) projects into IndexedDB, proves the copy, then removes the old keys. Throws to keep them. */
  async function copyLegacyIntoIdb(p) {
    const idx = json(INDEX);
    if (!idx || !Array.isArray(idx.order)) return null;
    const ids = idx.order.filter(isText);
    const raws = new Map();
    for (const id of ids) { const raw = json(KEY(id)); if (raw && typeof raw === "object") raws.set(id, raw); }
    if (!raws.size) return null;
    const rows = [...raws].map(([id, raw]) => ({ ...clone(raw), id }));
    const index = { v: 3, current: isText(idx.current) ? idx.current : ids[0], order: [...raws.keys()] };
    const info = { migratedFrom: "localStorage v2", at: clock(), projects: rows.length };
    const wrote = await p.putAll(rows, index, info).done;
    if (!wrote.ok) { const e = new Error(wrote.error); e.cause = wrote.cause; throw e; }
    const back = await p.readAll();
    const same = rows.every((r) => back.records.has(r.id) && JSON.stringify(back.records.get(r.id)) === JSON.stringify(r)) && back.index && JSON.stringify(back.index) === JSON.stringify(index);
    if (!same) {
      await p.clear(); // never leave a half-trusted database behind: the old keys are still there and still the truth
      throw new Error("the copy in IndexedDB did not match the original");
    }
    for (const id of raws.keys()) del(KEY(id)); // only now, and only what was proven copied
    del(INDEX);
    migration = { from: "localStorage", projects: rows.length };
    return back;
  }

  /** Reads IndexedDB (migrating localStorage into it on first use) and applies what the last session left unconfirmed. */
  async function loadFromIdb(p) {
    let state = await p.readAll();
    if (!state.index && !state.records.size) state = (await copyLegacyIntoIdb(p)) ?? state;
    const pending = journal();
    let changed = false;
    for (const [id, rec] of Object.entries(pending)) {
      const have = state.records.get(id);
      if (rec && typeof rec === "object" && (!have || Number(rec.updated) > Number(have.updated ?? 0))) { state.records.set(id, { ...rec, id }); changed = true; }
    }
    if (changed) {
      state.index = state.index ?? { v: 3, current: "", order: [] };
      state.index.order = [...(state.index.order ?? []).filter((id) => state.records.has(id)), ...[...state.records.keys()].filter((id) => !(state.index.order ?? []).includes(id))];
      const all = Object.keys(pending).filter((id) => state.records.has(id));
      const r = await p.putAll(all.map((id) => state.records.get(id)), state.index).done;
      if (r.ok) for (const id of Object.keys(pending)) journalClear(id);
    }
    return state;
  }

  async function load() {
    records.clear(); order = []; current = ""; migration = null; why = "";
    persister = null;
    let state = null;
    if (options.indexedDB) {
      try {
        const p = createIdbPersister(await openDb(options.indexedDB, options.openTimeoutMs));
        try { state = await loadFromIdb(p); persister = p; } catch (e) { p.close(); throw e; }
      } catch (e) { why = String(e?.message ?? e); state = null; persister = null; }
    }
    if (!persister) { persister = localPersister(); state = await persister.readAll(); }

    const idx = state.index;
    if (idx && Array.isArray(idx.order)) {
      for (const id of idx.order) {
        if (!isText(id)) continue;
        const rec = sanitize(id, state.records.get(id));
        if (rec) { records.set(id, rec); order.push(id); }
      }
      current = records.has(idx.current) ? idx.current : order[0] ?? "";
    }
    let migrated = false;
    if (!records.size) {
      const old = migrateV1();
      migrated = !!old;
      const rec = old ?? make(starterProject());
      current = rec.id;
      await save(rec).done;
    }
    return { migrated, kind: persister.kind, fellBackBecause: why, migration };
  }

  function commit(write, rec) {
    const r = write();
    if (rec && persister.kind === "indexeddb") r.done.then((res) => { if (res.ok) journalClear(rec.id); });
    return r;
  }

  function save(rec) {
    rec.updated = clock();
    if (persister.kind !== "indexeddb") return persister.putRecord(rec, indexObject());
    const snapshot = clone(rec);
    journalSet(rec.id, snapshot);          // synchronous: survives a page that is closing before the transaction commits
    return commit(() => persister.putRecord(snapshot, indexObject()), rec);
  }

  function writeIndex() { return persister.putIndex(indexObject()); }

  return {
    load,
    save,
    /** "indexeddb" or "localStorage": where the projects are being kept. */
    get kind() { return persister?.kind ?? "none"; },
    /** Resolves when every write started so far has committed or failed. */
    settled: () => (persister ? persister.settled() : Promise.resolve()),
    /** { usage, quota } in bytes from the browser, or null when it will not say. */
    async quota() { try { return options.estimate ? await options.estimate() : null; } catch { return null; } },
    /** Asks the browser not to evict this data under storage pressure. True when it agreed. */
    async keepPersistent() { try { return options.persist ? !!(await options.persist()) : false; } catch { return false; } },
    close() { persister?.close(); },
    current: () => records.get(current),
    get: (id) => records.get(id),
    /** Newest first, in a fixed order: the list does not reshuffle whenever a project is saved. */
    list: () => order.map((id) => records.get(id)).reverse(),
    switchTo(id) {
      if (!records.has(id)) return null;
      current = id;
      writeIndex();
      return records.get(id);
    },
    /** `project` is a core project object; with none, an empty one named `name`. */
    create(name, project, extra) {
      const rec = make(project ?? Core.projectCreate(name), extra);
      if (!project) rec.project = Core.projectRename(rec.project, name);
      current = rec.id;
      const r = save(rec);
      return { rec, saved: r };
    },
    createStarter(name) { return this.create(name, starterProject(name)); },
    duplicate(id, name) {
      const src = records.get(id);
      if (!src) return null;
      const copy = JSON.parse(JSON.stringify(src));
      const rec = make(Core.projectRename(copy.project, name || src.project.name + " copy"), { active: copy.active, open: copy.open, collapsed: copy.collapsed, stdin: copy.stdin, prefs: copy.prefs });
      current = rec.id;
      return { rec, saved: save(rec) };
    },
    rename(id, name) {
      const rec = records.get(id);
      if (!rec) return null;
      rec.project = Core.projectRename(rec.project, name);
      return { rec, saved: save(rec) };
    },
    /** Removes a project; never leaves the browser without one. Returns the project to show next. */
    remove(id) {
      if (!records.has(id)) return null;
      records.delete(id);
      order = order.filter((x) => x !== id);
      journalClear(id);
      if (!records.size) {
        persister.removeRecord(id, indexObject());
        const rec = make(starterProject());
        current = rec.id;
        save(rec);
        return rec;
      }
      if (current === id) current = this.list()[0].id;
      persister.removeRecord(id, indexObject());
      return records.get(current);
    },
    findByOrigin(origin) { return origin ? [...records.values()].find((r) => r.origin === origin) : undefined; },
  };
}
