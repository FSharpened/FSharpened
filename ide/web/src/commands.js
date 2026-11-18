// Every command of the IDE, by name: the command palette lists them, the context menus and the toolbar call them.
// A command is { id, title, category, keys?, when?, run }. `when` hides a command that makes no sense right now
// (no file open, nothing selected). `app` is what main.js lets commands do; this file never touches the page itself.

import { fmtKeys } from "./shortcuts.js";

export function buildCommands(app) {
  const ed = (name) => () => app.editor.run(name);
  const s = () => app.prefs.settings;
  const toggle = (key, label) => () => { const on = !s()[key]; app.prefs.update({ [key]: on }); app.toast(`${label} ${on ? "on" : "off"}`); };
  const hasFile = () => app.hasFile();
  const hasSource = () => app.hasFile() && app.activeLanguage() !== "";
  const list = [
    // ---- file
    { id: "file.new", title: "New File…", category: "File", run: () => app.newFile() },
    { id: "file.rename", title: "Rename or Move File…", category: "File", keys: "F2", when: hasFile, run: () => app.renameActive() },
    { id: "file.duplicate", title: "Duplicate File", category: "File", when: hasFile, run: () => app.duplicateActive() },
    { id: "file.delete", title: "Delete File", category: "File", keys: "Delete", when: hasFile, run: () => app.deleteActive() },
    { id: "file.close", title: "Close Editor", category: "File", when: hasFile, run: () => app.closeTab(app.activePath()) },
    { id: "file.closeOthers", title: "Close Other Editors", category: "File", when: () => app.openCount() > 1, run: () => app.closeOthers(app.activePath()) },
    { id: "file.closeAll", title: "Close All Editors", category: "File", when: () => app.openCount() > 0, run: () => app.closeAll() },
    { id: "file.copyPath", title: "Copy File Path", category: "File", when: hasFile, run: () => app.copyPath(app.activePath()) },
    { id: "file.copyText", title: "Copy File Contents", category: "File", when: hasFile, run: () => app.copyContents(app.activePath()) },
    { id: "file.download", title: "Download File", category: "File", when: hasFile, run: () => app.downloadPath(app.activePath()) },
    { id: "file.entry", title: "Set as Run Entry File", category: "File", when: hasSource, run: () => app.setEntry(app.activePath()) },
    { id: "file.revealTree", title: "Reveal in File Explorer", category: "File", when: hasFile, run: () => app.revealInTree(app.activePath()) },
    // ---- project
    { id: "project.new", title: "New Project…", category: "Project", run: () => app.newProject() },
    { id: "project.examples", title: "New Project from Examples", category: "Project", run: () => app.starterProject() },
    { id: "project.open", title: "Open Project…", category: "Project", run: () => app.pickProject() },
    { id: "project.duplicate", title: "Duplicate Project…", category: "Project", run: () => app.duplicateProject() },
    { id: "project.rename", title: "Rename Project…", category: "Project", run: () => app.renameProject() },
    { id: "project.delete", title: "Delete Project…", category: "Project", run: () => app.deleteProject() },
    { id: "project.zip", title: "Download Project as .zip", category: "Project", run: () => app.downloadProject() },
    { id: "project.importZip", title: "Import Project from .zip…", category: "Project", run: () => app.importZip() },
    { id: "project.importFolder", title: "Import Project from Folder…", category: "Project", run: () => app.importFolder() },
    { id: "project.addFiles", title: "Add Files to Project…", category: "Project", run: () => app.addFiles() },
    // ---- go
    { id: "go.file", title: "Go to File…", category: "Go", keys: "Mod+P", run: () => app.openQuick("") },
    { id: "go.symbol", title: "Go to Symbol in File…", category: "Go", keys: "Mod+Shift+O", when: hasFile, run: () => app.openQuick("@") },
    { id: "go.line", title: "Go to Line…", category: "Go", keys: "Mod+G", when: hasFile, run: () => app.openQuick(":") },
    { id: "go.definition", title: "Go to Definition", category: "Go", keys: "F12", when: hasFile, run: () => app.goToDefinition() },
    { id: "go.search", title: "Search in Project", category: "Go", keys: "Mod+Shift+F", run: () => app.openSearch() },
    { id: "go.searchSelection", title: "Search in Project for the Selection", category: "Go", when: () => app.hasFile() && app.editor.selectionText() !== "", run: () => app.openSearch(app.editor.selectionText()) },
    // ---- edit
    { id: "edit.undo", title: "Undo", category: "Edit", keys: "Mod+Z", when: hasFile, run: ed("undo") },
    { id: "edit.redo", title: "Redo", category: "Edit", keys: "Mod+Shift+Z", when: hasFile, run: ed("redo") },
    { id: "edit.cut", title: "Cut", category: "Edit", keys: "Mod+X", when: hasFile, run: () => app.clip("cut") },
    { id: "edit.copy", title: "Copy", category: "Edit", keys: "Mod+C", when: hasFile, run: () => app.clip("copy") },
    { id: "edit.paste", title: "Paste", category: "Edit", keys: "Mod+V", when: hasFile, run: () => app.clip("paste") },
    { id: "edit.selectAll", title: "Select All", category: "Edit", keys: "Mod+A", when: hasFile, run: ed("selectAll") },
    { id: "edit.find", title: "Find in File", category: "Edit", keys: "Mod+F", when: hasFile, run: ed("find") },
    { id: "edit.replace", title: "Replace in File", category: "Edit", keys: "Mod+H", when: hasFile, run: ed("find") },
    { id: "edit.comment", title: "Toggle Line Comment", category: "Edit", keys: "Mod+/", when: hasFile, run: ed("commentLine") },
    { id: "edit.blockComment", title: "Toggle Block Comment", category: "Edit", keys: "Shift+Alt+A", when: hasFile, run: ed("commentBlock") },
    { id: "edit.indent", title: "Auto-Indent Selection", category: "Edit", keys: "Mod+Alt+\\", when: hasFile, run: ed("reindent") },
    { id: "edit.duplicateDown", title: "Duplicate Line Down", category: "Edit", keys: "Shift+Alt+Down", when: hasFile, run: ed("duplicateDown") },
    { id: "edit.duplicateUp", title: "Duplicate Line Up", category: "Edit", keys: "Shift+Alt+Up", when: hasFile, run: ed("duplicateUp") },
    { id: "edit.moveDown", title: "Move Line Down", category: "Edit", keys: "Alt+Down", when: hasFile, run: ed("moveDown") },
    { id: "edit.moveUp", title: "Move Line Up", category: "Edit", keys: "Alt+Up", when: hasFile, run: ed("moveUp") },
    { id: "edit.deleteLine", title: "Delete Line", category: "Edit", keys: "Mod+Shift+K", when: hasFile, run: ed("deleteLine") },
    { id: "edit.join", title: "Join Lines", category: "Edit", when: hasFile, run: ed("joinLines") },
    { id: "edit.sortAsc", title: "Sort Lines Ascending", category: "Edit", when: hasFile, run: ed("sortAscending") },
    { id: "edit.sortDesc", title: "Sort Lines Descending", category: "Edit", when: hasFile, run: ed("sortDescending") },
    { id: "edit.unique", title: "Delete Duplicate Lines", category: "Edit", when: hasFile, run: ed("uniqueLines") },
    { id: "edit.trim", title: "Trim Trailing Whitespace", category: "Edit", when: hasFile, run: ed("trimTrailing") },
    { id: "edit.upper", title: "Transform to Uppercase", category: "Edit", when: hasFile, run: ed("upper") },
    { id: "edit.lower", title: "Transform to Lowercase", category: "Edit", when: hasFile, run: ed("lower") },
    { id: "edit.cursorAbove", title: "Add Cursor Above", category: "Edit", keys: "Mod+Alt+Up", when: hasFile, run: ed("cursorAbove") },
    { id: "edit.cursorBelow", title: "Add Cursor Below", category: "Edit", keys: "Mod+Alt+Down", when: hasFile, run: ed("cursorBelow") },
    { id: "edit.nextMatch", title: "Add Next Occurrence", category: "Edit", keys: "Mod+D", when: hasFile, run: ed("addNextMatch") },
    { id: "edit.allMatches", title: "Select All Occurrences", category: "Edit", keys: "Mod+Shift+L", when: hasFile, run: ed("selectAllMatches") },
    { id: "edit.foldAll", title: "Fold All", category: "Edit", when: hasFile, run: ed("foldAll") },
    { id: "edit.unfoldAll", title: "Unfold All", category: "Edit", when: hasFile, run: ed("unfoldAll") },
    { id: "edit.snippet", title: "Insert Snippet…", category: "Edit", when: hasSource, run: () => app.pickSnippet() },
    // ---- format of the open file
    { id: "format.spaces", title: "Indentation: Use Spaces", category: "Format", when: hasFile, run: () => { app.prefs.update({ insertSpaces: true }); app.editor.run("toSpaces"); app.toast("Indentation converted to spaces"); } },
    { id: "format.tabs", title: "Indentation: Use Tabs", category: "Format", when: hasFile, run: () => { app.prefs.update({ insertSpaces: false }); app.editor.run("toTabs"); app.toast("Indentation converted to tabs"); } },
    { id: "format.tabSize", title: "Indentation: Set Tab Size…", category: "Format", run: () => app.pickTabSize() },
    { id: "format.eolLf", title: "End of Line: LF", category: "Format", when: hasFile, run: () => app.setEol("\n") },
    { id: "format.eolCrlf", title: "End of Line: CRLF", category: "Format", when: hasFile, run: () => app.setEol("\r\n") },
    // ---- view
    { id: "view.sidebar", title: "Toggle Sidebar", category: "View", keys: "Mod+B", run: () => app.toggleSide() },
    { id: "view.files", title: "Show File Explorer", category: "View", run: () => app.showSide("files", true) },
    { id: "view.search", title: "Show Search", category: "View", run: () => app.showSide("search", true) },
    { id: "view.outline", title: "Show Outline", category: "View", run: () => app.showSide("outline", true) },
    { id: "view.projects", title: "Show Projects", category: "View", run: () => app.showSide("projects", true) },
    { id: "view.extensions", title: "Show Extensions and VS Code Import", category: "View", run: () => app.showSide("extensions", true) },
    { id: "view.output", title: "Show Output", category: "View", run: () => app.showPanel("output") },
    { id: "view.problems", title: "Show Problems", category: "View", run: () => app.showPanel("problems") },
    { id: "view.timeline", title: "Show Timeline (Statepoints)", category: "View", run: () => app.showPanel("timeline") },
    { id: "view.wrap", title: "Toggle Word Wrap", category: "View", keys: "Alt+Z", run: toggle("wordWrap", "Word wrap") },
    { id: "view.whitespace", title: "Toggle Render Whitespace", category: "View", run: toggle("renderWhitespace", "Render whitespace") },
    { id: "view.lineNumbers", title: "Toggle Line Numbers", category: "View", run: toggle("lineNumbers", "Line numbers") },
    { id: "view.breadcrumbs", title: "Toggle Breadcrumbs", category: "View", run: toggle("breadcrumbs", "Breadcrumbs") },
    { id: "view.autoClose", title: "Toggle Auto-Closing Brackets", category: "View", run: toggle("autoCloseBrackets", "Auto-closing brackets") },
    { id: "view.zoomIn", title: "Zoom In (Editor Font)", category: "View", run: () => app.zoom(1) },
    { id: "view.zoomOut", title: "Zoom Out (Editor Font)", category: "View", run: () => app.zoom(-1) },
    { id: "view.zoomReset", title: "Reset Editor Font Size", category: "View", run: () => app.zoom(0) },
    { id: "view.theme", title: "Color Theme…", category: "Preferences", run: () => app.pickTheme() },
    { id: "view.settings", title: "Open Settings", category: "Preferences", keys: "Mod+,", run: () => app.openSettings() },
    { id: "view.resetSettings", title: "Reset Settings to Defaults", category: "Preferences", run: () => app.resetSettings() },
    // ---- AI completion (opt-in, off by default; ADR-0026)
    { id: "ai.suggest", title: "Suggest Inline Completion (AI)", category: "AI", keys: "Alt+\\", when: hasSource, run: () => app.ai.request() },
    { id: "ai.enable", title: "Enable AI Completion…", category: "AI", when: () => !app.ai.state.enabled, run: () => app.enableAi() },
    { id: "ai.disable", title: "Disable AI Completion", category: "AI", when: () => app.ai.state.enabled, run: () => { app.ai.setEnabled(false); app.ai.dismiss(); app.toast("AI completion is off. Nothing is sent."); } },
    { id: "ai.endpoint", title: "Set AI Completion Endpoint…", category: "AI", run: () => app.enableAi({ keepState: true }) },
    // ---- run
    { id: "run.run", title: "Run", category: "Run", keys: "Mod+Enter", run: () => app.run() },
    { id: "run.snapshot", title: "Save Statepoint (Snapshot)", category: "Run", keys: "Mod+S", run: () => app.snapshot() },
    { id: "run.share", title: "Copy Share Link", category: "Run", run: () => app.share() },
    { id: "run.copyOutput", title: "Copy Output", category: "Run", run: () => app.copyOutput("out") },
    { id: "run.clearOutput", title: "Clear Output", category: "Run", run: () => app.clearOutputNow() },
    // ---- VS Code
    { id: "vscode.import", title: "Import Extension, Theme or Snippets…", category: "VS Code", run: () => app.importVsCode() },
    { id: "vscode.settings", title: "Import settings.json…", category: "VS Code", run: () => app.importSettingsFile() },
    { id: "vscode.download", title: "Download Project for VS Code (Copilot recommended)", category: "VS Code", run: () => app.downloadForVsCode() },
    { id: "vscode.about", title: "What Works Here and What Does Not", category: "VS Code", run: () => app.showSide("extensions", true) },
    // ---- help
    { id: "help.palette", title: "Show All Commands", category: "Help", keys: "F1", run: () => app.openQuick(">") },
    { id: "help.shortcuts", title: "Keyboard Shortcuts", category: "Help", run: () => app.openShortcuts() },
  ];
  const byId = new Map(list.map((c) => [c.id, c]));
  return {
    list,
    get: (id) => byId.get(id),
    /** The commands that apply now. */
    available: () => list.filter((c) => !c.when || c.when()),
    /** A menu item for a command: its title (or `label`), its shortcut, greyed out when it does not apply. */
    item(id, label) {
      const c = byId.get(id);
      if (!c) throw new Error("unknown command " + id);
      return { label: label ?? c.title, keys: fmtKeys(c.keys), disabled: c.when ? !c.when() : false, run: c.run };
    },
  };
}
