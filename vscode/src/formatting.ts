// Fantomas as the F# document formatter, on demand (Format Document) and optionally on save.

import * as vscode from "vscode";
import { FantomasDaemon } from "./fantomas.ts";
import { resolveFor, workspaceRoot } from "./host.ts";
import { readSettings } from "./settings.ts";

export interface FormatEvents {
  log(line: string): void;
  onMissing(tried: string[]): void;
}

export class Formatting implements vscode.DocumentFormattingEditProvider, vscode.Disposable {
  private readonly daemons = new Map<string, FantomasDaemon>();
  private warnedMissing = false;
  /** The last reason formatting failed, for the Doctor report and for explicit format commands. */
  lastError: string | undefined;

  constructor(private readonly events: FormatEvents) {}

  async provideDocumentFormattingEdits(doc: vscode.TextDocument, _options: vscode.FormattingOptions, token: vscode.CancellationToken): Promise<vscode.TextEdit[]> {
    const r = await this.edits(doc, token);
    return r.edits;
  }

  /** Edits for the whole document, plus the failure message when there are none because of an error. */
  async edits(doc: vscode.TextDocument, token?: vscode.CancellationToken): Promise<{ edits: vscode.TextEdit[]; error?: string }> {
    if (!vscode.workspace.isTrusted) return { edits: [], error: "the workspace is not trusted" };
    if (doc.uri.scheme !== "file") return { edits: [], error: "only files on disk can be formatted" };

    const { resolution } = resolveFor("fantomas", doc.uri);
    if (!resolution.found) {
      if (!this.warnedMissing) {
        this.warnedMissing = true;
        this.events.onMissing(resolution.tried);
      }
      return { edits: [], error: "Fantomas was not found" };
    }

    const launch = { command: resolution.command, args: resolution.args, cwd: resolution.cwd ?? workspaceRoot(doc.uri) };
    const key = JSON.stringify(launch);
    let daemon = this.daemons.get(key);
    if (!daemon) {
      daemon = new FantomasDaemon(launch);
      this.daemons.set(key, daemon);
    }

    const outcome = await daemon.format(doc.getText(), doc.uri.fsPath, readSettings(doc.uri).timeoutMs);
    if (token?.isCancellationRequested) return { edits: [] };
    switch (outcome.kind) {
      case "formatted": {
        this.lastError = undefined;
        const eol = doc.eol === vscode.EndOfLine.CRLF ? "\r\n" : "\n";
        const text = outcome.text.replace(/\r\n|\r|\n/g, eol);
        if (text === doc.getText()) return { edits: [] };
        const all = new vscode.Range(doc.positionAt(0), doc.positionAt(doc.getText().length));
        return { edits: [vscode.TextEdit.replace(all, text)] };
      }
      case "unchanged":
      case "ignored":
        this.lastError = undefined;
        return { edits: [] };
      case "error":
        this.lastError = outcome.message;
        this.events.log(`Fantomas could not format ${doc.uri.fsPath}: ${outcome.message}`);
        return { edits: [], error: outcome.message };
    }
  }

  /** Drop the running daemons, for example after the tool path setting changed. */
  reset(): void {
    for (const d of this.daemons.values()) d.dispose();
    this.daemons.clear();
    this.warnedMissing = false;
  }

  dispose(): void {
    this.reset();
  }
}
