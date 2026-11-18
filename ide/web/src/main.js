// FSharpened web IDE shell. Logic lives in the F# core (Fable); this file is DOM wiring only. No innerHTML anywhere.
// A workspace is a project (ADR-0021): many files in many languages, kept in this browser, snapshotted as a whole.
import * as Core from "../fable_out/Api.js";
import { createEditor } from "./editor.js";
import { $, el, fill, safe } from "./dom.js";
import { createStore } from "./store.js";
import { createTree } from "./tree.js";
import { download, copyText, readFiles, classify, filesFromDrop, pick } from "./transfer.js";
import { exportProjectZip, exportFolderZip, exportVsCodeZip, importArchive, importFiles, buildProject, useManifestName } from "./projectio.js";
import { decodeText } from "./archive.js";
import { createPrefs } from "./settings.js";
import { createWorkbench } from "./workbench.js";

const GATEWAY = __GATEWAY__;
useManifestName(Core.manifestName);

// ---------------------------------------------------------------- state
const languages = Core.languages();
const langName = (id) => languages.find((l) => l.id === id)?.name ?? id;
const limits = Core.projectLimits();
const narrow = () => matchMedia("(max-width: 56.25rem)").matches;
const SIDE_KEY = "fsharpened.ide.side";

let store;
let cur;                                         // the open project's record: { id, project, active, open, collapsed, stdin, prefs }
let current = null;                              // { session, key } of the open project: its statepoint history
const histories = new Map();                     // project id -> { session, key }, kept while the page lives
const warm = new Map();                          // layer 4: fingerprint -> report
let running = false;
let listing = [];                                // rows of the file tree
let persistent = true;                           // false when the browser would not give us storage

const fileAt = (path) => cur.project.files.find((f) => f.path === path);
const pathLang = (path) => (path ? Core.pathLanguage(path) : "");
const activeLang = () => pathLang(cur.active);
const activeText = () => fileAt(cur.active)?.text ?? "";
const dirOf = (p) => (p.includes("/") ? p.slice(0, p.lastIndexOf("/")) : "");
const baseOf = (p) => p.slice(p.lastIndexOf("/") + 1);
const stateOf = () => ({ project: cur.project, active: cur.active, stdin: cur.stdin });
const projectKey = () => JSON.stringify([cur.project, cur.stdin]);

function memoryStorage() {
  const m = new Map();
  return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => void m.set(k, String(v)), removeItem: (k) => void m.delete(k) };
}

let saveTimer;
let warnedSave = false;
function flush() {
  clearTimeout(saveTimer);
  const r = store.save(cur);
  watchSave(r);
}
/** A write can fail now (localStorage) or when IndexedDB commits it (`done`); either way say so once, until a write works again. */
function watchSave(r) {
  const report = (res) => {
    if (!res.ok && !warnedSave) { warnedSave = true; toast(res.error); refreshStorageNote(); }
    if (res.ok) warnedSave = false;
  };
  report(r);
  r.done?.then(report);
}
/** Typing saves after a short pause (a large project is not rewritten on every key); everything else saves at once. */
function persist(now = false) {
  clearTimeout(saveTimer);
  if (now) flush(); else saveTimer = setTimeout(flush, 300);
}

// ---------------------------------------------------------------- share links (#s=<base64url(deflate(json))>)
const b64u = (u8) => { let s = ""; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode(...u8.subarray(i, i + 0x8000)); return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, ""); };
const unb64u = (s) => Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
async function pipe(bytes, stream) { const r = new Blob([bytes]).stream().pipeThrough(stream); return new Uint8Array(await new Response(r).arrayBuffer()); }
async function encodeShare() {
  const p = cur.project;
  const payload = JSON.stringify({ v: 2, n: p.name, f: p.files.map((f) => [f.path, f.text]), e: p.entry, p: p.pipeline, a: cur.active, i: cur.stdin, c: cur.prefs.compiler, o: cur.prefs.options });
  const bytes = new TextEncoder().encode(payload);
  return typeof CompressionStream === "function" ? "z" + b64u(await pipe(bytes, new CompressionStream("deflate-raw"))) : "p" + b64u(bytes);
}
async function decodeShare(s) {
  const bytes = unb64u(s.slice(1));
  const raw = s[0] === "z" ? await pipe(bytes, new DecompressionStream("deflate-raw")) : bytes;
  return JSON.parse(new TextDecoder().decode(raw));
}
/** A link from this version (a project) or from before projects (one program) becomes a project record, or null. */
function projectFromShare(d) {
  if (d && d.v === 2 && Array.isArray(d.f)) {
    const files = d.f.filter((x) => Array.isArray(x) && typeof x[0] === "string" && typeof x[1] === "string").map(([path, text]) => ({ path, text }));
    const { project } = buildProject(Core, String(d.n ?? "Shared project"), files, typeof d.e === "string" ? d.e : "", Array.isArray(d.p) ? d.p.filter((s) => typeof s === "string") : []);
    const active = typeof d.a === "string" ? d.a : "";
    return { project, extra: { active: project.files.some((f) => f.path === active) ? active : undefined, stdin: String(d.i ?? ""), prefs: { compiler: d.c ?? {}, options: d.o ?? {} } } };
  }
  if (d && languages.some((l) => l.id === d.l)) {
    const project = Core.projectFromBuffers("Shared program", d.l, { [d.l]: String(d.t ?? "") });
    return { project, extra: { stdin: String(d.i ?? ""), prefs: { compiler: d.c ? { [d.l]: d.c } : {}, options: typeof d.o === "string" ? { [d.l]: d.o } : {} } } };
  }
  return null;
}

// ---------------------------------------------------------------- UI pieces
const ui = {
  work: $("#work"), side: $("#side"), sideBtn: $("#btn-side"), ftabs: $("#ftabs"), compiler: $("#compiler"), compilerWrap: $("#compiler-wrap"), options: $("#options"), optionsWrap: $("#options-wrap"),
  template: $("#template"), templateWrap: $("#template-wrap"), run: $("#btn-run"), snap: $("#btn-snap"), share: $("#btn-share"),
  summary: $("#summary"), stdout: $("#stdout"), stderr: $("#stderr"), problems: $("#problems"), pcount: $("#pcount"),
  timeline: $("#timeline"), tcount: $("#tcount"), stdin: $("#stdin"), status: $("#status"), diff: $("#diff"), empty: $("#empty"),
  tabs: [...document.querySelectorAll(".ptabs button")], panels: [...document.querySelectorAll(".panel")],
  stabs: [...document.querySelectorAll(".stabs [role=tab]")], spanels: [...document.querySelectorAll(".spanel")],
  tree: $("#tree"), projName: $("#proj-name"), projMeta: $("#proj-meta"), plist: $("#plist"), report: $("#report"),
  fNew: $("#f-new"), fRename: $("#f-rename"), fDelete: $("#f-delete"), fEntry: $("#f-entry"), fCopy: $("#f-copy"), fDownload: $("#f-download"),
  pNew: $("#p-new"), pStarter: $("#p-starter"), pDup: $("#p-dup"), pRename: $("#p-rename"), pDelete: $("#p-delete"), pZip: $("#p-zip"),
  pImportZip: $("#p-import-zip"), pImportFolder: $("#p-import-folder"), pAdd: $("#p-add"),
  pickZip: $("#pick-zip"), pickFolder: $("#pick-folder"), pickFiles: $("#pick-files"), pickExt: $("#pick-ext"),
  ask: { form: $("#askbar"), label: $("#ask-label"), input: $("#ask-input"), ok: $("#ask-ok"), cancel: $("#ask-cancel"), error: $("#ask-error") },
};
let editor;
let tree;
let prefs;                                       // editor settings, imported themes and snippets (settings.js)
let wb;                                          // the workbench: palette, menus, search, outline, status bar (workbench.js)
let backing;                                     // the storage everything is kept in: localStorage, or memory when blocked
let shown = null;                                // the run result on screen: { path, source, diagnostics }

