// The real file system and environment behind the injectable seams in resolve.ts and ci.ts.

import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import type { Context, Fs } from "./resolve.ts";
import type { DirFs } from "./ci.ts";
import { resolveTool, type Resolution, type ToolSpec } from "./resolve.ts";
import { readSettings } from "./settings.ts";

const MAX_READ = 1024 * 1024;

export const realFs: Fs & DirFs = {
  isFile(p) {
    try {
      return fs.statSync(p).isFile();
    } catch {
      return false;
    }
  },
  readText(p) {
    try {
      if (fs.statSync(p).size > MAX_READ) return undefined;
      return fs.readFileSync(p, "utf8");
    } catch {
      return undefined;
    }
  },
  list(dir) {
    try {
      return fs.readdirSync(dir);
    } catch {
      return undefined;
    }
  },
};

export function workspaceRoot(uri?: vscode.Uri): string | undefined {
  const folder = uri ? vscode.workspace.getWorkspaceFolder(uri) : vscode.workspace.workspaceFolders?.[0];
  const f = folder ?? vscode.workspace.workspaceFolders?.[0];
  return f && f.uri.scheme === "file" ? f.uri.fsPath : undefined;
}

export function contextFor(uri?: vscode.Uri): Context {
  return {
    env: process.env,
    fs: realFs,
    workspaceRoot: workspaceRoot(uri),
    startDir: uri && uri.scheme === "file" ? path.dirname(uri.fsPath) : undefined,
  };
}

export type ToolId = "languageServer" | "fantomas" | "fsh" | "python";

export function resolveFor(id: ToolId, uri?: vscode.Uri): { spec: ToolSpec; resolution: Resolution } {
  const s = readSettings(uri);
  const specs: Record<ToolId, ToolSpec> = {
    languageServer: {
      label: "FsAutoComplete (language server)",
      setting: s.languageServer.path,
      settingName: "fsharpened.languageServer.path",
      manifestCommand: "fsautocomplete",
      pathNames: ["fsautocomplete"],
    },
    fantomas: {
      label: "Fantomas (formatter)",
      setting: s.fantomas.path,
      settingName: "fsharpened.fantomas.path",
      manifestCommand: "fantomas",
      pathNames: ["fantomas"],
    },
    fsh: {
      label: "fsh (FSharpened diagnostics)",
      setting: s.fsh.path,
      settingName: "fsharpened.fsh.path",
      manifestCommand: "fsh",
      pathNames: ["fsh"],
    },
    python: {
      label: "Python 3 (local CI)",
      setting: s.python.path,
      settingName: "fsharpened.python.path",
      pathNames: ["python3", "python", "py"],
    },
  };
  const spec = specs[id];
  return { spec, resolution: resolveTool(spec, contextFor(uri)) };
}
