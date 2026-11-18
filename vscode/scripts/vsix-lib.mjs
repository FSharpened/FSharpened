// Reading a .vsix (it is a ZIP) and checking what is in it. No dependencies: the central directory is parsed here, so the
// verifier does not depend on the tool that produced the file. Used by verify-vsix.mjs and by test/vsix.test.ts.
import { inflateRawSync } from "node:zlib";

/** @returns {{name: string, size: number, compressed: number, data: () => Buffer}[]} */
export function readZip(buf) {
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 22 - 65535); i--) {
    if (buf.readUInt32LE(i) === 0x06054b50) {
      eocd = i;
      break;
    }
  }
  if (eocd < 0) throw new Error("not a zip file: no end-of-central-directory record");
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  const entries = [];
  for (let n = 0; n < count; n++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error("corrupt central directory");
    const method = buf.readUInt16LE(p + 10);
    const compressed = buf.readUInt32LE(p + 20);
    const size = buf.readUInt32LE(p + 24);
    const nameLen = buf.readUInt16LE(p + 28);
    const extraLen = buf.readUInt16LE(p + 30);
    const commentLen = buf.readUInt16LE(p + 32);
    const local = buf.readUInt32LE(p + 42);
    const name = buf.toString("utf8", p + 46, p + 46 + nameLen);
    entries.push({
      name,
      size,
      compressed,
      data() {
        const lh = local + 30 + buf.readUInt16LE(local + 26) + buf.readUInt16LE(local + 28);
        const raw = buf.subarray(lh, lh + compressed);
        if (method === 0) return raw;
        if (method === 8) return inflateRawSync(raw);
        throw new Error(`unsupported compression method ${method} for ${name}`);
      },
    });
    p += 46 + nameLen + extraLen + commentLen;
  }
  return entries;
}

/** Files a clean extension package may contain. Anything else (sources, tests, node_modules, maps, dotfiles) is a failure. */
export const ALLOWED = [
  /^\[Content_Types\]\.xml$/,
  /^extension\.vsixmanifest$/,
  /^extension\/package\.json$/,
  /^extension\/readme\.md$/i,
  /^extension\/license(\.txt|\.md)?$/i,
  /^extension\/language-configuration\.json$/,
  /^extension\/images\/icon\.png$/,
  /^extension\/dist\/extension\.js$/,
  /^extension\/dist\/THIRD-PARTY-NOTICES\.txt$/,
];

export const REQUIRED = ["extension.vsixmanifest", "extension/package.json", "extension/dist/extension.js", "extension/images/icon.png", "extension/language-configuration.json"];

export const MAX_BYTES = 1024 * 1024;

