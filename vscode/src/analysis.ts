// Running `fsh analyze --format json <file>` and reading its report. Pure with respect to VS Code.

import { parseReport, type Finding } from "./report.ts";
import { run, tail, type Runner } from "./proc.ts";
import type { Resolved } from "./resolve.ts";

export type AnalysisResult = { ok: true; tool: string; findings: Finding[] } | { ok: false; error: string };

export function analyzeArgs(fsh: Resolved, file: string): string[] {
  return [...fsh.args, "analyze", "--format", "json", file];
}

export async function analyzeFile(
  fsh: Resolved,
  file: string,
  opts: { cwd: string; timeoutMs: number; runner?: Runner },
): Promise<AnalysisResult> {
  const r = await (opts.runner ?? run)(fsh.command, analyzeArgs(fsh, file), { cwd: fsh.cwd ?? opts.cwd, timeoutMs: opts.timeoutMs });
  if (r.spawnError !== undefined) return { ok: false, error: `could not start fsh: ${r.spawnError}` };
  if (r.timedOut) return { ok: false, error: `fsh analyze timed out after ${Math.round(opts.timeoutMs / 1000)}s` };
  if (r.code !== 0) return { ok: false, error: `fsh analyze exited with ${String(r.code)}: ${tail(r.stderr || r.stdout, 6)}` };
  if (r.truncated) return { ok: false, error: "fsh analyze printed more than the output cap" };
  return parseReport(r.stdout);
}
