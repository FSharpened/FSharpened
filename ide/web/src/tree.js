// The project's file tree: a WAI-ARIA tree (arrow keys, Home/End, Left/Right to collapse and expand, Enter to open).
// It draws rows it is given (`Core.projectListing`) and reports what the person did; the project itself is main.js's.
import { el } from "./dom.js";

export function createTree(host, { langName, onOpen, onSelect, onCollapse }) {
  let rows = [];
  let collapsed = new Set();
  let selected = "";
  let active = "";
  let entry = "";

  host.setAttribute("role", "tree");
  host.setAttribute("aria-label", "Project files");

  const rowOf = (path) => rows.find((r) => r.path === path);

  /** Rows that are on screen: everything below a collapsed folder is left out. */
  function visibleRows() {
    const out = [];
    let hide = "";
    for (const r of rows) {
      if (hide && r.path.startsWith(hide + "/")) continue;
      hide = "";
      out.push(r);
      if (r.isFolder && collapsed.has(r.path)) hide = r.path;
    }
    return out;
  }

  const items = () => [...host.querySelectorAll('[role="treeitem"]')];
  const itemFor = (path) => items().find((i) => i.dataset.path === path);

  function paintSelection() {
    const stop = itemFor(selected) ? selected : items()[0]?.dataset.path;
    for (const i of items()) {
      const on = i.dataset.path === selected;
      i.setAttribute("aria-selected", String(on));
      i.tabIndex = i.dataset.path === stop ? 0 : -1;
      i.classList.toggle("sel", on);
    }
  }

  function toggle(path, open) {
    const isOpen = !collapsed.has(path);
    if (open === isOpen) return;
    if (isOpen) collapsed.add(path); else collapsed.delete(path);
    render();
    onCollapse?.([...collapsed]);
  }

  function select(path, focus) {
    selected = path;
    paintSelection();
    if (focus) itemFor(path)?.focus();
    onSelect?.(rowOf(path) ?? null);
  }

  function activate(r) {
    select(r.path, false);
    if (r.isFolder) toggle(r.path, collapsed.has(r.path));
    else onOpen?.(r.path);
  }

  function rowEl(r) {
    const folderOpen = r.isFolder && !collapsed.has(r.path);
    const lang = r.language ? langName(r.language) : "";
    const node = el("div", {
      role: "treeitem", class: "trow" + (r.isFolder ? " folder" : " file") + (r.path === active ? " active" : ""), "data-path": r.path, "aria-level": r.depth + 1,
      "aria-selected": "false", "aria-expanded": r.isFolder ? String(folderOpen) : false, tabindex: "-1", title: r.path + (lang ? " · " + lang : ""),
    },
    el("span", { class: "twisty", "aria-hidden": "true" }, r.isFolder ? (folderOpen ? "▾" : "▸") : ""),
    el("span", { class: "tname" }, r.name),
    r.path === entry ? el("span", { class: "tentry", title: "Entry file: Run starts here when the open file is not source code" }, "entry") : null,
    lang ? el("span", { class: "tlang" }, lang) : null);
    node.style.setProperty("--d", String(r.depth));
    node.addEventListener("click", () => activate(r));
    return node;
  }

  function render() {
    const hadFocus = host.contains(document.activeElement);
    const vis = visibleRows();
    host.replaceChildren(...(vis.length ? vis.map(rowEl) : [el("p", { class: "muted tempty" }, "No files yet. Use New file, or import a project.")]));
    paintSelection();
    if (hadFocus) (itemFor(selected) ?? items()[0])?.focus();
  }

  host.addEventListener("keydown", (e) => {
    const list = items();
    const i = list.indexOf(document.activeElement);
    if (i < 0 || e.altKey || e.ctrlKey || e.metaKey) return;
    const r = rowOf(list[i].dataset.path);
    let handled = true;
    switch (e.key) {
      case "ArrowDown": if (i + 1 < list.length) select(list[i + 1].dataset.path, true); break;
      case "ArrowUp": if (i > 0) select(list[i - 1].dataset.path, true); break;
      case "Home": select(list[0].dataset.path, true); break;
      case "End": select(list[list.length - 1].dataset.path, true); break;
      case "ArrowRight":
        if (r.isFolder && collapsed.has(r.path)) toggle(r.path, true);
        else if (r.isFolder && i + 1 < list.length) select(list[i + 1].dataset.path, true);
        break;
      case "ArrowLeft":
        if (r.isFolder && !collapsed.has(r.path)) toggle(r.path, false);
        else { const up = r.path.includes("/") ? r.path.slice(0, r.path.lastIndexOf("/")) : ""; if (up && itemFor(up)) select(up, true); }
        break;
      case "Enter": case " ": activate(r); break;
      default: handled = false;
    }
    if (handled) e.preventDefault();
  });

  return {
    /** Redraw. `state.collapsed` is the list of folder paths that are folded. */
    set(state) {
      rows = state.rows;
      collapsed = new Set(state.collapsed ?? [...collapsed]);
      active = state.active ?? active;
      entry = state.entry ?? entry;
      if (state.selected !== undefined) selected = state.selected;
      if (selected && !rowOf(selected)) selected = "";
      render();
    },
    selected: () => (rowOf(selected) ? selected : ""),
    selectRow: (path) => select(path, false),
    /** Unfold every folder above a path, so a file that was just opened or created is visible. */
    reveal(path) {
      let changed = false;
      for (let i = path.indexOf("/"); i > 0; i = path.indexOf("/", i + 1)) if (collapsed.delete(path.slice(0, i))) changed = true;
      if (changed) { render(); onCollapse?.([...collapsed]); }
      itemFor(path)?.scrollIntoView?.({ block: "nearest" });
    },
  };
}
