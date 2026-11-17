// The editor features (fuzzy matching, search, outline, snippets, themes, settings, VS Code export) are written in F# and
// tested natively. These run the *compiled* core: JavaScript regular expressions and string functions are not .NET's, so
// every behaviour that depends on them is checked again here against the same expectations.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";

const entry = new URL("../fable_out/Api.js", import.meta.url);
const built = existsSync(entry);
const Core = built ? await import(entry.href) : null;
const skip = built ? false : "Fable output missing: run `npm run fable` in ide/web first";

const project = (files, extra = {}) => ({ name: "P", files: files.map(([path, text]) => ({ path, text })), entry: null, pipeline: [], ...extra });
const plain = (query, more = {}) => ({ query, caseSensitive: false, wholeWord: false, regex: false, ...more });
const search = (opts, files, inc = "", exc = "") => Core.searchRun(project(files), opts, inc, exc);

// ---- fuzzy ---------------------------------------------------------------------------------------------

test("fuzzy: a subsequence match reports its positions and ranks word starts first", { skip }, () => {
  assert.deepEqual(Core.fuzzyScore("mn", "main.rs").positions, [0, 3]);
  assert.equal(Core.fuzzyScore("xyz", "main.rs"), null);
  assert.ok(Core.fuzzyScore("M A I N", "src/main.rs"));
  assert.deepEqual(Core.fuzzyRank("main", ["domain.fs", "docs/maintenance.md", "src/main.rs"], 10).map((r) => r.text), ["src/main.rs", "docs/maintenance.md", "domain.fs"]);
  assert.equal(Core.fuzzyRank("fb", ["foobar", "FooBar"], 10)[0].text, "FooBar");
  assert.deepEqual(Core.fuzzyRank("", ["c", "a", "b"], 2).map((r) => r.text), ["c", "a"]);
  assert.ok(Core.fuzzyScore("ba", "ab".repeat(5000)));
});

test("quick open: the mode prefixes and the line suffix", { skip }, () => {
  const q = (s) => { const r = Core.quickParse(s); return [r.mode, r.text, r.line, r.column]; };
  assert.deepEqual(q(">format file"), ["command", "format file", 0, 0]);
  assert.deepEqual(q("@main"), ["symbol", "main", 0, 0]);
  assert.deepEqual(q(":12"), ["line", "", 12, 0]);
  assert.deepEqual(q(":12:5"), ["line", "", 12, 5]);
  assert.deepEqual(q("main.rs:12"), ["file", "main.rs", 12, 0]);
  assert.deepEqual(q("src/a.rs:3:4"), ["file", "src/a.rs", 3, 4]);
  assert.deepEqual(q("  readme  "), ["file", "readme", 0, 0]);
});

// ---- search --------------------------------------------------------------------------------------------

test("search: plain, case and whole-word matching", { skip }, () => {
  const cols = (opts, text) => search(opts, [["f.txt", text]]).files[0]?.hits.map((h) => [h.line, h.column]) ?? [];
  assert.deepEqual(cols(plain("foo"), "Foo bar\nxfoo"), [[1, 1], [2, 2]]);
  assert.deepEqual(cols(plain("foo", { caseSensitive: true }), "Foo bar\nxfoo"), [[2, 2]]);
  assert.deepEqual(cols(plain("foo", { wholeWord: true }), "foo foobar _foo foo."), [[1, 1], [1, 17]]);
});

test("search: regular expressions, groups, anchors and errors", { skip }, () => {
  const hits = search(plain("\\d+", { regex: true }), [["f.txt", "a1 b22"]]).files[0].hits;
  assert.deepEqual(hits.map((h) => [h.column, h.length]), [[2, 1], [5, 2]]);
  const bad = search(plain("(", { regex: true }), [["f.txt", "("]]);
  assert.notEqual(bad.error, "");
  assert.equal(bad.files.length, 0);
  assert.equal(search(plain(""), [["f.txt", "x"]]).total, 0);
  assert.equal(search(plain("a|b", { regex: true, wholeWord: true }), [["f.txt", "a ab b"]]).total, 2);
  const g = Core.searchReplaceText(plain("(\\w+)@(\\w+)", { regex: true }), "$2:$1 [$&] $$", "a@b");
  assert.equal(g.text, "b:a [a@b] $");
  const anchor = Core.searchReplaceText(plain("^", { regex: true }), "// ", "ab\ncd");
  assert.equal(anchor.text, "// ab\n// cd");
  assert.equal(anchor.count, 2);
  assert.equal(Core.searchReplaceText(plain("x*", { regex: true }), "-", "ab").count, 3, "empty matches advance and terminate");
});