function toast(msg) { const t = $("#toast"); t.textContent = msg; t.classList.add("show"); clearTimeout(toast.t); toast.t = setTimeout(() => t.classList.remove("show"), 2600); }
function setStatus(text) { ui.status.textContent = text; }
function showPanel(name) {
  ui.tabs.forEach((b) => { const on = b.dataset.p === name; b.setAttribute("aria-selected", String(on)); b.tabIndex = on ? 0 : -1; });
  ui.panels.forEach((p) => { p.hidden = p.dataset.p !== name; });
}
ui.tabs.forEach((b) => b.addEventListener("click", () => showPanel(b.dataset.p)));

// WAI-ARIA tabs: one tab stop per list, arrow keys / Home / End move between tabs.
function bindTabKeys(container, activate) {
  container.addEventListener("keydown", (e) => {
    const tabs = [...container.querySelectorAll('[role="tab"]')];
    const i = tabs.indexOf(document.activeElement);
    if (i < 0) return;
    let n = -1;
    if (e.key === "ArrowRight" || e.key === "ArrowDown") n = (i + 1) % tabs.length;
    else if (e.key === "ArrowLeft" || e.key === "ArrowUp") n = (i - 1 + tabs.length) % tabs.length;
    else if (e.key === "Home") n = 0;
    else if (e.key === "End") n = tabs.length - 1;
    if (n < 0) return;
    e.preventDefault();
    tabs[n].focus();
    activate(tabs[n]);
  });
}
bindTabKeys($(".ptabs"), (b) => showPanel(b.dataset.p));

// ---------------------------------------------------------------- the sidebar: Files | Projects
function showSide(name) {
  ui.stabs.forEach((b) => { const on = b.dataset.s === name; b.setAttribute("aria-selected", String(on)); b.tabIndex = on ? 0 : -1; });
  ui.spanels.forEach((p) => { p.hidden = p.dataset.s !== name; });
  if (name === "projects") renderProjects(); // the file counts change with every edit to the file list
  wb?.sideShown(name);
}
ui.stabs.forEach((b) => b.addEventListener("click", () => showSide(b.dataset.s)));
bindTabKeys($(".stabs"), (b) => showSide(b.dataset.s));

function setSide(open, remember = true) {
  $("#ide").classList.toggle("side-open", open);
  ui.sideBtn.setAttribute("aria-expanded", String(open));
  if (remember && !narrow()) safe(() => localStorage.setItem(SIDE_KEY, open ? "1" : "0"));
}
const sideOpen = () => $("#ide").classList.contains("side-open");

// A small inline form instead of window.prompt/confirm: those block, and are unusable in embeds and automation.
let asking = null;
function ask({ label, value = "", ok = "OK", confirm = false, commit }) {
  closeAsk(false);
  asking = { commit, returnTo: document.activeElement };
  const a = ui.ask;
  a.label.textContent = label;
  a.input.hidden = confirm;
  a.input.value = value;
  a.ok.textContent = ok;
  a.error.hidden = true;
  a.form.hidden = false;
  if (confirm) a.ok.focus(); else { a.input.focus(); a.input.select(); }
}
function closeAsk(restoreFocus = true) {
  if (!asking) return;
  const back = asking.returnTo;
  // Give focus back to the control that opened the form, unless the action already moved it somewhere better (a new file's editor).
  const focusWasHere = ui.ask.form.contains(document.activeElement);
  asking = null;
  ui.ask.form.hidden = true;
  if (restoreFocus && focusWasHere && back?.isConnected) back.focus();
}
ui.ask.form.addEventListener("submit", (e) => {
  e.preventDefault();
  if (!asking) return;
  const err = asking.commit(ui.ask.input.value.trim());
  if (err) { ui.ask.error.textContent = err; ui.ask.error.hidden = false; (ui.ask.input.hidden ? ui.ask.ok : ui.ask.input).focus(); return; }
  closeAsk();
});
ui.ask.cancel.addEventListener("click", () => closeAsk());
ui.ask.form.addEventListener("keydown", (e) => { if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); closeAsk(); } });

// ---------------------------------------------------------------- rendering the project
function renderTree() {
  listing = Core.projectListing(cur.project);
  const n = cur.project.files.length;
  ui.projName.textContent = cur.project.name;
  const langs = Core.projectLanguages(cur.project).length;
  ui.projMeta.textContent = `${n} file${n === 1 ? "" : "s"} · ${langs} language${langs === 1 ? "" : "s"}`;
  tree.set({ rows: listing, collapsed: cur.collapsed, active: cur.active, entry: Core.projectEntry(cur.project) });
  updateToolbar();
}

function selectedRow() { const p = tree.selected(); return p ? listing.find((r) => r.path === p) : undefined; }
function updateToolbar() {
  const row = selectedRow();
  ui.fRename.disabled = !row;
  ui.fDelete.disabled = !row;
  ui.fEntry.disabled = !(row && !row.isFolder && row.language);
  ui.fCopy.disabled = !(row && !row.isFolder) && !cur.active;
  ui.fDownload.disabled = !row && !cur.active;
}

/** "Stored in this browser: 1.2 MB of about 3 GB", and a warning when the browser is nearly out of room for this site. */
let warnedQuota = false;
async function refreshStorageNote() {
  const note = $("#p-storage");
  if (!note || !store) return;
  const q = await store.quota();
  const where = store.kind === "indexeddb" ? "IndexedDB" : store.kind === "localStorage" && persistent ? "local storage (about 5 MB)" : "memory only";
  const mb = (n) => (n >= 1e9 ? (n / 1e9).toFixed(1) + " GB" : Math.max(0.1, n / 1e6).toFixed(n >= 1e7 ? 0 : 1) + " MB");
  note.textContent = q && q.quota ? `Kept in this browser (${where}): ${mb(q.usage)} used of about ${mb(q.quota)} available to this site.` : `Kept in this browser (${where}).`;
  if (q && q.quota && q.usage / q.quota > 0.8 && !warnedQuota) { warnedQuota = true; toast("The browser is nearly out of room for this site. Download projects you want to keep."); }
}

function renderProjects() {
  refreshStorageNote();
  ui.plist.replaceChildren(...store.list().map((r) => {
    const n = r.project.files.length;
    const b = el("button", { type: "button", "data-id": r.id, "aria-current": String(r.id === cur.id) }, el("span", { class: "pn" }, r.project.name), el("span", { class: "pm" }, `${n} file${n === 1 ? "" : "s"} · ${new Date(r.updated).toLocaleDateString()}`));
    b.addEventListener("click", () => { if (r.id !== cur.id) switchProject(r.id); });
    return el("li", {}, b);
  }));
}

function renderTabs() {
  const names = cur.open.map(baseOf);
  const nodes = cur.open.map((p, i) => {
    const on = p === cur.active;
    const dup = names.filter((n) => n === names[i]).length > 1;
    const tab = el("button", { type: "button", role: "tab", "aria-selected": String(on), tabindex: on ? "0" : "-1", "data-path": p, title: p + " (Delete closes the tab)" }, names[i], dup ? el("span", { class: "dir" }, " " + (dirOf(p) || "/")) : null);
    // The close button is a mouse and touch convenience: it is not a tab, so it is kept out of the tablist (aria-hidden) and out of the
    // tab order. The keyboard closes a tab with Delete, and the palette and the tab menu have Close Editor.
    const x = el("button", { type: "button", class: "x", "aria-hidden": "true", tabindex: "-1", title: "Close" }, "×");
    tab.addEventListener("click", () => openFile(p));
    x.addEventListener("click", () => closeFile(p));
    tab.addEventListener("auxclick", (e) => { if (e.button === 1) { e.preventDefault(); closeFile(p); } });
    return el("span", { class: "ftab" + (on ? " on" : ""), role: "presentation" }, tab, x);
  });
  const hadFocus = ui.ftabs.contains(document.activeElement);
  ui.ftabs.replaceChildren(...nodes);
  revealActiveTab();
  if (hadFocus) ui.ftabs.querySelector('[aria-selected="true"]')?.focus(); // the redraw replaced the focused tab: keep the keyboard on the open one
}
/** The strip scrolls when many files are open; keep the selected tab in view without scrolling anything else. */
function revealActiveTab() {
  const b = ui.ftabs.querySelector('[aria-selected="true"]')?.parentElement;
  if (!b) return;
  const pad = 12, left = b.offsetLeft, right = left + b.offsetWidth, c = ui.ftabs;
  if (left < c.scrollLeft + pad) c.scrollLeft = Math.max(0, left - pad);
  else if (right > c.scrollLeft + c.clientWidth - pad) c.scrollLeft = right - c.clientWidth + pad;
}

