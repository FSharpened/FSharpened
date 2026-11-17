// The IDE core runs in the browser as Fable-compiled JavaScript, and .NET and JavaScript regular expressions differ
// in small ways. These checks run the *compiled* core against output captured from the real execution services, so a
// parser that passes in xunit but misreads the same text in the browser is caught here.
//
// Needs the Fable output: `npm run fable` (CI and `python tools/site/build.py --require-ide` both produce it).
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";

const entry = new URL("../fable_out/Api.js", import.meta.url);
const built = existsSync(entry);
const Core = built ? await import(entry.href) : null;
const skip = built ? false : "Fable output missing: run `npm run fable` in ide/web first";

const raw = (over) => ({ engine: "test", buildFailed: false, buildOutput: "", stdout: "", stderr: "", exitCode: 0, timedOut: false, durationMs: 10, cached: false, ...over });

test("the language table reaches JavaScript with groups and flags", { skip }, () => {
  const langs = Core.languages();
  assert.equal(langs.length, 18);
  assert.deepEqual(langs.map((l) => l.id), ["fsharp", "csharp", "vb", "javascript", "typescript", "python", "rust", "dart", "c", "cpp", "go", "java", "ruby", "lua", "php", "r", "haskell", "ocaml"]);
  assert.deepEqual(langs.filter((l) => l.flags).map((l) => l.id), ["rust", "c", "cpp"]);
  assert.deepEqual([...new Set(langs.map((l) => l.group))], ["dotnet", "fable", "systems", "scripting", "functional"]);
  for (const l of langs) assert.ok(Core.compilers(l.id).length >= 1, l.id);
});

test("every language has a starter template that passes its own local checks", { skip }, () => {
  const templates = Core.templates();
  assert.equal(templates.length, 34);
  for (const l of Core.languages()) {
    const mine = templates.filter((t) => t.language === l.id);
    assert.ok(mine.length >= 1, l.id);
    for (const t of mine) assert.deepEqual(Core.localChecks(l.id, t.code), [], t.key);
  }
});

test("python traceback", { skip }, () => {
  const r = Core.interpret("python", raw({ exitCode: 1, stderr: 'Traceback (most recent call last):\n  File "/app/output.s", line 2, in <module>\n    undefined_name + 1\n    ^^^^^^^^^^^^^^\nNameError: name \'undefined_name\' is not defined' }));
  assert.equal(r.ok, false);
  assert.equal(r.diagnostics.length, 1);
  assert.equal(r.diagnostics[0].code, "NameError");
  assert.equal(r.diagnostics[0].line, 2);
});

test("go compile errors keep their columns", { skip }, () => {
  const r = Core.interpret("go", raw({ buildFailed: true, exitCode: -1, buildOutput: "# command-line-arguments\n./example.go:4:2: declared and not used: x\n./example.go:4:7: undefined: undefinedThing" }));
  assert.equal(r.phase, "build");
  assert.equal(r.summary, "Build failed: 2 errors");
  assert.deepEqual(r.diagnostics.map((d) => [d.line, d.column]), [[4, 2], [4, 7]]);
});

test("php errors are read from stdout", { skip }, () => {
  const r = Core.interpret("php", raw({ exitCode: 255, stdout: "1\nFatal error: Uncaught Exception: boom in /home/wandbox/prog.php:3\nStack trace:\n#0 {main}\n  thrown in /home/wandbox/prog.php on line 3\n" }));
  assert.equal(r.diagnostics.length, 1);
  assert.equal(r.diagnostics[0].line, 3);
  assert.equal(r.diagnostics[0].message, "Uncaught Exception: boom");
  assert.equal(Core.interpret("python", raw({ stdout: "Error: not an error" })).diagnostics.length, 0);
});

