// Bundles src/extension.ts into dist/extension.js (esbuild, like ide/web/build.mjs), or with --tests the node tests into out-test/.
// No source maps (they would carry absolute build paths into the .vsix), and a THIRD-PARTY-NOTICES.txt for what is bundled.
import { build } from "esbuild";
import { mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("./", import.meta.url));
const tests = process.argv.includes("--tests");

if (tests) {
  rmSync(root + "out-test", { recursive: true, force: true });
  const entries = readdirSync(root + "test")
    .filter((f) => f.endsWith(".test.ts"))
    .map((f) => root + "test/" + f);
  await build({
    entryPoints: entries,
    outdir: root + "out-test",
    outExtension: { ".js": ".mjs" },
    bundle: true,
    platform: "node",
    format: "esm",
    target: "node20",
    sourcemap: false,
    // esm output cannot `require` the CommonJS code vscode-jsonrpc bundles; give it one.
    banner: { js: 'import { createRequire as __cr } from "node:module"; const require = __cr(import.meta.url);' },
    logLevel: "warning",
  });
} else {
  rmSync(root + "dist", { recursive: true, force: true });
  mkdirSync(root + "dist", { recursive: true });
  const result = await build({
    entryPoints: [root + "src/extension.ts"],
    outfile: root + "dist/extension.js",
    bundle: true,
    platform: "node",
    format: "cjs",
    target: "node20", // VS Code 1.90 ships Node 20
    external: ["vscode"],
    minify: true,
    sourcemap: false,
    legalComments: "none",
    metafile: true,
    logLevel: "info",
  });

  // List the packages that ended up in the bundle, with their licenses, so the .vsix carries the notices MIT/ISC require.
  const packages = new Map();
  for (const input of Object.keys(result.metafile.inputs)) {
    const m = /(?:^|\/)node_modules\/((?:@[^/]+\/)?[^/]+)\//.exec(input.replaceAll("\\", "/"));
    if (m && !packages.has(m[1])) {
      const pj = JSON.parse(readFileSync(root + "node_modules/" + m[1] + "/package.json", "utf8"));
      packages.set(m[1], { version: pj.version, license: pj.license ?? "see package", repository: typeof pj.repository === "string" ? pj.repository : pj.repository?.url ?? "" });
    }
  }
  const lines = ["FSharpened for VS Code bundles these packages (see their repositories for the full license texts):", ""];
  for (const [name, p] of [...packages].sort()) lines.push(`${name}@${p.version}  ${p.license}  ${p.repository}`);
  writeFileSync(root + "dist/THIRD-PARTY-NOTICES.txt", lines.join("\n") + "\n");
}
