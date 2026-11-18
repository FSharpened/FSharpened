// Fantomas, through its own `--daemon` mode (the LSP-style JSON-RPC server editors use). Fantomas 7 no longer reads from
// stdin, and a daemon keeps the format-on-save latency low and lets Fantomas find the right .editorconfig for the file.
//
// Wire format (from Fantomas.Client): request `fantomas/formatDocument` with { SourceCode, FilePath }, answer
// { Case: "Formatted" | "Unchanged" | "IgnoredFile" | "Error", Fields: [...] }.
// Pure with respect to VS Code, so the node tests drive it against a fake daemon.

import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createMessageConnection, StreamMessageReader, StreamMessageWriter, type MessageConnection } from "vscode-jsonrpc/node";

export type FormatOutcome =
  | { kind: "formatted"; text: string }
  | { kind: "unchanged" }
  | { kind: "ignored" }
  | { kind: "error"; message: string };

export interface DaemonLaunch {
  command: string;
  args: string[];
  cwd?: string;
}

interface Pending {
  child: ChildProcessWithoutNullStreams;
  connection: MessageConnection;
}

export class FantomasDaemon {
  private current: Promise<Pending> | undefined;
  private disposed = false;
  /** The child process that `current` belongs to. */
  private owner: ChildProcessWithoutNullStreams | undefined;

  constructor(private readonly launch: DaemonLaunch) {}

  private start(): Promise<Pending> {
    return new Promise<Pending>((resolve, reject) => {
      const child = spawn(this.launch.command, [...this.launch.args, "--daemon"], {
        cwd: this.launch.cwd,
        shell: false,
        windowsHide: true,
        stdio: ["pipe", "pipe", "pipe"],
      });
      child.stderr.resume(); // never let an unread pipe stall the daemon
      const connection = createMessageConnection(new StreamMessageReader(child.stdout), new StreamMessageWriter(child.stdin));
      // Forget this daemon, but only if it is still the current one: a replaced daemon exits late, and must not wipe its successor.
      const reset = (): void => {
        if (this.owner === child) {
          this.owner = undefined;
          this.current = undefined;
        }
      };
      this.owner = child;
      child.once("error", (e) => {
        reset();
        reject(e);
      });
      child.once("exit", () => {
        reset();
        connection.dispose();
      });
      connection.onError(() => undefined);
      connection.listen();
      child.once("spawn", () => resolve({ child, connection }));
    });
  }

  private async connection(): Promise<Pending> {
    if (this.disposed) throw new Error("the Fantomas daemon was disposed");
    this.current ??= this.start().catch((e) => {
      this.current = undefined;
      throw e;
    });
    return this.current;
  }

  async format(source: string, filePath: string, timeoutMs: number): Promise<FormatOutcome> {
    let pending: Pending;
    try {
      pending = await this.connection();
    } catch (e) {
      return { kind: "error", message: `could not start Fantomas: ${(e as Error).message}` };
    }
    let timer: NodeJS.Timeout | undefined;
    try {
      const answer = await Promise.race([
        pending.connection.sendRequest("fantomas/formatDocument", { SourceCode: source, FilePath: filePath }),
        new Promise<never>((_, reject) => {
          timer = setTimeout(() => reject(new Error(`timed out after ${Math.round(timeoutMs / 1000)}s`)), timeoutMs);
        }),
      ]);
      return interpret(answer);
    } catch (e) {
      // A stuck or crashed daemon is replaced on the next request instead of being waited on.
      this.kill();
      return { kind: "error", message: (e as Error).message };
    } finally {
      clearTimeout(timer);
    }
  }

  async version(timeoutMs: number): Promise<string | undefined> {
    try {
      const pending = await this.connection();
      const v = await Promise.race([
        pending.connection.sendRequest("fantomas/version"),
        new Promise<never>((_, reject) => setTimeout(() => reject(new Error("timeout")), timeoutMs).unref()),
      ]);
      return typeof v === "string" ? v : undefined;
    } catch {
      this.kill();
      return undefined;
    }
  }

  private kill(): void {
    const p = this.current;
    this.current = undefined;
    this.owner = undefined;
    void p?.then((x) => {
      x.connection.dispose();
      // Closing the pipes ends a daemon that is a grandchild of `dotnet` too (it exits when its stdin closes), and keeps
      // a straggler from holding this process open.
      x.child.stdin.destroy();
      x.child.stdout.destroy();
      x.child.stderr.destroy();
      x.child.kill();
      x.child.unref();
    }, () => undefined);
  }

  dispose(): void {
    this.disposed = true;
    this.kill();
  }
}

export function interpret(answer: unknown): FormatOutcome {
  if (typeof answer !== "object" || answer === null) return { kind: "error", message: "unexpected answer from Fantomas" };
  const a = answer as { Case?: unknown; Fields?: unknown };
  const fields = Array.isArray(a.Fields) ? (a.Fields as unknown[]) : [];
  switch (a.Case) {
    case "Formatted":
      return typeof fields[1] === "string" ? { kind: "formatted", text: fields[1] } : { kind: "error", message: "Fantomas returned no text" };
    case "Unchanged":
      return { kind: "unchanged" };
    case "IgnoredFile":
      return { kind: "ignored" };
    case "Error":
      return { kind: "error", message: firstLine(typeof fields[1] === "string" ? fields[1] : "formatting failed") };
    default:
      return { kind: "error", message: `unknown Fantomas answer "${String(a.Case)}"` };
  }
}

/** A parse error from Fantomas is a long record dump; the editor shows the first useful line. */
function firstLine(text: string): string {
  const message = /Message\s*=\s*\n?\s*"([^"]+)"/.exec(text);
  return (message?.[1] ?? text.split(/\r?\n/)[0] ?? text).trim();
}
