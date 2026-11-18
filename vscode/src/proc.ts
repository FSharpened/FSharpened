// Running a child process: never through a shell, always with a timeout and an output cap.

import { spawn } from "node:child_process";

export interface RunOptions {
  cwd?: string;
  env?: NodeJS.ProcessEnv;
  input?: string;
  timeoutMs: number;
  maxBytes?: number;
}

export interface RunResult {
  /** null when the process did not start or was killed. */
  code: number | null;
  stdout: string;
  stderr: string;
  timedOut: boolean;
  /** Set when the process could not be started (ENOENT, EACCES...). */
  spawnError?: string;
  truncated: boolean;
}

export type Runner = (command: string, args: string[], opts: RunOptions) => Promise<RunResult>;

export const run: Runner = (command, args, opts) =>
  new Promise((resolve) => {
    const max = opts.maxBytes ?? 16 * 1024 * 1024;
    let stdout = "";
    let stderr = "";
    let timedOut = false;
    let truncated = false;
    let settled = false;

    const done = (r: Omit<RunResult, "stdout" | "stderr" | "timedOut" | "truncated">): void => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve({ ...r, stdout, stderr, timedOut, truncated });
    };

    let child;
    try {
      child = spawn(command, args, { cwd: opts.cwd, env: opts.env, shell: false, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    } catch (e) {
      resolve({ code: null, stdout, stderr, timedOut, truncated, spawnError: (e as Error).message });
      return;
    }
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill();
    }, opts.timeoutMs);

    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (d: string) => {
      if (stdout.length + d.length > max) truncated = true;
      else stdout += d;
    });
    child.stderr.on("data", (d: string) => {
      if (stderr.length + d.length > max) truncated = true;
      else stderr += d;
    });
    child.on("error", (e) => done({ code: null, spawnError: e.message }));
    child.on("close", (code) => done({ code }));
    child.stdin.on("error", () => undefined); // the child may exit before it reads its input
    child.stdin.end(opts.input ?? "");
  });

export function tail(text: string, lines = 12): string {
  const all = text.trimEnd().split(/\r?\n/);
  return all.slice(-lines).join("\n");
}
