// The ZIP codec and the project import/export glue. The archive tests need no build; the project tests use the compiled
// F# core (`npm run fable`) and are skipped, with the reason, when it is missing.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { buildZip, readZip, decodeText, encodeText, crc32 } from "../src/archive.js";
import { exportProjectZip, exportFolderZip, importArchive, importFiles, manifestText, parseManifest, useManifestName } from "../src/projectio.js";

const entry = new URL("../fable_out/Api.js", import.meta.url);
const built = existsSync(entry);
const Core = built ? await import(entry.href) : null;
const skip = built ? false : "Fable output missing: run `npm run fable` in ide/web first";
if (Core) useManifestName(Core.manifestName);

const fixed = new Date(2026, 10, 16, 12, 30, 10);
const file = (path, text) => ({ path, data: encodeText(text) });

test("crc32 matches the published check value", () => {
  assert.equal(crc32(encodeText("123456789")), 0xcbf43926);
});

test("a stored archive round-trips, including non-ASCII names and text", async () => {
  const zip = buildZip([file("a/b.py", "print('héllo ✓')\n"), file("ünï.txt", "x"), file("empty.txt", "")], fixed);
  const { files, skipped } = await readZip(zip);
  assert.deepEqual(skipped, []);
  assert.deepEqual(files.map((f) => f.path), ["a/b.py", "ünï.txt", "empty.txt"]);
  assert.equal(decodeText(files[0].data), "print('héllo ✓')\n");
  assert.equal(files[2].data.length, 0);
});

test("an archive made by another tool, with deflated entries, reads correctly", async () => {
  const { files, skipped } = await readZip(readFileSync(new URL("./fixtures/python-deflate.zip", import.meta.url)));
  assert.deepEqual(skipped, []);
  const names = files.map((f) => f.path).sort();
  assert.deepEqual(names, ["../evil.py", "demo/fsharpened.project.json", "demo/logo.bin", "demo/main.py", "demo/src/lib.rs"]); // the folder entry is not a file
  assert.equal(decodeText(files.find((f) => f.path === "demo/main.py").data), "print('hello')\n".repeat(40));
});

test("things that are not archives, or are damaged, are rejected or skipped, never crash", async () => {
  await assert.rejects(readZip(encodeText("not a zip at all, just text")), /not a ZIP/);
  const good = buildZip([file("a.py", "print(1)")], fixed);
  const flipped = good.slice();
  flipped[30 + 4] ^= 0xff; // a byte of the stored data
  const r = await readZip(flipped);
  assert.deepEqual(r.files, []);
  assert.match(r.skipped[0].reason, /checksum/);
  const cut = good.slice(0, good.length - 30);
  await assert.rejects(readZip(cut), /not a ZIP|damaged/);
});

test("encryption, odd compression methods and implausible sizes are skipped with a reason", async () => {
  const good = buildZip([file("a.py", "print(1)"), file("b.py", "print(2)"), file("c.py", "print(3)")], fixed);
  const view = new DataView(good.buffer);
  const centralAt = view.getUint32(good.length - 22 + 16, true);
  let p = centralAt;
  view.setUint16(p + 8, view.getUint16(p + 8, true) | 1, true); // a.py: encrypted flag
  p += 46 + 4;
  view.setUint16(p + 10, 99, true); // b.py: unknown method
  p += 46 + 4;
  view.setUint32(p + 24, 4 * 1024 * 1024, true); // c.py: claims 4 MB from 8 bytes
  const { files, skipped } = await readZip(good);
  assert.deepEqual(files, []);
  assert.deepEqual(skipped.map((s) => s.reason.split(" ")[0]), ["encrypted", "unsupported", "compression"]);
});

test("a declared entry count over the limit is refused outright", async () => {
  const good = buildZip([file("a.py", "1")], fixed);
  new DataView(good.buffer).setUint16(good.length - 22 + 10, 5000, true);
  await assert.rejects(readZip(good), /limit/);
});

test("text decoding refuses binary data and strips a byte-order mark", () => {
  assert.equal(decodeText(new Uint8Array([0x68, 0x00, 0x69])), null);
  assert.equal(decodeText(new Uint8Array([0xff, 0xfe, 0xfd])), null);
  assert.equal(decodeText(new Uint8Array([0xef, 0xbb, 0xbf, 0x68, 0x69])), "hi");
});

