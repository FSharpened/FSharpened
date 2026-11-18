// Builds the extension and packs it into ../artifacts/fsharpened-<version>.vsix with @vscode/vsce.
// Local only: this script has no publish path and refuses to run one. Publishing is a human decision (AGENTS.md).
import { spawnSync } from "node:child_process";
import { mkdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

if (process.argv.slice(2).some((a) => /publish|login|ovsx/i.test(a))) {
  console.error("package.mjs only packages. Never publish from here: a human publishes after review.");
  process.exit(2);
}

const root = fileURLToPath(new URL("../", import.meta.url));
const pkg = JSON.parse(readFileSync(root + "package.json", "utf8"));
const out = fileURLToPath(new URL("../../artifacts/", import.meta.url));
mkdirSync(out, { recursive: true });
const target = out + `fsharpened-${pkg.version}.vsix`;

const step = (cmd, args) => {
  const r = spawnSync(cmd, args, { cwd: root, stdio: "inherit" });
  if (r.status !== 0) {
    console.error(`${cmd} ${args.join(" ")} failed (${r.status ?? r.error})`);
    process.exit(r.status ?? 1);
  }
};

step(process.execPath, ["build.mjs"]);
// --no-dependencies: everything is bundled into dist/extension.js. --no-update-package-json and --no-git-tag-version:
// packing never edits the repository. vsce's `package` command makes no network request.
step(process.execPath, ["node_modules/@vscode/vsce/vsce", "package", "--no-dependencies", "--no-update-package-json", "--no-git-tag-version", "--out", target]);
console.log(`packed ${target}`);
