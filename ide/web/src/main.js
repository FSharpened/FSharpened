// FSharpened web IDE shell. Logic lives in the F# core (Fable); this file is DOM wiring only. No innerHTML anywhere.
import * as Core from "../fable_out/Api.js";
import { createEditor } from "./editor.js";

const GATEWAY = __GATEWAY__;
const $ = (s) => document.querySelector(s);
const el = (tag, attrs = {}, ...kids) => {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) { if (v === false || v == null) continue; if (k === "class") e.className = v; else e.setAttribute(k, v === true ? "" : v); }
  for (const c of kids.flat()) if (c != null) e.append(c.nodeType ? c : document.createTextNode(String(c)));
  return e;
};
const safe = (fn, fallback) => { try { return fn(); } catch { return fallback; } };

// ---------------------------------------------------------------- state
const languages = Core.languages();
const STORE = "fsharpened.ide.v1";
let ws = Core.initialWorkspace();            // { active, buffers: {id: text}, stdin }
const prefs = { compiler: {}, options: {} };  // per language
let session;
let lastSnapshotText = {};
const warm = new Map();                       // layer 4: fingerprint -> report
let running = false;

function persist() {
  safe(() => localStorage.setItem(STORE, JSON.stringify({ ws, prefs })));
}
function restoreSaved() {
  const raw = safe(() => localStorage.getItem(STORE), null);
  if (!raw) return;
  const d = safe(() => JSON.parse(raw), null);
  if (!d || !d.ws) return;
  ws = { ...ws, ...d.ws, buffers: { ...ws.buffers, ...d.ws.buffers } };
  Object.assign(prefs.compiler, d.prefs?.compiler); Object.assign(prefs.options, d.prefs?.options);
}

