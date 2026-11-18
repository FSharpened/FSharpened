// The FsAutoComplete language client. It only ever starts in a trusted workspace.

import * as vscode from "vscode";
import { LanguageClient, State, Trace, type LanguageClientOptions, type ServerOptions } from "vscode-languageclient/node";
import { resolveFor, workspaceRoot } from "./host.ts";
import { readSettings } from "./settings.ts";
import type { ServerState } from "./statusModel.ts";

export interface ServerEvents {
  onState(state: ServerState): void;
  onMissing(tried: string[]): void;
}

export class LanguageServer implements vscode.Disposable {
  private client: LanguageClient | undefined;
  state: ServerState = "off";

  constructor(
    private readonly output: vscode.OutputChannel,
    private readonly events: ServerEvents,
  ) {}

  private set(state: ServerState): void {
    this.state = state;
    this.events.onState(state);
  }

  async start(): Promise<void> {
    if (this.client) return;
    if (!vscode.workspace.isTrusted) return this.set("restricted");
    const settings = readSettings();
    if (!settings.languageServer.enabled) return this.set("off");

    const { resolution } = resolveFor("languageServer");
    if (!resolution.found) {
      this.set("missing");
      this.output.appendLine(`FsAutoComplete not found. Tried: ${resolution.tried.join("; ")}`);
      this.events.onMissing(resolution.tried);
      return;
    }

    const cwd = resolution.cwd ?? workspaceRoot();
    const serverOptions: ServerOptions = {
      command: resolution.command,
      args: [...resolution.args, ...settings.languageServer.args],
      options: { cwd },
    };
    const clientOptions: LanguageClientOptions = {
      documentSelector: [{ scheme: "file", language: "fsharp" }],
      outputChannel: this.output,
      // FsAutoComplete loads the workspace's projects by itself when told to (what Ionide does).
      initializationOptions: { AutomaticWorkspaceInit: true },
    };
    const client = new LanguageClient("fsharpened", "FSharpened: FsAutoComplete", serverOptions, clientOptions);
    client.onDidChangeState((e) => {
      if (e.newState === State.Running) this.set("running");
      else if (e.newState === State.Starting) this.set("starting");
      else if (this.client === client && e.oldState === State.Running) this.set("error");
    });
    this.client = client;
    this.set("starting");
    this.output.appendLine(`Starting FsAutoComplete: ${resolution.detail}`);
    try {
      await client.start();
      await client.setTrace(settings.languageServer.trace === "verbose" ? Trace.Verbose : settings.languageServer.trace === "messages" ? Trace.Messages : Trace.Off);
    } catch (e) {
      this.output.appendLine(`FsAutoComplete failed to start: ${(e as Error).message}`);
      this.client = undefined;
      this.set("error");
    }
  }

  async stop(): Promise<void> {
    const c = this.client;
    this.client = undefined;
    if (!c) return;
    try {
      await c.stop(3000);
    } catch {
      // The process may already be gone; there is nothing more to clean up.
    }
  }

  async restart(): Promise<void> {
    await this.stop();
    await this.start();
  }

  dispose(): void {
    void this.stop();
  }
}
