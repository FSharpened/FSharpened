// Multi-file runs (ADR-0024): the bundles the F# core makes from a project, checked by running them with the real
// interpreters and compilers where this machine has them (node, python, go, rustc, javac; TypeScript is run after
// esbuild strips the types). A language whose tool is missing is skipped with the reason, never passed unchecked.
// Needs the compiled F# core (`npm run fable`).
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawnSync } from "node:child_process";

const entry = new URL("../fable_out/Api.js", import.meta.url);
const built = existsSync(entry);
const Core = built ? await import(entry.href) : null;
const skip = built ? false : "Fable output missing: run `npm run fable` in ide/web first";

const project = (files, extra = {}) => ({ name: "t", files: Object.entries(files).map(([path, text]) => ({ path, text })), entry: null, pipeline: [], ...extra });
const make = (files, path) => Core.bundleMake(project(files), path);
const bundled = (files, path) => {
  const r = make(files, path);
  assert.equal(r.ok, true, "expected a bundle, got: " + JSON.stringify(r.problems));
  return r.bundle;
};
const problems = (files, path) => {
  const r = make(files, path);
  assert.equal(r.ok, false, "expected the bundle to be refused");
  return r.problems;
};

const have = (cmd, args = ["--version"]) => { try { return spawnSync(cmd, args, { encoding: "utf8" }).status === 0; } catch { return false; } };
const tool = (name, cmd, args) => (built ? (have(cmd, args) ? false : `${name} is not installed here`) : skip);

/** Runs source in a fresh folder: write it as `file`, then each command in turn. Returns the last command's result. */
const inFolder = (file, source, commands, input = "") => {
  const dir = mkdtempSync(join(tmpdir(), "fsh-bundle-"));
  try {
    writeFileSync(join(dir, file), source);
    let last;
    for (const [cmd, ...args] of commands) {
      last = spawnSync(cmd.replace("$DIR", dir), args.map((a) => a.replace("$DIR", dir)), { cwd: dir, encoding: "utf8", input, timeout: 120000 });
      if (last.status !== 0) break;
    }
    return { out: last.stdout, err: last.stderr, status: last.status };
  } finally { rmSync(dir, { recursive: true, force: true }); }
};

// ---------------------------------------------------------------- a project of one file is not changed at all
test("a file that needs no other file is sent exactly as written", { skip }, () => {
  for (const [path, text] of [["main.js", "import fs from 'fs';\nconsole.log(1)\n"], ["a.py", "import os\nprint(1)\n"], ["main.go", "package main\nfunc main(){}\n"], ["main.rs", "fn main(){}\n"], ["Main.java", "public class Main {}\n"], ["main.c", "#include <stdio.h>\nint main(){return 0;}\n"]]) {
    const b = bundled({ [path]: text, "README.md": "# hi" }, path);
    assert.equal(b.source, text, path);
    assert.deepEqual(b.included, [path]);
  }
});

// ---------------------------------------------------------------- JavaScript
const jsProject = {
  "main.js": [
    'import { greet, VERSION } from "./lib/greet.js";',
    'import helper from "./lib/helper";',
    'import * as ns from "./lib/greet.js";',
    'import fs from "fs";',
    'const util = require("./lib/util.cjs");',
    'const cfg = require("./cfg.json");',
    'export { greet as again } from "./lib/greet.js";',
    "console.log(greet('world'), VERSION, helper(2), util.double(4), typeof fs.readFileSync, cfg.name, Object.keys(ns).join('+'));",
    "",
  ].join("\n"),
  "lib/greet.js": 'export const VERSION = "1.0";\nexport function greet(n) { return "hello " + n; }\n',
  "lib/helper.js": 'import { VERSION } from "./greet.js";\nexport default function (x) { return VERSION === "1.0" ? x * 10 : 0; }\n',
  "lib/util.cjs": "exports.double = (x) => x * 2;\n",
  "cfg.json": '{ "name": "cfg" }\n',
};

