// Reading VS Code content in the browser layer: JSON with comments, .vsix packages, theme and snippet files, and the stored
// settings and imports. The F# core does the conversion; these check the JavaScript around it, against the compiled core.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { stripJsonc, parseJsonc, tryParseJsonc } from "../src/jsonc.js";
import { buildZip, encodeText } from "../src/archive.js";
import { readVsix, readJsonFile, resolvePath, themeInput, loadTheme, snippetDefs, describeContributions } from "../src/vsix.js";
import { fmtKeys, SHORTCUTS } from "../src/shortcuts.js";

const entry = new URL("../fable_out/Api.js", import.meta.url);
const built = existsSync(entry);
const Core = built ? await import(entry.href) : null;
const skip = built ? false : "Fable output missing: run `npm run fable` in ide/web first";
const date = new Date(Date.UTC(2026, 0, 1));
const file = (path, text) => ({ path, data: encodeText(text) });
const json = (o) => JSON.stringify(o);

// ---- JSON with comments -----------------------------------------------------------------------------------

test("jsonc: comments and trailing commas go, strings are left exactly as they were", () => {
  const text = `{
    // a line comment
    "url": "http://example.com/*not a comment*/", /* block */
    "list": [1, 2, 3,],
    "s": "a \\" // still text",
  }`;
  assert.deepEqual(parseJsonc(text), { url: "http://example.com/*not a comment*/", list: [1, 2, 3], s: 'a " // still text' });
});

test("jsonc: a byte-order mark is dropped, an unterminated comment ends the text, and bad JSON is reported or defaulted", () => {
  assert.deepEqual(parseJsonc("﻿{\"a\":1}"), { a: 1 });
  assert.equal(stripJsonc("1 /* never closed").trim(), "1");
  assert.throws(() => parseJsonc("{nope}"));
  assert.equal(tryParseJsonc("{nope}", "fallback"), "fallback");
});

test("jsonc: a comma that is not trailing stays", () => {
  assert.deepEqual(parseJsonc('{"a": [1,2], "b": {"c": 1,},}'), { a: [1, 2], b: { c: 1 } });
});

// ---- path resolution inside an archive ---------------------------------------------------------------------

test("resolvePath: joins, normalises, and refuses to climb out of the archive", () => {
  assert.equal(resolvePath("extension", "./themes/a.json"), "extension/themes/a.json");
  assert.equal(resolvePath("extension/themes", "../snippets/s.json"), "extension/snippets/s.json");
  assert.equal(resolvePath("extension", "../../escape.json"), null);
  assert.equal(resolvePath("", "a\\b.json"), "a/b.json");
});

// ---- themes -----------------------------------------------------------------------------------------------

test("theme input: scopes split on commas, rules without a colour are dropped, the extension's uiTheme decides the type", () => {
  const t = themeInput({ name: "Mine", colors: { "editor.background": "#101010", n: 3 }, tokenColors: [{ settings: { foreground: "#eeeeee" } }, { scope: "keyword, storage.type", settings: { foreground: "#ff0000" } }, { scope: "string", settings: { fontStyle: "bold" } }] }, "fallback", "vs-dark");
  assert.equal(t.name, "Mine");
  assert.equal(t.type, "dark");
  assert.deepEqual(t.colors, [{ key: "editor.background", value: "#101010" }]);
  assert.deepEqual(t.tokenColors, [{ scopes: [], foreground: "#eeeeee" }, { scopes: ["keyword", "storage.type"], foreground: "#ff0000" }]);
});

