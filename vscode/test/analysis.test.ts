import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { analyzeArgs, analyzeFile } from "../src/analysis.ts";
import type { Runner, RunResult } from "../src/proc.ts";
import type { Resolved } from "../src/resolve.ts";

const golden = readFileSync(fileURLToPath(new URL("../test/fixtures/analyze-sample.golden.json", import.meta.url)), "utf8");
const fsh: Resolved = { found: true, command: "dotnet", args: ["fsh"], source: "dotnet-tool", detail: "x", cwd: "/repo" };

const result = (r: Partial<RunResult>): RunResult => ({ code: 0, stdout: "", stderr: "", timedOut: false, truncated: false, ...r });
const runnerOf = (r: Partial<RunResult>): { runner: Runner; calls: Array<{ command: string; args: string[]; cwd?: string }> } => {
  const calls: Array<{ command: string; args: string[]; cwd?: string }> = [];
  return {
    calls,
    runner: async (command, args, opts) => {
      calls.push({ command, args, cwd: opts.cwd });
      return result(r);
    },
  };
};

test("the command line is fixed: the tool's own arguments, then analyze --format json and the file", () => {
  assert.deepEqual(analyzeArgs(fsh, "/repo/a.fs"), ["fsh", "analyze", "--format", "json", "/repo/a.fs"]);
});

test("a good run returns the parsed findings and runs in the tool's folder", async () => {
  const { runner, calls } = runnerOf({ stdout: golden });
  const r = await analyzeFile(fsh, "/repo/Sample.fs", { cwd: "/elsewhere", timeoutMs: 1000, runner });
  assert.ok(r.ok);
  assert.equal(r.findings.length, 2);
  assert.equal(calls[0]?.cwd, "/repo");
});

test("a file argument that looks like an option stays one argument", async () => {
  const { runner, calls } = runnerOf({ stdout: golden });
  await analyzeFile({ ...fsh, cwd: undefined }, "--format", { cwd: "/w", timeoutMs: 1000, runner });
  assert.deepEqual(calls[0]?.args.slice(-1), ["--format"]);
  assert.equal(calls[0]?.cwd, "/w");
});

test("every way the run can fail becomes a message, never a throw", async () => {
  const cases: Array<[Partial<RunResult>, RegExp]> = [
    [{ spawnError: "spawn ENOENT", code: null }, /could not start fsh: spawn ENOENT/],
    [{ timedOut: true, code: null }, /timed out/],
    [{ code: 2, stderr: "fsh analyze: not found: x" }, /exited with 2: fsh analyze: not found/],
    [{ truncated: true, stdout: golden }, /output cap/],
    [{ stdout: "<html>" }, /not JSON/],
    [{ stdout: '{"schema":"fsharpened.diagnostics/9","diagnostics":[]}' }, /not supported/],
  ];
  for (const [partial, expected] of cases) {
    const r = await analyzeFile(fsh, "a.fs", { cwd: ".", timeoutMs: 1000, runner: runnerOf(partial).runner });
    assert.equal(r.ok, false);
    assert.match(r.ok ? "" : r.error, expected);
  }
});
