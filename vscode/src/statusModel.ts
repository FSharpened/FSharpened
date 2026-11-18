// What the status bar item says. Pure, so the wording is tested.

export type ServerState = "off" | "restricted" | "starting" | "running" | "missing" | "error";

export interface StatusInput {
  trusted: boolean;
  server: ServerState;
  /** Findings from the last analysis of the active file, or undefined when none was run. */
  findings: number | undefined;
  /** fsh could not be found (diagnostics unavailable). */
  fshMissing: boolean;
  busy: boolean;
}

export interface StatusView {
  text: string;
  tooltip: string;
  /** "warning" and "error" map to the theme's status bar colours. */
  level: "normal" | "warning" | "error";
}

export function describeStatus(s: StatusInput): StatusView {
  if (!s.trusted) {
    return {
      text: "$(shield) FSharpened: restricted",
      tooltip: "Workspace is not trusted: no FSharpened tool is started. Trust the workspace to enable the language server, Fantomas and diagnostics. Click for the Doctor report.",
      level: "warning",
    };
  }
  const parts: string[] = [];
  let level: StatusView["level"] = "normal";
  const lines: string[] = [];

  switch (s.server) {
    case "running":
      lines.push("Language server: running");
      break;
    case "starting":
      parts.push("starting");
      lines.push("Language server: starting");
      break;
    case "missing":
      parts.push("no language server");
      lines.push("Language server: FsAutoComplete not found (see Doctor)");
      level = "warning";
      break;
    case "error":
      parts.push("language server failed");
      lines.push("Language server: failed to start (see the FSharpened output)");
      level = "error";
      break;
    case "off":
      lines.push("Language server: off (fsharpened.languageServer.enabled)");
      break;
    case "restricted":
      lines.push("Language server: waiting for workspace trust");
      break;
  }

  if (s.fshMissing) {
    parts.push("no fsh");
    lines.push("Diagnostics: the fsh CLI was not found (see Doctor)");
    if (level === "normal") level = "warning";
  } else if (s.findings !== undefined) {
    parts.push(s.findings === 0 ? "no findings" : `${s.findings} finding${s.findings === 1 ? "" : "s"}`);
    lines.push(`Diagnostics: ${s.findings} in the active file`);
  }

  const icon = s.busy ? "$(sync~spin)" : level === "error" ? "$(error)" : level === "warning" ? "$(warning)" : "$(check)";
  return {
    text: parts.length === 0 ? `${icon} FSharpened` : `${icon} FSharpened: ${parts.join(", ")}`,
    tooltip: `${lines.join("\n")}\nClick for the Doctor report.`,
    level,
  };
}