test("theme include: the child overrides the parent, depth is bounded, a missing file is noted", () => {
  const files = {
    "t/child.json": { include: "./base.json", colors: { "editor.background": "#000000" }, tokenColors: [{ scope: "x", settings: { foreground: "#111111" } }] },
    "t/base.json": { name: "Base", colors: { "editor.background": "#ffffff", "editor.foreground": "#222222" }, tokenColors: [{ scope: "y", settings: { foreground: "#333333" } }] },
    "t/loop.json": { include: "./loop.json" },
    "t/orphan.json": { include: "./gone.json", colors: {} },
  };
  const read = (p) => files[p] ?? null;
  const notes = [];
  const merged = loadTheme("t/child.json", read, notes);
  assert.equal(merged.colors["editor.background"], "#000000");
  assert.equal(merged.colors["editor.foreground"], "#222222");
  assert.equal(merged.tokenColors.length, 2);
  assert.equal(merged.name, "Base");
  const loopNotes = [];
  assert.ok(loadTheme("t/loop.json", read, loopNotes));
  assert.ok(loopNotes.some((n) => /deeper/.test(n)));
  const orphanNotes = [];
  loadTheme("t/orphan.json", read, orphanNotes);
  assert.ok(orphanNotes.some((n) => /could not be read/.test(n)));
});

// ---- snippets ---------------------------------------------------------------------------------------------

test("snippet files: prefixes and bodies are normalised, entries that are not snippets are counted", () => {
  const r = snippetDefs({ Log: { prefix: ["log", "cl"], body: ["console.log($1);", "$0"], description: "print", scope: "javascript, typescript" }, Bad: { prefix: "x" }, Junk: 5 }, "");
  assert.equal(r.defs.length, 1);
  assert.deepEqual(r.defs[0].prefixes, ["log", "cl"]);
  assert.equal(r.defs[0].body, "console.log($1);\n$0");
  assert.deepEqual(r.defs[0].scope, ["javascript", "typescript"]);
  assert.equal(r.skipped, 2);
  assert.deepEqual(snippetDefs({ A: { prefix: "a", body: "b" } }, "python").defs[0].scope, ["python"]);
});

test("a picked JSON file is recognised as a theme, snippets or settings", () => {
  assert.equal(readJsonFile("t.json", '{ "name": "T", "colors": {"editor.background": "#fff"} }').kind, "theme");
  assert.equal(readJsonFile("fsharp.code-snippets", '{ "L": { "prefix": "l", "body": "x" } }').kind, "snippets");
  assert.equal(readJsonFile("settings.json", '// c\n{ "editor.fontSize": 16, }').kind, "settings");
  assert.equal(readJsonFile("x.json", "[1,2]").kind, "unknown");
  assert.equal(readJsonFile("x.json", "{oops").kind, "unknown");
  const snip = readJsonFile("rust.json", '{ "L": { "prefix": "l", "body": "x" } }');
  assert.deepEqual(snip.defs[0].scope, ["rust"]); // the file name says the language
});

// ---- a .vsix ----------------------------------------------------------------------------------------------

const vsix = (extra = []) => buildZip([
  file("extension/package.json", json({
    name: "pretty", publisher: "acme", version: "1.2.3", displayName: "%displayName%", main: "./dist/extension.js",
    contributes: {
      themes: [{ label: "Pretty Dark", uiTheme: "vs-dark", path: "./themes/dark.json" }, { label: "Pretty Light", uiTheme: "vs", path: "./themes/light.json" }],
      snippets: [{ language: "python", path: "./snippets/py.json" }],
      commands: [{ command: "a" }, { command: "b" }], configuration: { title: "x" },
    },
  })),
  file("extension/package.nls.json", json({ displayName: "Pretty Theme Pack" })),
  file("extension/themes/dark.json", '{ // comment\n "name": "Pretty Dark", "colors": { "editor.background": "#101820", "editor.foreground": "#e0e6ee" }, "tokenColors": [{ "scope": "keyword", "settings": { "foreground": "#ff9d00" } },], }'),
  file("extension/themes/light.json", json({ include: "./dark.json", colors: { "editor.background": "#ffffff", "editor.foreground": "#101820" } })),
  file("extension/snippets/py.json", json({ Main: { prefix: "main", body: ["def main():", "\t$0"] } })),
  file("extension/dist/extension.js", "throw new Error('must never run')"),
  file("extension/node_modules/x/package.json", "{}"),
  ...extra,
], date);

