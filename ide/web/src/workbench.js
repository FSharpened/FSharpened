// The workbench: the VS Code-style layer on top of the project and the editor. It wires the command palette and quick
// open, the right-click menus, the search and outline panels, the status bar, the settings and shortcut dialogs and the
// VS Code import panel, all through `app`, the operations main.js lets it use. No project logic lives here.
import { $, el, fill, safe } from "./dom.js";
import { createPalette } from "./palette.js";
import { createContextMenu } from "./contextmenu.js";
import { buildCommands } from "./commands.js";
import { editorMenu, treeMenu, tabMenu, outputMenu, projectMenu } from "./menus.js";
import { SHORTCUTS, fmtKeys } from "./shortcuts.js";
import { createSearchPanel } from "./searchpanel.js";
import { createOutline } from "./outline.js";
import { createExtensionsPanel } from "./extpanel.js";
import { pick } from "./transfer.js";

const RECENT = "fsharpened.ide.recent.v1";
const LINE_COMMENT = { python: "#", ruby: "#", r: "#", lua: "--", haskell: "--", vb: "'" };

export function createWorkbench(app) {
  const { Core, prefs, editor } = app;
  const limits = Core.settingsLimits();
  const palette = createPalette({ dialog: $("#palette"), input: $("#pal-input"), list: $("#pal-list"), hint: $("#pal-hint"), title: $("#pal-title") });
  const menu = createContextMenu();
  const toast = (m) => app.toast(m);
  let info = { line: 1, column: 1, cursors: 1, selected: 0, selectedLines: 0, lines: 1, eol: "LF" };

  // ---------------------------------------------------------------- panels
  const search = createSearchPanel({
    Core, root: $('.spanel[data-s="search"]'), project: () => app.project(),
    open: (path, line, column, length) => { app.openFile(path); editor.reveal(line, column, length); },
    apply: (project, paths) => app.applyReplaced(project, paths), snapshot: (l) => app.snapshotNow(l), toast, ask: (o) => app.ask(o),
  });
  const outline = createOutline({
    Core, filter: $("#ol-filter"), list: $("#ol-list"), note: $("#ol-note"), crumbs: $("#crumbs"),
    go: (line, column) => { editor.reveal(line, column); if (app.narrow()) app.setSide(false, false); },
  });
  const ext = createExtensionsPanel({ Core, prefs, root: $('.spanel[data-s="extensions"]'), project: () => app.project(), toast });

  // ---------------------------------------------------------------- commands and the palette
  const cmds = buildCommands(Object.assign(app, {
    openQuick, openSearch, goToDefinition, pickSnippet, pickTabSize, pickTheme, pickProject, openSettings, openShortcuts, resetSettings, zoom, clip, importVsCode,
    importSettingsFile: importVsCode,
  }));
  let recent = (safe(() => JSON.parse(app.storage.getItem(RECENT)), []) ?? []).filter((x) => typeof x === "string").slice(0, 8);
  const remember = (id) => { recent = [id, ...recent.filter((x) => x !== id)].slice(0, 8); safe(() => app.storage.setItem(RECENT, JSON.stringify(recent))); };

  const commandRows = (q) => {
    const avail = cmds.available();
    const label = (c) => `${c.category}: ${c.title}`;
    const mk = (c, positions) => ({ id: c.id, label: label(c), positions, keys: fmtKeys(c.keys), cmd: c });
    if (!q) {
      const first = recent.map((id) => avail.find((c) => c.id === id)).filter(Boolean);
      return [...first.map((c) => ({ ...mk(c, []), badge: "recent" })), ...avail.filter((c) => !first.includes(c)).map((c) => mk(c, []))];
    }
    return Core.fuzzyRank(q, avail.map(label), 200).map((r) => mk(avail[r.index], r.positions));
  };

  const fileRows = (p) => {
    const files = app.project().files;
    const paths = files.map((f) => f.path);
    const detail = (path) => { const l = app.languageOf(path); return l ? app.langName(l) : ""; };
    let rows;
    if (!p.text) {
      const open = app.openPaths().slice().reverse(), seen = new Set(open);
      rows = [...open.map((path) => ({ path, label: path, detail: detail(path), badge: "open" })), ...paths.filter((x) => !seen.has(x)).map((path) => ({ path, label: path, detail: detail(path) }))];
    } else rows = Core.fuzzyRank(p.text, paths, 100).map((r) => ({ path: r.text, label: r.text, positions: r.positions, detail: detail(r.text) }));
    if (p.line > 0) rows = rows.map((r) => ({ ...r, line: p.line, column: p.column || 1 }));
    return { rows, hint: files.length ? "Files in this project. > commands · @ symbols in this file · :12 go to line" : "This project has no files yet." };
  };

  const symbolRows = (p) => {
    const syms = outline.symbols();
    if (!app.hasFile()) return { rows: [{ label: "Open a file first", disabled: true }] };
    if (!syms.length) return { rows: [{ label: "No declarations found in this file", disabled: true }], hint: "The outline is an index of declarations (functions, types, modules), not a full parse." };
    const rows = p.text
      ? Core.fuzzyRank(p.text, syms.map((s) => s.name), 100).map((r) => ({ sym: syms[r.index], positions: r.positions }))
      : syms.map((sym) => ({ sym, positions: [] }));
    return { rows: rows.map(({ sym, positions }) => ({ label: sym.name, positions, detail: `${sym.kind} · line ${sym.line}`, badge: sym.container || "", symbol: sym })), hint: "Symbols in this file" };
  };

  const lineRows = (p) => {
    if (!app.hasFile()) return { rows: [{ label: "Open a file first", disabled: true }] };
    const n = info.lines;
    if (p.line >= 1 && p.line <= n) return { rows: [{ label: `Go to line ${p.line}${p.column ? `, column ${p.column}` : ""}`, goto: { line: p.line, column: p.column || 1 } }], hint: `This file has ${n} line${n === 1 ? "" : "s"}` };
    return { rows: [{ label: p.line > n ? `That line is past the end: this file has ${n} line${n === 1 ? "" : "s"}` : `Type a line number from 1 to ${n}`, disabled: true }] };
  };

  const quickProvider = (raw) => {
    const p = Core.quickParse(raw);
    if (p.mode === "command") return { rows: commandRows(p.text), hint: "Commands. Delete the > to search files instead." };
    if (p.mode === "symbol") return symbolRows(p);
    if (p.mode === "line") return lineRows(p);
    return fileRows(p);
  };

  function quickPick(row) {
    if (row.cmd) { remember(row.cmd.id); row.cmd.run(); return; }
    if (row.path) { app.openFile(row.path, { focus: true }); if (row.line) editor.reveal(row.line, row.column || 1); return; }
    if (row.symbol) { editor.reveal(row.symbol.line, row.symbol.column); return; }
    if (row.goto) editor.reveal(row.goto.line, row.goto.column);
  }

  function openQuick(prefix = "") {
    menu.close(false);
    palette.open({
      title: prefix === ">" ? "Command palette" : prefix === "@" ? "Go to symbol" : prefix === ":" ? "Go to line" : "Go to file",
      placeholder: "Search files by name. Type > for commands, @ for symbols, : for a line",
      value: prefix, provider: quickProvider, pick: quickPick,
    });
  }

  /** A palette over a fixed list: fuzzy-filtered by label. `rows`: [{ label, detail?, badge?, ... }]. */
  function listPalette({ title, placeholder, rows, pick: onPick, hint = "" }) {
    palette.open({
      title, placeholder, hint,
      provider: (q) => ({ rows: q ? Core.fuzzyRank(q, rows.map((r) => r.label), 100).map((r) => ({ ...rows[r.index], positions: r.positions })) : rows }),
      // like VS Code, a pick leaves you in the editor, not on the status-bar item or button that opened the list
      pick: (row) => { onPick(row); editor.focus(); },
    });
  }

  function pickTheme() {
    const cur = prefs.settings.theme;
    listPalette({
      title: "Color theme", placeholder: "Pick a colour theme", hint: "Imported VS Code themes are listed too; add more in the VS Code tab",
      rows: prefs.themes().map((t) => ({ id: t.id, label: t.name, detail: t.id === "auto" ? "follows the site's light or dark setting" : t.scheme + (t.contrast === "high" ? " · high contrast" : ""), badge: t.id === cur ? "current" : "" })),
      pick: (r) => { prefs.update({ theme: r.id }); toast("Theme: " + prefs.theme().name); },
    });
  }

  function pickProject() {
    listPalette({
      title: "Open project", placeholder: "Pick a project", hint: "Projects kept in this browser",
      rows: app.projects().map((r) => ({ id: r.id, label: r.project.name, detail: `${r.project.files.length} file${r.project.files.length === 1 ? "" : "s"}`, badge: r.id === app.currentProjectId() ? "open" : "" })),
      pick: (r) => app.projectAction(r.id, "open"),
    });
  }

  function pickSnippet() {
    const lang = app.activeLanguage();
    const list = prefs.snippetsFor(lang);
    if (!list.length) { toast("No snippets for this language yet. Import some from VS Code in the VS Code tab."); return; }
    listPalette({
      title: "Insert snippet", placeholder: "Pick a snippet", hint: "Tab moves between the fields of a snippet",
      rows: list.map((s) => ({ label: s.label, detail: s.detail, body: s.body })),
      pick: (r) => editor.insertSnippet(r.body),
    });
  }

  function pickTabSize() {
    const s = prefs.settings;
    const rows = [
      { id: "spaces", label: "Indent Using Spaces", badge: s.insertSpaces ? "current" : "" },
      { id: "tabs", label: "Indent Using Tabs", badge: s.insertSpaces ? "" : "current" },
      ...[2, 3, 4, 8].map((n) => ({ id: "size" + n, label: `Tab Size: ${n}`, badge: s.tabSize === n ? "current" : "" })),
      { id: "toSpaces", label: "Convert Indentation to Spaces" },
      { id: "toTabs", label: "Convert Indentation to Tabs" },
    ];
    listPalette({
      title: "Indentation", placeholder: "Select an action", rows,
      pick: (r) => {
        if (r.id === "spaces") prefs.update({ insertSpaces: true });
        else if (r.id === "tabs") prefs.update({ insertSpaces: false });
        else if (r.id.startsWith("size")) prefs.update({ tabSize: Number(r.id.slice(4)) });
        else cmds.get(r.id === "toSpaces" ? "format.spaces" : "format.tabs").run();
      },
    });
  }

  function goToDefinition() {
    const w = editor.wordAtCaret();
    if (!w) { toast("Put the caret on a name first"); return; }
    const defs = Core.symbolsDefinitions(app.project(), w.text);
    if (!defs.length) { toast(`No declaration of “${w.text}” was found in this project. This looks at declarations in your files, not at libraries.`); return; }
    const here = app.activePath();
    const go = (d) => { app.openFile(d.path, { focus: true }); editor.reveal(d.line, d.column); };
    if (defs.length === 1) { go(defs[0]); return; }
    defs.sort((a, b) => (a.path === here ? -1 : 0) - (b.path === here ? -1 : 0));
    listPalette({ title: "Go to definition", placeholder: "Pick a declaration", hint: `${defs.length} declarations of “${w.text}”`, rows: defs.map((d) => ({ label: `${d.path}:${d.line}`, detail: d.kind, def: d })), pick: (r) => go(r.def) });
  }

  // ---------------------------------------------------------------- actions used by commands
  function openSearch(text = "", folder = null) {
    app.setSide(true, false);
    app.showSide("search");
    search.focus(text, folder);
  }
  async function clip(what) {
    if (what === "paste") { if (!(await editor.paste())) toast("This browser does not let a page read the clipboard. Press Ctrl+V in the editor instead."); }
    else if (!(await editor.copy(what === "cut"))) toast("The browser would not copy. Select the text and press Ctrl+C.");
  }
  function zoom(delta) {
    const size = delta === 0 ? Core.settingsDefaults().fontSize : Math.min(limits.fontMax, Math.max(limits.fontMin, prefs.settings.fontSize + delta));
    prefs.update({ fontSize: size });
    toast(`Editor font size ${prefs.settings.fontSize}px`);
  }
  function resetSettings() { app.setLiveCheck(false, false); prefs.reset(); toast("Settings are back to their defaults"); }
  async function importVsCode() {
    app.setSide(true, false);
    app.showSide("extensions");
    const files = await pick($("#pick-ext"));
    if (files.length) await ext.importFiles(files);
  }

  // ---------------------------------------------------------------- settings and shortcut dialogs
  const setDlg = $("#settings"), scDlg = $("#shortcuts");
  const field = { theme: $("#set-theme"), font: $("#set-font"), tab: $("#set-tab"), spaces: $("#set-spaces"), wrap: $("#set-wrap"), lines: $("#set-lines"), ws: $("#set-ws"), close: $("#set-close"), crumbs: $("#set-crumbs"), live: $("#set-live") };
  field.font.min = String(limits.fontMin); field.font.max = String(limits.fontMax);
  field.tab.min = String(limits.tabMin); field.tab.max = String(limits.tabMax);

  function syncSettings() {
    const s = prefs.settings, themes = prefs.themes();
    const group = (label, list) => (list.length ? el("optgroup", { label }, list.map((t) => el("option", { value: t.id, selected: t.id === s.theme }, t.name + (t.contrast === "high" ? " (high contrast)" : "")))) : null);
    fill(field.theme, group("Built in", themes.filter((t) => prefs.isBuiltin(t.id))), group("Imported from VS Code", themes.filter((t) => !prefs.isBuiltin(t.id))));
    field.theme.value = s.theme;
    field.font.value = String(s.fontSize);
    field.tab.value = String(s.tabSize);
    field.spaces.checked = s.insertSpaces; field.wrap.checked = s.wordWrap; field.lines.checked = s.lineNumbers;
    field.ws.checked = s.renderWhitespace; field.close.checked = s.autoCloseBrackets; field.crumbs.checked = s.breadcrumbs; field.live.checked = app.liveCheckOn();
    const t = prefs.theme();
    const ratio = t.vars?.fg && t.vars["ed-bg"] ? Core.themeContrast(t.vars.fg, t.vars["ed-bg"]) : 0;
    $("#set-theme-note").textContent = t.id === "auto" ? "Follows the site's light or dark setting (the sun/moon button in the header)." : `Text on the editor background: ${ratio.toFixed(1)} to 1${ratio >= 7 ? " (meets AAA)" : ratio >= 4.5 ? " (meets AA)" : " (below the WCAG AA level of 4.5)"}.`;
  }
  const apply = (patch) => prefs.update(patch);
  field.theme.addEventListener("change", () => apply({ theme: field.theme.value }));
  field.font.addEventListener("change", () => apply({ fontSize: Number(field.font.value) }));
  field.tab.addEventListener("change", () => apply({ tabSize: Number(field.tab.value) }));
  field.spaces.addEventListener("change", () => apply({ insertSpaces: field.spaces.checked }));
  field.wrap.addEventListener("change", () => apply({ wordWrap: field.wrap.checked }));
  field.lines.addEventListener("change", () => apply({ lineNumbers: field.lines.checked }));
  field.ws.addEventListener("change", () => apply({ renderWhitespace: field.ws.checked }));
  field.close.addEventListener("change", () => apply({ autoCloseBrackets: field.close.checked }));
  field.crumbs.addEventListener("change", () => apply({ breadcrumbs: field.crumbs.checked }));
  field.live.addEventListener("change", () => app.setLiveCheck(field.live.checked));
  $("#set-reset").addEventListener("click", () => { app.setLiveCheck(false, false); field.live.checked = false; prefs.reset(); toast("Settings are back to their defaults"); });
  $("#set-shortcuts").addEventListener("click", () => { setDlg.close(); openShortcuts(); });

  function openSettings() { menu.close(false); palette.close(false); syncSettings(); if (!setDlg.open) setDlg.showModal(); }
  function openShortcuts() {
    menu.close(false);
    palette.close(false);
    fill($("#sc-body"), SHORTCUTS.map((g) => el("section", { class: "scgroup" }, el("h3", {}, g.group), g.items.map(([k, what]) => el("div", { class: "sc-row" }, el("span", {}, what), el("kbd", {}, fmtKeys(k)))))));
    if (!scDlg.open) scDlg.showModal();
  }

  // ---------------------------------------------------------------- status bar
  const st = { box: $("#stitems"), pos: $("#st-pos"), sel: $("#st-sel"), indent: $("#st-indent"), lang: $("#st-lang"), eol: $("#st-eol") };
  function paintStatus() {
    const has = app.hasFile();
    st.box.hidden = !has;
    if (!has) return;
    const s = prefs.settings;
    st.pos.textContent = `Ln ${info.line}, Col ${info.column}`;
    const bits = [];
    if (info.selected) bits.push(`${info.selected} selected${info.selectedLines > 1 ? ` in ${info.selectedLines} lines` : ""}`);
    if (info.cursors > 1) bits.push(`${info.cursors} cursors`);
    st.sel.textContent = bits.join(", ");
    st.indent.textContent = `${s.insertSpaces ? "Spaces" : "Tab Size"}: ${s.tabSize}`;
    const l = app.activeLanguage();
    st.lang.textContent = l ? app.langName(l) : "Plain Text";
    st.eol.textContent = info.eol;
  }
  st.pos.addEventListener("click", () => openQuick(":"));
  st.indent.addEventListener("click", pickTabSize);
  st.eol.addEventListener("click", () => listPalette({ title: "End of line", placeholder: "Select the end-of-line sequence", rows: [{ id: "lf", label: "LF (Unix, macOS)", badge: info.eol === "LF" ? "current" : "" }, { id: "crlf", label: "CRLF (Windows)", badge: info.eol === "CRLF" ? "current" : "" }], pick: (r) => app.setEol(r.id === "lf" ? "\n" : "\r\n") }));

  prefs.onChange((s) => { editor.configure(s); outline.showCrumbs(s.breadcrumbs); paintStatus(); if (setDlg.open) syncSettings(); });

  // ---------------------------------------------------------------- right-click menus
  const pointOf = (node) => { const r = node.getBoundingClientRect(); return { x: r.left + Math.min(24, r.width / 2), y: r.bottom - 2 }; };

  document.addEventListener("contextmenu", (e) => {
    if (e.shiftKey) return; // Shift + right-click keeps the browser's own menu (spell check, inspect)
    const t = e.target instanceof Element ? e.target : null;
    if (!t || t.closest("dialog, .cm-panels, .cm-tooltip, input, textarea, select, .ctx")) return;
    const fromKey = e.button !== 2 && !e.pointerType; // the Menu key or Shift+F10 reports no pointer
    let x = e.clientX, y = e.clientY, items = null, label = "Context menu", from = document.activeElement;
    const tab = t.closest(".ftab"), row = t.closest('#tree [role="treeitem"]'), proj = t.closest("#plist li");

    if (t.closest(".cm-content, .cm-gutters") && app.hasFile()) {
      const c = fromKey ? editor.prepareContext() : editor.prepareContext(x, y);
      if (fromKey) { const p = editor.caretPoint(); if (p) ({ x, y } = p); }
      items = editorMenu(cmds, app, c);
      label = "Editor";
      from = document.querySelector(".cm-content") ?? from;
    } else if (tab) {
      const path = tab.querySelector("[data-path]")?.dataset.path;
      if (!path) return;
      if (fromKey) ({ x, y } = pointOf(tab));
      items = tabMenu(cmds, app, path);
      label = "Tab " + path;
      from = tab.querySelector("[data-path]");
    } else if (row) {
      const path = row.dataset.path;
      app.selectRow(path);
      if (fromKey) ({ x, y } = pointOf(row));
      items = treeMenu(cmds, app, app.rowOf(path));
      label = "File " + path;
      from = row;
    } else if (t.closest("#tree")) {
      items = treeMenu(cmds, app, null);
      label = "Files";
    } else if (proj) {
      const id = proj.querySelector("[data-id]")?.dataset.id;
      const rec = id && app.projects().find((r) => r.id === id);
      if (!rec) return;
      if (fromKey) ({ x, y } = pointOf(proj));
      items = projectMenu(cmds, app, rec);
      label = "Project " + rec.project.name;
      from = proj.querySelector("[data-id]");
    } else if (t.closest('.panel[data-p="output"], .panel[data-p="problems"]')) {
      items = outputMenu(cmds, app);
      label = "Output";
    }
    if (!items) return;
    e.preventDefault();
    menu.open({ x, y, items, label, from, focusFirst: fromKey });
  });

  // ---------------------------------------------------------------- keyboard
  const typing = (t) => t instanceof Element && !!t.closest("input, textarea, select, [contenteditable=true]:not(.cm-content)");
  addEventListener("keydown", (e) => {
    if (e.defaultPrevented || menu.isOpen() || e.isComposing) return;
    const mod = e.ctrlKey || e.metaKey;
    const k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    const t = e.target instanceof Element ? e.target : null;
    const inEditor = !!t?.closest(".cm-content");
    const inPanel = !!t?.closest(".cm-panels");
    const dlgOpen = setDlg.open || scDlg.open;
    const handle = (fn) => { e.preventDefault(); e.stopPropagation(); fn(); };

    if (k === "F1" || (mod && e.shiftKey && !e.altKey && k === "p")) return handle(() => openQuick(">"));
    if (dlgOpen) return;
    if (mod && !e.shiftKey && !e.altKey && k === "p") return handle(() => openQuick(""));
    if (mod && e.shiftKey && !e.altKey && k === "o") return handle(() => openQuick("@"));
    if (mod && !e.shiftKey && !e.altKey && k === "g" && !inPanel) return handle(() => openQuick(":"));
    if (mod && e.shiftKey && !e.altKey && k === "f") return handle(() => openSearch(editor.selectionText()));
    if (mod && !e.shiftKey && !e.altKey && k === ",") return handle(openSettings);
    if (palette.isOpen()) return;
    if (k === "F12" && inEditor) return handle(goToDefinition);
    if (e.altKey && !mod && !e.shiftKey && k === "z" && inEditor) return handle(() => cmds.get("view.wrap").run());
    if (!mod && !e.altKey && !e.shiftKey && !typing(t)) {
      if (k === "F2" && (t?.closest("#tree") || inEditor)) return handle(() => (t.closest("#tree") ? app.renameSelected() : app.renameActive()));
      if (k === "Delete" && t?.closest("#tree")) return handle(() => app.deleteSelected());
    }
  }, true);

  // ---------------------------------------------------------------- buttons
  $("#btn-palette").addEventListener("click", () => openQuick(">"));
  $("#btn-settings").addEventListener("click", openSettings);
  $("#x-import").addEventListener("click", importVsCode);
  $("#x-download").addEventListener("click", () => app.downloadForVsCode());
  // dialogs close when the backdrop is clicked
  for (const d of [setDlg, scDlg]) d.addEventListener("mousedown", (e) => { if (e.target === d) d.close(); });

  outline.showCrumbs(prefs.settings.breadcrumbs);

  return {
    cmds, palette, menu, outline, search, ext,
    /** What the editor reports as the caret moves. */
    cursor(i) { info = i; paintStatus(); outline.caret(i.line); },
    /** The open file changed (or there is none). */
    activeChanged() { const p = app.activePath(); outline.update(p, app.activeText(), true); paintStatus(); },
    /** The open file's text changed. */
    textChanged(path, text) { outline.update(path, text); if (!$('.spanel[data-s="search"]').hidden) search.refresh(); },
    /** A sidebar tab was shown. */
    sideShown(name) { if (name === "extensions") ext.refresh(); },
    /** Files dropped on the editor: the .vsix ones are VS Code content. Returns the rest. */
    async takeVsix(files) {
      const mine = files.filter((f) => /\.vsix$/i.test(f.name));
      if (!mine.length) return files;
      app.setSide(true, false);
      app.showSide("extensions");
      await ext.importFiles(mine);
      return files.filter((f) => !mine.includes(f));
    },
    snippetContext() {
      const p = app.activePath(), d = new Date();
      return { fileName: p.slice(p.lastIndexOf("/") + 1), filePath: p, folder: p.includes("/") ? p.slice(0, p.lastIndexOf("/")) : "", projectName: app.project().name, year: d.getFullYear(), month: d.getMonth() + 1, day: d.getDate(), lineComment: LINE_COMMENT[app.activeLanguage()] ?? "//" };
    },
  };
}