function renderCompilers() {
  const lang = activeLang();
  ui.compilerWrap.hidden = !lang;
  ui.templateWrap.hidden = !lang;
  ui.optionsWrap.hidden = !lang || !languages.find((l) => l.id === lang)?.flags; // only C, C++ and Rust take compiler flags
  if (!lang) return;
  const list = Core.compilers(lang);
  const want = cur.prefs.compiler[lang] || list[0].id;
  ui.compiler.replaceChildren(...list.map((c) => el("option", { value: c.id, selected: c.id === want }, c.label)));
  const chosen = list.find((c) => c.id === ui.compiler.value) || list[0];
  cur.prefs.compiler[lang] = chosen.id;
  ui.options.value = cur.prefs.options[lang] ?? chosen.defaultOptions;
}
function renderTemplates() {
  const lang = activeLang();
  const t = lang ? Core.templates().filter((x) => x.language === lang) : [];
  ui.template.replaceChildren(el("option", { value: "" }, "Load example…"), ...t.map((x) => el("option", { value: x.key }, x.title)));
}

/** Put the open file in the editor, or the "no file" message when none is open. */
function showActive() {
  const f = fileAt(cur.active);
  if (!f) {
    editor.show("", "", "");
    ui.empty.replaceChildren(el("p", {}, el("strong", {}, cur.project.files.length ? "No file is open." : "This project has no files.")), el("p", { class: "muted" }, cur.project.files.length ? "Pick a file from the list." : "Create one with New file, or drop a .zip or a folder here."));
    ui.empty.hidden = false;
    return;
  }
  ui.empty.hidden = true;
  editor.show(f.path, pathLang(f.path), f.text);
}

function afterActiveChange() {
  showActive();
  renderTabs();
  renderTree();
  renderCompilers();
  renderTemplates();
  liveChecks(0);
  ui.diff.hidden = true;
  const l = activeLang();
  setStatus(cur.active ? `${cur.active} · ${l ? langName(l) : "plain text"} · ready` : "No file open");
  wb?.activeChanged();
}

function renderAll() {
  renderProjects();
  ui.stdin.value = cur.stdin;
  afterActiveChange();
  renderTimeline();
}

// ---------------------------------------------------------------- files: open, close, create, rename, delete
function openFile(path, { focus = false } = {}) {
  if (!fileAt(path)) return;
  if (!cur.open.includes(path)) cur.open.push(path);
  cur.active = path;
  afterActiveChange();
  tree.selectRow(path);
  tree.reveal(path);
  persist();
  if (narrow() && sideOpen()) { setSide(false, false); ui.sideBtn.focus(); } // the drawer closes; keep keyboard focus somewhere real
  if (focus) editor.focus();
}
function closeFile(path) {
  const i = cur.open.indexOf(path);
  if (i < 0) return;
  cur.open.splice(i, 1);
  if (cur.active === path) cur.active = cur.open[i] ?? cur.open[i - 1] ?? "";
  afterActiveChange();
  ui.ftabs.querySelector('[aria-selected="true"]')?.focus();
  persist();
}
function closeOthers(path) {
  if (!cur.open.includes(path)) return;
  cur.open = [path];
  cur.active = path;
  afterActiveChange();
  persist();
}
function closeToRight(path) {
  const i = cur.open.indexOf(path);
  if (i < 0) return;
  cur.open = cur.open.slice(0, i + 1);
  if (!cur.open.includes(cur.active)) cur.active = path;
  afterActiveChange();
  persist();
}
function closeAll() {
  cur.open = [];
  cur.active = "";
  afterActiveChange();
  persist();
}
bindTabKeys(ui.ftabs, (b) => openFile(b.dataset.path));
ui.ftabs.addEventListener("keydown", (e) => {
  if ((e.key === "Delete" || e.key === "Backspace") && document.activeElement?.dataset?.path) { e.preventDefault(); closeFile(document.activeElement.dataset.path); }
});

/** Apply a structural change the core made. Returns the error text, or "" when it worked. */
function apply(result, { reopen } = {}) {
  if (!result.ok) return result.error;
  cur.project = result.project;
  const paths = new Set(cur.project.files.map((f) => f.path));
  cur.open = cur.open.filter((p) => paths.has(p));
  if (!paths.has(cur.active)) cur.active = reopen && paths.has(reopen) ? reopen : cur.open[0] ?? "";
  if (cur.active && !cur.open.includes(cur.active)) cur.open.push(cur.active);
  return "";
}

const folderOf = (row) => (!row ? "" : row.isFolder ? row.path : dirOf(row.path));

function newFile(folder = folderOf(selectedRow())) {
  ask({
    label: "New file. Folders are made as needed, e.g. src/engine.rs", value: folder ? folder + "/" : "", ok: "Create",
    commit(raw) {
      const n = Core.pathNormalize(raw);
      if (!n.ok) return n.error;
      const err = apply(Core.projectAddFile(cur.project, n.path, ""));
      if (err) return err;
      cur.active = n.path;
      cur.open.push(n.path);
      afterActiveChange();
      tree.reveal(n.path);
      persist(true);
      editor.focus();
      return "";
    },
  });
}

function renameSelected() { const row = selectedRow(); if (row) renameRow(row); }
function renamePath(path) { const row = listing.find((r) => r.path === path); if (row) renameRow(row); }
function renameRow(row) {
  ask({
    label: (row.isFolder ? "Rename or move the folder " : "Rename or move ") + row.path, value: row.path, ok: "Rename",
    commit(raw) {
      const n = Core.pathNormalize(raw);
      if (!n.ok) return n.error;
      if (n.path === row.path) return "";
      const before = cur.project.files.map((f) => f.path);
      const openBefore = [...cur.open], activeBefore = cur.active;
      const from = row.path.toLowerCase();
      const mapPath = (p) => (p.toLowerCase() === from || (row.isFolder && p.toLowerCase().startsWith(from + "/")) ? (row.isFolder ? n.path + p.slice(row.path.length) : n.path) : p);
      snapshotNow("Before renaming " + row.path);
      const err = apply(Core.projectMove(cur.project, row.path, n.path));
      if (err) return err;
      // the editor keeps each moved file's undo history, and the open tabs follow their files
      for (const old of before) {
        const moved = mapPath(old);
        if (moved !== old) editor.move(old, moved, pathLang(moved), fileAt(moved)?.text ?? "");
      }
      cur.open = [...new Set(openBefore.map(mapPath))].filter((p) => fileAt(p));
      cur.active = fileAt(mapPath(activeBefore)) ? mapPath(activeBefore) : cur.open[0] ?? "";
      if (cur.active && !cur.open.includes(cur.active)) cur.open.push(cur.active);
      if (shown && mapPath(shown.path) !== shown.path) shown = null;
      afterActiveChange();
      tree.selectRow(n.path);
      tree.reveal(n.path);
      persist(true);
      toast(`Moved to ${n.path}`);
      return "";
    },
  });
}