test("search: replace keeps line endings, is literal when not a regex, and can target one match", { skip }, () => {
  const r = Core.searchReplaceText(plain("foo"), "bar", "foo\r\nfoo foo\n");
  assert.equal(r.text, "bar\r\nbar bar\n");
  assert.equal(r.count, 3);
  assert.equal(Core.searchReplaceText(plain("a"), "$1", "a").text, "$1");
  assert.equal(Core.searchReplaceOne(plain("foo"), "X", "foo foo\nfoo", 1, 5).text, "foo X\nfoo");
  assert.equal(Core.searchReplaceOne(plain("foo"), "X", "foo", 1, 2).ok, false);
});

test("search: previews, caps and file patterns", { skip }, () => {
  const line = "x".repeat(100) + "needle" + "y".repeat(100);
  const h = search(plain("needle"), [["f.txt", line]]).files[0].hits[0];
  assert.equal(h.column, 101);
  assert.equal(h.preview.slice(h.column - 1 - h.previewStart, h.column - 1 - h.previewStart + h.length), "needle");
  assert.ok(h.preview.length <= 200);

  const big = "x\n".repeat(5000);
  const r = search(plain("x"), Array.from({ length: 12 }, (_, i) => [`f${i + 1}.txt`, big]));
  assert.equal(r.files[0].hits.length, 200);
  assert.equal(r.files[0].more, true);
  assert.equal(r.total, 2000);
  assert.equal(r.truncated, true);
  assert.equal(r.files.length, 10);

  const files = [["src/a.rs", "needle"], ["src/b.py", "needle"], ["tests/c.rs", "needle"], ["node_modules/d.js", "needle"]];
  const paths = (inc, exc) => search(plain("needle"), files, inc, exc).files.map((f) => f.path);
  assert.deepEqual(paths("*.rs", ""), ["src/a.rs", "tests/c.rs"]);
  assert.deepEqual(paths("src/**", ""), ["src/a.rs", "src/b.py"]);
  assert.deepEqual(paths("", "node_modules"), ["src/a.rs", "src/b.py", "tests/c.rs"]);
  assert.deepEqual(paths("*.rs, *.py", "tests, *.py"), ["src/a.rs"]);
});

test("search: replacing in a project reports a file that would grow past its limit", { skip }, () => {
  const limit = Core.projectLimits().fileChars;
  const p = project([["big.txt", "a".repeat(limit - 1)], ["small.txt", "a"]]);
  const r = Core.searchReplaceProject(p, plain("a"), "bb", ["big.txt", "small.txt"]);
  assert.deepEqual(r.changed, [{ path: "small.txt", count: 1 }]);
  assert.equal(r.failed.length, 1);
  assert.equal(r.project.files.find((f) => f.path === "small.txt").text, "bb");
  assert.equal(r.project.files.find((f) => f.path === "big.txt").text.length, limit - 1);
});

// ---- outline -------------------------------------------------------------------------------------------

