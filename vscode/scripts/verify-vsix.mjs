// Verifies the .vsix package.mjs produced: exact file list, size, no secrets, no source maps, no absolute build paths.
// Usage: node scripts/verify-vsix.mjs [path-to.vsix]   (default: ../artifacts/fsharpened-<version>.vsix)
import { readFileSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { fileURLToPath } from "node:url";
import { checkEntries, readZip } from "./vsix-lib.mjs";

const root = fileURLToPath(new URL("../", import.meta.url));
const pkg = JSON.parse(readFileSync(root + "package.json", "utf8"));
const file = process.argv[2] ?? fileURLToPath(new URL(`../../artifacts/fsharpened-${pkg.version}.vsix`, import.meta.url));

let buf;
try {
  buf = readFileSync(file);
} catch {
  console.error(`verify: ${file} does not exist (run \`npm run package\` first)`);
  process.exit(1);
}

const entries = readZip(buf);
const total = statSync(file).size;
// This machine's own directories must not appear in the package: the repository root and the home folder.
const repoRoot = fileURLToPath(new URL("../../", import.meta.url)).replace(/[\\/]$/, "");
const result = checkEntries(entries, total, { version: pkg.version, engine: pkg.engines.vscode, extraPaths: [repoRoot, homedir()] });

for (const f of result.files) console.log(`  ${String(f.size).padStart(8)}  ${f.name}`);
console.log(`${result.files.length} files, ${total} bytes (${file})`);
if (result.problems.length > 0) {
  for (const p of result.problems) console.error(`FAIL ${p}`);
  process.exit(1);
}
console.log("verify: OK (file list, size, no secrets, no source maps, no absolute paths)");