function deleteSelected() { const row = selectedRow(); if (row) deleteRow(row); }
function deletePath(path) { const row = listing.find((r) => r.path === path); if (row) deleteRow(row); }
function deleteRow(row) {
  const doomed = cur.project.files.filter((f) => f.path === row.path || f.path.startsWith(row.path + "/")).map((f) => f.path);
  snapshotNow("Before deleting " + row.path);
  const err = apply(row.isFolder ? Core.projectRemoveFolder(cur.project, row.path) : Core.projectRemoveFile(cur.project, row.path));
  if (err) { toast(err); return; }
  doomed.forEach((p) => editor.forget(p));
  if (shown && doomed.includes(shown.path)) shown = null;
  afterActiveChange();
  tree.selectRow("");
  persist(true);
  toast(`Deleted ${row.path}. To undo, open the Timeline and Restore the statepoint before it`);
}

function setEntry() { const row = selectedRow(); if (row && !row.isFolder) setEntryPath(row.path); }
function setEntryPath(path) {
  const err = apply(Core.projectSetEntry(cur.project, path));
  if (err) { toast(err); return; }
  renderTree();
  persist(true);
  toast(`Run now starts from ${path} when the open file is not source code`);
}

async function copyContents(path) {
  const f = fileAt(path);
  if (!f) { toast("Select a file to copy"); return; }
  toast((await copyText(f.text)) ? `Copied ${path}` : "The browser would not copy; select the text and copy it yourself");
}
async function copyPath(path) {
  toast((await copyText(path)) ? `Copied the path ${path}` : "The browser would not copy; select the name and copy it yourself");
}
function copyFile() {
  const row = selectedRow();
  copyContents(row && !row.isFolder ? row.path : cur.active);
}

/** A copy of a file next to it: name-copy.ext, name-copy-2.ext, … */
function duplicatePath(path) {
  const f = fileAt(path);
  if (!f) return;
  const base = baseOf(path), dir = dirOf(path), dot = base.lastIndexOf(".");
  const stem = dot > 0 ? base.slice(0, dot) : base, ext = dot > 0 ? base.slice(dot) : "";
  const taken = (p) => cur.project.files.some((x) => x.path.toLowerCase() === p.toLowerCase());
  let n = 1, candidate;
  do { candidate = (dir ? dir + "/" : "") + stem + "-copy" + (n > 1 ? "-" + n : "") + ext; n++; } while (taken(candidate));
  const err = apply(Core.projectAddFile(cur.project, candidate, f.text));
  if (err) { toast(err); return; }
  if (!cur.open.includes(candidate)) cur.open.push(candidate);
  cur.active = candidate;
  afterActiveChange();
  tree.reveal(candidate);
  tree.selectRow(candidate);
  persist(true);
  toast(`Copied to ${candidate}`);
}

// ---- folding the tree from outside (the context menu)
function setCollapsed(list) {
  cur.collapsed = list;
  tree.set({ rows: listing, collapsed: list, active: cur.active, entry: Core.projectEntry(cur.project) });
  persist();
}
const isCollapsed = (p) => cur.collapsed.includes(p);
const toggleFolder = (p) => setCollapsed(isCollapsed(p) ? cur.collapsed.filter((x) => x !== p) : [...cur.collapsed, p]);
const collapseAll = () => setCollapsed(listing.filter((r) => r.isFolder).map((r) => r.path));
const expandAll = () => setCollapsed([]);
function revealInTree(path) {
  setSide(true, false);
  showSide("files");
  tree.reveal(path);
  tree.selectRow(path);
}

function downloadSelected() { downloadPath(selectedRow()?.path ?? cur.active); }
function downloadPath(path) {
  const row = listing.find((r) => r.path === path);
  if (row?.isFolder) {
    const z = exportFolderZip(cur.project, row.path, new Date());
    if (!z.count) { toast("That folder has no files"); return; }
    download(z.name, z.bytes, "application/zip");
    toast(`Downloaded ${z.name} (${z.count} file${z.count === 1 ? "" : "s"})`);
    return;
  }
  const f = fileAt(path);
  if (!f) { toast("Nothing to download: pick a file"); return; }
  download(baseOf(path), f.text, "text/plain;charset=utf-8");
  toast(`Downloaded ${baseOf(path)}`);
}

ui.fNew.addEventListener("click", () => newFile());
ui.fRename.addEventListener("click", renameSelected);
ui.fDelete.addEventListener("click", deleteSelected);
ui.fEntry.addEventListener("click", setEntry);
ui.fCopy.addEventListener("click", copyFile);
ui.fDownload.addEventListener("click", downloadSelected);

// ---------------------------------------------------------------- projects: new, switch, duplicate, rename, delete
function sessionFor(rec) {
  let h = histories.get(rec.id);
  // key "" so the first run or snapshot always makes a statepoint, as it did before projects
  if (!h) { h = { session: new Core.ProjectSession({ project: rec.project, active: rec.active, stdin: rec.stdin }), key: "" }; histories.set(rec.id, h); }
  return h;
}

function enter(rec, notice) {
  if (cur) flush();
  cur = rec;
  store.switchTo(rec.id);
  current = sessionFor(rec);
  editor.reset();
  shown = null;
  clearOutput();
  closeAsk(false);
  tree.set({ rows: [], selected: "" });
  renderAll();
  showPanel("output");
  if (notice) ui.summary.textContent = notice;
}
function switchProject(id) {
  const rec = store.get(id);
  if (!rec) return;
  enter(rec, `Opened “${rec.project.name}”. Press Ctrl+Enter to run.`);
  if (narrow()) setSide(false, false);
  showSide("files");
}

function savedOrWarn(r) { watchSave(r); }

function newProject() {
  ask({ label: "Name of the new, empty project", value: "", ok: "Create", commit(name) {
    if (!name) return "Give the project a name";
    const { rec, saved } = store.create(name);
    savedOrWarn(saved);
    enter(rec, `Created “${rec.project.name}”. Add a file with New file.`);
    showSide("files");
    return "";
  } });
}
function starterProject() {
  const { rec, saved } = store.createStarter("Starter examples");
  savedOrWarn(saved);
  enter(rec, "One starter program per language. Open a file, then press Ctrl+Enter.");
  showSide("files");
}
function duplicateProject() {
  ask({ label: "Name for the copy", value: cur.project.name + " copy", ok: "Duplicate", commit(name) {
    if (!name) return "Give the copy a name";
    flush();
    const made = store.duplicate(cur.id, name);
    if (!made) return "Could not copy this project";
    savedOrWarn(made.saved);
    enter(made.rec, `Copied to “${made.rec.project.name}”.`);
    return "";
  } });
}
function renameProject() {
  ask({ label: "Rename the project", value: cur.project.name, ok: "Rename", commit(name) {
    if (!name) return "Give the project a name";
    cur.project = Core.projectRename(cur.project, name);
    persist(true);
    renderTree();
    renderProjects();
    return "";
  } });
}
function deleteProject() {
  ask({ label: `Delete the project “${cur.project.name}” from this browser? This cannot be undone. Download it first if you want a copy.`, ok: "Delete", confirm: true, commit() {
    const gone = cur.project.name;
    histories.delete(cur.id);
    const next = store.remove(cur.id);
    cur = null;
    enter(next, `Deleted “${gone}”.`);
    return "";
  } });
}
function downloadProject(rec = cur) {
  if (!rec.project.files.length) { toast("The project has no files yet"); return; }
  const z = exportProjectZip(rec.project, new Date());
  download(z.name, z.bytes, "application/zip");
  toast(`Downloaded ${z.name}`);
}
/** The project as a folder VS Code opens properly: its files, .vscode settings, and the extensions to install (Copilot among them). */
function downloadForVsCode(rec = cur) {
  if (!rec.project.files.length) { toast("The project has no files yet"); return; }
  const z = exportVsCodeZip(rec.project, Core.vsCodeFiles(rec.project, prefs.settings), new Date());
  download(z.name, z.bytes, "application/zip");
  toast(`Downloaded ${z.name}. Unzip it, open the folder in VS Code and accept the recommended extensions (GitHub Copilot is one).`);
}
/** An action on a project from the Projects list. Rename, duplicate and delete work on the open project, so it is opened first. */
function projectAction(id, what) {
  const rec = store.get(id);
  if (!rec) return;
  if (what === "zip") { downloadProject(rec); return; }
  if (what === "vscode") { downloadForVsCode(rec); return; }
  if (id !== cur.id) switchProject(id);
  if (what === "rename") renameProject(); else if (what === "duplicate") duplicateProject(); else if (what === "delete") deleteProject();
}
ui.pNew.addEventListener("click", newProject);
ui.pStarter.addEventListener("click", starterProject);
ui.pDup.addEventListener("click", duplicateProject);
ui.pRename.addEventListener("click", renameProject);
ui.pDelete.addEventListener("click", deleteProject);
ui.pZip.addEventListener("click", () => downloadProject());