// The same table as tests/FSharpened.Ide.Tests/EditorTests.fs: it is the regular expressions that differ between engines.
const samples = {
  fsharp: ["module Geometry\n\ntype Shape = Circle of float\n\nlet area s =\n    let half = 2.0\n    half\n\nlet pi = 3.14\n", [["Geometry", "module"], ["Shape", "type"], ["area", "function"], ["pi", "variable"]]],
  csharp: ["namespace Demo\n{\n    public class Board\n    {\n        public Board() { }\n        public int Size()\n        {\n            return Helper(1);\n        }\n    }\n}\n", [["Demo", "namespace"], ["Board", "class"], ["Board", "constructor"], ["Size", "method"]]],
  vb: ["Module Program\n    Sub Main()\n    End Sub\nEnd Module\n", [["Program", "module"], ["Main", "function"]]],
  javascript: ["class Board {\n  place(x) {\n    if (x) {\n      return x;\n    }\n  }\n}\nfunction main() {}\nconst run = () => 1;\nexport const LIMIT = 4;\n", [["Board", "class"], ["place", "method"], ["main", "function"], ["run", "function"], ["LIMIT", "constant"]]],
  typescript: ["interface Cell { x: number }\ntype Grid = Cell[];\nexport enum Mode { A }\nexport function make(): Grid { return []; }\n", [["Cell", "interface"], ["Grid", "type"], ["Mode", "enum"], ["make", "function"]]],
  python: ['MAX = 3\nclass Board:\n    def place(self):\n        """def fake(): pass"""\n        def inner(): pass\n\nasync def main():\n    pass\n', [["MAX", "constant"], ["Board", "class"], ["place", "method"], ["inner", "function"], ["main", "function"]]],
  rust: ["pub struct Board { n: u8 }\nimpl Board {\n    pub fn new() -> Self { Board { n: 0 } }\n    fn helper(&self) {}\n}\nimpl Display for Board {}\n// fn commented() {}\nfn main() {}\n", [["Board", "struct"], ["impl Board", "impl"], ["new", "method"], ["helper", "method"], ["impl Display for Board", "impl"], ["main", "function"]]],
  dart: ["class Board {\n  int size() {\n    return 1;\n  }\n}\nvoid main() {\n  print(1);\n}\n", [["Board", "class"], ["size", "method"], ["main", "function"]]],
  c: ["#define MAX 10\nstruct Board {\n  int size;\n};\nint main(void) {\n  return 0;\n}\nstatic int add(int a, int b)\n{\n  return a + b;\n}\nint proto(int a);\n", [["MAX", "macro"], ["Board", "struct"], ["main", "function"], ["add", "function"]]],
  cpp: ["namespace demo {\nclass Board {\npublic:\n  int size() const { return 1; }\n};\n}\nint main() { return 0; }\n", [["demo", "namespace"], ["Board", "class"], ["size", "method"], ["main", "function"]]],
  go: ["package main\n\ntype Board struct{}\n\nfunc (b *Board) Place(x int) {}\n\nfunc main() {}\n", [["main", "namespace"], ["Board", "struct"], ["Board.Place", "method"], ["main", "function"]]],
  java: ["package demo;\npublic class Board {\n    public int size() {\n        return 1;\n    }\n}\n", [["demo", "namespace"], ["Board", "class"], ["size", "method"]]],
  ruby: ["module Demo\n  class Board\n    def place(x)\n    end\n  end\nend\n", [["Demo", "module"], ["Board", "class"], ["place", "method"]]],
  lua: ["local function add(a, b)\n  return a + b\nend\nfunction Board.place(x) end\nBoard.size = function() end\n", [["add", "function"], ["Board.place", "function"], ["Board.size", "function"]]],
  php: ["<?php\nnamespace Demo;\nclass Board {\n    public function size() { return 1; }\n}\nfunction main() {}\n", [["Demo", "namespace"], ["Board", "class"], ["size", "method"], ["main", "function"]]],
  r: ['area <- function(x) {\n  x\n}\nsetClass("Board", representation())\n', [["area", "function"], ["Board", "class"]]],
  haskell: ["module Main where\n\ndata Board = Board Int\n\nsize :: Board -> Int\nsize (Board n) = n\n\nmain :: IO ()\nmain = pure ()\n", [["Main", "module"], ["Board", "type"], ["size", "function"], ["main", "function"]]],
  ocaml: ["module Geo = struct\n  let area r = r\nend\ntype shape = Circle\nlet rec go n = n\nlet x = 5\n", [["Geo", "module"], ["area", "function"], ["shape", "type"], ["go", "function"], ["x", "variable"]]],
};

