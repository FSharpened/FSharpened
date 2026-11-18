import { test } from "node:test";
import assert from "node:assert/strict";
import * as path from "node:path";
import { asProfile, ciArgs, latestReport, type DirFs } from "../src/ci.ts";
import { formatDoctor, HINTS } from "../src/doctor.ts";
import { describeStatus } from "../src/statusModel.ts";

test("the CI command is the fast profile of the repo's runner, with nothing spliced in", () => {
  assert.deepEqual(ciArgs("fast"), [path.join("tools", "ci", "fsh_ci.py"), "run", "--profile", "fast"]);
  assert.equal(asProfile("all"), "all");
  assert.equal(asProfile("fast; rm -rf /"), "fast");
  assert.equal(asProfile(""), "fast");
});

test("the latest CI report is the newest run folder that has one", () => {
  const root = path.resolve("repo");
  const runs = path.join(root, ".fsharpened", "ci", "runs");
  const files = new Set([path.join(runs, "20261117-100000", "report.md"), path.join(runs, "20261116-090000", "report.md")]);
  const fs: DirFs = {
    list: (d) => (d === runs ? ["20261116-090000", "20261117-100000", "20261118-000000"] : undefined),
    isFile: (p) => files.has(p),
  };
  assert.equal(latestReport(root, fs), path.join(runs, "20261117-100000", "report.md"), "the newest folder has no report yet, so the one before it");
  assert.equal(latestReport(root, { list: () => undefined, isFile: () => false }), undefined);
});

const found = { found: true as const, command: "c", args: [], source: "path" as const, detail: "PATH (/x/fantomas)" };

test("the Doctor report says what was found, what is missing, what was tried and how to fix it", () => {
  const text = formatDoctor({
    extensionVersion: "0.0.1",
    trusted: true,
    workspaceRoot: "/w",
    languageServer: "running",
    tools: [
      { label: "Fantomas (formatter)", resolution: found, version: "7.0.0", hint: HINTS.fantomas },
      { label: "fsh", resolution: { found: false, tried: ["fsharpened.fsh.path (not set)", "`fsh` on PATH"] }, hint: HINTS.fsh },
    ],
    fshDoctor: "rid: win-x64\naot: False\n",
  });
  assert.match(text, /\[ok\]\s+Fantomas \(formatter\): PATH \(\/x\/fantomas\), 7\.0\.0/);
  assert.match(text, /\[missing\] fsh\n\s+tried: fsharpened\.fsh\.path \(not set\)\n\s+tried: `fsh` on PATH\n\s+fix:\s+build tools\/FSharpened\.Cli/);
  assert.match(text, /fsh doctor:\n  rid: win-x64\n  aot: False/);
  assert.match(text, /No network calls and no telemetry/);
});

test("the Doctor report is loud about an untrusted workspace", () => {
  const text = formatDoctor({ extensionVersion: "0.0.1", trusted: false, workspaceRoot: undefined, languageServer: "restricted", tools: [] });
  assert.match(text, /trusted:\s+NO: no tool is started/);
  assert.match(text, /\(no folder open\)/);
});

test("the status bar item says restricted, missing, failed, and counts findings", () => {
  const base = { trusted: true, server: "running" as const, findings: undefined, fshMissing: false, busy: false };
  assert.equal(describeStatus({ ...base, trusted: false }).text, "$(shield) FSharpened: restricted");
  assert.equal(describeStatus(base).text, "$(check) FSharpened");
  assert.equal(describeStatus({ ...base, findings: 0 }).text, "$(check) FSharpened: no findings");
  assert.equal(describeStatus({ ...base, findings: 1 }).text, "$(check) FSharpened: 1 finding");
  assert.equal(describeStatus({ ...base, findings: 3 }).text, "$(check) FSharpened: 3 findings");
  const missing = describeStatus({ ...base, server: "missing" });
  assert.equal(missing.level, "warning");
  assert.match(missing.text, /no language server/);
  assert.equal(describeStatus({ ...base, server: "error" }).level, "error");
  assert.match(describeStatus({ ...base, fshMissing: true }).text, /no fsh/);
  assert.match(describeStatus({ ...base, busy: true }).text, /sync~spin/);
  assert.match(describeStatus(base).tooltip, /Click for the Doctor report/);
});
