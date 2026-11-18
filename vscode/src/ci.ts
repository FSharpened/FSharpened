// The local CI: how the command is built and where the last report is. Pure.

import * as path from "node:path";

export const CI_SCRIPT = path.join("tools", "ci", "fsh_ci.py");
export const PROFILES = ["fast", "full", "all"] as const;
export type Profile = (typeof PROFILES)[number];

export function asProfile(v: string): Profile {
  return (PROFILES as readonly string[]).includes(v) ? (v as Profile) : "fast";
}

/** The arguments after the Python executable. A fixed set: nothing from the workspace is spliced in except the profile enum. */
export function ciArgs(profile: Profile): string[] {
  return [CI_SCRIPT, "run", "--profile", profile];
}

export interface DirFs {
  /** Names inside dir, or undefined when dir is not a readable directory. */
  list(dir: string): string[] | undefined;
  isFile(p: string): boolean;
}

/** `.fsharpened/ci/runs/<timestamp>/report.md`: the newest run folder that has a report. Timestamps sort as text. */
export function latestReport(root: string, fs: DirFs): string | undefined {
  const runs = path.join(root, ".fsharpened", "ci", "runs");
  const names = fs.list(runs);
  if (!names) return undefined;
  for (const name of [...names].sort().reverse()) {
    const candidate = path.join(runs, name, "report.md");
    if (fs.isFile(candidate)) return candidate;
  }
  return undefined;
}