test("JavaScript: import, export, require and JSON files run as one program", { skip: tool("node", "node") }, () => {
  const b = bundled(jsProject, "main.js");
  assert.deepEqual([...b.included].sort(), ["cfg.json", "lib/greet.js", "lib/helper.js", "lib/util.cjs", "main.js"]);
  const r = inFolder("bundle.js", b.source, [["node", "bundle.js"]]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.out.trim(), "hello world 1.0 20 8 function cfg VERSION+greet");
});

test("JavaScript: strings and comments that look like imports are left alone, and circular imports work", { skip: tool("node", "node") }, () => {
  const files = {
    "main.js": 'import { a } from "./a.js";\n// import { zzz } from "./nowhere.js";\nconst s = "import x from \\"./nope\\"";\nconst t = `export const y = ${a()} // import q from "./nada"`;\nconsole.log(a(), s.length > 0, t.length > 0);\n',
    "a.js": 'import { b } from "./b.js";\nexport function a() { return "a" + b(); }\n',
    "b.js": 'import { a } from "./a.js";\nexport function b() { return typeof a === "function" ? "b" : "?"; }\n',
  };
  const b = bundled(files, "main.js");
  assert.deepEqual([...b.included].sort(), ["a.js", "b.js", "main.js"]);
  const r = inFolder("bundle.js", b.source, [["node", "bundle.js"]]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.out.trim(), "ab true true");
});

test("JavaScript: a line in a runtime error maps back to its file", { skip: tool("node", "node") }, () => {
  const files = { "main.js": 'const { boom } = require("./lib/boom.js");\nboom();\n', "lib/boom.js": 'exports.boom = function () {\n  throw new Error("kaboom");\n};\n' };
  const b = bundled(files, "main.js");
  const r = inFolder("bundle.js", b.source, [["node", "bundle.js"]]);
  assert.notEqual(r.status, 0);
  const line = Number(/bundle\.js:(\d+)/.exec(r.err)[1]);
  assert.deepEqual(Core.bundleLocate(b, r.err, line), { path: "lib/boom.js", line: 2 });
});

