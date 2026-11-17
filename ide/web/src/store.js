// The projects a person keeps in this browser (ADR-0021). Pure logic over an injected `storage` (localStorage in the page,
// a Map-backed fake in tests) and the compiled F# core, so it runs and is tested in Node.
//
// Layout in storage:
//   fsharpened.ide.v2          { v: 2, current: id, order: [id, ...] }
//   fsharpened.ide.v2.p.<id>   one record per project, so one big project cannot make the others unsaveable
//   fsharpened.ide.v1          the single-language workspace of earlier versions. Read once, never written or deleted:
//                              it stays as a backup of what the migration started from.
const INDEX = "fsharpened.ide.v2";
const OLD = "fsharpened.ide.v1";
const KEY = (id) => `${INDEX}.p.${id}`;

const isText = (v) => typeof v === "string";
const plain = (o) => (o && typeof o === "object" && !Array.isArray(o) ? o : {});

/** Accepts only what a record must look like; anything else is dropped rather than trusted. */
function sanitize(id, raw) {
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

export function createStore(Core, storage, clock = Date.now) {
  const get = (k) => { try { return storage.getItem(k); } catch { return null; } };
  const put = (k, v) => { try { storage.setItem(k, v); return { ok: true }; } catch (e) { return { ok: false, error: /quota/i.test(String(e?.name) + String(e?.message)) ? "The browser's storage is full. Download the project to keep it." : "The browser would not save this project." }; } };
  const del = (k) => { try { storage.removeItem(k); } catch { /* nothing to remove */ } };
  const json = (k) => { const t = get(k); if (!t) return null; try { return JSON.parse(t); } catch { return null; } };

  const records = new Map();
  let order = [];
  let current = "";

  const newId = () => {
    let id;
    do id = "p" + clock().toString(36) + Math.random().toString(36).slice(2, 6); while (records.has(id));
    return id;
  };

  function writeIndex() {
    return put(INDEX, JSON.stringify({ v: 2, current, order }));
  }

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

  function migrate() {
    const d = json(OLD);
    if (!d || !d.ws || typeof d.ws !== "object") return null;
    const buffers = plain(d.ws.buffers);
    const known = Object.fromEntries(Object.entries(buffers).filter(([, v]) => isText(v)));
    if (!Object.keys(known).length) return null;
    const lang = isText(d.ws.active) ? d.ws.active : "fsharp";
    return make(Core.projectFromBuffers("My workspace", lang, known), { stdin: isText(d.ws.stdin) ? d.ws.stdin : "", prefs: plain(d.prefs) });
  }

  function load() {
    const idx = json(INDEX);
    if (idx && Array.isArray(idx.order)) {
      for (const id of idx.order) {
        if (!isText(id)) continue;
        const rec = sanitize(id, json(KEY(id)));
        if (rec) { records.set(id, rec); order.push(id); }
      }
      current = records.has(idx.current) ? idx.current : order[0] ?? "";
    }
    let migrated = false;
    if (!records.size) {
      const old = migrate();
      migrated = !!old;
      const rec = old ?? make(starterProject());
      current = rec.id;
      save(rec);
    }
    return { migrated };
  }

  function save(rec) {
    rec.updated = clock();
    const r = put(KEY(rec.id), JSON.stringify(rec));
    if (!r.ok) return r;
    return writeIndex();
  }

  return {
    load,
    save,
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
      del(KEY(id));
      if (!records.size) { const rec = make(starterProject()); current = rec.id; save(rec); return rec; }
      if (current === id) current = this.list()[0].id;
      writeIndex();
      return records.get(current);
    },
    findByOrigin(origin) { return origin ? [...records.values()].find((r) => r.origin === origin) : undefined; },
  };
}
