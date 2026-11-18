// Contract tests on package.json and the sources: what the extension declares is what it does, and what it promises
// (trust, no network, no shell, no telemetry) is checked by reading the code, because VS Code itself is not available to the unit tests.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";

const here = (p: string): string => fileURLToPath(new URL(`../${p}`, import.meta.url));
const read = (p: string): string => readFileSync(here(p), "utf8");
const pkg = JSON.parse(read("package.json")) as {
  engines: { vscode: string };
  activationEvents: string[];
  capabilities: { untrustedWorkspaces: { supported: string; restrictedConfigurations: string[]; description: string }; virtualWorkspaces: { supported: boolean } };
  contributes: { commands: Array<{ command: string }>; configuration: { properties: Record<string, { default?: unknown; scope?: string }> } };
  dependencies: Record<string, string>;
  devDependencies: Record<string, string>;
  scripts: Record<string, string>;
};
const sources = readdirSync(here("src"))
  .filter((f) => f.endsWith(".ts"))
  .map((f) => ({ name: f, text: read(`src/${f}`) }));
const allSource = sources.map((s) => s.text).join("\n");

test("every contributed command is registered, and every registered command is contributed", () => {
  const declared = pkg.contributes.commands.map((c) => c.command).sort();
  const registered = [...allSource.matchAll(/registerCommand\("([^"]+)"/g)].map((m) => m[1] as string).sort();
  assert.deepEqual(registered, declared);
});

test("the task's commands exist", () => {
  const ids = pkg.contributes.commands.map((c) => c.command);
  for (const id of ["fsharpened.runCi", "fsharpened.doctor", "fsharpened.showCiReport"]) assert.ok(ids.includes(id), id);
});

test("every setting the code reads is declared, and every restricted setting exists", () => {
  const props = Object.keys(pkg.contributes.configuration.properties);
  const read_ = [...read("src/settings.ts").matchAll(/c\.get<[^>]+>\("([^"]+)"/g)].map((m) => "fsharpened." + m[1]);
  for (const k of read_) assert.ok(props.includes(k), `${k} is read but not declared`);
  for (const k of pkg.capabilities.untrustedWorkspaces.restrictedConfigurations) assert.ok(props.includes(k), `${k} is restricted but not declared`);
});

test("every setting that names a program is restricted in untrusted workspaces and cannot be set by the workspace silently", () => {
  const programs = Object.keys(pkg.contributes.configuration.properties).filter((k) => /\.(path|args)$/.test(k));
  assert.ok(programs.length >= 5);
  for (const k of programs) {
    assert.ok(pkg.capabilities.untrustedWorkspaces.restrictedConfigurations.includes(k), `${k} must be restricted`);
    assert.equal(pkg.contributes.configuration.properties[k]?.scope, "machine-overridable", `${k} scope`);
  }
});

test("workspace trust is declared honestly: limited, with a description, and virtual workspaces off", () => {
  assert.equal(pkg.capabilities.untrustedWorkspaces.supported, "limited");
  assert.match(pkg.capabilities.untrustedWorkspaces.description, /None of that runs until you trust/);
  assert.equal(pkg.capabilities.virtualWorkspaces.supported, false);
});

test("the vscode engine is a pinned floor and the types match it", () => {
  assert.match(pkg.engines.vscode, /^\^1\.\d+\.0$/);
  assert.equal(pkg.devDependencies["@types/vscode"], pkg.engines.vscode.slice(1), "@types/vscode must equal the engine floor, or the code may use newer APIs");
});

test("dependencies are pinned exactly", () => {
  for (const [name, range] of Object.entries({ ...pkg.dependencies, ...pkg.devDependencies })) assert.match(range, /^\d+\.\d+\.\d+$/, `${name}@${range}`);
});

test("nothing in src opens a network connection, uses a shell, or sends telemetry", () => {
  const banned: Array<[RegExp, string]> = [
    [/from\s+"node:(http|https|http2|net|tls|dgram|dns)"/, "a network module"],
    [/require\(\s*["'](http|https|net|tls|dgram|dns)["']/, "a network module"],
    [/\bfetch\s*\(/, "fetch"],
    [/\bXMLHttpRequest\b|\bWebSocket\b/, "a browser network API"],
    [/shell\s*:\s*true/, "shell: true"],
    [/import\s*\{[^}]*\b(exec|execSync|execFile|execFileSync|spawnSync|fork)\b[^}]*\}\s*from\s*"node:child_process"/, "a synchronous or shell-based child process"],
    [/\bvscode-extension-telemetry\b|\bappinsights\b|\bapplicationinsights\b|\btelemetry\.(send|log)/i, "telemetry"],
  ];
  for (const s of sources) {
    for (const [re, what] of banned) assert.doesNotMatch(s.text, re, `${s.name} uses ${what}`);
  }
});

test("every entry point that starts a process checks workspace trust first", () => {
  // Process starters: the language client, the Fantomas daemon, the analyzer, the CI terminal, the Doctor probes.
  assert.match(read("src/languageServer.ts"), /if \(!vscode\.workspace\.isTrusted\) return/);
  assert.match(read("src/formatting.ts"), /if \(!vscode\.workspace\.isTrusted\) return/);
  assert.match(read("src/problems.ts"), /!vscode\.workspace\.isTrusted/);
  const ext = read("src/extension.ts");
  assert.match(ext, /const needTrust = /);
  for (const cmd of ["restartLanguageServer", "analyzeFile", "formatDocument", "runCi"]) {
    const body = ext.slice(ext.indexOf(`registerCommand("fsharpened.${cmd}"`));
    assert.match(body.slice(0, 200), /needTrust\(\)/, `${cmd} must check trust first`);
  }
  assert.match(ext, /vscode\.workspace\.isTrusted && id !== "fsh"/, "Doctor probes versions only when trusted");
  assert.match(ext, /fsh && fsh\.found && vscode\.workspace\.isTrusted|fsh\?\.found && vscode\.workspace\.isTrusted/, "Doctor runs fsh doctor only when trusted");
});

test("the activation events do not start work in an untrusted workspace by themselves", () => {
  // Activation only registers things; start() is gated. The events are language and project presence, never a command that runs a tool.
  assert.deepEqual(pkg.activationEvents, ["onLanguage:fsharp", "workspaceContains:**/*.fsproj"]);
});

test("the extension icon is the project's mark, byte for byte", () => {
  assert.deepEqual(readFileSync(here("images/icon.png")), readFileSync(here("../branding/icons/icon-512.png")));
});
