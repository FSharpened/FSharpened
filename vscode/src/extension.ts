// FSharpened for VS Code. Local only: it starts programs the person already has (FsAutoComplete, Fantomas, fsh, Python),
// never opens a network connection and sends no telemetry. In an untrusted workspace it starts nothing at all.

import * as path from "node:path";
import * as vscode from "vscode";
import { ciArgs, CI_SCRIPT, latestReport } from "./ci.ts";
import { formatDoctor, HINTS, type ToolFact } from "./doctor.ts";
import { Formatting } from "./formatting.ts";
import { realFs, resolveFor, workspaceRoot, type ToolId } from "./host.ts";
import { LanguageServer } from "./languageServer.ts";
import { Problems } from "./problems.ts";
import { run, tail } from "./proc.ts";
import { readSettings } from "./settings.ts";
import { describeStatus, type ServerState } from "./statusModel.ts";

const SELECTOR: vscode.DocumentSelector = { scheme: "file", language: "fsharp" };

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const output = vscode.window.createOutputChannel("FSharpened");
  const statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
  statusItem.command = "fsharpened.doctor";
  context.subscriptions.push(output, statusItem);

  let server: ServerState = "off";
  let fshMissing = false;
  let busy = 0;
  const refreshStatus = (): void => {
    const editor = vscode.window.activeTextEditor;
    const doc = editor?.document;
    const view = describeStatus({
      trusted: vscode.workspace.isTrusted,
      server,
      findings: doc && doc.languageId === "fsharp" ? problems.count(doc.uri) : undefined,
      fshMissing,
      busy: busy > 0,
    });
    statusItem.text = view.text;
    statusItem.tooltip = view.tooltip;
    statusItem.backgroundColor = view.level === "error" ? new vscode.ThemeColor("statusBarItem.errorBackground") : view.level === "warning" ? new vscode.ThemeColor("statusBarItem.warningBackground") : undefined;
    statusItem.show();
  };

  const missingNotice = (what: string, hint: string, setting: string) => async (tried: string[]): Promise<void> => {
    output.appendLine(`${what} not found. Tried: ${tried.join("; ")}`);
    const pick = await vscode.window.showWarningMessage(`FSharpened: ${what} was not found. ${hint}`, "Open settings", "Show Doctor");
    if (pick === "Open settings") await vscode.commands.executeCommand("workbench.action.openSettings", setting);
    else if (pick === "Show Doctor") await vscode.commands.executeCommand("fsharpened.doctor");
  };

  const problems = new Problems({
    onFshMissing: (tried) => {
      fshMissing = true;
      refreshStatus();
      void missingNotice("the fsh command line", "FSharpened diagnostics are off until it is found.", "fsharpened.fsh.path")(tried);
    },
    onResult: (_uri, _count, isBusy) => {
      busy = Math.max(0, busy + (isBusy ? 1 : -1));
      if (!isBusy) fshMissing = false;
      refreshStatus();
    },
    log: (line) => output.appendLine(line),
  });
  const formatting = new Formatting({
    log: (line) => output.appendLine(line),
    onMissing: (tried) => void missingNotice("Fantomas", "Formatting is unavailable until it is found.", "fsharpened.fantomas.path")(tried),
  });
  const languageServer = new LanguageServer(output, {
    onState: (s) => {
      server = s;
      output.appendLine(`language server: ${s}`);
      refreshStatus();
    },
    onMissing: (tried) => void missingNotice("FsAutoComplete", "Language features are unavailable until it is found.", "fsharpened.languageServer.path")(tried),
  });
  context.subscriptions.push(problems, formatting, languageServer);

  // --- language features -------------------------------------------------------------------------------------------
  context.subscriptions.push(
    vscode.languages.registerDocumentFormattingEditProvider(SELECTOR, formatting),
    vscode.languages.registerCodeActionsProvider(SELECTOR, problems, { providedCodeActionKinds: [vscode.CodeActionKind.QuickFix] }),
  );

  const analyzeVisible = (): void => {
    for (const e of vscode.window.visibleTextEditors) void problems.analyze(e.document);
  };

  context.subscriptions.push(
    vscode.workspace.onDidSaveTextDocument((doc) => {
      if (readSettings(doc.uri).diagnostics.runOnSave) void problems.analyze(doc);
    }),
    vscode.workspace.onDidOpenTextDocument((doc) => {
      if (readSettings(doc.uri).diagnostics.runOnOpen) void problems.analyze(doc);
    }),
    vscode.workspace.onDidCloseTextDocument((doc) => problems.clear(doc.uri)),
    vscode.workspace.onWillSaveTextDocument((e) => {
      if (e.document.languageId !== "fsharp" || !vscode.workspace.isTrusted) return;
      if (!readSettings(e.document.uri).fantomas.formatOnSave) return;
      e.waitUntil(formatting.edits(e.document).then((r) => r.edits));
    }),
    vscode.window.onDidChangeActiveTextEditor(refreshStatus),
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (!e.affectsConfiguration("fsharpened")) return;
      if (e.affectsConfiguration("fsharpened.fantomas")) formatting.reset();
      if (e.affectsConfiguration("fsharpened.languageServer")) void languageServer.restart().then(refreshStatus);
      if (e.affectsConfiguration("fsharpened.diagnostics") || e.affectsConfiguration("fsharpened.fsh")) {
        problems.clearAll();
        fshMissing = false;
        analyzeVisible();
      }
      refreshStatus();
    }),
    vscode.workspace.onDidGrantWorkspaceTrust(() => {
      void languageServer.start().then(refreshStatus);
      analyzeVisible();
      refreshStatus();
    }),
  );

  // --- commands ----------------------------------------------------------------------------------------------------
  const needTrust = (): boolean => {
    if (vscode.workspace.isTrusted) return true;
    void vscode.window.showWarningMessage("FSharpened starts local programs, so it is turned off until you trust this workspace.", "Manage workspace trust").then((p) => {
      if (p) void vscode.commands.executeCommand("workbench.trust.manage");
    });
    return false;
  };

  const probeVersion = async (id: ToolId, flag = "--version"): Promise<ToolFact> => {
    const { spec, resolution } = resolveFor(id, vscode.window.activeTextEditor?.document.uri);
    const hint = { languageServer: HINTS.languageServer, fantomas: HINTS.fantomas, fsh: HINTS.fsh, python: HINTS.python }[id];
    const fact: ToolFact = { label: spec.label, resolution, hint };
    if (resolution.found && vscode.workspace.isTrusted && id !== "fsh") {
      const r = await run(resolution.command, [...resolution.args, flag], { cwd: resolution.cwd ?? workspaceRoot(), timeoutMs: 15000 });
      if (r.code === 0) fact.version = tail(r.stdout || r.stderr, 1).slice(0, 80);
      else if (r.spawnError) fact.version = `could not run: ${r.spawnError}`;
    }
    return fact;
  };

  const doctor = async (): Promise<void> => {
    const tools = [await probeVersion("languageServer"), await probeVersion("fantomas"), await probeVersion("fsh"), await probeVersion("python")];
    let fshDoctor: string | undefined;
    const fsh = tools[2]?.resolution;
    if (fsh?.found && vscode.workspace.isTrusted) {
      const r = await run(fsh.command, [...fsh.args, "doctor"], { cwd: fsh.cwd ?? workspaceRoot(), timeoutMs: 30000 });
      fshDoctor = r.code === 0 ? r.stdout : `fsh doctor failed (${r.spawnError ?? String(r.code)}): ${tail(r.stderr, 4)}`;
    }
    const text = formatDoctor({
      extensionVersion: String(context.extension.packageJSON.version ?? "?"),
      trusted: vscode.workspace.isTrusted,
      workspaceRoot: workspaceRoot(),
      languageServer: server,
      tools,
      fshDoctor,
    });
    output.appendLine("");
    output.appendLine("=== FSharpened Doctor ===");
    output.appendLine(text);
    output.show(true);
  };

  context.subscriptions.push(
    vscode.commands.registerCommand("fsharpened.doctor", doctor),
    vscode.commands.registerCommand("fsharpened.showOutput", () => output.show(true)),
    vscode.commands.registerCommand("fsharpened.restartLanguageServer", async () => {
      if (!needTrust()) return;
      await languageServer.restart();
      refreshStatus();
    }),
    vscode.commands.registerCommand("fsharpened.analyzeFile", async () => {
      if (!needTrust()) return;
      const doc = vscode.window.activeTextEditor?.document;
      if (!doc || doc.languageId !== "fsharp") return void vscode.window.showInformationMessage("FSharpened: open an F# file first.");
      if (doc.isDirty) await doc.save();
      await problems.analyze(doc);
    }),
    vscode.commands.registerCommand("fsharpened.formatDocument", async () => {
      if (!needTrust()) return;
      const editor = vscode.window.activeTextEditor;
      if (!editor || editor.document.languageId !== "fsharp") return void vscode.window.showInformationMessage("FSharpened: open an F# file first.");
      const r = await formatting.edits(editor.document);
      if (r.error) return void vscode.window.showErrorMessage(`FSharpened: Fantomas could not format this file: ${r.error}`);
      if (r.edits.length > 0) await editor.edit((b) => r.edits.forEach((e) => b.replace(e.range, e.newText)));
    }),
    vscode.commands.registerCommand("fsharpened.runCi", async () => {
      if (!needTrust()) return;
      const root = workspaceRoot();
      if (!root || !realFs.isFile(path.join(root, CI_SCRIPT))) {
        return void vscode.window.showErrorMessage(`FSharpened: this folder has no ${CI_SCRIPT.replaceAll("\\", "/")}; open the FSharpened repository to run its local CI.`);
      }
      const { resolution } = resolveFor("python");
      if (!resolution.found) return void vscode.window.showErrorMessage(`FSharpened: Python 3 was not found. ${HINTS.python}`);
      // The interpreter is the terminal's process, with fixed arguments: no shell parses anything from the workspace.
      const terminal = vscode.window.createTerminal({
        name: "FSharpened CI",
        cwd: root,
        shellPath: resolution.command,
        shellArgs: [...resolution.args, ...ciArgs(readSettings().ciProfile)],
      });
      terminal.show();
    }),
    vscode.commands.registerCommand("fsharpened.showCiReport", async () => {
      const root = workspaceRoot();
      const report = root ? latestReport(root, realFs) : undefined;
      if (!report) return void vscode.window.showInformationMessage("FSharpened: no CI report yet. Run \"FSharpened: Run local CI\" first (reports are written to .fsharpened/ci/runs/).");
      await vscode.commands.executeCommand("markdown.showPreview", vscode.Uri.file(report)).then(undefined, () => vscode.window.showTextDocument(vscode.Uri.file(report)));
    }),
  );

  // --- start -------------------------------------------------------------------------------------------------------
  refreshStatus();
  output.appendLine(`FSharpened ${String(context.extension.packageJSON.version)} activated (workspace trusted: ${String(vscode.workspace.isTrusted)})`);
  if (vscode.workspace.isTrusted) {
    await languageServer.start();
    analyzeVisible();
  } else {
    server = "restricted";
    output.appendLine("Workspace is not trusted: no FSharpened tool will be started until it is.");
  }
  refreshStatus();
}

export function deactivate(): void {
  // Everything that holds a process is in context.subscriptions and is disposed by VS Code.
}