const SECRETS = [
  [/-----BEGIN [A-Z ]*PRIVATE KEY-----/, "a private key"],
  [/\bAKIA[0-9A-Z]{16}\b/, "an AWS access key id"],
  [/\bghp_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{30,}\b|\bgho_[A-Za-z0-9]{30,}\b/, "a GitHub token"],
  [/\bxox[abprs]-[A-Za-z0-9-]{10,}/, "a Slack token"],
  [/\bsk-ant-[A-Za-z0-9_-]{20,}/, "an Anthropic key"],
  [/\bsk-[A-Za-z0-9]{32,}\b/, "an API key"],
  [/\bAIza[0-9A-Za-z_-]{35}\b/, "a Google API key"],
  [/\bnpm_[A-Za-z0-9]{36}\b/, "an npm token"],
  [/\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/, "a JSON web token"],
  [/\b(?:password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\b["']?\s*[:=]\s*["'][^"'\s]{12,}["']/i, "a credential assignment"],
];

/** Absolute build-machine paths that must not be baked into the package. `extra` adds this machine's own directories. */
export function pathPatterns(extra = []) {
  const escape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const own = extra.filter((e) => e && e.length > 3).flatMap((e) => [escape(e), escape(e.replaceAll("\\", "/")), escape(e.replaceAll("\\", "\\\\"))]);
  return [
    ...own.map((o) => [new RegExp(o, "i"), `this machine's path ${o}`]),
    [/\b[A-Za-z]:[\\/]+(?:Users|Projects|Documents and Settings)[\\/]/, "a Windows user or project path"],
    [/(?:^|[^A-Za-z0-9_.])\/(?:Users|home)\/[A-Za-z0-9_.-]+\//, "a home directory path"],
    [/\/(?:runner|work)\/(?:work\/)?[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/vscode\//, "a CI checkout path"],
  ];
}

/** Problems found in one text file, as strings. */
export function scanText(name, text, extraPaths = []) {
  const problems = [];
  for (const [re, what] of SECRETS) if (re.test(text)) problems.push(`${name}: looks like it contains ${what}`);
  for (const [re, what] of pathPatterns(extraPaths)) if (re.test(text)) problems.push(`${name}: contains ${what}`);
  if (/\/\/[#@]\s*sourceMappingURL=/.test(text)) problems.push(`${name}: references a source map`);
  return problems;
}

const TEXT = /\.(js|json|md|txt|xml|vsixmanifest)$|^LICENSE|\[Content_Types\]/i;

/**
 * @param {ReturnType<typeof readZip>} entries
 * @param {{ version: string, engine: string, extraPaths?: string[] }} expected
 * @returns {{ problems: string[], files: {name: string, size: number}[], total: number }}
 */
export function checkEntries(entries, totalBytes, expected) {
  const problems = [];
  const names = entries.map((e) => e.name);
  for (const e of entries) {
    if (e.name.endsWith("/")) continue;
    if (!ALLOWED.some((re) => re.test(e.name))) problems.push(`unexpected file in the package: ${e.name}`);
    if (/\.map$/i.test(e.name)) problems.push(`source map in the package: ${e.name}`);
  }
  for (const r of REQUIRED) if (!names.includes(r)) problems.push(`missing from the package: ${r}`);
  if (!names.some((n) => /^extension\/readme\.md$/i.test(n))) problems.push("missing from the package: extension/README.md");
  if (!names.some((n) => /^extension\/license(\.txt|\.md)?$/i.test(n))) problems.push("missing from the package: extension/LICENSE");
  if (totalBytes > MAX_BYTES) problems.push(`package is ${totalBytes} bytes; the limit is ${MAX_BYTES}`);

  for (const e of entries) {
    if (e.name.endsWith("/") || !TEXT.test(e.name)) continue;
    problems.push(...scanText(e.name, e.data().toString("utf8"), expected.extraPaths));
  }

  const pkgEntry = entries.find((e) => e.name === "extension/package.json");
  if (pkgEntry) {
    const pkg = JSON.parse(pkgEntry.data().toString("utf8"));
    if (pkg.version !== expected.version) problems.push(`package.json version ${pkg.version} is not ${expected.version}`);
    if (pkg.engines?.vscode !== expected.engine) problems.push(`package.json engine ${pkg.engines?.vscode} is not ${expected.engine}`);
    if (pkg.main !== "./dist/extension.js") problems.push(`package.json main is ${pkg.main}`);
    if (pkg.scripts && pkg.scripts["vscode:prepublish"]) problems.push("package.json has a vscode:prepublish script");
  }
  const manifest = entries.find((e) => e.name === "extension.vsixmanifest");
  if (manifest) {
    const m = manifest.data().toString("utf8");
    if (!/Publisher="fsharpened"/.test(m) || !new RegExp(`Version="${expected.version.replaceAll(".", "\\.")}"`).test(m)) problems.push("extension.vsixmanifest does not carry the expected publisher and version");
  }
  return { problems, files: entries.filter((e) => !e.name.endsWith("/")).map((e) => ({ name: e.name, size: e.size })), total: totalBytes };
}
