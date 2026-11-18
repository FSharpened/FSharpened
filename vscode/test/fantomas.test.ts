import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { FantomasDaemon, interpret } from "../src/fantomas.ts";

const fake = fileURLToPath(new URL("../test/fixtures/fake-fantomas.mjs", import.meta.url));
const repoRoot = fileURLToPath(new URL("../../", import.meta.url));
const launch = { command: process.execPath, args: [fake] };

test("interpret maps every answer Fantomas gives", () => {
  assert.deepEqual(interpret({ Case: "Formatted", Fields: ["a.fs", "let x = 1\n", null] }), { kind: "formatted", text: "let x = 1\n" });
  assert.deepEqual(interpret({ Case: "Unchanged", Fields: ["a.fs"] }), { kind: "unchanged" });
  assert.deepEqual(interpret({ Case: "IgnoredFile", Fields: ["a.fs"] }), { kind: "ignored" });
  assert.equal(interpret({ Case: "Formatted", Fields: ["a.fs"] }).kind, "error");
  assert.equal(interpret({ Case: "Surprise" }).kind, "error");
  assert.equal(interpret(null).kind, "error");
});

test("a parse error shows the compiler's message, not the whole record dump", () => {
  const dump = 'ParseException\n  [{ Severity = Error\n     Message =\n      "Unmatched \'(\'" }]';
  assert.deepEqual(interpret({ Case: "Error", Fields: ["a.fs", dump] }), { kind: "error", message: "Unmatched '('" });
});

test("the daemon formats, reports unchanged text, and reports errors", async () => {
  const d = new FantomasDaemon(launch);
  try {
    assert.deepEqual(await d.format("let x = 1   \n", "a.fs", 10_000), { kind: "formatted", text: "let x = 1\n" });
    assert.deepEqual(await d.format("let x = 1\n", "a.fs", 10_000), { kind: "unchanged" });
    const bad = await d.format("SYNTAX_ERROR", "a.fs", 10_000);
    assert.equal(bad.kind, "error");
    assert.equal(bad.kind === "error" && bad.message, "Unexpected syntax");
    assert.equal(await d.version(10_000), "0.0.0-fake");
  } finally {
    d.dispose();
  }
});

test("one daemon serves several requests at once", async () => {
  const d = new FantomasDaemon(launch);
  try {
    const all = await Promise.all([1, 2, 3, 4].map((i) => d.format(`let x${i} = 1  \n`, `a${i}.fs`, 10_000)));
    assert.deepEqual(all.map((r) => (r.kind === "formatted" ? r.text : r.kind)), ["let x1 = 1\n", "let x2 = 1\n", "let x3 = 1\n", "let x4 = 1\n"]);
  } finally {
    d.dispose();
  }
});

test("a hung daemon times out, and the next request starts a fresh one", async () => {
  const d = new FantomasDaemon(launch);
  try {
    const hung = await d.format("HANG", "a.fs", 300);
    assert.equal(hung.kind, "error");
    assert.match(hung.kind === "error" ? hung.message : "", /timed out/);
    assert.deepEqual(await d.format("let y = 2 \n", "a.fs", 10_000), { kind: "formatted", text: "let y = 2\n" });
  } finally {
    d.dispose();
  }
});

test("a crashed daemon is an error for that request and is replaced for the next", async () => {
  const d = new FantomasDaemon(launch);
  try {
    const crashed = await d.format("CRASH", "a.fs", 10_000);
    assert.equal(crashed.kind, "error");
    assert.deepEqual(await d.format("let z = 3 \n", "a.fs", 10_000), { kind: "formatted", text: "let z = 3\n" });
  } finally {
    d.dispose();
  }
});

test("a program that cannot start is an error, not a crash", async () => {
  const d = new FantomasDaemon({ command: path.join(tmpdir(), "definitely-not-a-program"), args: [] });
  try {
    const r = await d.format("let x = 1\n", "a.fs", 5_000);
    assert.equal(r.kind, "error");
    assert.match(r.kind === "error" ? r.message : "", /could not start/);
  } finally {
    d.dispose();
  }
});

test("a disposed daemon refuses further work", async () => {
  const d = new FantomasDaemon(launch);
  d.dispose();
  assert.equal((await d.format("let x = 1\n", "a.fs", 1000)).kind, "error");
});

// The one test that needs a real Fantomas: the repo's own `dotnet fantomas` (restored by `dotnet tool restore`). It is
// skipped, loudly, on a machine without it, and run by `fsh_ci.py` where the .NET stages run.
const real = spawnSync("dotnet", ["fantomas", "--version"], { cwd: repoRoot, encoding: "utf8", timeout: 60_000 });
test("real Fantomas formats through the daemon and honours .editorconfig", { skip: real.status === 0 ? false : "dotnet fantomas is not available (run `dotnet tool restore`)" }, async () => {
  const dir = mkdtempSync(path.join(repoRoot, "vscode", "test", ".real-fantomas-"));
  try {
    writeFileSync(path.join(dir, ".editorconfig"), "root = true\n[*.fs]\nindent_size = 2\nend_of_line = lf\n");
    const file = path.join(dir, "A.fs");
    const d = new FantomasDaemon({ command: "dotnet", args: ["fantomas"], cwd: repoRoot });
    try {
      const r = await d.format("module A\nlet f x =\n       x+1\n", file, 60_000);
      assert.deepEqual(r, { kind: "formatted", text: "module A\n\nlet f x = x + 1\n" });
      const two = "module A\n\nlet f x =\n  match x with\n  | 1 -> 2\n  | _ -> 3\n";
      const wide = await d.format("module A\nlet f x =\n        match x with\n        | 1 -> 2\n        | _ -> 3\n", file, 60_000);
      assert.ok(wide.kind === "formatted" && wide.text === two, "two-space indent comes from the .editorconfig: " + JSON.stringify(wide));
      const bad = await d.format("let f x = (\n", file, 60_000);
      assert.equal(bad.kind, "error");
    } finally {
      d.dispose();
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
