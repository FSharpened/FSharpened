// Tries the packaged .vsix in a real VS Code, in a throwaway user-data dir and extensions dir (your own VS Code settings
// and extensions are untouched): installs it, opens a workspace with an .fs file, and waits for the extension's own log
// lines. The language server is a FAKE (scripts/fake-lsp.mjs) unless FSH_SMOKE_REAL_LSP names a real FsAutoComplete;
// fsh is the repository's own build if it exists. Not part of CI (needs a desktop VS Code); run it by hand:
//   npm run package && node scripts/smoke-vscode.mjs
// Environment: FSH_CODE = path to the `code` launcher (default: `code` on PATH), FSH_SMOKE_REAL_LSP = fsautocomplete path,
// FSH_SMOKE_KEEP=1 keeps the temporary folders.
import { spawn, spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

const here = fileURLToPath(new URL("../", import.meta.url));
const repo = path.resolve(here, "..");
const pkg = JSON.parse(readFileSync(path.join(here, "package.json"), "utf8"));
const vsix = path.join(repo, "artifacts", `fsharpened-${pkg.version}.vsix`);
const win = process.platform === "win32";

const fail = (m) => {
  console.error(`smoke: FAIL ${m}`);
  process.exit(1);
};

function findOnPath(name) {
  for (const dir of (process.env.PATH ?? "").split(path.delimiter)) {
    for (const ext of win ? [".cmd", ".exe", ""] : [""]) {
      const p = path.join(dir, name + ext);
      if (dir && existsSync(p)) return p;
    }
  }
  return undefined;
}

const code = process.env.FSH_CODE ?? findOnPath("code");
if (!code) {
  console.log("smoke: SKIPPED, VS Code (`code`) was not found; set FSH_CODE to its launcher. The extension was NOT run in a real VS Code.");
  process.exit(0);
}
if (!existsSync(vsix)) fail(`${vsix} does not exist; run \`npm run package\` first`);

const root = mkdtempSync(path.join(tmpdir(), "fsh-smoke-"));
const userData = path.join(root, "user-data");
const extDir = path.join(root, "extensions");
const ws = path.join(root, "workspace");
const lspLog = path.join(root, "fake-lsp.log");
mkdirSync(path.join(userData, "User"), { recursive: true });
mkdirSync(path.join(ws, ".vscode"), { recursive: true });

// Keep VS Code itself quiet: no telemetry, updates, welcome page or extension update checks in the throwaway profile.
writeFileSync(path.join(userData, "User", "settings.json"), JSON.stringify({ "telemetry.telemetryLevel": "off", "update.mode": "none", "workbench.startupEditor": "none", "extensions.autoUpdate": false, "extensions.autoCheckUpdates": false, "workbench.enableExperiments": false }));
cpSync(path.join(here, "test", "fixtures", "Sample.fs"), path.join(ws, "Sample.fs"));

const fshDll = path.join(repo, "tools", "FSharpened.Cli", "bin", "Release", "net8.0", "fsh.dll");
const realLsp = process.env.FSH_SMOKE_REAL_LSP;
const settings = realLsp
  ? { "fsharpened.languageServer.path": realLsp }
  : { "fsharpened.languageServer.path": process.execPath, "fsharpened.languageServer.args": [path.join(here, "scripts", "fake-lsp.mjs")] };
if (existsSync(fshDll)) settings["fsharpened.fsh.path"] = fshDll;
writeFileSync(path.join(ws, ".vscode", "settings.json"), JSON.stringify(settings, null, 2));

const env = { ...process.env, FAKE_LSP_LOG: lspLog };
delete env.ELECTRON_RUN_AS_NODE; // it would make VS Code start as plain Node

const q = (s) => `"${s}"`;
const cli = (args) => spawnSync(win ? q(code) : code, args.map((a) => (win ? q(a) : a)), { env, encoding: "utf8", shell: win, timeout: 120000 });

console.log(`smoke: installing ${path.basename(vsix)} into a throwaway extensions dir (${extDir})`);
const install = cli(["--user-data-dir", userData, "--extensions-dir", extDir, "--install-extension", vsix, "--force"]);
console.log((install.stdout ?? "").trim());
if (install.status !== 0) fail(`install failed: ${install.stderr}`);
const listed = cli(["--user-data-dir", userData, "--extensions-dir", extDir, "--list-extensions", "--show-versions"]);
console.log(`smoke: installed extensions: ${(listed.stdout ?? "").trim()}`);
if (!/fsharpened\.fsharpened@/.test(listed.stdout ?? "")) fail("the extension is not in --list-extensions");

// Launch the real window. On Windows run Code.exe itself so the process can be stopped with its whole tree.
const launcherDir = path.dirname(path.dirname(code));
const exe = win ? path.join(launcherDir, "Code.exe") : code;
const args = ["--user-data-dir", userData, "--extensions-dir", extDir, "--disable-workspace-trust", "--skip-welcome", "--skip-release-notes", "--new-window", ws, path.join(ws, "Sample.fs")];
console.log(`smoke: starting VS Code on ${ws}`);
const child = spawn(exe, args, { env, stdio: "ignore", detached: !win });

const stop = () => {
  try {
    if (win) spawnSync("taskkill", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore" });
    else process.kill(-child.pid);
  } catch {
    // already gone
  }
};

function walk(dir, out = []) {
  let names = [];
  try {
    names = readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const n of names) {
    const p = path.join(dir, n.name);
    if (n.isDirectory()) walk(p, out);
    else out.push(p);
  }
  return out;
}

const readExtensionLog = () =>
  walk(path.join(userData, "logs"))
    .filter((f) => /fsharpened/i.test(path.basename(f)) && !/exthost\.log$/.test(f))
    .map((f) => readFileSync(f, "utf8"))
    .join("\n");

const wantFsh = existsSync(fshDll);
const deadline = Date.now() + 120000;
let log = "";
let ok = false;
while (Date.now() < deadline) {
  await new Promise((r) => setTimeout(r, 2000));
  log = readExtensionLog();
  const lsp = existsSync(lspLog) ? readFileSync(lspLog, "utf8") : "";
  const activated = /FSharpened 0\.0\.1 activated/.test(log);
  const started = /language server: running/.test(log) && (realLsp || /didOpen .*Sample\.fs languageId=fsharp/.test(lsp));
  const analyzed = !wantFsh || /analyzed .*Sample\.fs: 2 finding\(s\)/.test(log);
  if (activated && started && analyzed) {
    ok = true;
    break;
  }
}

console.log("--- extension output (FSharpened channel) ---");
console.log(log.trim() || "(no log found)");
if (existsSync(lspLog)) {
  console.log("--- fake language server received ---");
  console.log(readFileSync(lspLog, "utf8").trim());
}
stop();
await new Promise((r) => setTimeout(r, 2000));
if (process.env.FSH_SMOKE_KEEP !== "1") {
  try {
    rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
  } catch (e) {
    console.log(`smoke: could not remove the throwaway folder ${root} (${e.code}); delete it by hand`);
  }
}
if (!ok) fail("the extension did not show the expected log lines within 120s");
console.log(`smoke: OK in real VS Code ${(spawnSync(win ? q(code) : code, ["--version"], { encoding: "utf8", shell: win }).stdout ?? "").split("\n")[0]} (language server: ${realLsp ? "real FsAutoComplete" : "FAKE"}, fsh: ${wantFsh ? "real build" : "not built, diagnostics not checked"})`);
