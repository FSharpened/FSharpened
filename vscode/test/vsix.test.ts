// The .vsix verifier, tested on synthetic packages (a tiny ZIP writer below) so it is known to fail when it should.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { deflateRawSync } from "node:zlib";
import { randomBytes } from "node:crypto";
import { checkEntries, readZip, scanText } from "../scripts/vsix-lib.mjs";

function crc32(buf: Buffer): number {
  let c = ~0;
  for (const b of buf) {
    c ^= b;
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1));
  }
  return ~c >>> 0;
}

/** A minimal ZIP: each file stored or deflated, with a central directory. */
function zip(files: Record<string, string>, deflate = false): Buffer {
  const locals: Buffer[] = [];
  const central: Buffer[] = [];
  let offset = 0;
  for (const [name, text] of Object.entries(files)) {
    const data = Buffer.from(text, "utf8");
    const body = deflate ? deflateRawSync(data) : data;
    const nameBuf = Buffer.from(name, "utf8");
    const lh = Buffer.alloc(30);
    lh.writeUInt32LE(0x04034b50, 0);
    lh.writeUInt16LE(20, 4);
    lh.writeUInt16LE(deflate ? 8 : 0, 8);
    lh.writeUInt32LE(crc32(data), 14);
    lh.writeUInt32LE(body.length, 18);
    lh.writeUInt32LE(data.length, 22);
    lh.writeUInt16LE(nameBuf.length, 26);
    const local = Buffer.concat([lh, nameBuf, body]);
    const ch = Buffer.alloc(46);
    ch.writeUInt32LE(0x02014b50, 0);
    ch.writeUInt16LE(20, 4);
    ch.writeUInt16LE(20, 6);
    ch.writeUInt16LE(deflate ? 8 : 0, 10);
    ch.writeUInt32LE(crc32(data), 16);
    ch.writeUInt32LE(body.length, 20);
    ch.writeUInt32LE(data.length, 24);
    ch.writeUInt16LE(nameBuf.length, 28);
    ch.writeUInt32LE(offset, 42);
    central.push(Buffer.concat([ch, nameBuf]));
    locals.push(local);
    offset += local.length;
  }
  const dir = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(central.length, 8);
  end.writeUInt16LE(central.length, 10);
  end.writeUInt32LE(dir.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, dir, end]);
}

const good = (): Record<string, string> => ({
  "[Content_Types].xml": "<Types/>",
  "extension.vsixmanifest": '<PackageManifest><Identity Publisher="fsharpened" Version="1.2.3"/></PackageManifest>',
  "extension/package.json": JSON.stringify({ version: "1.2.3", engines: { vscode: "^1.90.0" }, main: "./dist/extension.js" }),
  "extension/readme.md": "# hi",
  "extension/LICENSE.txt": "Apache",
  "extension/language-configuration.json": "{}",
  "extension/images/icon.png": "png",
  "extension/dist/extension.js": "console.log(1)",
  "extension/dist/THIRD-PARTY-NOTICES.txt": "notices",
});
const expected = { version: "1.2.3", engine: "^1.90.0", extraPaths: ["/build/machine/repo"] };
const check = (files: Record<string, string>, deflate = false): string[] => {
  const buf = zip(files, deflate);
  return checkEntries(readZip(buf), buf.length, expected).problems;
};

test("a clean package passes, stored or deflated", () => {
  assert.deepEqual(check(good()), []);
  assert.deepEqual(check(good(), true), []);
});

test("the zip reader returns the bytes that went in", () => {
  const entries = readZip(zip({ "a.txt": "hello", "b/c.txt": "x".repeat(5000) }, true));
  assert.deepEqual(entries.map((e) => e.name), ["a.txt", "b/c.txt"]);
  assert.equal(entries[1]?.data().toString(), "x".repeat(5000));
  assert.throws(() => readZip(Buffer.from("not a zip at all, certainly not one")), /not a zip/);
});