// ---------------------------------------------------------------- share links (#s=<base64url(deflate(json))>)
const b64u = (u8) => btoa(String.fromCharCode(...u8)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const unb64u = (s) => Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
async function pipe(bytes, stream) { const r = new Blob([bytes]).stream().pipeThrough(stream); return new Uint8Array(await new Response(r).arrayBuffer()); }
async function encodeShare() {
  const lang = ws.active;
  const payload = JSON.stringify({ l: lang, c: prefs.compiler[lang] || "", o: prefs.options[lang] ?? "", t: ws.buffers[lang], i: ws.stdin });
  const bytes = new TextEncoder().encode(payload);
  return typeof CompressionStream === "function" ? "z" + b64u(await pipe(bytes, new CompressionStream("deflate-raw"))) : "p" + b64u(bytes);
}
async function decodeShare(s) {
  const bytes = unb64u(s.slice(1));
  const raw = s[0] === "z" ? await pipe(bytes, new DecompressionStream("deflate-raw")) : bytes;
  return JSON.parse(new TextDecoder().decode(raw));
}

// ---------------------------------------------------------------- UI pieces
const ui = {
  langs: $("#langs"), compiler: $("#compiler"), options: $("#options"), optionsWrap: $("#options-wrap"), template: $("#template"),
  run: $("#btn-run"), snap: $("#btn-snap"), share: $("#btn-share"), reset: $("#btn-reset"),
  summary: $("#summary"), stdout: $("#stdout"), stderr: $("#stderr"), problems: $("#problems"), pcount: $("#pcount"),
  timeline: $("#timeline"), tcount: $("#tcount"), stdin: $("#stdin"), status: $("#status"), diff: $("#diff"),
  tabs: [...document.querySelectorAll(".ptabs button")], panels: [...document.querySelectorAll(".panel")],
};
let editor;
let lastProblems = [];

function toast(msg) { const t = $("#toast"); t.textContent = msg; t.classList.add("show"); clearTimeout(toast.t); toast.t = setTimeout(() => t.classList.remove("show"), 2200); }
function setStatus(text) { ui.status.textContent = text; }
function showPanel(name) {
  ui.tabs.forEach((b) => { const on = b.dataset.p === name; b.setAttribute("aria-selected", String(on)); b.tabIndex = on ? 0 : -1; });
  ui.panels.forEach((p) => { p.hidden = p.dataset.p !== name; });
}
ui.tabs.forEach((b) => b.addEventListener("click", () => showPanel(b.dataset.p)));

// WAI-ARIA tabs: one tab stop per list, arrow keys / Home / End move between tabs.
function bindTabKeys(container, activate) {
  container.addEventListener("keydown", (e) => {
    const tabs = [...container.querySelectorAll('[role="tab"], button')];
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

function renderLangs() {
  ui.langs.replaceChildren(...languages.map((l) => {
    const b = el("button", { type: "button", role: "tab", "aria-selected": String(ws.active === l.id), tabindex: ws.active === l.id ? "0" : "-1", "data-lang": l.id, title: l.file }, l.name);
    b.addEventListener("click", () => setLanguage(l.id));
    return b;
  }));
}
function renderCompilers() {
  const list = Core.compilers(ws.active);
  const want = prefs.compiler[ws.active] || list[0].id;
  ui.compiler.replaceChildren(...list.map((c) => el("option", { value: c.id, selected: c.id === want }, c.label)));
  const cur = list.find((c) => c.id === ui.compiler.value) || list[0];
  prefs.compiler[ws.active] = cur.id;
  const isNative = ws.active === "c" || ws.active === "cpp";
  ui.optionsWrap.hidden = !isNative;
  ui.options.value = prefs.options[ws.active] ?? cur.defaultOptions;
}
function renderTemplates() {
  const t = Core.templates().filter((x) => x.language === ws.active);
  ui.template.replaceChildren(el("option", { value: "" }, "Load example…"), ...t.map((x) => el("option", { value: x.key }, x.title)));
}

// The run result currently on screen, and the exact source it was produced from. Live checks must never overwrite
// compiler diagnostics that still describe the text in the editor.
let shown = null;
function clearOutput() { shown = null; ui.summary.textContent = ""; ui.summary.className = ""; ui.stdout.textContent = ""; ui.stderr.textContent = ""; }

function renderProblems(list) {
  lastProblems = list;
  const n = list.filter((d) => d.severity === "error").length, w = list.filter((d) => d.severity === "warning").length;
  ui.pcount.textContent = list.length ? `${n ? n + "E " : ""}${w ? w + "W" : ""}`.trim() || String(list.length) : "";
  if (!list.length) { ui.problems.replaceChildren(el("p", { class: "muted" }, "No problems.")); editor.mark([]); return; }
  ui.problems.replaceChildren(...list.map((d) => {
    const row = el("button", { class: "prob " + d.severity }, el("span", { class: "sev" }, d.severity), el("span", { class: "loc" }, d.line ? `${d.line}:${d.column}` : "–"), el("span", { class: "msg" }, d.message), d.fix ? el("span", { class: "fix" }, "Fix: " + d.fix) : null);
    row.addEventListener("click", () => d.line && editor.reveal(d.line, d.column));
    return row;
  }));
  editor.mark(list);
}

function renderReport(rep, extra = "") {
  ui.summary.textContent = rep.summary + extra;
  ui.summary.className = rep.ok ? "ok" : "bad";
  ui.stdout.textContent = rep.stdout;
  ui.stderr.textContent = rep.stderr;
  renderProblems(rep.diagnostics);
  showPanel(rep.ok || !rep.diagnostics.some((d) => d.severity === "error") ? "output" : "problems");
}

// ---------------------------------------------------------------- statepoint timeline UI
function currentWorkspace() { return { active: ws.active, buffers: { ...ws.buffers }, stdin: ws.stdin }; }
function snapshot(label) {
  const id = session.snapshot(label, currentWorkspace());
  lastSnapshotText[ws.active] = ws.buffers[ws.active];
  renderTimeline();
  return id;
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
  const nid = session.fork(forkFrom.id, name);
  forkBar.hidden = true;
  if (nid) { const w = session.restore(nid); applyWorkspace(w); renderTimeline(); toast("Forked to branch " + name); } else toast("Could not fork from that point");
  forkFrom = null;
});

function renderTimeline() {
  const entries = session.entries();
  ui.tcount.textContent = String(entries.length);
  ui.timeline.replaceChildren(...[...entries].reverse().map((e) => {
    const restore = el("button", { class: "mini" }, "Restore");
    const fork = el("button", { class: "mini" }, "Fork");
    const diff = el("button", { class: "mini" }, "Diff");
    restore.addEventListener("click", () => { const w = session.restore(e.id); if (w) { applyWorkspace(w); renderTimeline(); toast("Rolled back to “" + e.label + "”"); } });
    fork.addEventListener("click", () => askBranchName(e));
    diff.addEventListener("click", () => showDiff(e));
    return el("li", { class: "tp" + (e.isHead ? " head" : "") }, el("div", { class: "tp-main" }, el("strong", {}, e.label), el("span", { class: "muted" }, ` ${e.branch} · ${fmtTime(e.createdMs)}${e.isHead ? " · head" : ""}`)), el("div", { class: "tp-actions" }, diff, restore, fork));
  }));
}
function showDiff(entry) {
  const w = session.read(entry.id);
  if (!w) return;
  const lang = ws.active;
  const lines = Core.diffSummary(w.buffers[lang] ?? "", ws.buffers[lang] ?? "");
  ui.diff.replaceChildren(el("h3", {}, `${lang.toUpperCase()}: “${entry.label}” → current`), el("pre", { class: "diff" }, lines.map((l) => el("span", { class: "dl " + l.kind }, (l.kind === "added" ? "+ " : l.kind === "removed" ? "- " : "  ") + l.text + "\n"))));
  ui.diff.hidden = false;
}
function applyWorkspace(w) {
  ws = { active: w.active, buffers: { ...w.buffers }, stdin: w.stdin ?? "" };
  for (const l of languages) editor.setText(l.id, ws.buffers[l.id]);
  ui.stdin.value = ws.stdin;
  setLanguage(ws.active, true);
  persist();
}

// ---------------------------------------------------------------- language / editing
function setLanguage(id, keepText = false) {
  ws = { ...ws, active: id };
  renderLangs(); renderCompilers(); renderTemplates();
  editor.show(id, ws.buffers[id]);
  if (keepText) editor.setText(id, ws.buffers[id]);
  liveChecks();
  clearOutput(); ui.diff.hidden = true;
  setStatus(`${languages.find((l) => l.id === id).file} · ready`);
  persist();
}
let liveTimer;
function liveChecks() {
  clearTimeout(liveTimer);
  liveTimer = setTimeout(() => {
    // live problems only replace the list when no run result still describes the text in the editor
    if (shown && shown.lang === ws.active && shown.source === ws.buffers[ws.active]) return;
    renderProblems(Core.localChecks(ws.active, ws.buffers[ws.active]));
  }, 350);
}
function onChange(text) {
  ws = { ...ws, buffers: { ...ws.buffers, [ws.active]: text } };
  persist();
  liveChecks();
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

async function run() {
  if (running) return;
  running = true; ui.run.disabled = true; ui.run.textContent = "Running…";
  clearOutput(); showPanel("output");
  const lang = ws.active, source = ws.buffers[lang], stdin = ws.stdin;
  const options = prefs.options[lang] ?? "";
  clearTimeout(liveTimer);
  // Show a result, remembering which text it belongs to. If the user kept typing during the run, the result is
  // still shown, but the editor markers go back to live checks for the newer text.
  const present = (rep, extra) => {
    renderReport(rep, extra);
    shown = { lang, source };
    if (ws.active !== lang || ws.buffers[lang] !== source) { shown = null; liveChecks(); }
  };
  if (!lastSnapshotText[lang] || lastSnapshotText[lang] !== source) snapshot("Run: " + languages.find((l) => l.id === lang).name);
  const key = Core.fingerprint([lang, prefs.compiler[lang] || "", options, source, stdin]);
  const notes = [];
  try {
    for (const step of Core.chain(lang, prefs.compiler[lang])) {
      if (step.kind === "compiler") {
        setStatus(`Compiling on ${step.compiler.label}…`);
        let r;
        try { r = await callGateway(step.compiler.id, step.compiler.id === prefs.compiler[lang] ? options : step.compiler.defaultOptions, source, stdin); }
        catch (e) { notes.push(`${step.compiler.label}: ${e.name === "AbortError" ? "timed out" : "unreachable"}`); continue; }
        if (r.status === 200) {
          const rep = Core.interpret(lang, { engine: step.compiler.label, buildFailed: r.body.buildFailed, buildOutput: r.body.buildOutput, stdout: r.body.stdout, stderr: r.body.stderr, exitCode: r.body.exitCode, timedOut: r.body.timedOut, durationMs: r.body.durationMs, cached: !!r.body.cached });
          if (rep.phase !== "infrastructure") warm.set(key, rep);
          present(rep, notes.length ? `  (fell back after: ${notes.join("; ")})` : "");
          setStatus(`${rep.ok ? "OK" : "Failed"} · ${step.compiler.label}`);
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
  } finally { running = false; ui.run.disabled = false; ui.run.textContent = "▶ Run"; persist(); }
}

// ---------------------------------------------------------------- wiring
async function init() {
  restoreSaved();
  session = new Core.Session(currentWorkspace());
  editor = createEditor($("#editor"), { onChange, onRun: run });
  ui.stdin.value = ws.stdin;
  ui.stdin.addEventListener("input", () => { ws = { ...ws, stdin: ui.stdin.value }; persist(); });
  ui.compiler.addEventListener("change", () => { prefs.compiler[ws.active] = ui.compiler.value; const c = Core.compilers(ws.active).find((x) => x.id === ui.compiler.value); ui.options.value = c.defaultOptions; prefs.options[ws.active] = c.defaultOptions; persist(); });
  ui.options.addEventListener("input", () => { prefs.options[ws.active] = ui.options.value; persist(); });
  ui.template.addEventListener("change", () => {
    const t = Core.templates().find((x) => x.key === ui.template.value);
    if (!t) return;
    snapshot("Before loading “" + t.title + "”");
    ws = { ...ws, buffers: { ...ws.buffers, [ws.active]: t.code } };
    editor.setText(ws.active, t.code); ui.template.value = ""; persist(); liveChecks(); toast("Loaded “" + t.title + "”");
  });
  ui.run.addEventListener("click", run);
  ui.snap.addEventListener("click", () => { snapshot("Snapshot"); toast("Statepoint saved"); showPanel("timeline"); });
  // No blocking confirm(): a statepoint is saved first, so Reset is undoable from the Timeline ("Before reset" → Restore).
  ui.reset.addEventListener("click", () => { snapshot("Before reset"); const w = Core.initialWorkspace(); applyWorkspace({ ...w, active: ws.active }); toast("Reset to the starter examples. Undo: Timeline → “Before reset” → Restore"); });
  ui.share.addEventListener("click", async () => {
    try { const s = await encodeShare(); const url = location.origin + location.pathname + "#s=" + s; if (url.length > 12000) toast("Large program: link may not open everywhere"); history.replaceState(null, "", "#s=" + s); await navigator.clipboard.writeText(url); toast("Link copied"); }
    catch { toast("Link is in the address bar; copy it from there"); }
  });
  addEventListener("keydown", (e) => { if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") { e.preventDefault(); snapshot("Snapshot"); toast("Statepoint saved"); } });

  // shared link wins over saved state
  const m = location.hash.match(/^#s=(.+)$/);
  if (m) {
    try {
      const d = await decodeShare(m[1]);
      if (languages.some((l) => l.id === d.l)) { ws = { ...ws, active: d.l, buffers: { ...ws.buffers, [d.l]: String(d.t ?? "") }, stdin: String(d.i ?? "") }; if (d.c) prefs.compiler[d.l] = d.c; if (typeof d.o === "string") prefs.options[d.l] = d.o; toast("Loaded shared program"); }
    } catch { toast("Could not read that share link"); }
  }
  session = new Core.Session(currentWorkspace());
  renderTimeline();
  for (const l of languages) if (l.id !== ws.active) editor.setText(l.id, ws.buffers[l.id]);
  setLanguage(ws.active, true);
  showPanel("output");
  ui.summary.textContent = "Press Ctrl+Enter (or ▶ Run) to compile and run.";

  bindTabKeys(ui.langs, (b) => { setLanguage(b.dataset.lang); ui.langs.querySelector('[aria-selected="true"]')?.focus(); });
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
  const apply = (p, persist) => {
    share = Math.min(0.8, Math.max(0.2, p));
    const fr = (share / (1 - share)).toFixed(3) + "fr";
    split.style.setProperty("--ed", fr);
    split.style.setProperty("--edv", fr);
    sep.setAttribute("aria-valuemin", "20"); sep.setAttribute("aria-valuemax", "80"); sep.setAttribute("aria-valuenow", String(Math.round(share * 100)));
    if (persist) safe(() => localStorage.setItem(KEY, String(share)));
  };
  const saved = Number(safe(() => localStorage.getItem(KEY), null));
  if (saved >= 0.2 && saved <= 0.8) apply(saved, false); else { sep.setAttribute("aria-valuemin", "20"); sep.setAttribute("aria-valuemax", "80"); sep.setAttribute("aria-valuenow", "58"); }
  orient();
  matchMedia("(max-width: 56.25em)").addEventListener?.("change", orient);

  sep.addEventListener("pointerdown", (e) => { sep.setPointerCapture(e.pointerId); sep.classList.add("drag"); e.preventDefault(); });
  sep.addEventListener("pointermove", (e) => {
    if (!sep.hasPointerCapture(e.pointerId)) return;
    const r = split.getBoundingClientRect();
    apply(stacked() ? (e.clientY - r.top) / r.height : (e.clientX - r.left) / r.width, false);
  });
  const end = () => { if (sep.classList.contains("drag")) { sep.classList.remove("drag"); apply(share, true); } };
  sep.addEventListener("pointerup", end); sep.addEventListener("pointercancel", end);
  sep.addEventListener("dblclick", () => apply(0.58, true));
  sep.addEventListener("keydown", (e) => {
    const grow = stacked() ? "ArrowDown" : "ArrowRight", shrink = stacked() ? "ArrowUp" : "ArrowLeft";
    if (e.key === grow) apply(share + 0.03, true);
    else if (e.key === shrink) apply(share - 0.03, true);
    else if (e.key === "Home") apply(0.2, true);
    else if (e.key === "End") apply(0.8, true);
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