test("JavaScript: an import that cannot be found names the file, the line and what was looked for", { skip }, () => {
  const ps = problems({ "main.js": 'const x = 1;\nimport { y } from "./missing";\n', "other.js": "" }, "main.js");
  assert.equal(ps.length, 1);
  assert.equal(ps[0].path, "main.js");
  assert.equal(ps[0].line, 2);
  assert.match(ps[0].message, /cannot find "\.\/missing" \(looked for missing, missing\.js/);
});

test("JavaScript: a file of another language, or outside the project, is refused", { skip }, () => {
  assert.match(problems({ "main.js": 'require("./x");', "x.ts": "export {}" }, "main.js")[0].message, /x\.ts is TypeScript, not JavaScript/);
  assert.match(problems({ "a/main.js": 'require("../../x");' }, "a/main.js")[0].message, /climbs out of the project/);
  assert.match(problems({ "main.js": 'import("./late.js");', "late.js": "" }, "main.js")[0].message, /dynamic import/);
});

test("a bundle over the gateway's limit is refused with its size, not truncated", { skip }, () => {
  const big = "// " + "x".repeat(40000) + "\n";
  const ps = problems({ "main.js": 'require("./a");\nrequire("./b");\n', "a.js": big, "b.js": big }, "main.js");
  assert.match(ps[0].message, /the bundle is \d+ characters; the gateway takes at most 65536/);
});

// ---------------------------------------------------------------- TypeScript
test("TypeScript: imported files share one scope, so types survive; the program still runs", { skip: tool("esbuild", process.execPath, ["--version"]) }, async () => {
  const files = {
    "main.ts": 'import { Shape, area } from "./shapes";\nimport Counter from "./counter.js";\nimport type { Named } from "./shapes";\nimport fs from "fs";\nconst s: Shape = { w: 2, h: 3 };\nconst n: Named = { name: "box" };\nconsole.log(area(s), n.name, new Counter().next(), typeof fs.existsSync);\n',
    "shapes.ts": "export interface Shape { w: number; h: number }\nexport type Named = { name: string };\nexport function area(s: Shape): number { return s.w * s.h; }\n",
    "counter.ts": "export default class Counter {\n  private n = 0;\n  next(): number { return ++this.n; }\n}\n",
  };
  const b = bundled(files, "main.ts");
  assert.deepEqual(b.included, ["shapes.ts", "counter.ts", "main.ts"]);
  assert.equal(b.source.split("\n")[0], 'import fs from "fs";', "package imports are kept, once, at the top");
  assert.ok(!/import .* from "\.\//.test(b.source));
  const { transformSync } = await import("esbuild");
  const js = transformSync(b.source, { loader: "ts", format: "cjs" }).code.replace(/^var import_fs.*$/m, "const fs = require('fs');").replace(/import_fs\.default/g, "fs");
  const r = inFolder("bundle.cjs", js, [["node", "bundle.cjs"]]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.out.trim(), "6 box 1 function");
});

test("TypeScript: what cannot be expressed in one scope is refused, not guessed", { skip }, () => {
  const lib = 'export const a = 1;\nexport default 5;\n';
  assert.match(problems({ "main.ts": 'import { a as b } from "./lib";', "lib.ts": lib }, "main.ts")[0].message, /import \{ a as b \}/);
  assert.match(problems({ "main.ts": 'import * as l from "./lib";', "lib.ts": lib }, "main.ts")[0].message, /import \* as/);
  assert.match(problems({ "main.ts": 'import D from "./lib";', "lib.ts": "export const a = 1;" }, "main.ts")[0].message, /has no default export/);
});

// ---------------------------------------------------------------- Python
const pyProject = {
  "main.py": "import util\nfrom pkg import mod\nfrom pkg.sub import deep\nimport os\nprint(util.add(1, 2), mod.NAME, deep.value(), os.sep != '')\n",
  "util.py": "def add(a, b):\n    return a + b\n",
  "pkg/__init__.py": "from . import mod\n",
  "pkg/mod.py": 'NAME = "mod"\n',
  "pkg/sub/deep.py": "from .. import mod\n\ndef value():\n    return mod.NAME + '!'\n",
};

test("Python: modules, packages, namespace packages and relative imports are found by import", { skip: tool("python", "python") }, () => {
  const b = bundled(pyProject, "main.py");
  assert.deepEqual([...b.included].sort(), ["main.py", "pkg/__init__.py", "pkg/mod.py", "pkg/sub/deep.py", "util.py"]);
  const r = inFolder("bundle.py", b.source, [["python", "bundle.py"]]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.out.trim(), "3 mod mod! True");
});

test("Python: __name__ is __main__ for the entry, and its input is the program's input", { skip: tool("python", "python") }, () => {
  const files = { "app/main.py": "import helper\nif __name__ == '__main__':\n    print(helper.shout(input()))\n", "app/helper.py": "def shout(s):\n    return s.upper()\n" };
  const b = bundled(files, "app/main.py");
  const r = inFolder("bundle.py", b.source, [["python", "bundle.py"]], "quiet\n");
  assert.equal(r.status, 0, r.err);
  assert.equal(r.out.trim(), "QUIET");
});

test("Python: a traceback names the real file and line, and the bundle can find it", { skip: tool("python", "python") }, () => {
  const files = { "main.py": "import util\nutil.fail()\n", "util.py": "def fail():\n    x = 1\n    raise ValueError('nope')\n" };
  const b = bundled(files, "main.py");
  const r = inFolder("bundle.py", b.source, [["python", "bundle.py"]]);
  assert.notEqual(r.status, 0);
  assert.match(r.err, /File "util\.py", line 3, in fail/);
  assert.deepEqual(Core.bundleLocate(b, r.err, 3), { path: "util.py", line: 3 });
  assert.deepEqual(Core.bundleLocate(b, r.err, 2), { path: "main.py", line: 2 });
});

test("Python: an import that is in the project but cannot be found says where it looked", { skip }, () => {
  const ps = problems({ "main.py": "import pkg.nothing\n", "pkg/__init__.py": "" }, "main.py");
  assert.equal(ps[0].line, 1);
  assert.match(ps[0].message, /cannot find module "pkg\.nothing" \(looked for pkg\/nothing\.py and pkg\/nothing\/__init__\.py\)/);
  assert.match(problems({ "main.py": "from . import x\n", "x.py": "" }, "main.py")[0].message, /relative import in the entry file/);
  assert.match(problems({ "main.py": "import a\n", "a.py": "from ... import q\n" }, "main.py")[0].message, /climbs above the top/);
});

// ---------------------------------------------------------------- Go
test("Go: the files of one package in the entry's folder are merged, imports once", { skip: tool("go", "go", ["version"]) }, () => {
  const files = {
    "main.go": 'package main\n\nimport (\n\t"fmt"\n\t"strings"\n)\n\nfunc main() {\n\tfmt.Println(shout("go"), strings.Repeat("=", 3), count())\n}\n',
    "util.go": 'package main\n\nimport "strings"\n\nfunc shout(s string) string { return strings.ToUpper(s) }\n',
    "more.go": 'package main\n\nimport m "math"\n\nfunc count() int { return int(m.Sqrt(16)) }\n',
    "other/x.go": "package other\n",
    "tool.go": 'package main\n\nfunc main() { println("second main") }\n',
  };
  const b = bundled(files, "main.go");
  assert.ok(b.notes.some((n) => /tool\.go: it has its own main/.test(n)), JSON.stringify(b.notes));
  assert.equal((b.source.match(/"strings"/g) ?? []).length, 1);
  const r = inFolder("bundle.go", b.source, [["go", "run", "bundle.go"]]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.out.trim(), "GO === 4");
});

test("Go: a compiler error is found in the file and line it came from", { skip: tool("go", "go", ["version"]) }, () => {
  const files = { "main.go": 'package main\n\nfunc main() {\n\thello()\n}\n', "util.go": 'package main\n\nimport "fmt"\n\nfunc hello() {\n\tvar x int = "text"\n\tfmt.Println(x)\n}\n' };
  const b = bundled(files, "main.go");
  const r = inFolder("bundle.go", b.source, [["go", "run", "bundle.go"]]);
  assert.notEqual(r.status, 0);
  const line = Number(/bundle\.go:(\d+):/.exec(r.err)[1]);
  assert.deepEqual(Core.bundleLocate(b, r.err, line), { path: "util.go", line: 6 });
});

// ---------------------------------------------------------------- Rust
test("Rust: `mod` declarations become inline modules, nested ones too", { skip: tool("rustc", "rustc") }, () => {
  const files = {
    "main.rs": "mod util;\npub mod shapes;\n\nfn main() {\n    println!(\"{} {}\", util::double(21), shapes::circle::area(2));\n}\n",
    "util.rs": "pub fn double(x: i32) -> i32 { x * 2 }\n",
    "shapes.rs": "pub mod circle;\n",
    "shapes/circle.rs": "pub fn area(r: i32) -> i32 { 3 * r * r }\n",
    "unused.rs": "fn nope() {}\n",
  };
  const b = bundled(files, "main.rs");
  assert.deepEqual(b.included, ["main.rs", "util.rs", "shapes.rs", "shapes/circle.rs"]);
  assert.ok(b.notes.some((n) => /not reached from main\.rs: unused\.rs/.test(n)));
  const r = inFolder("bundle.rs", b.source, [["rustc", "--edition", "2021", "-o", "bundle.exe", "bundle.rs"], [join("$DIR", "bundle.exe")]]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.out.trim(), "42 12");
});

test("Rust: an error in a module is mapped to the module's file", { skip: tool("rustc", "rustc") }, () => {
  const files = { "main.rs": "mod util;\nfn main() { util::f(); }\n", "util.rs": "// helper\npub fn f() {\n    let x: i32 = \"no\";\n}\n" };
  const b = bundled(files, "main.rs");
  const r = inFolder("bundle.rs", b.source, [["rustc", "--edition", "2021", "-o", "bundle.exe", "bundle.rs"]]);
  assert.notEqual(r.status, 0);
  const line = Number(/-->\s*bundle\.rs:(\d+):/.exec(r.err)[1]);
  assert.deepEqual(Core.bundleLocate(b, r.err, line), { path: "util.rs", line: 3 });
});

test("Rust: a missing module file says which two names were tried", { skip }, () => {
  const ps = problems({ "main.rs": "fn x() {}\nmod ghost;\nfn main() {}\n" }, "main.rs");
  assert.equal(ps[0].line, 2);
  assert.match(ps[0].message, /`mod ghost;` has no file: add ghost\.rs or ghost\/mod\.rs/);
});

// ---------------------------------------------------------------- Java
test("Java: the classes the entry names are merged into one file, and only the entry stays public", { skip: tool("javac", "javac", ["-version"]) }, () => {
  const files = {
    "Main.java": "import java.util.List;\n\npublic class Main {\n    public static void main(String[] args) {\n        System.out.println(Helper.twice(List.of(1, 2).size()) + \" \" + new Shape().name());\n    }\n}\n",
    "Helper.java": "package demo;\n\nimport java.util.Map;\nimport demo.Shape;\n\npublic class Helper {\n    static int twice(int n) { return n * 2 + Map.of().size(); }\n}\n",
    "Shape.java": "public final class Shape {\n    String name() { return \"shape\"; }\n}\n",
    "Unused.java": "public class Unused {}\n",
  };
  const b = bundled(files, "Main.java");
  assert.deepEqual(b.included, ["Main.java", "Helper.java", "Shape.java"]);
  assert.equal((b.source.match(/public /g) ?? []).length, 2, "the entry's class and its main method; the other classes are not public");
  assert.ok(!/public (final )?class (Helper|Shape)/.test(b.source));
  const r = inFolder("Main.java", b.source, [["javac", "Main.java"], ["java", "Main"]]);
  assert.equal(r.status, 0, r.err);
  assert.equal(r.out.trim(), "4 shape");
});

// ---------------------------------------------------------------- C and C++ (no compiler needed to check the text)
test("C: quoted includes are replaced by the file once, and the matching .c follows", { skip }, () => {
  const files = {
    "main.c": '#include <stdio.h>\n#include "util.h"\n#include "util.h"\n#include "config.h"\nint main(void) { printf("%d\\n", twice(2)); return 0; }\n',
    "util.h": "#pragma once\nint twice(int x);\n",
    "util.c": '#include "util.h"\nint twice(int x) { return x * 2; }\n',
    "config.h": "#define N 1\n",
  };
  const b = bundled(files, "main.c");
  assert.deepEqual(b.included, ["main.c", "util.h", "config.h", "util.c"]);
  assert.equal((b.source.match(/int twice\(int x\);/g) ?? []).length, 1);
  assert.ok(!b.source.includes("#pragma once"));
  assert.ok(b.source.indexOf("int twice(int x) {") > b.source.indexOf("int main"), "the implementation follows the entry");
  // every line is accounted for: lines of the entry and of the files map back
  const where = (needle) => Core.bundleLocate(b, "", b.source.slice(0, b.source.indexOf(needle)).split("\n").length);
  assert.deepEqual(where("#define N 1"), { path: "config.h", line: 1 });
  assert.deepEqual(where("int main"), { path: "main.c", line: 5 });
  assert.deepEqual(where("int twice(int x) {"), { path: "util.c", line: 2 });
});

test("C++: headers and their .cpp, and an include that is not in the project is left for the compiler", { skip }, () => {
  const files = { "main.cpp": '#include "box.hpp"\n#include "vendor.h"\nint main() { return Box{}.size(); }\n', "box.hpp": "struct Box { int size() const; };\n", "box.cpp": '#include "box.hpp"\nint Box::size() const { return 0; }\n' };
  const b = bundled(files, "main.cpp");
  assert.deepEqual(b.included, ["main.cpp", "box.hpp", "box.cpp"]);
  assert.ok(b.source.includes('#include "vendor.h"'));
  assert.ok(b.notes.some((n) => /vendor\.h.*left as it is/.test(n)));
});

// ---------------------------------------------------------------- the languages that cannot be merged
test("the other languages run the entry file alone and say so", { skip }, () => {
  const b = bundled({ "main.rb": "puts 1\n", "lib.rb": "x = 1\n" }, "main.rb");
  assert.equal(b.source, "puts 1\n");
  assert.match(b.notes[0], /only main\.rb is sent; the other Ruby files stay behind/);
  for (const id of ["javascript", "typescript", "python", "rust", "go", "java", "c", "cpp"]) assert.equal(Core.bundleSupport(id).merged, true, id);
  for (const id of ["fsharp", "csharp", "vb", "dart", "ruby", "lua", "php", "r", "haskell", "ocaml"]) {
    const s = Core.bundleSupport(id);
    assert.equal(s.merged, false, id);
    assert.ok(s.text.length > 10, id);
  }
});

// ---------------------------------------------------------------- the pipeline
test("a pipeline plan bundles every step, and stops at the first one that fails or prints too much", { skip }, () => {
  const p = project({ "gen.py": "print('abc')\n", "up.js": "console.log(require('fs').readFileSync(0,'utf8').toUpperCase())\n", "README.md": "x" }, { pipeline: ["gen.py", "up.js"] });
  const plan = Core.pipelinePlan(p);
  assert.equal(plan.ok, true);
  assert.deepEqual(plan.stages.map((s) => [s.index, s.path, s.language]), [[0, "gen.py", "python"], [1, "up.js", "javascript"]]);
  const paths = Core.pipelineSteps(p);
  assert.deepEqual(paths, ["gen.py", "up.js"]);
  const first = Core.pipelineNext(paths, [], "typed");
  assert.deepEqual([first.done, first.index, first.stdin], [false, 0, "typed"]);
  const second = Core.pipelineNext(paths, [{ path: "gen.py", ok: true, stdout: "abc\n", summary: "Finished" }], "typed");
  assert.deepEqual([second.done, second.index, second.stdin], [false, 1, "abc\n"]);
  const all = Core.pipelineNext(paths, [{ path: "gen.py", ok: true, stdout: "abc\n", summary: "Finished" }, { path: "up.js", ok: true, stdout: "ABC\n", summary: "Finished in 0.1s" }], "");
  assert.deepEqual([all.done, all.ok, all.summary], [true, true, "All 2 steps finished"]);
  const failed = Core.pipelineNext(paths, [{ path: "gen.py", ok: false, stdout: "", summary: "Exited with code 1" }], "");
  assert.deepEqual([failed.done, failed.ok], [true, false]);
  assert.match(failed.summary, /Step 1 of 2 \(gen\.py\) failed: Exited with code 1; 1 step not run/);
  const heavy = Core.pipelineNext(paths, [{ path: "gen.py", ok: true, stdout: "x".repeat(5000), summary: "Finished" }], "");
  assert.equal(heavy.done, true);
  assert.match(heavy.summary, /Step 2 \(up\.js\) cannot start: the output of gen\.py is 5000 characters and the gateway takes at most 4096/);
});

test("a project without a pipeline runs its entry file as the only step", { skip }, () => {
  const p = project({ "main.py": "print(1)\n", "util.py": "" });
  assert.deepEqual(Core.pipelineSteps(p), ["main.py"]);
  assert.equal(Core.pipelinePlan(p).stages.length, 1);
  assert.equal(Core.pipelinePlan(project({ "README.md": "x" })).ok, false);
});

test("a pipeline plan reports the problems of every step at once", { skip }, () => {
  const p = project({ "a.js": 'require("./nope1");', "b.js": 'require("./nope2");' }, { pipeline: ["a.js", "b.js"] });
  const plan = Core.pipelinePlan(p);
  assert.equal(plan.ok, false);
  assert.deepEqual(plan.problems.map((x) => x.path), ["a.js", "b.js"]);
});
