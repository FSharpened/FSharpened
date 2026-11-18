// The text of `FSharpened: Doctor`. Pure: it is handed facts and returns lines.

import type { Resolution } from "./resolve.ts";

export interface ToolFact {
  label: string;
  resolution: Resolution;
  /** Output of `--version`, when it could be run. */
  version?: string;
  /** What to do when it is missing. */
  hint: string;
}

export interface DoctorFacts {
  extensionVersion: string;
  trusted: boolean;
  workspaceRoot: string | undefined;
  languageServer: string;
  tools: ToolFact[];
  /** Output of `fsh doctor`, when fsh was found and ran. */
  fshDoctor?: string;
}

export function formatDoctor(f: DoctorFacts): string {
  const lines: string[] = [];
  lines.push(`FSharpened extension ${f.extensionVersion}`);
  lines.push(`workspace:       ${f.workspaceRoot ?? "(no folder open)"}`);
  lines.push(`trusted:         ${f.trusted ? "yes" : "NO: no tool is started until the workspace is trusted"}`);
  lines.push(`language server: ${f.languageServer}`);
  lines.push("");
  for (const t of f.tools) {
    const r = t.resolution;
    if (r.found) {
      lines.push(`[ok]      ${t.label}: ${r.detail}${t.version ? `, ${t.version}` : ""}`);
    } else {
      lines.push(`[missing] ${t.label}`);
      for (const tried of r.tried) lines.push(`            tried: ${tried}`);
      lines.push(`            fix:   ${t.hint}`);
    }
  }
  if (f.fshDoctor !== undefined) {
    lines.push("");
    lines.push("fsh doctor:");
    for (const l of f.fshDoctor.trimEnd().split(/\r?\n/)) lines.push(`  ${l}`);
  }
  lines.push("");
  lines.push("No network calls and no telemetry: this extension only starts the local programs listed above.");
  return lines.join("\n");
}

export const HINTS = {
  languageServer: "dotnet tool install fsautocomplete (or add it to .config/dotnet-tools.json and run `dotnet tool restore`), or set fsharpened.languageServer.path",
  fantomas: "dotnet tool install fantomas (or add it to .config/dotnet-tools.json), or set fsharpened.fantomas.path",
  fsh: "build tools/FSharpened.Cli (dotnet build tools/FSharpened.Cli -c Release), then set fsharpened.fsh.path to the fsh executable or fsh.dll, or put fsh on PATH",
  python: "install Python 3, or set fsharpened.python.path",
  dotnet: "install the .NET SDK (https://dotnet.microsoft.com/download) and make sure `dotnet` is on PATH",
} as const;