// ---------------------------------------------------------------- import: .zip, a folder, loose files
function showReport(title, kept, skipped) {
  if (!skipped.length) { ui.report.hidden = true; return; }
  const shownRows = skipped.slice(0, 40);
  const close = el("button", { type: "button", class: "mini" }, "Dismiss");
  close.addEventListener("click", () => { ui.report.hidden = true; });
  fill(ui.report,
    el("div", { class: "rhead" }, el("h3", {}, title), close),
    el("p", {}, `${kept} file${kept === 1 ? "" : "s"} kept, ${skipped.length} left out:`),
    el("ul", {}, shownRows.map((s) => el("li", {}, el("strong", {}, s.path), " — " + s.reason))),
    skipped.length > shownRows.length ? el("p", { class: "muted" }, `…and ${skipped.length - shownRows.length} more.`) : null,
  );
  ui.report.hidden = false;
}

const baseName = (name) => name.replace(/\.zip$/i, "");

/** Zip or folder or files, as a new project. */
async function importAsProject(picked) {
  if (!picked.length) return;
  setStatus("Reading…");
  const kind = classify(picked);
  let result, fallback;
  try {
    if (kind.zip) {
      const { files, skipped } = await readFiles([kind.zip]);
      fallback = baseName(kind.zip.name);
      if (!files.length) { showReport("Import failed", 0, skipped); toast(skipped[0]?.reason ?? "Could not read that file"); setStatus("Import failed"); return; }
      result = await importArchive(Core, files[0].data, fallback);
    } else {
      const { files, skipped: unread } = await readFiles(kind.loose);
      const rel = kind.loose[0].webkitRelativePath || kind.loose[0].path || "";
      fallback = rel.includes("/") ? rel.split("/")[0] : "Imported files"; // a picked or dropped folder names the project
      result = importFiles(Core, files, fallback);
      result.skipped = [...unread, ...result.skipped];
    }
  } catch (e) {
    showReport("Import failed", 0, [{ path: kind.zip?.name ?? "selection", reason: String(e?.message ?? e) }]);
    toast("Could not import: " + String(e?.message ?? e));
    setStatus("Import failed");
    showSide("projects");
    return;
  }
  if (!result.project.files.length) {
    showReport("Nothing to import", 0, result.skipped);
    toast("No usable text files were found");
    setStatus("Import found no files");
    showSide("projects");
    return;
  }
  const { rec, saved } = store.create(result.project.name, result.project);
  savedOrWarn(saved);
  enter(rec, `Imported “${rec.project.name}”: ${rec.project.files.length} file${rec.project.files.length === 1 ? "" : "s"}.`);
  showReport("Import report", rec.project.files.length, result.skipped);
  showSide(result.skipped.length ? "projects" : "files");
  toast(`Imported ${rec.project.files.length} file${rec.project.files.length === 1 ? "" : "s"}` + (result.skipped.length ? `; ${result.skipped.length} left out` : ""));
}

/** Loose files, into the open project. Each one is judged on its own; an existing file is replaced (undoable). */
async function addToProject(picked) {
  if (!picked.length) return;
  const { files, skipped } = await readFiles(picked);
  const left = [...skipped];
  const staged = [];
  for (const f of files) {
    const text = decodeText(f.data);
    if (text === null) left.push({ path: f.path, reason: "binary file (the IDE holds text)" });
    else staged.push({ path: f.path, text });
  }
  if (!staged.length) { showReport("Nothing added", 0, left); toast("No usable text files were found"); showSide("projects"); return; }
  snapshotNow("Before adding files");
  let added = 0;
  let last = "";
  for (const s of staged) {
    const n = Core.pathNormalize(s.path);
    if (!n.ok) { left.push({ path: s.path, reason: n.error }); continue; }
    const exists = cur.project.files.some((f) => f.path.toLowerCase() === n.path.toLowerCase());
    const r = exists ? Core.projectSetText(cur.project, cur.project.files.find((f) => f.path.toLowerCase() === n.path.toLowerCase()).path, s.text) : Core.projectAddFile(cur.project, n.path, s.text);
    if (!r.ok) { left.push({ path: s.path, reason: r.error }); continue; }
    cur.project = r.project;
    const real = cur.project.files.find((f) => f.path.toLowerCase() === n.path.toLowerCase()).path;
    editor.setText(real, pathLang(real), s.text);
    added++;
    last = real;
  }
  if (last) { if (!cur.open.includes(last)) cur.open.push(last); cur.active = last; }
  afterActiveChange();
  if (last) tree.reveal(last);
  persist(true);
  showReport("Import report", added, left);
  toast(`Added ${added} file${added === 1 ? "" : "s"}` + (left.length ? `; ${left.length} left out` : ""));
}

ui.pImportZip.addEventListener("click", async () => importAsProject(await pick(ui.pickZip)));
ui.pImportFolder.addEventListener("click", async () => importAsProject(await pick(ui.pickFolder)));
ui.pAdd.addEventListener("click", async () => addToProject(await pick(ui.pickFiles)));

// dropping files anywhere on the work area
{
  let depth = 0;
  const hasFiles = (e) => [...(e.dataTransfer?.types ?? [])].includes("Files");
  ui.work.addEventListener("dragenter", (e) => { if (!hasFiles(e)) return; e.preventDefault(); depth++; ui.work.classList.add("dropping"); });
  ui.work.addEventListener("dragover", (e) => { if (!hasFiles(e)) return; e.preventDefault(); e.dataTransfer.dropEffect = "copy"; });
  ui.work.addEventListener("dragleave", () => { depth = Math.max(0, depth - 1); if (!depth) ui.work.classList.remove("dropping"); });
  ui.work.addEventListener("drop", async (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    depth = 0;
    ui.work.classList.remove("dropping");
    const all = await filesFromDrop(e.dataTransfer);
    const dropped = await wb.takeVsix(all); // a .vsix is VS Code content (themes, snippets), not project files
    if (!dropped.length) return;
    const isFolder = dropped.some((f) => (f.path || "").includes("/"));
    if (isFolder || (dropped.length === 1 && /\.zip$/i.test(dropped[0].name))) await importAsProject(dropped); else await addToProject(dropped);
  });
}

// ---------------------------------------------------------------- the editor's problems and run output
// The run result currently on screen, and the exact source it was produced from. Live checks must never overwrite
// compiler diagnostics that still describe the text in the editor.
function clearOutput() { shown = null; ui.summary.textContent = ""; ui.summary.className = ""; ui.stdout.textContent = ""; ui.stderr.textContent = ""; }

/** `path` is the file the list describes: its markers go in the editor only while that file is the one open. */
function renderProblems(list, path) {
  const n = list.filter((d) => d.severity === "error").length, w = list.filter((d) => d.severity === "warning").length;
  ui.pcount.textContent = list.length ? `${n ? n + "E " : ""}${w ? w + "W" : ""}`.trim() || String(list.length) : "";
  const here = path === cur.active;
  if (!list.length) { ui.problems.replaceChildren(el("p", { class: "muted" }, "No problems.")); if (here) editor.mark([]); return; }
  ui.problems.replaceChildren(...list.map((d) => {
    const row = el("button", { class: "prob " + d.severity }, el("span", { class: "sev" }, d.severity), el("span", { class: "loc" }, d.line ? `${d.line}:${d.column}` : "–"), el("span", { class: "msg" }, d.message), d.fix ? el("span", { class: "fix" }, "Fix: " + d.fix) : null);
    row.addEventListener("click", () => { if (path && path !== cur.active) openFile(path); if (d.line) editor.reveal(d.line, d.column); });
    return row;
  }));
  if (here) editor.mark(list);
}

