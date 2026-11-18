import * as vscode from "vscode";
import { asProfile, type Profile } from "./ci.ts";

export interface Settings {
  languageServer: { enabled: boolean; path: string; args: string[]; trace: "off" | "messages" | "verbose" };
  fantomas: { path: string; formatOnSave: boolean };
  fsh: { path: string };
  diagnostics: { enabled: boolean; runOnSave: boolean; runOnOpen: boolean };
  python: { path: string };
  ciProfile: Profile;
  timeoutMs: number;
}

export function readSettings(scope?: vscode.ConfigurationScope): Settings {
  const c = vscode.workspace.getConfiguration("fsharpened", scope);
  const trace = c.get<string>("languageServer.trace", "off");
  return {
    languageServer: {
      enabled: c.get<boolean>("languageServer.enabled", true),
      path: c.get<string>("languageServer.path", ""),
      args: c.get<unknown[]>("languageServer.args", []).filter((a): a is string => typeof a === "string"),
      trace: trace === "messages" || trace === "verbose" ? trace : "off",
    },
    fantomas: { path: c.get<string>("fantomas.path", ""), formatOnSave: c.get<boolean>("fantomas.formatOnSave", false) },
    fsh: { path: c.get<string>("fsh.path", "") },
    diagnostics: {
      enabled: c.get<boolean>("diagnostics.enabled", true),
      runOnSave: c.get<boolean>("diagnostics.runOnSave", true),
      runOnOpen: c.get<boolean>("diagnostics.runOnOpen", true),
    },
    python: { path: c.get<string>("python.path", "") },
    ciProfile: asProfile(c.get<string>("ci.profile", "fast")),
    timeoutMs: Math.min(600, Math.max(5, c.get<number>("toolTimeoutSeconds", 60))) * 1000,
  };
}