test("outline: all 18 languages read the same declarations as the native tests", { skip }, () => {
  assert.deepEqual(Object.keys(samples).sort(), Core.languages().map((l) => l.id).sort());
  for (const l of Core.languages()) {
    const [text, expected] = samples[l.id];
    const got = Core.symbolsOutline(l.file, text).map((s) => [s.name, s.kind]);
    assert.deepEqual(got, expected, l.id);
  }
});

test("outline: nesting, comments, strings, columns and markdown", { skip }, () => {
  const syms = Core.symbolsOutline("a.rs", "impl Board {\n    fn new() {}\n    fn go() {}\n}\nfn main() {}\n");
  const impl = syms.find((s) => s.name === "impl Board");
  const fnew = syms.find((s) => s.name === "new");
  assert.deepEqual([impl.depth, fnew.depth, fnew.container, impl.endLine, fnew.endLine], [0, 1, "impl Board", 4, 2]);
  assert.equal(Core.symbolsOutline("a.rs", "// fn a() {}\n/* fn b() {}\nfn c() {} */\n").length, 0);
  assert.equal(Core.symbolsOutline("a.py", '# def a(): pass\n"""\ndef b(): pass\n"""\n').length, 0);
  assert.equal(Core.symbolsOutline("a.cs", "/* x */ class A {}\n").length, 1);
  assert.deepEqual(Core.symbolsOutline("a.js", "const s = \"/*\";\nconst t = '//';\nfunction f() {}\n").map((s) => s.name), ["f"]);
  const hello = Core.symbolsOutline("a.rs", "    pub fn hello() {}\n")[0];
  assert.deepEqual([hello.line, hello.column], [1, 12]);
  const md = Core.symbolsOutline("README.md", "# Title\ntext\n## Part\n```\n# not a heading\n```\n## Other\n");
  assert.deepEqual(md.map((s) => [s.name, s.depth]), [["Title", 0], ["Part", 1], ["Other", 1]]);
  assert.equal(md[0].endLine, 7);
  assert.equal(Core.symbolsOutline("data.json", "{ }").length, 0);
});

test("outline: breadcrumbs, go to definition and the word under the cursor", { skip }, () => {
  const syms = Core.symbolsOutline("a.rs", "impl Board {\n    fn new() {\n        let x = 1;\n    }\n}\nfn main() {}\n");
  assert.deepEqual(Core.symbolsEnclosing(syms, 3).map((s) => s.name), ["impl Board", "new"]);
  assert.deepEqual(Core.symbolsEnclosing(syms, 6).map((s) => s.name), ["main"]);
  assert.equal(Core.symbolsEnclosing(syms, 99).length, 0);
  const p = project([["a.go", "package a\nfunc (s *Shape) Area() {}\n"], ["b.rs", "fn area() {}\nfn Area() {}\n"], ["c.md", "# Area\n"]]);
  assert.deepEqual(Core.symbolsDefinitions(p, "Area").map((d) => [d.path, d.line]), [["a.go", 2], ["b.rs", 2]]);
  assert.equal(Core.symbolsDefinitions(p, "").length, 0);
  assert.equal(Core.wordAt("let value = compute(x)", 6), "value");
  assert.equal(Core.wordAt("let value = compute(x)", 5), "value");
  assert.equal(Core.wordAt("let value = compute(x)", 15), "compute");
  assert.equal(Core.wordAt("  ", 2), "");
});

// ---- snippets ------------------------------------------------------------------------------------------

const context = { fileName: "main.rs", filePath: "src/main.rs", folder: "src", selected: "picked", projectName: "Demo", year: 2026, month: 3, day: 7, lineComment: "//" };
const tpl = (body) => Core.snippetTemplate(body, context);

