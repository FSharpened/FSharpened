// What each right-click menu holds. Items come from the command list (commands.js), so a command is named, shortcut-labelled
// and enabled the same way in the palette and in the menus. Shift + right-click still opens the browser's own menu.
import { fmtKeys } from "./shortcuts.js";

const quote = (s) => (s.length > 28 ? s.slice(0, 27) + "…" : s);

/** The editor. `ctx` is { hasSelection, word } from the editor, taken after the caret moved to the click. */
export function editorMenu(cmds, app, ctx) {
  const it = (id, label) => cmds.item(id, label);
  const target = ctx.hasSelection ? app.editor.selectionText() : ctx.word;
  const search = target && !target.includes("\n")
    ? { label: `Search in Project for “${quote(target)}”`, keys: fmtKeys("Mod+Shift+F"), run: () => app.openSearch(target) }
    : it("go.search", "Search in Project…");
  return [
    { ...it("go.definition", ctx.word ? `Go to Definition of “${quote(ctx.word)}”` : "Go to Definition"), disabled: !ctx.word },
    it("go.symbol", "Go to Symbol in File…"),
    it("go.line", "Go to Line…"),
    search,
    "-",
    it("edit.cut"), it("edit.copy"), it("edit.paste"), it("edit.selectAll"),
    "-",
    it("edit.comment"),
    it("edit.blockComment"),
    it("edit.indent", "Auto-Indent Selection"),
    it("edit.snippet", "Insert Snippet…"),
    {
      label: "Multiple Cursors",
      submenu: [it("edit.cursorAbove"), it("edit.cursorBelow"), "-", it("edit.nextMatch"), it("edit.allMatches")],
    },
    {
      label: "Lines",
      submenu: [it("edit.duplicateDown"), it("edit.duplicateUp"), it("edit.moveDown"), it("edit.moveUp"), it("edit.deleteLine"), it("edit.join"), "-",
        it("edit.sortAsc"), it("edit.sortDesc"), it("edit.unique"), "-", it("edit.trim")],
    },
    { label: "Change Case", submenu: [it("edit.upper"), it("edit.lower")] },
    { label: "Fold", submenu: [it("edit.foldAll"), it("edit.unfoldAll")] },
    "-",
    it("edit.find", "Find…"),
    it("edit.replace", "Replace…"),
    "-",
    { ...it("run.run", "Run"), disabled: !app.canRun() },
    it("go.file", "Go to File…"),
    it("help.palette", "Command Palette…"),
  ];
}

/** A file or folder row of the explorer, or the empty space under it (`row` null). */
export function treeMenu(cmds, app, row) {
  if (!row) {
    return [
      { label: "New File…", run: () => app.newFileIn("") },
      "-",
      { label: "Collapse All Folders", run: () => app.collapseAll() },
      { label: "Expand All Folders", run: () => app.expandAll() },
      "-",
      { label: "Add Files to Project…", run: () => app.addFiles() },
      { label: "Search in Project…", run: () => app.openSearch() },
    ];
  }
  const folder = row.isFolder ? row.path : row.path.includes("/") ? row.path.slice(0, row.path.lastIndexOf("/")) : "";
  const items = [];
  if (!row.isFolder) items.push({ label: "Open", run: () => app.openPath(row.path) }, "-");
  else items.push({ label: app.isCollapsed(row.path) ? "Expand" : "Collapse", run: () => app.toggleFolder(row.path) }, "-");
  items.push(
    { label: row.isFolder ? "New File in This Folder…" : "New File Next to This…", run: () => app.newFileIn(folder) },
    { label: "Rename or Move…", keys: fmtKeys("F2"), run: () => app.renamePath(row.path) },
  );
  if (!row.isFolder) items.push({ label: "Duplicate", run: () => app.duplicatePath(row.path) });
  items.push({ label: row.isFolder ? "Delete Folder" : "Delete", keys: fmtKeys("Delete"), danger: true, run: () => app.deletePath(row.path) }, "-");
  if (!row.isFolder && row.language) items.push({ label: row.path === app.entryPath() ? "Run Entry File (current)" : "Set as Run Entry File", disabled: row.path === app.entryPath(), run: () => app.setEntry(row.path) }, "-");
  items.push({ label: "Copy Path", run: () => app.copyPath(row.path) });
  if (!row.isFolder) items.push({ label: "Copy Contents", run: () => app.copyContents(row.path) });
  items.push({ label: row.isFolder ? "Download Folder as .zip" : "Download", run: () => app.downloadPath(row.path) }, "-", { label: `Search in ${row.isFolder ? "This Folder" : folder ? "This Folder" : "Project"}…`, run: () => app.openSearch("", folder) });
  return items;
}

/** An open file's tab. */
export function tabMenu(cmds, app, path) {
  const open = app.openPaths();
  const i = open.indexOf(path);
  const lang = app.languageOf(path);
  return [
    { label: "Close", run: () => app.closeTab(path) },
    { label: "Close Others", disabled: open.length < 2, run: () => app.closeOthers(path) },
    { label: "Close to the Right", disabled: i < 0 || i === open.length - 1, run: () => app.closeToRight(path) },
    { label: "Close All", run: () => app.closeAll() },
    "-",
    { label: "Copy Path", run: () => app.copyPath(path) },
    { label: "Reveal in File Explorer", run: () => app.revealInTree(path) },
    "-",
    { label: "Rename or Move…", keys: fmtKeys("F2"), run: () => app.renamePath(path) },
    { label: "Duplicate", run: () => app.duplicatePath(path) },
    lang ? { label: path === app.entryPath() ? "Run Entry File (current)" : "Set as Run Entry File", disabled: path === app.entryPath(), run: () => app.setEntry(path) } : null,
    { label: "Download", run: () => app.downloadPath(path) },
    "-",
    { label: "Delete", danger: true, run: () => app.deletePath(path) },
  ].filter((x) => x !== null);
}

/** The Output, Problems and Timeline panels. */
export function outputMenu(cmds, app) {
  return [
    { label: "Copy Output", run: () => app.copyOutput("out") },
    { label: "Copy Errors", run: () => app.copyOutput("err") },
    { label: "Copy Problems", run: () => app.copyOutput("problems") },
    "-",
    { label: "Clear Output", run: () => app.clearOutputNow() },
    "-",
    cmds.item("run.run", "Run"),
  ];
}

/** A project in the Projects list. */
export function projectMenu(cmds, app, rec) {
  const here = app.currentProjectId() === rec.id;
  return [
    { label: here ? "Open (this project is open)" : "Open", disabled: here, run: () => app.projectAction(rec.id, "open") },
    "-",
    { label: "Rename…", run: () => app.projectAction(rec.id, "rename") },
    { label: "Duplicate…", run: () => app.projectAction(rec.id, "duplicate") },
    { label: "Download as .zip", run: () => app.projectAction(rec.id, "zip") },
    { label: "Download for VS Code", run: () => app.projectAction(rec.id, "vscode") },
    "-",
    { label: "Delete…", danger: true, run: () => app.projectAction(rec.id, "delete") },
  ];
}
