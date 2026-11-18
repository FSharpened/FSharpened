import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { parseReport } from "../src/report.ts";

const fixture = (name: string): string => readFileSync(fileURLToPath(new URL(`../test/fixtures/${name}`, import.meta.url)), "utf8");

const wrap = (diag: object): string => JSON.stringify({ schema: "fsharpened.diagnostics/1", tool: "t", diagnostics: [diag] });
const valid = {
  code: "X1",
  severity: "warning",
  file: "a.fs",
  range: { startLine: 3, startColumn: 4, endLine: 3, endColumn: 9 },
  message: "m",
  construct: null,
  docs: "https://example.test/x",
  fix: null,
  context: {},
};

test("the golden report written by the .NET side parses, with zero-based lines", () => {
  const r = parseReport(fixture("analyze-sample.golden.json"));
  assert.ok(r.ok, r.ok ? "" : r.error);
  assert.equal(r.findings.length, 2);
  const [printf, platform] = r.findings;
  assert.equal(printf?.code, "FSH0002");
  assert.equal(printf?.severity, "info");
  assert.deepEqual(printf?.range, { startLine: 3, startColumn: 4, endLine: 3, endColumn: 11 });
  assert.equal(printf?.fix?.edits[0]?.newText, "System.Console.Out.WriteLine");
  assert.match(printf?.docs ?? "", /^https:\/\/github\.com\/.*#fsh0002$/);
  assert.equal(platform?.code, "FSH0001");
  assert.equal(platform?.severity, "warning");
  assert.deepEqual(platform?.fix?.edits, []);
  assert.equal(platform?.construct, "#if WINDOWS");
});

test("an empty report is valid", () => {
  const r = parseReport('{"schema":"fsharpened.diagnostics/1","tool":"t","diagnostics":[]}\n');
  assert.ok(r.ok);
  assert.deepEqual(r.findings, []);
});

test("a byte order mark is tolerated", () => {
  assert.ok(parseReport("﻿" + '{"schema":"fsharpened.diagnostics/1","diagnostics":[]}').ok);
});

test("output that is not JSON is rejected with a reason", () => {
  const r = parseReport("Unhandled exception. System.IO.IOException");
  assert.equal(r.ok, false);
  assert.match(r.ok ? "" : r.error, /not JSON/);
});

test("another schema or another major version is rejected", () => {
  assert.equal(parseReport('{"schema":"other/1","diagnostics":[]}').ok, false);
  const r = parseReport('{"schema":"fsharpened.diagnostics/2","diagnostics":[]}');
  assert.equal(r.ok, false);
  assert.match(r.ok ? "" : r.error, /not supported/);
  assert.ok(parseReport('{"schema":"fsharpened.diagnostics/1.4","diagnostics":[]}').ok, "a minor bump stays readable");
});

test("an unknown severity, a missing field or a bad range rejects the whole report", () => {
  assert.equal(parseReport(wrap({ ...valid, severity: "fatal" })).ok, false);
  const { message: _m, ...noMessage } = valid;
  assert.equal(parseReport(wrap(noMessage)).ok, false);
  assert.equal(parseReport(wrap({ ...valid, range: { startLine: 0, startColumn: 0, endLine: 1, endColumn: 0 } })).ok, false, "lines are 1-based");
  assert.equal(parseReport(wrap({ ...valid, range: { startLine: 5, startColumn: 0, endLine: 4, endColumn: 0 } })).ok, false, "ends before it starts");
  assert.equal(parseReport(wrap({ ...valid, range: { startLine: 1.5, startColumn: 0, endLine: 2, endColumn: 0 } })).ok, false);
});

test("only http and https documentation links survive", () => {
  for (const bad of ["javascript:alert(1)", "file:///etc/passwd", "command:workbench.action.reloadWindow", "not a url"]) {
    const r = parseReport(wrap({ ...valid, docs: bad }));
    assert.ok(r.ok);
    assert.equal(r.findings[0]?.docs, null, bad);
  }
  const ok = parseReport(wrap({ ...valid, docs: "http://example.test/a" }));
  assert.ok(ok.ok);
  assert.equal(ok.findings[0]?.docs, "http://example.test/a");
});

test("a diagnostic with no file or range is allowed", () => {
  const r = parseReport(wrap({ ...valid, file: null, range: null }));
  assert.ok(r.ok);
  assert.equal(r.findings[0]?.range, null);
});

test("a report larger than the cap is rejected before it is parsed", () => {
  const r = parseReport(" ".repeat(9 * 1024 * 1024));
  assert.equal(r.ok, false);
  assert.match(r.ok ? "" : r.error, /larger than/);
});