test("the manifest reader is tolerant", () => {
  assert.deepEqual(parseManifest("{ nope"), {});
  assert.deepEqual(parseManifest('{"entry": 5, "pipeline": "x"}'), { name: undefined, entry: undefined, pipeline: [] });
  assert.deepEqual(parseManifest(manifestText({ name: "N", entry: "a.py", pipeline: ["a.py"] })), { name: "N", entry: "a.py", pipeline: ["a.py"] });
});

const sample = () => {
  let r = Core.projectAddFile(Core.projectCreate("Chess demo"), "src/engine.rs", "fn main() {}");
  r = Core.projectAddFile(r.project, "main.py", "print(1)");
  r = Core.projectAddFile(r.project, "src/ui/board.js", "console.log(1)");
  r = Core.projectAddFile(r.project, "README.md", "# hi");
  r = Core.projectSetPipeline(r.project, ["main.py", "src/engine.rs"]);
  r = Core.projectSetEntry(r.project, "src/engine.rs");
  assert.ok(r.ok, r.error);
  return r.project;
};

test("a project exported to a zip imports back identical", { skip }, async () => {
  const project = sample();
  const { name, bytes } = exportProjectZip(project, fixed);
  assert.equal(name, "Chess-demo.zip");
  const back = await importArchive(Core, bytes, "fallback");
  assert.deepEqual(back.skipped, []);
  assert.equal(back.project.name, "Chess demo");
  assert.deepEqual(back.project.files, project.files);
  assert.equal(back.project.entry, "src/engine.rs");
  assert.deepEqual(back.project.pipeline, ["main.py", "src/engine.rs"]);
});

test("a folder exports with paths relative to it", { skip }, async () => {
  const { name, bytes, count } = exportFolderZip(sample(), "src", fixed);
  assert.equal(name, "src.zip");
  assert.equal(count, 2);
  const { files } = await readZip(bytes);
  assert.deepEqual(files.map((f) => f.path).sort(), ["src/engine.rs", "src/ui/board.js"]);
});

test("importing an archive from another tool keeps text, reports binaries and escapes, and applies its manifest", { skip }, async () => {
  const bytes = readFileSync(new URL("./fixtures/python-deflate.zip", import.meta.url));
  const { project, skipped } = await importArchive(Core, bytes, "fallback");
  assert.equal(project.name, "Demo");
  assert.deepEqual(project.files.map((f) => f.path), ["main.py", "src/lib.rs"]);
  assert.equal(project.entry, "src/lib.rs");
  assert.deepEqual(project.pipeline, ["main.py", "src/lib.rs"]);
  const why = Object.fromEntries(skipped.map((s) => [s.path, s.reason]));
  assert.match(why["demo/logo.bin"], /binary/);
  assert.ok("../evil.py" in why, "a path that escapes the project is reported");
});

test("loose files import through the same rules", { skip }, () => {
  const { project, skipped } = importFiles(Core, [file("a.py", "1"), file("sub\\b.rs", "2"), { path: "x.bin", data: new Uint8Array([0, 1]) }], "Dropped");
  assert.equal(project.name, "Dropped");
  assert.deepEqual(project.files.map((f) => f.path), ["a.py", "sub/b.rs"]);
  assert.equal(skipped.length, 1);
});

test("the project API refuses what the model refuses", { skip }, () => {
  const p = Core.projectCreate("x");
  assert.equal(Core.projectAddFile(p, "../x.py", "").ok, false);
  assert.equal(Core.projectAddFile(p, "con.py", "").ok, false);
  const a = Core.projectAddFile(p, "a.py", "1");
  assert.equal(Core.projectAddFile(a.project, "A.PY", "2").ok, false);
  assert.equal(Core.pathLanguage("x/y.rs"), "rust");
  assert.equal(Core.pathLanguage("README.md"), "");
  assert.deepEqual(Core.projectListing(a.project).map((r) => r.path), ["a.py"]);
  assert.equal(Core.projectEntry(a.project), "a.py");
  assert.deepEqual(Core.projectLanguages(a.project), ["python"]);
});

test("buffers from before projects carry over", { skip }, () => {
  const project = Core.projectFromBuffers("Mine", "python", { python: "print('kept')", rust: "fn main() {}" });
  assert.deepEqual(project.files.map((f) => f.path), ["main.py", "main.rs"]);
  assert.equal(project.entry, "main.py");
});
