// "Search in project": the sidebar panel. Matching, globs and replacement are the F# core's (Search.fs); this draws the
// form and the grouped results, and tells the page where to go or what text changed.
import { el, fill } from "./dom.js";

/**
 * host: { Core, root, project(): the open project, open(path, line, column, length), apply(project, changedPaths),
 *         snapshot(label), toast(msg), ask({label, ok, confirm, commit}) }
 */
export function createSearchPanel({ Core, root, project, open, apply, snapshot, toast, ask }) {
  const q = root.querySelector("#sr-q"), repl = root.querySelector("#sr-r");
  const inc = root.querySelector("#sr-inc"), exc = root.querySelector("#sr-exc");
  const caseB = root.querySelector("#sr-case"), wordB = root.querySelector("#sr-word"), reB = root.querySelector("#sr-re");
  const replAll = root.querySelector("#sr-replace-all"), sum = root.querySelector("#sr-sum"), out = root.querySelector("#sr-results");
  const more = root.querySelector("#sr-more");
  let last = null;          // the last result shown
  let lastOptions = null;
  let timer;
  let collapsedFiles = new Set();

  const flag = (b) => b.getAttribute("aria-pressed") === "true";
  const options = () => ({ query: q.value, caseSensitive: flag(caseB), wholeWord: flag(wordB), regex: flag(reB) });
  const searchScope = () => (inc.value.trim() || exc.value.trim() ? " (filtered)" : "");

  function run() {
    clearTimeout(timer);
    const opts = options();
    replAll.disabled = true;
    if (!opts.query) { last = null; sum.textContent = ""; fill(out); return; }
    const r = Core.searchRun(project(), opts, inc.value, exc.value);
    last = r;
    lastOptions = opts;
    if (r.error) { sum.textContent = r.error; fill(out); return; }
    const n = r.total;
    sum.textContent = n === 0
      ? `No results${searchScope()} in ${r.filesSearched} file${r.filesSearched === 1 ? "" : "s"}.`
      : `${n}${r.truncated ? "+" : ""} result${n === 1 ? "" : "s"} in ${r.files.length} file${r.files.length === 1 ? "" : "s"}${searchScope()}.${r.truncated ? " Only the first ones are shown: narrow the search." : ""}`;
    replAll.disabled = n === 0;
    draw(r);
  }

  /** The match inside its preview line, with the match itself in <mark>. */
  function preview(h) {
    const text = h.preview;
    const from = Math.max(0, h.column - 1 - h.previewStart);
    const to = Math.min(text.length, from + h.length);
    const lead = h.previewStart > 0 ? "…" : "";
    return el("span", { class: "sr-line" }, lead, text.slice(0, from), el("mark", {}, text.slice(from, to)), text.slice(to));
  }

  function draw(r) {
    fill(out, r.files.map((f) => {
      const shut = collapsedFiles.has(f.path);
      const name = f.path.slice(f.path.lastIndexOf("/") + 1), dir = f.path.includes("/") ? f.path.slice(0, f.path.lastIndexOf("/")) : "";
      const head = el("button", { type: "button", class: "sr-file", "aria-expanded": String(!shut), title: f.path },
        el("span", { class: "twisty", "aria-hidden": "true" }, shut ? "▸" : "▾"), el("span", { class: "sr-name" }, name), dir ? el("span", { class: "sr-dir" }, dir) : null,
        el("span", { class: "badge" }, f.hits.length + (f.more ? "+" : "")));
      head.addEventListener("click", () => { if (shut) collapsedFiles.delete(f.path); else collapsedFiles.add(f.path); draw(last); head.focus(); });
      const replaceFile = el("button", { type: "button", class: "mini sr-act", title: "Replace every match in this file", "aria-label": "Replace in " + f.path }, "Replace");
      replaceFile.addEventListener("click", () => replaceIn([f.path], `Replace all in ${f.path}?`));
      const hits = shut ? [] : f.hits.map((h) => {
        const b = el("button", { type: "button", class: "sr-hit", title: `${f.path}:${h.line}:${h.column}` }, el("span", { class: "sr-ln" }, String(h.line)), preview(h));
        b.addEventListener("click", () => open(f.path, h.line, h.column, h.length));
        const one = el("button", { type: "button", class: "mini sr-act", "aria-label": `Replace this match at line ${h.line} of ${f.path}` }, "Replace");
        one.addEventListener("click", () => replaceOne(f.path, h));
        return el("div", { class: "sr-row" }, b, one);
      });
      return el("div", { class: "sr-group", role: "group", "aria-label": f.path }, el("div", { class: "sr-head" }, head, replaceFile), hits, !shut && f.more ? el("p", { class: "muted sr-more" }, "More matches in this file are not listed.") : null);
    }));
  }

  /** A replacement works on the project as it is now; the search is run again first so it never replaces stale matches. */
  function replaceOne(path, hit) {
    const p = project();
    const file = p.files.find((f) => f.path === path);
    if (!file) return;
    const r = Core.searchReplaceOne(lastOptions, repl.value, file.text, hit.line, hit.column);
    if (!r.ok) { toast(r.error || "That match has changed: the search was run again"); run(); return; }
    snapshot(`Before replacing in ${path}`);
    const set = Core.projectSetText(p, path, r.text);
    if (!set.ok) { toast(set.error); return; }
    apply(set.project, [path]);
    toast("Replaced 1 match");
    run();
  }

  function replaceIn(paths, label) {
    if (!last || !last.total) return;
    const n = last.files.filter((f) => paths.includes(f.path)).reduce((a, f) => a + f.hits.length, 0);
    ask({
      label: `${label} ${n}${last.truncated ? "+" : ""} match${n === 1 ? "" : "es"} become “${repl.value}”. You can undo this from the Timeline.`, ok: "Replace", confirm: true,
      commit() {
        const r = Core.searchReplaceProject(project(), lastOptions, repl.value, paths);
        if (r.failed.length && !r.changed.length) return r.failed[0].reason;
        snapshot("Before replace in project");
        apply(r.project, r.changed.map((c) => c.path));
        const total = r.changed.reduce((a, c) => a + c.count, 0);
        toast(`Replaced ${total} match${total === 1 ? "" : "es"} in ${r.changed.length} file${r.changed.length === 1 ? "" : "s"}` + (r.failed.length ? `; ${r.failed.length} left alone (${r.failed[0].reason})` : ""));
        run();
        return "";
      },
    });
  }

  const schedule = () => { clearTimeout(timer); timer = setTimeout(run, 220); };
  q.addEventListener("input", schedule);
  inc.addEventListener("input", schedule);
  exc.addEventListener("input", schedule);
  for (const b of [caseB, wordB, reB]) b.addEventListener("click", () => { b.setAttribute("aria-pressed", String(!flag(b))); run(); });
  root.querySelector("#sr-form").addEventListener("submit", (e) => { e.preventDefault(); run(); });
  replAll.addEventListener("click", () => { if (last?.files.length) replaceIn(last.files.map((f) => f.path), "Replace all:"); });
  // Alt+C / Alt+W / Alt+R toggle the options while the form has focus, as in VS Code
  root.querySelector("#sr-form").addEventListener("keydown", (e) => {
    if (!e.altKey || e.ctrlKey || e.metaKey) return;
    const b = { c: caseB, w: wordB, r: reB }[e.key.toLowerCase()];
    if (b) { e.preventDefault(); b.click(); }
  });

  return {
    /** Show the panel ready to search: `text` fills the query, `folder` limits the search to a folder ("" is the whole project, null leaves the filter as it was). */
    focus(text = "", folder = null) {
      if (text) q.value = text.split("\n")[0].slice(0, 200);
      if (folder !== null) { inc.value = folder ? folder + "/**" : ""; if (folder) more.open = true; }
      q.focus();
      q.select();
      run();
    },
    /** The project changed under the panel (a file was edited or replaced): refresh what it shows, if anything. */
    refresh() { if (q.value) schedule(); },
    run,
  };
}