test("vsix: themes, snippets and the display name are read; code is not", { skip }, async () => {
  const r = await readVsix(vsix());
  assert.equal(r.ok, true);
  assert.equal(r.name, "Pretty Theme Pack");
  assert.equal(r.id, "acme.pretty");
  assert.equal(r.version, "1.2.3");
  assert.equal(r.themes.length, 2);
  assert.deepEqual(r.themes.map((t) => t.type), ["dark", "light"]);
  assert.equal(r.themes[1].colors.find((c) => c.key === "editor.background").value, "#ffffff"); // child over parent
  assert.equal(r.themes[1].tokenColors.length, 1); // inherited from the dark theme
  assert.equal(r.snippets.length, 1);
  assert.equal(r.runsCode, true);
  assert.ok(r.left.some((l) => /commands/.test(l)));
  assert.ok(r.used.some((u) => /colour theme/.test(u)));
  // the core turns each theme into a complete one
  for (const t of r.themes) {
    const out = Core.themeImport(t);
    assert.ok(out.theme.vars["ed-bg"]);
    assert.ok(out.theme.id.startsWith("vsc-"));
  }
});

test("vsix: not an extension, not a zip, and an unreadable theme are reported, never thrown", { skip }, async () => {
  const none = await readVsix(buildZip([file("readme.txt", "hi")], date));
  assert.equal(none.ok, false);
  assert.match(none.error, /package\.json/);
  const bad = await readVsix(new Uint8Array([1, 2, 3, 4]));
  assert.equal(bad.ok, false);
  const broken = await readVsix(buildZip([file("extension/package.json", json({ name: "n", publisher: "p", contributes: { themes: [{ label: "L", uiTheme: "vs", path: "./missing.json" }] } }))], date));
  assert.equal(broken.ok, true);
  assert.equal(broken.themes.length, 0);
  assert.ok(broken.themeNotes.length > 0);
});

test("vsix: a .vsix with more than 2000 entries is fine, because only JSON files are read", { skip }, async () => {
  const many = Array.from({ length: 2300 }, (_, i) => file(`extension/assets/img${i}.png`, "x"));
  const r = await readVsix(vsix(many));
  assert.equal(r.ok, true);
  assert.equal(r.themes.length, 2);
});

test("vsix: GitHub Copilot's package is described honestly: nothing to use, a lot left out", { skip }, async () => {
  const z = buildZip([file("extension/package.json", json({
    name: "copilot-chat", publisher: "GitHub", version: "0.1.0", displayName: "GitHub Copilot Chat", main: "./dist/extension",
    contributes: { commands: [{}, {}, {}], chatParticipants: [{}], languageModelTools: [{}, {}], configuration: {}, keybindings: [{}] },
  }))], date);
  const r = await readVsix(z);
  assert.equal(r.ok, true);
  assert.equal(r.id, "GitHub.copilot-chat");
  assert.equal(r.themes.length + r.snippets.length, 0);
  assert.equal(r.runsCode, true);
  assert.ok(r.left.some((l) => /chat participants/.test(l)));
  assert.ok(r.left.some((l) => /language model tools/.test(l)));
});

test("describeContributions: counts what is used and what is left", () => {
  const d = describeContributions({ main: "x", contributes: { themes: [{}], snippets: [{}, {}], grammars: [{}], debuggers: [{}] } });
  assert.deepEqual(d.used, ["1 colour theme", "2 snippet files"]);
  assert.deepEqual(d.left, ["1 syntax grammars", "1 debuggers"]);
  assert.equal(d.runsCode, true);
});

// ---- shortcuts ---------------------------------------------------------------------------------------------

test("shortcut labels: Mod is Ctrl off a Mac, alternatives are kept, and every shortcut in the sheet is well formed", () => {
  assert.equal(fmtKeys("Mod+Shift+P"), "Ctrl+Shift+P");
  assert.equal(fmtKeys("F1 / Mod+Shift+P"), "F1 / Ctrl+Shift+P");
  assert.equal(fmtKeys(""), "");
  for (const g of SHORTCUTS) for (const [k, what] of g.items) { assert.ok(k.length > 0); assert.ok(what.length >= 4); }
});

// ---- the stored settings, themes and snippets ----------------------------------------------------------------------

