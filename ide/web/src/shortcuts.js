// The keyboard shortcuts of the IDE, as data: the shortcut sheet, the command palette and the status bar read the same
// list. "Mod" is Ctrl, or Cmd on a Mac.
const mac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform ?? "");

/** "Mod+Shift+P" as "Ctrl+Shift+P", or "⌘⇧P" on a Mac. */
export function fmtKeys(spec) {
  if (!spec) return "";
  return spec.split(" / ").map((alt) => {
    const parts = alt.split("+");
    if (!mac) return parts.map((p) => (p === "Mod" ? "Ctrl" : p)).join("+");
    const glyph = { Mod: "⌘", Shift: "⇧", Alt: "⌥", Ctrl: "⌃" };
    return parts.map((p) => glyph[p] ?? p).join("");
  }).join(" / ");
}

export const SHORTCUTS = [
  { group: "Go anywhere", items: [
    ["F1 / Mod+Shift+P", "Command palette: every command, by name"],
    ["Mod+P", "Quick open a file. Type > for commands, @ for symbols, : for a line"],
    ["Mod+Shift+O", "Go to a symbol in this file"],
    ["Mod+G", "Go to a line (and column) in this file"],
    ["F12", "Go to the definition of the word at the caret"],
    ["Mod+Shift+F", "Search in the whole project"],
    ["Mod+B", "Show or hide the sidebar"],
    ["Mod+,", "Settings"],
  ] },
  { group: "Run and save", items: [
    ["Mod+Enter", "Compile and run"],
    ["Mod+S", "Save a statepoint of the whole project"],
  ] },
  { group: "Editing", items: [
    ["Mod+Z / Mod+Shift+Z", "Undo / redo"],
    ["Mod+/", "Toggle line comment"],
    ["Shift+Alt+A", "Toggle block comment"],
    ["Alt+Up / Alt+Down", "Move the line up or down"],
    ["Shift+Alt+Up / Shift+Alt+Down", "Copy the line up or down"],
    ["Mod+Shift+K", "Delete the line"],
    ["Mod+] / Mod+[", "Indent / outdent"],
    ["Mod+Alt+\\", "Auto-indent the selection"],
    ["Mod+Shift+\\", "Jump to the matching bracket"],
    ["Mod+Shift+[ / Mod+Shift+]", "Fold / unfold the block"],
    ["Ctrl+Space", "Show suggestions, keywords and snippets"],
    ["Tab", "Accept a suggestion, or move to the next snippet field"],
    ["Alt+\\", "Ask for an inline AI suggestion (opt-in, off by default). Tab accepts it, Escape dismisses it"],
  ] },
  { group: "Several cursors", items: [
    ["Alt+Click", "Add a cursor"],
    ["Mod+Alt+Up / Mod+Alt+Down", "Add a cursor above or below"],
    ["Mod+D", "Select the next occurrence of the selection"],
    ["Mod+Shift+L", "Select every occurrence of the selection"],
    ["Shift+Alt+Drag", "Select a rectangle of text"],
  ] },
  { group: "Find and replace in the file", items: [
    ["Mod+F", "Find"],
    ["Mod+H", "Find and replace"],
    ["F3 / Shift+F3", "Next / previous match"],
    ["Escape", "Close the find bar"],
  ] },
  { group: "Keyboard users", items: [
    ["Escape, then Tab", "Leave the editor (Tab otherwise indents)"],
  ] },
];
