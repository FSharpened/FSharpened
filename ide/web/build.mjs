// Bundles the Fable output + CodeMirror into the static site (site/public/ide). Run `npm run fable` first.
import { build } from "esbuild";
import { mkdirSync } from "node:fs";

const gateway = process.env.IDE_GATEWAY ?? "https://ide.fsharpened.workers.dev";
const out = new URL("../../site/public/ide/", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
mkdirSync(out, { recursive: true });

await build({
  entryPoints: ["src/main.js"],
  bundle: true,
  minify: true,
  sourcemap: false,
  target: ["es2022"],
  format: "esm",
  outfile: out + "/ide.js",
  define: { __GATEWAY__: JSON.stringify(gateway) },
  legalComments: "none",
  logLevel: "info",
});
