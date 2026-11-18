import { test } from "node:test";
import assert from "node:assert/strict";
import * as path from "node:path";
import { findManifest, findOnPath, manifestCommands, resolveTool, type Context, type Env, type ToolSpec } from "../src/resolve.ts";

/** A fake disk: the set of files and their text. Paths are built with path.join so the tests hold on every OS. */
function disk(files: Record<string, string>) {
  const norm = (p: string): string => path.resolve(p);
  const map = new Map(Object.entries(files).map(([k, v]) => [norm(k), v]));
  return {
    isFile: (p: string): boolean => map.has(norm(p)),
    readText: (p: string): string | undefined => map.get(norm(p)),
  };
}

const root = path.resolve("ws");
const bin = path.resolve("bin");
const manifest = (...commands: string[]): string => JSON.stringify({ version: 1, isRoot: true, tools: Object.fromEntries(commands.map((c) => [c, { version: "1", commands: [c] }])) });
const ctx = (files: Record<string, string>, env: Env = {}, extra: Partial<Context> = {}): Context => ({
  env: { PATH: bin, ...env },
  fs: disk(files),
  workspaceRoot: root,
  startDir: undefined,
  ...extra,
});

const fantomas: ToolSpec = { label: "Fantomas", setting: "", settingName: "fsharpened.fantomas.path", manifestCommand: "fantomas", pathNames: ["fantomas"] };

test("a configured path wins and is used as is", () => {
  const exe = path.join(bin, "my-fantomas");
  const r = resolveTool({ ...fantomas, setting: exe }, ctx({ [exe]: "", [path.join(root, ".config", "dotnet-tools.json")]: manifest("fantomas") }));
  assert.ok(r.found);
  assert.equal(r.command, exe);
  assert.equal(r.source, "setting");
});

test("a configured path that does not exist is an error, not a silent fall-back", () => {
  const r = resolveTool({ ...fantomas, setting: path.join(bin, "nope") }, ctx({ [path.join(root, ".config", "dotnet-tools.json")]: manifest("fantomas"), [path.join(bin, "dotnet")]: "" }));
  assert.equal(r.found, false);
  assert.match(r.found ? "" : r.tried[0] ?? "", /not found/);
});

test("a configured .dll runs through dotnet", () => {
  const dll = path.join(root, "out", "fsh.dll");
  const dotnet = path.join(bin, "dotnet");
  const r = resolveTool({ ...fantomas, setting: "${workspaceFolder}/out/fsh.dll" }, ctx({ [dll]: "", [dotnet]: "" }));
  assert.ok(r.found);
  assert.equal(r.command, dotnet);
  assert.equal(r.args.length, 1);
  assert.match(r.args[0] ?? "", /fsh\.dll$/);
});

test("a .dll without dotnet on PATH says so", () => {
  const r = resolveTool({ ...fantomas, setting: path.join(root, "fsh.dll") }, ctx({ [path.join(root, "fsh.dll")]: "" }));
  assert.equal(r.found, false);
  assert.match(r.found ? "" : r.tried[0] ?? "", /needs `dotnet`/);
});

test("the local tool manifest is used through dotnet, with the manifest's folder as cwd", () => {
  const dotnet = path.join(bin, "dotnet");
  const r = resolveTool(fantomas, ctx({ [path.join(root, ".config", "dotnet-tools.json")]: manifest("fantomas", "fable"), [dotnet]: "" }));
  assert.ok(r.found);
  assert.equal(r.source, "dotnet-tool");
  assert.equal(r.command, dotnet);
  assert.deepEqual(r.args, ["fantomas"]);
  assert.equal(r.cwd, root);
});

test("the nearest manifest above the file is the one that counts", () => {
  const nested = path.join(root, "sub", "deep");
  const fs = disk({ [path.join(root, ".config", "dotnet-tools.json")]: manifest("fantomas"), [path.join(root, "sub", ".config", "dotnet-tools.json")]: manifest("fable") });
  assert.equal(findManifest(nested, fs), path.join(root, "sub", ".config", "dotnet-tools.json"));
  const r = resolveTool(fantomas, ctx({ [path.join(root, ".config", "dotnet-tools.json")]: manifest("fantomas"), [path.join(root, "sub", ".config", "dotnet-tools.json")]: manifest("fable"), [path.join(bin, "dotnet")]: "" }, {}, { startDir: nested }));
  assert.equal(r.found, false, "the nearer manifest does not list fantomas, and dotnet would not find it either");
});

test("a manifest that lists the tool but no dotnet on PATH falls through to PATH, then reports both", () => {
  const exe = path.join(bin, "fantomas");
  const viaPath = resolveTool(fantomas, ctx({ [path.join(root, ".config", "dotnet-tools.json")]: manifest("fantomas"), [exe]: "" }));
  assert.ok(viaPath.found);
  assert.equal(viaPath.source, "path");
  const none = resolveTool(fantomas, ctx({ [path.join(root, ".config", "dotnet-tools.json")]: manifest("fantomas") }));
  assert.equal(none.found, false);
  assert.ok(none.found === false && none.tried.some((t) => /dotnet.*not on PATH/.test(t)));
});

test("PATH is searched in order and PATHEXT adds only .exe and .com, never .cmd or .bat", () => {
  const a = path.resolve("a");
  const b = path.resolve("b");
  const env = { PATH: [a, b].join(path.delimiter), PATHEXT: ".COM;.EXE;.BAT;.CMD" };
  assert.equal(findOnPath("tool", { env, fs: disk({ [path.join(b, "tool.exe")]: "" }) }), path.join(b, "tool.exe"));
  assert.equal(findOnPath("tool", { env, fs: disk({ [path.join(a, "tool.cmd")]: "", [path.join(a, "tool.bat")]: "" }) }), undefined);
  assert.equal(findOnPath("tool", { env: { PATH: a }, fs: disk({ [path.join(a, "tool")]: "" }) }), path.join(a, "tool"), "no PATHEXT: the bare name");
  assert.equal(findOnPath("tool", { env: { Path: a }, fs: disk({ [path.join(a, "tool")]: "" }) }), path.join(a, "tool"), "the variable name is matched case-insensitively");
});

test("nothing found lists everything that was tried", () => {
  const r = resolveTool(fantomas, ctx({}));
  assert.equal(r.found, false);
  assert.ok(r.found === false);
  assert.ok(r.tried.some((t) => t.includes("fsharpened.fantomas.path")));
  assert.ok(r.tried.some((t) => t.includes("dotnet-tools.json")));
  assert.ok(r.tried.some((t) => t.includes("`fantomas` on PATH")));
});

test("a tool with no manifest command skips the manifest", () => {
  const python: ToolSpec = { label: "Python", setting: "", settingName: "fsharpened.python.path", pathNames: ["python3", "python", "py"] };
  const py = path.join(bin, "python");
  const r = resolveTool(python, ctx({ [py]: "" }));
  assert.ok(r.found);
  assert.equal(r.command, py);
});

test("manifestCommands reads command names and survives garbage", () => {
  assert.deepEqual(manifestCommands(manifest("a", "b")), ["a", "b"]);
  assert.deepEqual(manifestCommands("{ not json"), []);
  assert.deepEqual(manifestCommands('{"tools":{"x":{"commands":[1,"y"]}}}'), ["y"]);
  assert.deepEqual(manifestCommands("{}"), []);
});
