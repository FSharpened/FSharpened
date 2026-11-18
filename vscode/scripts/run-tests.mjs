// Runs the bundled tests in out-test/ with node's own runner. Lists the files itself so it works on Node 20 and 22, on any shell.
import { readdirSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const dir = fileURLToPath(new URL("../out-test/", import.meta.url));
const files = readdirSync(dir)
  .filter((f) => f.endsWith(".test.mjs"))
  .map((f) => dir + f);
if (files.length === 0) {
  console.error("no tests found in out-test/ (run `node build.mjs --tests` first)");
  process.exit(1);
}
const r = spawnSync(process.execPath, ["--test", ...files], { stdio: "inherit" });
process.exit(r.status ?? 1);