test("snippets: VS Code syntax becomes editor templates", { skip }, () => {
  assert.equal(tpl("for ($1; ${2:x}; $0)").template, "for (${1}; ${2:x}; ${})");
  assert.equal(tpl("${1:name} and $1").template, "${1:name} and ${1:name}");
  const choice = tpl("${1|a,b,c|}");
  assert.equal(choice.template, "${1:a}");
  assert.equal(choice.warnings.length, 1);
  assert.equal(tpl("${1/(.*)/${1:/upcase}/}").template, "${1}");
  assert.equal(tpl("${1:a ${2:b}}").template, "${1:a b}");
  assert.equal(tpl("$TM_FILENAME ${TM_FILENAME_BASE} $RELATIVE_FILEPATH $WORKSPACE_NAME $TM_SELECTED_TEXT $CURRENT_YEAR-$CURRENT_MONTH-$CURRENT_DATE $LINE_COMMENT").template, "main.rs main src/main.rs Demo picked 2026-03-07 //");
  const unknown = tpl("${FOO:fallback} $BAR");
  assert.equal(unknown.template, "fallback BAR");
  assert.equal(unknown.warnings.length, 2);
  assert.equal(tpl("cost \\$1 and \\}").template, "cost $1 and }");
  const literal = tpl("a \\${b}");
  assert.equal(literal.template, "a {b}");
  assert.equal(literal.warnings.length, 1);
  for (const body of ["${1:abc", "${", "$", "${1|a", "${1/x", "$1000000", "${a b}", "}}"]) assert.equal(typeof tpl(body).template, "string", body);
});

test("snippets: every built-in converts cleanly and every language has some", { skip }, () => {
  for (const l of Core.languages()) {
    const mine = Core.snippetsFor(l.id, []);
    assert.ok(mine.length >= 3, l.id);
    for (const s of mine) assert.deepEqual(tpl(s.body).warnings, [], `${l.id}/${s.label}`);
  }
});

test("snippets: imported definitions are read per prefix and scoped", { skip }, () => {
  const got = Core.snippetsImport([
    { name: "Loop", prefixes: ["lp", "loop"], body: "for (;;) {}", description: "a loop", scope: ["javascriptreact", "c++"] },
    { name: "Nothing", prefixes: [""], body: "x", description: "", scope: [] },
    { name: "Huge", prefixes: ["huge"], body: "x".repeat(5000), description: "", scope: [] },
    { name: "Shell", prefixes: ["sh"], body: "echo", description: "", scope: ["shellscript"] },
    { name: "All", prefixes: ["all"], body: "x", description: "", scope: [] },
  ]);
  assert.deepEqual(got.map((s) => s.label), ["lp", "loop", "sh", "all"]);
  assert.deepEqual(got[0].languages, ["javascript", "cpp"]);
  assert.deepEqual(got.find((s) => s.label === "sh").languages, ["unsupported"]);
  const rust = Core.snippetsFor("rust", got).map((s) => s.label);
  assert.ok(rust.includes("all") && !rust.includes("lp") && !rust.includes("sh"));
  assert.ok(Core.snippetsFor("cpp", got).some((s) => s.label === "lp"));
});

// ---- themes --------------------------------------------------------------------------------------------

test("themes: colours are validated and contrast follows WCAG", { skip }, () => {
  assert.equal(Core.themeColor("#FFF"), "#ffffff");
  assert.equal(Core.themeColor("#abcd"), "#aabbcc");
  assert.equal(Core.themeColor("#112233ff"), "#112233");
  for (const bad of ["red", "#12", "#12345", "#ggg", "url(javascript:alert(1))", "#fff; background: url(x)", ""]) assert.equal(Core.themeColor(bad), "", bad);
  assert.ok(Math.abs(Core.themeContrast("#000000", "#ffffff") - 21) < 0.001);
  assert.equal(Core.themeContrast("nope", "#ffffff"), 0);
});

