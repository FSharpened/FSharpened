// The multi-file side of running (ADR-0024): putting a bundled run's diagnostics back in the files they came from, and the
// small pieces of page the pipeline needs (the step list under the output, and the pipeline editor in the Projects tab).
// No state of its own: the page hands in what it shows.
import { el } from "./dom.js";

/** Diagnostics of a run, each with the project file it belongs to. A bundled run reports lines of the bundle. */
export function relocate(Core, bundle, target, rep) {
  const single = !bundle || bundle.included.length < 2;
  const output = [rep.stderr, rep.stdout].join("\n");
  return rep.diagnostics.map((d) => {
    if (single || !d.line) return { ...d, path: target };
    const at = Core.bundleLocate(bundle, output, d.line);
    if (at) return { ...d, path: at.path, line: at.line };
    // the line is in code the bundler wrote: keep the message, drop the position, and say where it was
    return { ...d, path: target, line: 0, column: 0, message: `${d.message} (line ${d.line} of the generated bundle)` };
  });
}

/** Why a project cannot be bundled, as the diagnostics the Problems tab shows. */
export function bundleDiagnostics(problems, fallback) {
  return problems.map((p) => ({ code: "bundle", severity: "error", message: p.message, line: p.line, column: p.line ? 1 : 0, endLine: p.line, endColumn: p.line ? 2 : 0, fix: "", path: p.path || fallback }));
}

/** One line per thing the bundler did that a person would want to know (files left out, includes left to the compiler). */
export function bundleNotes(bundle) {
  if (!bundle) return [];
  const used = bundle.included.length > 1 ? [`Bundled ${bundle.included.length} files into one source: ${bundle.included.join(", ")}`] : [];
  return [...used, ...bundle.notes];
}

/** The step list of a pipeline run. `steps` are { path, language, state: "run" | "ok" | "fail" | "skip", summary, stdout, stderr }. */
export function renderSteps(list, steps) {
  list.replaceChildren(...steps.map((s, i) => {
    const mark = { ok: "✓", fail: "✗", run: "…", skip: "–" }[s.state];
    const body = [];
    if (s.stdout) body.push(el("pre", { tabindex: "0", "aria-label": `Output of ${s.path}` }, s.stdout));
    if (s.stderr) body.push(el("pre", { class: "err", tabindex: "0", "aria-label": `Errors of ${s.path}` }, s.stderr));
    const head = el("span", { class: "step-head" }, el("span", { class: "step-mark", "aria-hidden": "true" }, mark), el("span", { class: "step-n" }, `${i + 1}. `), el("span", { class: "step-path" }, s.path), el("span", { class: "step-sum" }, ` ${s.summary}`));
    const li = el("li", { class: "step " + s.state }, body.length ? el("details", { open: s.state === "fail" }, el("summary", {}, head), ...body) : head);
    return li;
  }));
  list.hidden = steps.length === 0;
}

/**
 * The pipeline editor: the steps in order with move and remove buttons. `act` is { move(i, by), remove(i), open(path) }.
 * `problems` maps a step's path to why it cannot run, when it cannot.
 */
export function renderPipelineList(list, steps, act, problems = new Map()) {
  list.replaceChildren(...steps.map((path, i) => {
    const name = el("button", { type: "button", class: "mini link", title: `Open ${path}` }, path);
    name.addEventListener("click", () => act.open(path));
    const up = el("button", { type: "button", class: "mini", "aria-label": `Move ${path} up`, disabled: i === 0 }, "↑");
    const down = el("button", { type: "button", class: "mini", "aria-label": `Move ${path} down`, disabled: i === steps.length - 1 }, "↓");
    const rm = el("button", { type: "button", class: "mini", "aria-label": `Remove ${path} from the pipeline` }, "✕");
    up.addEventListener("click", () => act.move(i, -1));
    down.addEventListener("click", () => act.move(i, 1));
    rm.addEventListener("click", () => act.remove(i));
    const bad = problems.get(path);
    return el("li", {}, name, up, down, rm, bad ? el("span", { class: "muted bad" }, " " + bad) : null);
  }));
}

/** Which languages merge several files and which do not: the table under the pipeline editor. */
export function renderSupport(host, Core, languages) {
  const rows = languages.map((l) => {
    const s = Core.bundleSupport(l.id);
    return el("tr", {}, el("th", { scope: "row" }, l.name), el("td", {}, s.merged ? "merged" : "entry only"), el("td", {}, s.text));
  });
  host.replaceChildren(el("table", { class: "support" }, el("thead", {}, el("tr", {}, el("th", { scope: "col" }, "Language"), el("th", { scope: "col" }, "Several files"), el("th", { scope: "col" }, "How"))), el("tbody", {}, ...rows)));
}
