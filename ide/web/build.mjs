// Bundles the Fable output + CodeMirror into site/dist/ide.js. tools/site/build.py fingerprints it into the site.
// Run `npm run fable` first (or `npm run build`, which does both).
import { build } from "esbuild";
import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";

const gateway = process.env.IDE_GATEWAY ?? "https://ide.fsharpened.workers.dev";
const out = fileURLToPath(new URL("../../site/dist/", import.meta.url));
mkdirSync(out, { recursive: true });

await build({
  entryPoints: ["src/main.js"],
  bundle: true,
  minify: true,
  sourcemap: false,
  // Syntax floor: browsers from roughly 2020 (ES modules, optional chaining, BigInt for the Fable core).
  target: ["es2020", "chrome87", "edge88", "firefox78", "safari14"],
  format: "esm",
  outfile: out + "ide.js",
  define: { __GATEWAY__: JSON.stringify(gateway) },
  legalComments: "none",
  logLevel: "info",
});
