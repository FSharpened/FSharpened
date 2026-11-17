// The outline of the open file (sidebar list), and the breadcrumbs above the editor. The symbols come from the F# core
// (Symbols.fs): a fast, table-driven index of declarations in 18 languages, not a parser, and the panel says so.
import { el, fill } from "./dom.js";

const SHORT = { function: "fn", method: "fn", class: "C", struct: "S", interface: "I", enum: "E", module: "M", namespace: "N", type: "T", variable: "v", constant: "c", property: "p", field: "p", heading: "#", trait: "T", record: "R", union: "U" };

export function createOutline({ Core, filter, list, note, crumbs, go }) {
  let path = "", symbols = [], caretLine = 1, timer, wantCrumbs = true, innerKey = "";

  const chain = () => (symbols.length ? Core.symbolsEnclosing(symbols, caretLine) : []);

  function drawList() {
    const q = filter.value.trim();
    let rows = symbols.map((s, index) => ({ s, index, positions: [] }));
    if (q && symbols.length) {
      rows = Core.fuzzyRank(q, symbols.map((s) => s.name), 200).map((r) => ({ s: symbols[r.index], index: r.index, positions: r.positions }));
    }
    const here = chain();
    const inner = here.length ? here[here.length - 1] : null;
    note.hidden = !path;
    if (!path) { fill(list, el("li", { class: "muted ol-empty" }, "Open a file to see its outline.")); return; }
    if (!symbols.length) { fill(list, el("li", { class: "muted ol-empty" }, "No declarations found in this file.")); return; }
    if (!rows.length) { fill(list, el("li", { class: "muted ol-empty" }, "Nothing matches.")); return; }
    fill(list, rows.map(({ s, positions }) => {
      const on = inner && inner.line === s.line && inner.name === s.name;
      const b = el("button", { type: "button", class: "ol-row", "aria-current": on ? "true" : null, title: `${s.kind} ${s.name} (line ${s.line})`, style: null },
        el("span", { class: "ol-kind k-" + s.kind, "aria-hidden": "true" }, SHORT[s.kind] ?? s.kind.slice(0, 1)),
        el("span", { class: "ol-name" }, nameWithMarks(s.name, positions)), el("span", { class: "sr-only" }, ` ${s.kind}`),
        el("span", { class: "ol-line" }, String(s.line)));
      b.style.setProperty("--d", q ? "0" : String(s.depth));
      b.addEventListener("click", () => go(s.line, s.column));
      return el("li", {}, b);
    }));
  }

  function nameWithMarks(name, positions) {
    if (!positions.length) return name;
    const on = new Set(positions);
    return [...name].map((c, i) => (on.has(i) ? el("mark", {}, c) : c));
  }

  function drawCrumbs() {
    crumbs.hidden = !wantCrumbs || !path;
    if (crumbs.hidden) return;
    const parts = path.split("/");
    const nodes = [];
    parts.forEach((p, i) => { if (i) nodes.push(sep()); nodes.push(el("span", { class: "crumb path" + (i === parts.length - 1 ? " leaf" : "") }, p)); });
    for (const s of chain()) {
      nodes.push(sep());
      const b = el("button", { type: "button", class: "crumb sym", title: `${s.kind} ${s.name}: go to line ${s.line}` }, el("span", { class: "ol-kind k-" + s.kind, "aria-hidden": "true" }, SHORT[s.kind] ?? s.kind.slice(0, 1)), s.name);
      b.addEventListener("click", () => go(s.line, s.column));
      nodes.push(b);
    }
    fill(crumbs, nodes);
  }
  const sep = () => el("span", { class: "crumb-sep", "aria-hidden": "true" }, "›");

  function compute(p, text) {
    path = p;
    symbols = p ? Core.symbolsOutline(p, text) : [];
    drawList();
    drawCrumbs();
  }

  filter.addEventListener("input", drawList);

  return {
    /** The open file or its text changed. Typing recomputes after a short pause; switching files does so at once. */
    update(p, text, immediate = false) {
      clearTimeout(timer);
      if (immediate || p !== path) compute(p, text);
      else timer = setTimeout(() => compute(p, text), 250);
    },
    caret(line) {
      if (line === caretLine) return;
      caretLine = line;
      drawCrumbs();
      const here = chain(), key = here.length ? here[here.length - 1].line + ":" + here[here.length - 1].name : "";
      if (key !== innerKey) { innerKey = key; if (symbols.length) drawList(); } // the list only needs redrawing when the caret enters another symbol
    },
    showCrumbs(on) { wantCrumbs = on; drawCrumbs(); },
    symbols: () => symbols,
    path: () => path,
    /** The symbols whose line is in the visible chain, innermost last. */
    enclosing: chain,
  };
}