test("ghc, ocaml, dart, haskell runtime and node errors", { skip }, () => {
  const ghc = Core.interpret("haskell", raw({ buildFailed: true, exitCode: -1, buildOutput: "[1 of 2] Compiling Main             ( <source>, /app/example.o )\n<source>:2:22: error: [GHC-83865]\n    * Couldn't match type `Bool' with `[Char]'\n      Expected: String\n        Actual: Bool\n  |\n2 | main = putStrLn (1 + True)\n  |                      ^^^^" }));
  assert.equal(ghc.diagnostics.length, 1);
  assert.deepEqual([ghc.diagnostics[0].line, ghc.diagnostics[0].column], [2, 22]);
  assert.match(ghc.diagnostics[0].message, /Couldn't match type/);

  const ml = Core.interpret("ocaml", raw({ buildFailed: true, exitCode: -1, buildOutput: 'File "<source>", line 1, characters 23-24:\n1 | let () = print_endline 1\n                           ^\nError: The constant 1 has type int but an expression was expected of type\n         string' }));
  assert.deepEqual([ml.diagnostics[0].line, ml.diagnostics[0].column], [1, 24]);

  const dart = Core.interpret("dart", raw({ exitCode: 255, stderr: "Unhandled exception:\nBad state: boom\n#0      main (file:///app/example.dart:2)\n#1      _delayEntrypointInvocation.<anonymous closure> (dart:isolate-patch/isolate_patch.dart:313)" }));
  assert.equal(dart.diagnostics.length, 1);
  assert.equal(dart.diagnostics[0].line, 2);

  const hsRun = Core.interpret("haskell", raw({ exitCode: 1, stderr: "output.s: Uncaught exception ghc-internal:GHC.Internal.Exception.ErrorCall:\n\nboom\n\nHasCallStack backtrace:\n  error, called at /app/example.hs:2:8 in main:Main\n" }));
  assert.equal(hsRun.diagnostics.length, 1);
  assert.equal(hsRun.diagnostics[0].message, "boom");

  const js = Core.interpret("javascript", raw({ exitCode: 1, stderr: "/home/wandbox/prog.js:2\nnull.foo\n     ^\n\nTypeError: Cannot read properties of null (reading 'foo')\n    at Object.<anonymous> (/home/wandbox/prog.js:2:6)\n\nNode.js v20.17.0\n" }));
  assert.equal(js.diagnostics.length, 1);
  assert.equal(js.diagnostics[0].line, 2);
});

test("tsc, vb and lua output", { skip }, () => {
  const ts = Core.interpret("typescript", raw({ buildFailed: true, exitCode: 2, buildOutput: "prog.ts(1,7): error TS2322: Type 'string' is not assignable to type 'number'.\n" }));
  assert.deepEqual([ts.diagnostics[0].code, ts.diagnostics[0].line, ts.diagnostics[0].column], ["TS2322", 1, 7]);

  const vb = Core.interpret("vb", raw({ buildFailed: true, exitCode: -1, buildOutput: "<source>(3) : error BC30451: 'missing' is not declared. It may be inaccessible due to its protection level.\n\n        Console.WriteLine(missing)\n                          ~~~~~~~" }));
  assert.deepEqual([vb.diagnostics[0].code, vb.diagnostics[0].line], ["BC30451", 3]);

  const lua = Core.interpret("lua", raw({ exitCode: 1, stderr: "/opt/compiler-explorer/lua-5.4.7/bin/lua: /app/output.s:2: boom\nstack traceback:\n\t[C]: in function 'error'\n\t/app/output.s:2: in main chunk\n\t[C]: in ?" }));
  assert.equal(lua.diagnostics.length, 1);
  assert.equal(lua.diagnostics[0].message, "boom");
});

test("the bracket check stays out of languages it does not understand", { skip }, () => {
  assert.deepEqual(Core.localChecks("python", 's = """(\nprint(s)'), []);
  assert.deepEqual(Core.localChecks("lua", "--[[ ( ]]\nprint('x')"), []);
  assert.ok(Core.localChecks("java", "class Main { public static void main(String[] a) {").some((d) => d.severity === "error"));
});

test("the run request carries the compiler's own language", { skip }, () => {
  const body = JSON.parse(Core.requestJson("dotnet90vbcoreclr", "", "Module M\nEnd Module", ""));
  assert.equal(body.language, "vb");
  assert.equal(body.compiler, "dotnet90vbcoreclr");
});
