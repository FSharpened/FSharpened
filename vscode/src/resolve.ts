// Finding the external tools: a setting, then the nearest .config/dotnet-tools.json, then PATH. Pure and injectable
// (a fake file system and environment), so the node tests cover it without VS Code or any installed tool.
//
// Platform behaviour comes from the environment, not from a platform check here: the path separator is path.delimiter and
// the executable suffixes are whatever PATHEXT says (unset on Linux and macOS, so nothing is appended there).

import * as path from "node:path";

export interface Fs {
  isFile(p: string): boolean;
  readText(p: string): string | undefined;
}

export type Env = Record<string, string | undefined>;

export type Source = "setting" | "dotnet-tool" | "path";

export interface Resolved {
  found: true;
  /** Executable to start (never a shell). */
  command: string;
  /** Arguments that come before the tool's own (for `dotnet fantomas`: ["fantomas"]). */
  args: string[];
  source: Source;
  /** Human text: where it was found. */
  detail: string;
  /** Directory to start it in, when the source needs one (a local tool needs a cwd under its manifest). */
  cwd?: string;
}

export interface Missing {
  found: false;
  /** What was tried, in order, for the Doctor report and the install hint. */
  tried: string[];
}

export type Resolution = Resolved | Missing;

export interface ToolSpec {
  label: string;
  /** The configured path (may be empty). */
  setting: string;
  settingName: string;
  /** The command name a dotnet local tool declares in the manifest, which `dotnet <name>` runs. */
  manifestCommand?: string;
  /** Names to look for on PATH, in order. */
  pathNames: string[];
}

export interface Context {
  env: Env;
  fs: Fs;
  /** Workspace folder, used for ${workspaceFolder} and as the manifest search floor when nothing is nearer. */
  workspaceRoot: string | undefined;
  /** Directory to start the manifest search from (the file's folder); defaults to the workspace root. */
  startDir: string | undefined;
}

function envGet(env: Env, name: string): string | undefined {
  const k = Object.keys(env).find((x) => x.toUpperCase() === name.toUpperCase());
  return k === undefined ? undefined : env[k];
}

/** Executable suffixes to try after a bare name. Only .exe and .com: a child process cannot be started from a .cmd or .bat without a shell. */
function suffixes(env: Env): string[] {
  const ext = envGet(env, "PATHEXT");
  if (!ext) return [""];
  const keep = ext
    .split(";")
    .map((e) => e.trim().toLowerCase())
    .filter((e) => e === ".exe" || e === ".com");
  return ["", ...keep];
}

export function findOnPath(name: string, ctx: Pick<Context, "env" | "fs">): string | undefined {
  const sfx = suffixes(ctx.env);
  if (name.includes("/") || name.includes("\\") || path.isAbsolute(name)) {
    return sfx.map((s) => name + s).find((c) => ctx.fs.isFile(c));
  }
  const dirs = (envGet(ctx.env, "PATH") ?? "").split(path.delimiter).filter((d) => d.length > 0);
  for (const dir of dirs) {
    for (const s of sfx) {
      const candidate = path.join(dir, name + s);
      if (ctx.fs.isFile(candidate)) return candidate;
    }
  }
  return undefined;
}

export function substitute(value: string, workspaceRoot: string | undefined): string {
  return workspaceRoot === undefined ? value : value.replaceAll("${workspaceFolder}", workspaceRoot);
}

/** The nearest `.config/dotnet-tools.json` at or above `dir`, the way the dotnet CLI looks one up. */
export function findManifest(dir: string, fs: Fs): string | undefined {
  let cur = path.resolve(dir);
  for (let i = 0; i < 64; i++) {
    const candidate = path.join(cur, ".config", "dotnet-tools.json");
    if (fs.isFile(candidate)) return candidate;
    const parent = path.dirname(cur);
    if (parent === cur) return undefined;
    cur = parent;
  }
  return undefined;
}

/** The command names the manifest's tools declare. Anything unparseable is treated as "none": a broken manifest is the dotnet CLI's error to report. */
export function manifestCommands(text: string): string[] {
  try {
    const doc = JSON.parse(text) as { tools?: Record<string, { commands?: unknown }> };
    const out: string[] = [];
    for (const t of Object.values(doc.tools ?? {})) {
      if (Array.isArray(t.commands)) for (const c of t.commands) if (typeof c === "string") out.push(c);
    }
    return out;
  } catch {
    return [];
  }
}

export function resolveTool(spec: ToolSpec, ctx: Context): Resolution {
  const tried: string[] = [];
  const dotnet = (): string | undefined => findOnPath("dotnet", ctx);

  const configured = substitute(spec.setting.trim(), ctx.workspaceRoot);
  if (configured.length > 0) {
    if (configured.toLowerCase().endsWith(".dll")) {
      const d = dotnet();
      if (d === undefined) return { found: false, tried: [`${spec.settingName} = ${configured} needs \`dotnet\` on PATH to run a .dll`] };
      if (!ctx.fs.isFile(configured)) return { found: false, tried: [`${spec.settingName} = ${configured}: no such file`] };
      return { found: true, command: d, args: [configured], source: "setting", detail: `${spec.settingName} (dotnet ${configured})` };
    }
    const hit = findOnPath(configured, ctx);
    if (hit === undefined) return { found: false, tried: [`${spec.settingName} = ${configured}: not found`] };
    return { found: true, command: hit, args: [], source: "setting", detail: `${spec.settingName} (${hit})` };
  }
  tried.push(`${spec.settingName} (not set)`);

  const startDir = ctx.startDir ?? ctx.workspaceRoot;
  if (spec.manifestCommand !== undefined && startDir !== undefined) {
    const manifest = findManifest(startDir, ctx.fs);
    if (manifest === undefined) {
      tried.push("no .config/dotnet-tools.json above the file");
    } else {
      const text = ctx.fs.readText(manifest);
      if (text !== undefined && manifestCommands(text).includes(spec.manifestCommand)) {
        const d = dotnet();
        if (d !== undefined) {
          return {
            found: true,
            command: d,
            args: [spec.manifestCommand],
            source: "dotnet-tool",
            detail: `dotnet ${spec.manifestCommand} (${manifest})`,
            cwd: path.dirname(path.dirname(manifest)),
          };
        }
        tried.push(`${manifest} lists \`${spec.manifestCommand}\` but \`dotnet\` is not on PATH`);
      } else {
        tried.push(`${manifest} does not list \`${spec.manifestCommand}\``);
      }
    }
  }

  for (const name of spec.pathNames) {
    const hit = findOnPath(name, ctx);
    if (hit !== undefined) return { found: true, command: hit, args: [], source: "path", detail: `PATH (${hit})` };
    tried.push(`\`${name}\` on PATH`);
  }
  return { found: false, tried };
}

/** The first of several interpreter names that exists on PATH, for the local CI. */
export function resolvePython(setting: string, ctx: Context): Resolution {
  return resolveTool(
    { label: "Python", setting, settingName: "fsharpened.python.path", pathNames: ["python3", "python", "py"] },
    ctx,
  );
}