function renderReport(rep, extra, target) {
  const multi = cur.project.files.length > 1;
  ui.summary.textContent = (multi ? target + " · " : "") + rep.summary + extra;
  ui.summary.className = rep.ok ? "ok" : "bad";
  ui.stdout.textContent = rep.stdout;
  ui.stderr.textContent = rep.stderr;
  renderProblems(rep.diagnostics, target);
  showPanel(rep.ok || !rep.diagnostics.some((d) => d.severity === "error") ? "output" : "problems");
}

let liveTimer;
function liveChecks(delay = 350) {
  clearTimeout(liveTimer);
  liveTimer = setTimeout(() => {
    const path = cur.active, text = activeText();
    // live problems only replace the list when no run result still describes the text in the editor
    if (shown && shown.path === path && shown.source === text) { renderProblems(shown.diagnostics, path); return; }
    const lang = pathLang(path);
    renderProblems(lang ? Core.localChecks(lang, text) : [], path);
    if (text.length > limits.fileChars) setStatus(`${path} is over ${limits.fileChars.toLocaleString()} characters: it cannot be run, and an import would leave it out`);
  }, delay);
}
function onChange(text) {
  const f = fileAt(cur.active);
  if (!f) return;
  f.text = text;
  persist();
  liveChecks();
  wb?.textChanged(cur.active, text);
}

// ---------------------------------------------------------------- statepoint timeline UI
function snapshotNow(label) { return snapshot(label, false); }
/** A statepoint of the whole project. Unless forced, nothing is saved when it would repeat the latest one. */
function snapshot(label, force = true) {
  const key = projectKey();
  if (!force && current.key === key) return false;
  current.session.snapshot(label, stateOf());
  current.key = key;
  renderTimeline();
  return true;
}
function fmtTime(ms) { return new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" }); }
// Forking asks for a branch name with an inline form (no blocking window.prompt: unusable in embeds and automation).
const forkName = el("input", { type: "text", id: "fork-name", maxlength: "40", spellcheck: "false", "aria-label": "New branch name" });
const forkBar = el("form", { class: "forkbar", hidden: true }, el("label", { for: "fork-name" }, "New branch "), forkName, el("button", { type: "submit", class: "mini" }, "Create"), el("button", { type: "button", class: "mini", id: "fork-cancel" }, "Cancel"));
ui.timeline.before(forkBar);
let forkFrom = null;
function askBranchName(entry) { forkFrom = entry; forkName.value = "experiment"; forkBar.hidden = false; forkName.focus(); forkName.select(); }
forkBar.querySelector("#fork-cancel").addEventListener("click", () => { forkBar.hidden = true; forkFrom = null; });
forkBar.addEventListener("submit", (ev) => {
  ev.preventDefault();
  const name = forkName.value.trim().replace(/\s+/g, "-").slice(0, 40);
  if (!forkFrom || !name) { toast("Give the branch a name"); return; }
  const nid = current.session.fork(forkFrom.id, name);
  forkBar.hidden = true;
  if (nid) { const s = current.session.restore(nid); applyState(s); toast("Forked to branch " + name); } else toast("Could not fork from that point");
  forkFrom = null;
});

function renderTimeline() {
  const entries = current.session.entries();
  ui.tcount.textContent = String(entries.length);
  ui.timeline.replaceChildren(...[...entries].reverse().map((e) => {
    const restore = el("button", { class: "mini" }, "Restore");
    const fork = el("button", { class: "mini" }, "Fork");
    const diff = el("button", { class: "mini" }, "Diff");
    restore.addEventListener("click", () => { const s = current.session.restore(e.id); if (s) { applyState(s); toast("Rolled back to “" + e.label + "”"); } });
    fork.addEventListener("click", () => askBranchName(e));
    diff.addEventListener("click", () => showDiff(e));
    return el("li", { class: "tp" + (e.isHead ? " head" : "") }, el("div", { class: "tp-main" }, el("strong", {}, e.label), el("span", { class: "muted" }, ` ${e.branch} · ${fmtTime(e.createdMs)}${e.isHead ? " · head" : ""}`)), el("div", { class: "tp-actions" }, diff, restore, fork));
  }));
}

/** What changed between a statepoint and now: which files, and the line diff of one of them. */
function showDiff(entry, pathOverride) {
  const old = current.session.read(entry.id);
  if (!old) return;
  const was = new Map(old.project.files.map((f) => [f.path, f.text]));
  const now = new Map(cur.project.files.map((f) => [f.path, f.text]));
  const changed = [
    ...[...now].filter(([p]) => !was.has(p)).map(([p]) => ({ path: p, kind: "added" })),
    ...[...was].filter(([p]) => !now.has(p)).map(([p]) => ({ path: p, kind: "removed" })),
    ...[...now].filter(([p, t]) => was.has(p) && was.get(p) !== t).map(([p]) => ({ path: p, kind: "changed" })),
  ];
  const path = pathOverride ?? (changed.find((c) => c.path === cur.active) ?? changed[0])?.path ?? cur.active;
  const lines = path ? Core.diffSummary(was.get(path) ?? "", now.get(path) ?? "") : [];
  const chips = changed.map((c) => { const b = el("button", { type: "button", class: "mini", "aria-pressed": String(c.path === path) }, `${c.kind}: ${c.path}`); b.addEventListener("click", () => showDiff(entry, c.path)); return b; });
  fill(ui.diff,
    el("h3", {}, `“${entry.label}” → current`),
    changed.length ? el("div", { class: "chips" }, chips) : el("p", { class: "muted" }, "No file differs from this statepoint."),
    path && changed.length ? el("h4", {}, path) : null,
    changed.length ? el("pre", { class: "diff" }, lines.map((l) => el("span", { class: "dl " + l.kind }, (l.kind === "added" ? "+ " : l.kind === "removed" ? "- " : "  ") + l.text + "\n"))) : null,
  );
  ui.diff.hidden = false;
}

/** Make a restored or forked statepoint the open project. */
function applyState(s) {
  cur.project = s.project;
  cur.stdin = s.stdin ?? "";
  const paths = new Set(cur.project.files.map((f) => f.path));
  cur.open = cur.open.filter((p) => paths.has(p));
  cur.active = paths.has(s.active) ? s.active : cur.open[0] ?? cur.project.files[0]?.path ?? "";
  if (cur.active && !cur.open.includes(cur.active)) cur.open.push(cur.active);
  current.key = projectKey();
  editor.reset();
  shown = null;
  clearOutput();
  renderAll();
  persist(true);
}

// ---------------------------------------------------------------- running (fallback chain from the F# core)
async function callGateway(compilerId, options, source, stdin) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 35000);
  try {
    const res = await fetch(GATEWAY + "/api/run", { method: "POST", headers: { "content-type": "application/json" }, body: Core.requestJson(compilerId, options, source, stdin), signal: ctrl.signal });
    const body = await res.json().catch(() => ({}));
    return { status: res.status, body };
  } finally { clearTimeout(timer); }
}

/** The open file when it is source code, otherwise the project's entry file. */
function runTarget() {
  if (cur.active && activeLang()) return cur.active;
  return Core.projectEntry(cur.project);
}

