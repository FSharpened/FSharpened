// FSharpened diagnostics as VS Code problems, plus the quick fixes their reports suggest.

import * as vscode from "vscode";
import { analyzeFile } from "./analysis.ts";
import { resolveFor, workspaceRoot } from "./host.ts";
import { readSettings } from "./settings.ts";
import type { Finding, Range0 } from "./report.ts";

interface Entry {
  diagnostic: vscode.Diagnostic;
  finding: Finding;
}

interface Stored {
  version: number;
  entries: Entry[];
}

const SOURCE = "FSharpened";

const SEVERITY: Record<Finding["severity"], vscode.DiagnosticSeverity> = {
  error: vscode.DiagnosticSeverity.Error,
  warning: vscode.DiagnosticSeverity.Warning,
  info: vscode.DiagnosticSeverity.Information,
  hidden: vscode.DiagnosticSeverity.Hint,
};

function toRange(r: Range0 | null): vscode.Range {
  return r ? new vscode.Range(r.startLine, r.startColumn, r.endLine, r.endColumn) : new vscode.Range(0, 0, 0, 0);
}

export interface ProblemsEvents {
  /** fsh is missing: raised once per window, not per file. */
  onFshMissing(tried: string[]): void;
  onResult(uri: vscode.Uri, count: number | undefined, busy: boolean): void;
  log(line: string): void;
}

export class Problems implements vscode.CodeActionProvider, vscode.Disposable {
  private readonly collection = vscode.languages.createDiagnosticCollection("fsharpened");
  private readonly stored = new Map<string, Stored>();
  private readonly generation = new Map<string, number>();
  private warnedMissing = false;

  constructor(private readonly events: ProblemsEvents) {}

  /** Analyze one saved or opened F# file on disk. Quietly does nothing in an untrusted workspace or for other files. */
  async analyze(doc: vscode.TextDocument): Promise<void> {
    if (!vscode.workspace.isTrusted || doc.languageId !== "fsharp" || doc.uri.scheme !== "file" || doc.isDirty) return;
    const settings = readSettings(doc.uri);
    if (!settings.diagnostics.enabled) {
      this.clear(doc.uri);
      return;
    }
    const { resolution } = resolveFor("fsh", doc.uri);
    if (!resolution.found) {
      if (!this.warnedMissing) {
        this.warnedMissing = true;
        this.events.onFshMissing(resolution.tried);
      }
      return;
    }

    const key = doc.uri.toString();
    const mine = (this.generation.get(key) ?? 0) + 1;
    this.generation.set(key, mine);
    this.events.onResult(doc.uri, undefined, true);

    // The tool reads the file from disk, so only a saved document is analyzed: what it reports matches what is on disk.
    const version = doc.version;
    const result = await analyzeFile(resolution, doc.uri.fsPath, { cwd: workspaceRoot(doc.uri) ?? ".", timeoutMs: settings.timeoutMs });
    if (this.generation.get(key) !== mine) return; // a newer run owns the result
    if (!result.ok) {
      this.events.log(`analysis of ${doc.uri.fsPath} failed: ${result.error}`);
      this.events.onResult(doc.uri, undefined, false);
      return;
    }

    const entries: Entry[] = result.findings
      .filter((f) => f.file === null || sameFile(f.file, doc.uri.fsPath))
      .map((finding) => {
        const d = new vscode.Diagnostic(toRange(finding.range), finding.message, SEVERITY[finding.severity]);
        d.source = SOURCE;
        d.code = finding.docs ? { value: finding.code, target: vscode.Uri.parse(finding.docs) } : finding.code;
        return { diagnostic: d, finding };
      });
    this.stored.set(key, { version, entries });
    this.collection.set(doc.uri, entries.map((e) => e.diagnostic));
    this.events.log(`analyzed ${doc.uri.fsPath}: ${entries.length} finding(s) from ${result.tool}`);
    this.events.onResult(doc.uri, entries.length, false);
  }

  clear(uri: vscode.Uri): void {
    this.generation.set(uri.toString(), (this.generation.get(uri.toString()) ?? 0) + 1);
    this.stored.delete(uri.toString());
    this.collection.delete(uri);
  }

  clearAll(): void {
    this.collection.clear();
    this.stored.clear();
  }

  count(uri: vscode.Uri): number | undefined {
    return this.stored.get(uri.toString())?.entries.length;
  }

  provideCodeActions(doc: vscode.TextDocument, _range: vscode.Range, context: vscode.CodeActionContext): vscode.CodeAction[] {
    const stored = this.stored.get(doc.uri.toString());
    // The edits are positions in the text that was analyzed. If the document changed since, they could land anywhere.
    if (!stored || stored.version !== doc.version || doc.isDirty) return [];
    const actions: vscode.CodeAction[] = [];
    for (const wanted of context.diagnostics) {
      if (wanted.source !== SOURCE) continue;
      const entry = stored.entries.find((e) => e.diagnostic.range.isEqual(wanted.range) && codeOf(e.diagnostic) === codeOf(wanted));
      const fix = entry?.finding.fix;
      if (!entry || !fix || fix.edits.length === 0) continue;
      const action = new vscode.CodeAction(fix.title, vscode.CodeActionKind.QuickFix);
      action.diagnostics = [wanted];
      action.isPreferred = true;
      const edit = new vscode.WorkspaceEdit();
      for (const e of fix.edits) edit.replace(doc.uri, toRange(e.range), e.newText);
      action.edit = edit;
      actions.push(action);
    }
    return actions;
  }

  dispose(): void {
    this.collection.dispose();
  }
}

function codeOf(d: vscode.Diagnostic): string {
  return typeof d.code === "object" ? String(d.code.value) : String(d.code ?? "");
}

function sameFile(reported: string, actual: string): boolean {
  const norm = (p: string): string => p.replaceAll("\\", "/").toLowerCase();
  const a = norm(actual);
  const r = norm(reported);
  return a === r || a.endsWith("/" + r.replace(/^\.\//, ""));
}