test("sources, tests, node_modules, source maps and dotfiles are refused", () => {
  for (const extra of ["extension/src/extension.ts", "extension/test/a.test.ts", "extension/node_modules/x/index.js", "extension/dist/extension.js.map", "extension/.env", "extension/out-test/a.mjs"]) {
    const problems = check({ ...good(), [extra]: "x" });
    assert.ok(problems.some((p) => p.includes(extra)), `${extra}: ${problems.join("|")}`);
  }
});

test("a missing required file is reported", () => {
  const { "extension/dist/extension.js": _gone, ...rest } = good();
  assert.ok(check(rest).some((p) => p.includes("missing from the package: extension/dist/extension.js")));
  const { "extension/LICENSE.txt": _l, ...noLicense } = good();
  assert.ok(check(noLicense).some((p) => /LICENSE/.test(p)));
});

test("secrets in any text file are found", () => {
  const github = "ghp_" + "a1B2c3D4e5".repeat(4);
  const aws = "AKIA" + "ABCDEFGHIJKLMNOP";
  const pem = "-----BEGIN RSA " + "PRIVATE KEY-----";
  for (const secret of [github, aws, pem, 'const apiKey = "abcdefghijklmnop1234"', "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnopqrstuvwx"]) {
    const problems = check({ ...good(), "extension/dist/extension.js": `var x = '${secret}';` });
    assert.ok(problems.some((p) => /looks like it contains/.test(p)), secret);
  }
  assert.deepEqual(scanText("a.js", "const token = cancellationToken; // password handling docs"), [], "words are not secrets");
});

test("absolute build paths and source map comments are found", () => {
  assert.ok(check({ ...good(), "extension/dist/extension.js": 'var p="/build/machine/repo/vscode/src/x.ts"' }).some((p) => /this machine's path/.test(p)));
  assert.ok(check({ ...good(), "extension/dist/extension.js": 'var p="C:\\\\Users\\\\someone\\\\proj"' }).some((p) => /Windows user or project path/.test(p)));
  assert.ok(check({ ...good(), "extension/dist/extension.js": "x\n//# sourceMappingURL=extension.js.map" }).some((p) => /source map/.test(p)));
  assert.ok(check({ ...good(), "extension/dist/extension.js": 'var p="/home/runner/work/FSharpened/FSharpened/vscode/src/x.ts"' }).length > 0);
});

test("a package over the size limit, or with the wrong version or engine, is refused", () => {
  const big = zip({ ...good(), "extension/dist/extension.js": "a".repeat(2 * 1024 * 1024) }, true);
  const noisy = zip({ ...good(), "extension/dist/extension.js": randomBytes(1_200_000).toString("base64") }, true);
  assert.ok(checkEntries(readZip(noisy), noisy.length, expected).problems.some((p) => /limit/.test(p)));
  assert.ok(checkEntries(readZip(big), big.length, expected).problems.every((p) => !/limit/.test(p)), "highly compressible content is measured by the package size");
  assert.ok(check({ ...good(), "extension/package.json": JSON.stringify({ version: "9.9.9", engines: { vscode: "^1.90.0" }, main: "./dist/extension.js" }) }).some((p) => /version 9\.9\.9/.test(p)));
  assert.ok(check({ ...good(), "extension/package.json": JSON.stringify({ version: "1.2.3", engines: { vscode: "^1.0.0" }, main: "./dist/extension.js" }) }).some((p) => /engine/.test(p)));
  assert.ok(check({ ...good(), "extension.vsixmanifest": '<PackageManifest><Identity Publisher="someone-else" Version="1.2.3"/></PackageManifest>' }).some((p) => /publisher/.test(p)));
});

test("the licence in the package is the repository's", () => {
  const here = (p: string): string => fileURLToPath(new URL(`../${p}`, import.meta.url));
  assert.ok(existsSync(here("LICENSE")));
  assert.equal(readFileSync(here("LICENSE"), "utf8"), readFileSync(here("../LICENSE"), "utf8"));
});