async function run() {
  if (running) return;
  const target = runTarget();
  const file = target && fileAt(target);
  if (!file) { toast("Nothing to run yet: add or open a source file"); return; }
  running = true; ui.run.disabled = true; ui.run.textContent = "Running…";
  clearOutput(); showPanel("output");
  const lang = pathLang(target), source = file.text, stdin = cur.stdin;
  const options = cur.prefs.options[lang] ?? "";
  const rec = cur;
  clearTimeout(liveTimer);
  // Show a result, remembering which text it belongs to. If the user kept typing during the run, the result is
  // still shown, but the editor markers go back to live checks for the newer text.
  const present = (rep, extra) => {
    if (cur !== rec) return; // another project was opened meanwhile: this result is not about it
    renderReport(rep, extra, target);
    shown = { path: target, source, diagnostics: rep.diagnostics };
    if (fileAt(target)?.text !== source) { shown = null; liveChecks(); }
  };
  snapshotNow("Run: " + target);
  const key = Core.fingerprint([lang, cur.prefs.compiler[lang] || "", options, source, stdin]);
  const notes = [];
  try {
    for (const step of Core.chain(lang, cur.prefs.compiler[lang])) {
      if (step.kind === "compiler") {
        setStatus(`Compiling ${target} on ${step.compiler.label}…`);
        let r;
        try { r = await callGateway(step.compiler.id, step.compiler.id === cur.prefs.compiler[lang] ? options : step.compiler.defaultOptions, source, stdin); }
        catch (e) { notes.push(`${step.compiler.label}: ${e.name === "AbortError" ? "timed out" : "unreachable"}`); continue; }
        if (r.status === 200) {
          const rep = Core.interpret(lang, { engine: step.compiler.label, buildFailed: r.body.buildFailed, buildOutput: r.body.buildOutput, stdout: r.body.stdout, stderr: r.body.stderr, exitCode: r.body.exitCode, timedOut: r.body.timedOut, durationMs: r.body.durationMs, cached: !!r.body.cached });
          if (rep.phase !== "infrastructure") warm.set(key, rep);
          present(rep, notes.length ? `  (fell back after: ${notes.join("; ")})` : "");
          setStatus(`${rep.ok ? "OK" : "Failed"} · ${target} · ${step.compiler.label}`);
          return;
        }
        if (r.status === 429) { present(Core.infrastructure(r.body.error || "Rate limited. Wait a moment and run again.", []), ""); setStatus("Rate limited"); return; }
        if (r.status >= 400 && r.status < 500) { present(Core.infrastructure("Request rejected: " + (r.body.error || r.status), []), ""); setStatus("Rejected"); return; }
        notes.push(`${step.compiler.label}: ${r.body.error || "HTTP " + r.status}`);
      } else if (step.kind === "warm") {
        const hit = warm.get(key);
        if (hit) { present(hit, `  (showing your last result for identical code; engines unavailable: ${notes.join("; ")})`); setStatus("Warm result"); return; }
      } else if (step.kind === "local") {
        const local = Core.localChecks(lang, source);
        const rep = Core.infrastructure("Could not reach an execution engine (" + notes.join("; ") + "). Showing offline checks only.", local);
        present(rep, ""); setStatus("Offline checks only");
        if (local.some((d) => d.severity === "error")) return; // real problems found: that is the useful answer
      } else if (step.kind === "escalate") {
        ui.summary.textContent += "  " + step.message;
        return;
      }
    }
  } finally { running = false; ui.run.disabled = false; ui.run.textContent = "▶ Run"; persist(true); }
}

// ---------------------------------------------------------------- operations the workbench (palette, menus, search) uses
async function shareLink() {
  try {
    const s = await encodeShare();
    const url = location.origin + location.pathname + "#s=" + s;
    if (url.length > 12000) toast("Large project: the link may not open everywhere");
    if (await copyText(url)) toast("Link copied"); else { history.replaceState(null, "", "#s=" + s); toast("Link is in the address bar; copy it from there"); }
  } catch { toast("Could not make a link for this project"); }
}

async function copyOutput(which) {
  const text = which === "out" ? ui.stdout.textContent : which === "err" ? ui.stderr.textContent : [...ui.problems.querySelectorAll(".prob")].map((r) => r.textContent).join("\n");
  if (!text) { toast("Nothing to copy"); return; }
  toast((await copyText(text)) ? "Copied" : "The browser would not copy; select the text and copy it yourself");
}
function clearOutputNow() { clearOutput(); toast("Output cleared"); }

/** Change every line ending of the open file. The editor keeps a file's own endings, so this is how to change them. */
function setEol(eol) {
  const f = fileAt(cur.active);
  if (!f) return;
  const text = f.text.replace(/\r\n|\r|\n/g, eol);
  if (text === f.text) { toast(`Already ${eol === "\n" ? "LF" : "CRLF"}`); return; }
  snapshotNow("Before changing line endings of " + f.path);
  f.text = text;
  editor.setText(f.path, pathLang(f.path), text);
  persist();
  liveChecks();
  toast(`Line endings are now ${eol === "\n" ? "LF" : "CRLF"}`);
}

/** Search and replace changed these files: take the new project, and show the new text in the editor. */
function applyReplaced(project, paths) {
  cur.project = project;
  for (const p of paths) { const f = fileAt(p); if (f) editor.setText(p, pathLang(p), f.text); }
  afterActiveChange();
  persist(true);
}

const canRun = () => { const t = runTarget(); return !!(t && fileAt(t)); };

/** What commands, menus and the palette may do. They never touch the page or the project except through this. */
const app = {
  Core, get editor() { return editor; }, get prefs() { return prefs; }, get storage() { return backing; },
  toast, ask, narrow, setSide, showSide, showPanel, toggleSide: () => setSide(!sideOpen()),
  project: () => cur.project, projects: () => store.list(), currentProjectId: () => cur.id, langName,
  hasFile: () => !!fileAt(cur.active), activePath: () => cur.active, activeText, activeLanguage: activeLang, languageOf: pathLang,
  openPaths: () => [...cur.open], openCount: () => cur.open.length, entryPath: () => Core.projectEntry(cur.project),
  rowOf: (path) => listing.find((r) => r.path === path), selectRow: (path) => tree.selectRow(path),
  openFile, openPath: (path) => openFile(path, { focus: true }),
  newFile, newFileIn: newFile, renameSelected, renameActive: () => renamePath(cur.active), renamePath,
  deleteSelected, deleteActive: () => deletePath(cur.active), deletePath, duplicateActive: () => duplicatePath(cur.active), duplicatePath,
  closeTab: closeFile, closeOthers, closeToRight, closeAll, copyPath, copyContents, downloadPath, setEntry: setEntryPath,
  revealInTree, collapseAll, expandAll, toggleFolder, isCollapsed,
  newProject, starterProject, duplicateProject, renameProject, deleteProject, downloadProject: () => downloadProject(), downloadForVsCode: () => downloadForVsCode(), projectAction,
  importZip: async () => importAsProject(await pick(ui.pickZip)), importFolder: async () => importAsProject(await pick(ui.pickFolder)), addFiles: async () => addToProject(await pick(ui.pickFiles)),
  run, snapshot: () => { snapshot("Snapshot"); toast("Statepoint saved"); }, share: shareLink, canRun,
  copyOutput, clearOutputNow, setEol, applyReplaced, snapshotNow,
};