test("themes: the built-in set is complete, readable and made of hex only", { skip }, () => {
  const all = Core.themes();
  assert.equal(all[0].id, "auto");
  assert.deepEqual(Object.keys(all[0].vars), []);
  assert.equal(new Set(all.map((t) => t.id)).size, all.length);
  const names = (t) => Object.keys(t.vars).sort().join(",");
  for (const t of all.slice(1)) {
    assert.equal(names(t), names(all[1]), t.id);
    for (const [k, v] of Object.entries(t.vars)) assert.match(v, /^#[0-9a-f]{6}([0-9a-f]{2})?$/, `${t.id} ${k}`);
    const need = t.contrast === "high" ? 7 : 4.5;
    const ok = (a, b, min) => assert.ok(Core.themeContrast(t.vars[a], t.vars[b]) >= min, `${t.id}: ${a} on ${b}`);
    ok("fg", "bg", need); ok("mut", "bg", need); ok("mut", "card", need); ok("fg", "card", need); ok("brand", "bg", need); ok("brand-fg", "brand", need);
    for (const r of ["keyword", "string", "number", "comment", "type", "function", "meta", "op"]) ok(`syn-${r}`, "ed-bg", need);
    for (const s of ["bad", "warn", "ok"]) ok(s, "bg", 3);
  }
});

const vs = (name, type, colors, tokenColors) => ({ name, type, colors: Object.entries(colors).map(([key, value]) => ({ key, value })), tokenColors });

test("themes: a VS Code theme maps its scopes to the editor's colours", { skip }, () => {
  const r = Core.themeImport(vs("Test", "dark", { "editor.background": "#000000", "editor.foreground": "#eeeeee" }, [
    { scopes: ["keyword"], foreground: "#ff0000" },
    { scopes: ["keyword.control"], foreground: "#00ff00" },
    { scopes: ["string"], foreground: "#ffff00" },
    { scopes: ["comment", "punctuation.definition.comment"], foreground: "#aaaaaa" },
  ]));
  const v = r.theme.vars;
  assert.deepEqual([v["ed-bg"], v.fg, v["syn-keyword"], v["syn-op"], v["syn-string"], v["syn-comment"], r.theme.scheme, r.theme.id], ["#000000", "#eeeeee", "#00ff00", "#ff0000", "#ffff00", "#aaaaaa", "dark", "vsc-test"]);
  const later = Core.themeImport(vs("T", "dark", { "editor.background": "#000000" }, [{ scopes: ["string"], foreground: "#ff0000" }, { scopes: ["string"], foreground: "#00ff00" }]));
  assert.equal(later.theme.vars["syn-string"], "#00ff00");
  const desc = Core.themeImport(vs("T", "dark", { "editor.background": "#000000" }, [{ scopes: ["source.js string.quoted"], foreground: "#ff00ff" }]));
  assert.equal(desc.theme.vars["syn-string"], "#ff00ff");
});

test("themes: bad colours are ignored, gaps are filled, poor contrast is reported", { skip }, () => {
  const base = Core.themes()[1].vars;
  const sparse = Core.themeImport(vs("Sparse", "light", { "editor.background": "#ffffff", "editor.foreground": "javascript:alert(1)" }, [{ scopes: ["keyword"], foreground: "url(x)" }]));
  assert.equal(sparse.theme.scheme, "light");
  assert.equal(sparse.theme.vars["syn-keyword"], base["syn-keyword"]);
  assert.equal(sparse.theme.vars.fg, base.fg);
  assert.equal(Core.themeImport(vs("L", "", { "editor.background": "#fafafa" }, [])).theme.scheme, "light");
  assert.equal(Core.themeImport(vs("D", "", { "editor.background": "#101010" }, [])).theme.scheme, "dark");
  assert.equal(Core.themeImport(vs("U", "", {}, [])).theme.scheme, "dark");
  assert.equal(Core.themeImport(vs("H", "hc", {}, [])).theme.contrast, "high");
  const dim = Core.themeImport(vs("Dim", "dark", { "editor.background": "#000000", "editor.foreground": "#222222" }, [{ scopes: ["comment"], foreground: "#111111" }]));
  assert.ok(dim.warnings.length >= 2);
  assert.ok(dim.warnings.some((w) => w.includes("comment")));
  assert.equal(dim.theme.vars["syn-comment"], "#111111");
  const empty = Core.themeImport(vs("Nothing", "dark", {}, []));
  assert.equal(Object.keys(empty.theme.vars).sort().join(), Object.keys(base).sort().join());
  assert.equal(empty.warnings.length, 0);
  const named = Core.themeImport(vs("<b>My Cool Theme!</b>", "dark", {}, []));
  assert.equal(named.theme.name, "bMy Cool Theme!/b");
  assert.match(named.theme.id, /^vsc-[a-z0-9-]+$/);
  const brand = (c) => Core.themeImport(vs("A", "dark", { "editor.background": "#000000", "button.background": c }, [])).theme.vars.brand;
  assert.equal(brand("#3794ff"), "#3794ff");
  assert.equal(brand("#0a1a2a"), Core.themes()[2].vars.brand);
});

// ---- settings and VS Code export -----------------------------------------------------------------------

test("settings: any object is brought into range", { skip }, () => {
  const d = Core.settingsDefaults();
  assert.deepEqual(Core.settingsSanitize(null), d);
  assert.deepEqual(Core.settingsSanitize({}), d);
  const s = Core.settingsSanitize({ fontSize: 3, tabSize: 40, theme: "Bad Theme!", wordWrap: "yes", insertSpaces: false });
  const lim = Core.settingsLimits();
  assert.deepEqual([s.fontSize, s.tabSize, s.theme, s.wordWrap, s.insertSpaces], [lim.fontMin, lim.tabMax, "auto", d.wordWrap, false]);
  assert.equal(Core.settingsSanitize({ fontSize: 999 }).fontSize, lim.fontMax);
  assert.equal(Core.settingsSanitize({ theme: "dark-plus" }).theme, "dark-plus");
  assert.equal(Core.settingsSanitize({ fontSize: "big" }).fontSize, d.fontSize);
});

test("settings: VS Code settings are read back and strangers are reported", { skip }, () => {
  const r = Core.settingsFromVsCode(null, [
    { key: "editor.tabSize", value: 2 }, { key: "editor.fontSize", value: 99 }, { key: "editor.wordWrap", value: "on" },
    { key: "editor.insertSpaces", value: false }, { key: "editor.renderWhitespace", value: "all" }, { key: "editor.lineNumbers", value: "off" },
    { key: "editor.formatOnSave", value: true }, { key: "editor.fontSize", value: "big" }, { key: "editor.tabSize", value: 2.5 },
  ]);
  const s = r.settings;
  assert.deepEqual([s.tabSize, s.fontSize, s.wordWrap, s.insertSpaces, s.renderWhitespace, s.lineNumbers], [2, Core.settingsLimits().fontMax, true, false, true, false]);
  assert.deepEqual([...r.ignored].sort(), ["editor.fontSize", "editor.formatOnSave", "editor.tabSize"]);
});

test("VS Code export: recommendations, valid JSON files, nothing overwritten", { skip }, () => {
  const p = project([["a.rs", ""], ["b.py", ""], ["c.js", ""], ["d.c", ""], ["e.cpp", ""]]);
  assert.deepEqual(Core.vsCodeRecommendations(p), ["ms-python.python", "rust-lang.rust-analyzer", "ms-vscode.cpptools", "GitHub.copilot", "GitHub.copilot-chat"]);
  assert.deepEqual(Core.vsCodeRecommendations(project([])), ["GitHub.copilot", "GitHub.copilot-chat"]);

  const files = Core.vsCodeFiles(project([["main.rs", "fn main() {}"]], { name: "My project!" }), { tabSize: 2, theme: "dark-plus" });
  assert.deepEqual(files.map((f) => f.path), [".vscode/extensions.json", ".vscode/settings.json", "My-project.code-workspace"]);
  const parsed = files.map((f) => JSON.parse(f.text));
  assert.deepEqual(parsed[0].recommendations, ["rust-lang.rust-analyzer", "GitHub.copilot", "GitHub.copilot-chat"]);
  assert.equal(parsed[1]["editor.tabSize"], 2);
  assert.equal(parsed[1]["workbench.colorTheme"], "Default Dark+");
  assert.deepEqual(parsed[2].folders, [{ path: "." }]);
  assert.deepEqual(parsed[2].extensions.recommendations, parsed[0].recommendations);
  for (const f of files) assert.equal(Core.pathNormalize(f.path).ok, true, f.path);

  const have = Core.vsCodeFiles(project([["main.rs", ""], [".vscode/settings.json", "{}"]]), null).map((f) => f.path);
  assert.ok(!have.includes(".vscode/settings.json") && have.includes(".vscode/extensions.json"));
});