function fakeDocument() {
  const props = new Map();
  const style = { setProperty: (k, v) => props.set(k, v), removeProperty: (k) => props.delete(k), colorScheme: "" };
  globalThis.document = { documentElement: { style, dataset: {} } };
  return props;
}
const memory = () => { const m = new Map(); return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => void m.set(k, String(v)), removeItem: (k) => void m.delete(k), m }; };

test("prefs: settings are sanitised, saved, applied to the page, and survive a reload", { skip }, async () => {
  const { createPrefs } = await import("../src/settings.js");
  const props = fakeDocument();
  const store = memory();
  const p = createPrefs(Core, store);
  p.apply();
  assert.equal(props.get("--ed-fs"), "14px");
  p.update({ fontSize: 9999, tabSize: -4, theme: "dracula" });
  assert.ok(p.settings.fontSize <= Core.settingsLimits().fontMax);
  assert.ok(p.settings.tabSize >= Core.settingsLimits().tabMin);
  assert.equal(p.settings.theme, "dracula");
  assert.ok(props.get("--bg").startsWith("#"));
  assert.equal(globalThis.document.documentElement.dataset.ideTheme, "dracula");
  assert.equal(createPrefs(Core, store).settings.theme, "dracula"); // a reload reads it back
  p.update({ theme: "auto" });
  assert.equal(props.has("--bg"), false); // Auto sets nothing: the site's own light/dark applies
});

test("prefs: an imported theme is validated, added, chosen, and falls back to Auto when its import is removed", { skip }, async () => {
  const { createPrefs } = await import("../src/settings.js");
  fakeDocument();
  const store = memory();
  const p = createPrefs(Core, store);
  const r = await readVsix(vsix());
  const themes = r.themes.map((t) => Core.themeImport(t).theme);
  const added = p.addPack({ id: "ext-pretty", name: r.name, themes, snippets: [], notes: [], left: r.left, runsCode: true });
  assert.equal(added.ok, true);
  assert.equal(p.themes().length, Core.themes().length + 2);
  p.update({ theme: themes[0].id });
  assert.equal(p.theme().id, themes[0].id);
  p.removePack("ext-pretty");
  assert.equal(p.settings.theme, "auto");
  assert.equal(p.packs().length, 0);
});

test("prefs: stored content that is not a plain colour or a plain string is thrown away on load", { skip }, async () => {
  const { createPrefs } = await import("../src/settings.js");
  fakeDocument();
  const store = memory();
  const good = Core.themeImport({ name: "G", type: "dark", colors: [], tokenColors: [] }).theme;
  const evil = { ...good, id: "evil", vars: { ...good.vars, bg: "red; background:url(//x)" } };
  const short = { ...good, id: "short", vars: { bg: "#000000" } };
  store.setItem("fsharpened.ide.packs.v1", JSON.stringify([
    { id: "ok", name: "OK", themes: [good], snippets: [{ label: "a", body: "b", detail: "", languages: [] }, { label: 7, body: "x" }], notes: [], left: [] },
    { id: "bad", name: "Bad", themes: [evil, short], snippets: [], notes: [], left: [] },
    { id: "../x", name: "Path", themes: [good], snippets: [], notes: [], left: [] },
    "junk",
  ]));
  const p = createPrefs(Core, store);
  assert.deepEqual(p.packs().map((x) => x.id), ["ok", "bad"]);
  assert.equal(p.packs()[0].snippets.length, 1);
  assert.equal(p.packs()[1].themes.length, 0);
  assert.equal(p.snippets().length, 1);
});

test("prefs: pack limits and a pack with nothing usable are refused", { skip }, async () => {
  const { createPrefs, LIMITS } = await import("../src/settings.js");
  fakeDocument();
  const p = createPrefs(Core, memory());
  assert.equal(p.addPack({ id: "empty", name: "E", themes: [], snippets: [] }).ok, false);
  const many = Array.from({ length: LIMITS.snippets + 1 }, (_, i) => ({ label: "s" + i, body: "x", detail: "", languages: [] }));
  assert.equal(p.addPack({ id: "big", name: "B", themes: [], snippets: many }).ok, false);
  assert.equal(p.addPack({ id: "fine", name: "F", themes: [], snippets: many.slice(0, 5) }).ok, true);
});