// ---------------------------------------------------------------- wiring
async function init() {
  const storage = safe(() => { localStorage.setItem("fsharpened.ide.probe", "1"); localStorage.removeItem("fsharpened.ide.probe"); return localStorage; }, null);
  backing = storage ?? memoryStorage();           // settings, themes and snippets: small, kept in localStorage
  // projects: IndexedDB (a far larger quota, and failures are reported), with localStorage as the fallback (store.js, ADR-0024)
  store = createStore(Core, backing, Date.now, {
    indexedDB: safe(() => window.indexedDB, undefined),
    estimate: () => navigator.storage.estimate(),
    persist: () => navigator.storage.persist(),
  });
  const { migrated, kind } = await store.load();
  persistent = !!storage || kind === "indexeddb";
  store.keepPersistent();                         // ask the browser not to evict the projects under storage pressure
  prefs = createPrefs(Core, backing);
  prefs.apply();
  editor = createEditor($("#editor"), {
    onChange, onRun: run, onCursor: (i) => wb?.cursor(i),
    snippets: (lang) => prefs.snippetsFor(lang), snippetTemplate: (body, ctx) => Core.snippetTemplate(body, ctx), snippetContext: () => wb.snippetContext(),
  });
  editor.configure(prefs.settings);
  wb = createWorkbench(app);
  tree = createTree(ui.tree, {
    langName,
    onOpen: (path) => openFile(path),
    onSelect: () => updateToolbar(),
    onCollapse: (list) => { cur.collapsed = list; persist(); },
  });

  // a shared link wins over saved state, and becomes its own project instead of overwriting yours
  let notice = "";
  let start = store.current();
  const m = location.hash.match(/^#s=(.+)$/);
  if (m) {
    try {
      const origin = Core.fingerprint(["share", m[1]]);
      const known = store.findByOrigin(origin);
      if (known) { start = known; notice = "Opened the shared project you already had."; }
      else {
        const made = projectFromShare(await decodeShare(m[1]));
        if (made && made.project.files.length) {
          const { rec, saved } = store.create(made.project.name, made.project, { ...made.extra, origin });
          savedOrWarn(saved);
          start = rec;
          notice = `Opened a shared project as “${rec.project.name}”. Your own projects are untouched.`;
        } else notice = "That share link has nothing in it.";
      }
    } catch { notice = "Could not read that share link."; }
    history.replaceState(null, "", location.pathname + location.search);
  }

  cur = null;
  // Wide windows show the sidebar (unless the person hid it); narrow ones keep it as a drawer that starts closed.
  // Crossing the breakpoint, by resizing or rotating, picks the right default again.
  const sideDefault = () => safe(() => localStorage.getItem(SIDE_KEY), "1") !== "0" && !narrow();
  setSide(sideDefault(), false);
  matchMedia("(max-width: 56.25rem)").addEventListener?.("change", () => setSide(sideDefault(), false));
  showSide("files");
  enter(start);
  ui.stdin.addEventListener("input", () => { cur.stdin = ui.stdin.value; persist(); });
  ui.compiler.addEventListener("change", () => { const lang = activeLang(); cur.prefs.compiler[lang] = ui.compiler.value; const c = Core.compilers(lang).find((x) => x.id === ui.compiler.value); ui.options.value = c.defaultOptions; cur.prefs.options[lang] = c.defaultOptions; persist(); });
  ui.options.addEventListener("input", () => { cur.prefs.options[activeLang()] = ui.options.value; persist(); });
  ui.template.addEventListener("change", () => {
    const lang = activeLang();
    const t = Core.templates().find((x) => x.key === ui.template.value);
    if (!t || !cur.active) return;
    snapshotNow("Before loading “" + t.title + "”");
    fileAt(cur.active).text = t.code;
    editor.setText(cur.active, lang, t.code);
    ui.template.value = "";
    persist();
    liveChecks();
    toast("Loaded “" + t.title + "”");
  });
  ui.run.addEventListener("click", run);
  ui.snap.addEventListener("click", () => { snapshot("Snapshot"); toast("Statepoint saved"); showPanel("timeline"); });
  ui.sideBtn.addEventListener("click", () => setSide(!sideOpen()));
  $("#btn-side-close").addEventListener("click", () => { setSide(false, false); ui.sideBtn.focus(); });
  ui.share.addEventListener("click", shareLink);
  addEventListener("keydown", (e) => {
    const mod = e.ctrlKey || e.metaKey;
    if (mod && e.key.toLowerCase() === "s") { e.preventDefault(); snapshot("Snapshot"); toast("Statepoint saved"); }
    else if (mod && e.key.toLowerCase() === "b") { e.preventDefault(); setSide(!sideOpen()); }
    else if (e.key === "Escape" && narrow() && sideOpen() && ui.side.contains(document.activeElement)) { setSide(false, false); ui.sideBtn.focus(); }
  });
  addEventListener("pagehide", flush);
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "hidden") flush(); });

  showPanel("output");
  ui.summary.textContent = notice || (migrated ? "Your earlier programs were kept as the project “My workspace”. Press Ctrl+Enter (or ▶ Run) to compile and run." : "Press Ctrl+Enter (or ▶ Run) to compile and run.");
  if (!persistent) { setStatus("Projects are not saved: this browser blocks storage. Download to keep your work."); toast("This browser blocks storage, so projects are not saved. Use Download .zip to keep your work."); }

  bindOptions();
  bindSplitter();
  bindConnectivity();
  window.__ideReady = true; // read by the load watchdog in site.js
  $("#ide").classList.add("ready");
}

// ---------------------------------------------------------------- adaptive layout
/** Narrow screens keep tabs and Run in view and tuck the rest behind an Options button. */
function bindOptions() {
  const bar = $("#bar"), btn = $("#btn-opts");
  btn.addEventListener("click", () => { const open = !bar.classList.contains("open"); bar.classList.toggle("open", open); btn.setAttribute("aria-expanded", String(open)); });
}

/** Draggable and keyboard-operable divider between the editor and the output panels. */
function bindSplitter() {
  const split = $("#split"), sep = $("#sep"), KEY = "fsharpened.ide.split";
  const stacked = () => matchMedia("(max-width: 56.25em)").matches;
  const orient = () => sep.setAttribute("aria-orientation", stacked() ? "horizontal" : "vertical");
  let share = 0.58; // share of the space given to the editor
  const applyShare = (p, remember) => {
    share = Math.min(0.8, Math.max(0.2, p));
    const fr = (share / (1 - share)).toFixed(3) + "fr";
    split.style.setProperty("--ed", fr);
    split.style.setProperty("--edv", fr);
    sep.setAttribute("aria-valuemin", "20"); sep.setAttribute("aria-valuemax", "80"); sep.setAttribute("aria-valuenow", String(Math.round(share * 100)));
    if (remember) safe(() => localStorage.setItem(KEY, String(share)));
  };
  const saved = Number(safe(() => localStorage.getItem(KEY), null));
  if (saved >= 0.2 && saved <= 0.8) applyShare(saved, false); else { sep.setAttribute("aria-valuemin", "20"); sep.setAttribute("aria-valuemax", "80"); sep.setAttribute("aria-valuenow", "58"); }
  orient();
  matchMedia("(max-width: 56.25em)").addEventListener?.("change", orient);

  sep.addEventListener("pointerdown", (e) => { sep.setPointerCapture(e.pointerId); sep.classList.add("drag"); e.preventDefault(); });
  sep.addEventListener("pointermove", (e) => {
    if (!sep.hasPointerCapture(e.pointerId)) return;
    const r = split.getBoundingClientRect();
    applyShare(stacked() ? (e.clientY - r.top) / r.height : (e.clientX - r.left) / r.width, false);
  });
  const end = () => { if (sep.classList.contains("drag")) { sep.classList.remove("drag"); applyShare(share, true); } };
  sep.addEventListener("pointerup", end); sep.addEventListener("pointercancel", end);
  sep.addEventListener("dblclick", () => applyShare(0.58, true));
  sep.addEventListener("keydown", (e) => {
    const grow = stacked() ? "ArrowDown" : "ArrowRight", shrink = stacked() ? "ArrowUp" : "ArrowLeft";
    if (e.key === grow) applyShare(share + 0.03, true);
    else if (e.key === shrink) applyShare(share - 0.03, true);
    else if (e.key === "Home") applyShare(0.2, true);
    else if (e.key === "End") applyShare(0.8, true);
    else return;
    e.preventDefault();
  });
}

/** The IDE keeps working offline (editing, snapshots, local checks); say so instead of failing silently. */
function bindConnectivity() {
  addEventListener("offline", () => { setStatus("Offline: Run is limited to offline checks"); toast("You are offline. Editing and snapshots still work."); });
  addEventListener("online", () => { setStatus("Back online"); toast("Back online"); });
  if (!navigator.onLine) setStatus("Offline: Run is limited to offline checks");
}
init();
