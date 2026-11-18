// Parser for the report `fsh analyze --format json` prints (schema fsharpened.diagnostics/1, produced by
// src/FSharpened.Diagnostics/Report.fs). Pure: no vscode import, so the node tests cover it.
//
// The input is data from a child process, never trusted: every field is validated and anything unexpected rejects the
// whole report with a reason, instead of half-working.

export type Severity = "error" | "warning" | "info" | "hidden";

/** Zero-based lines and columns (UTF-16 code units), end exclusive: what VS Code uses. */
export interface Range0 {
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
}

export interface Edit {
  range: Range0;
  newText: string;
}

export interface Fix {
  title: string;
  edits: Edit[];
}

export interface Finding {
  code: string;
  severity: Severity;
  message: string;
  /** Path as the tool printed it; null for diagnostics that belong to no file. */
  file: string | null;
  range: Range0 | null;
  construct: string | null;
  /** Only an http(s) URL survives parsing. */
  docs: string | null;
  fix: Fix | null;
  context: Record<string, string>;
}

export type ParseResult = { ok: true; tool: string; findings: Finding[] } | { ok: false; error: string };

export const SCHEMA_PREFIX = "fsharpened.diagnostics/";
export const SUPPORTED_MAJOR = "1";
export const MAX_REPORT_BYTES = 8 * 1024 * 1024;
const MAX_FINDINGS = 5000;
const MAX_EDITS = 100;

const SEVERITIES: readonly string[] = ["error", "warning", "info", "hidden"];

class Bad extends Error {}

function obj(v: unknown, what: string): Record<string, unknown> {
  if (typeof v !== "object" || v === null || Array.isArray(v)) throw new Bad(`${what} must be an object`);
  return v as Record<string, unknown>;
}

function str(v: unknown, what: string): string {
  if (typeof v !== "string") throw new Bad(`${what} must be a string`);
  return v;
}

function optStr(v: unknown, what: string): string | null {
  if (v === null || v === undefined) return null;
  return str(v, what);
}

function int(v: unknown, what: string): number {
  if (typeof v !== "number" || !Number.isInteger(v) || v < 0 || v > 100_000_000) throw new Bad(`${what} must be a small non-negative integer`);
  return v;
}

/** The tool's lines are 1-based (F# compiler convention); VS Code's are 0-based. Columns are 0-based in both. */
function range(v: unknown, what: string): Range0 {
  const r = obj(v, what);
  const startLine = int(r["startLine"], `${what}.startLine`);
  const endLine = int(r["endLine"], `${what}.endLine`);
  if (startLine < 1 || endLine < 1) throw new Bad(`${what}: lines are 1-based`);
  const out: Range0 = {
    startLine: startLine - 1,
    startColumn: int(r["startColumn"], `${what}.startColumn`),
    endLine: endLine - 1,
    endColumn: int(r["endColumn"], `${what}.endColumn`),
  };
  if (out.endLine < out.startLine || (out.endLine === out.startLine && out.endColumn < out.startColumn)) {
    throw new Bad(`${what} ends before it starts`);
  }
  return out;
}

function safeUrl(v: string | null): string | null {
  if (v === null) return null;
  try {
    const u = new URL(v);
    return u.protocol === "https:" || u.protocol === "http:" ? u.toString() : null;
  } catch {
    return null;
  }
}

function finding(v: unknown, i: number): Finding {
  const at = `diagnostics[${i}]`;
  const o = obj(v, at);
  const severity = str(o["severity"], `${at}.severity`);
  if (!SEVERITIES.includes(severity)) throw new Bad(`${at}.severity "${severity}" is not one of ${SEVERITIES.join(", ")}`);

  let fix: Fix | null = null;
  const rawFix = o["fix"];
  if (rawFix !== null && rawFix !== undefined) {
    const f = obj(rawFix, `${at}.fix`);
    const edits = f["edits"];
    if (!Array.isArray(edits) || edits.length > MAX_EDITS) throw new Bad(`${at}.fix.edits must be an array of at most ${MAX_EDITS}`);
    fix = {
      title: str(f["title"], `${at}.fix.title`),
      edits: edits.map((e, j) => {
        const eo = obj(e, `${at}.fix.edits[${j}]`);
        return { range: range(eo["range"], `${at}.fix.edits[${j}].range`), newText: str(eo["newText"], `${at}.fix.edits[${j}].newText`) };
      }),
    };
  }

  const context: Record<string, string> = {};
  const rawCtx = o["context"];
  if (rawCtx !== null && rawCtx !== undefined) {
    for (const [k, val] of Object.entries(obj(rawCtx, `${at}.context`))) context[k] = str(val, `${at}.context.${k}`);
  }

  const rawRange = o["range"];
  return {
    code: str(o["code"], `${at}.code`),
    severity: severity as Severity,
    message: str(o["message"], `${at}.message`),
    file: optStr(o["file"], `${at}.file`),
    range: rawRange === null || rawRange === undefined ? null : range(rawRange, `${at}.range`),
    construct: optStr(o["construct"], `${at}.construct`),
    docs: safeUrl(optStr(o["docs"], `${at}.docs`)),
    fix,
    context,
  };
}

export function parseReport(text: string): ParseResult {
  if (Buffer.byteLength(text, "utf8") > MAX_REPORT_BYTES) return { ok: false, error: `report is larger than ${MAX_REPORT_BYTES} bytes` };
  let doc: unknown;
  try {
    doc = JSON.parse(text.charCodeAt(0) === 0xfeff ? text.slice(1) : text);
  } catch (e) {
    return { ok: false, error: `output is not JSON (${(e as Error).message})` };
  }
  try {
    const o = obj(doc, "report");
    const schema = str(o["schema"], "schema");
    if (!schema.startsWith(SCHEMA_PREFIX)) throw new Bad(`unknown schema "${schema}"`);
    const major = schema.slice(SCHEMA_PREFIX.length).split(".")[0];
    if (major !== SUPPORTED_MAJOR) throw new Bad(`schema "${schema}" is not supported (this extension reads ${SCHEMA_PREFIX}${SUPPORTED_MAJOR}); update the extension or fsh`);
    const list = o["diagnostics"];
    if (!Array.isArray(list)) throw new Bad("diagnostics must be an array");
    if (list.length > MAX_FINDINGS) throw new Bad(`more than ${MAX_FINDINGS} diagnostics`);
    return { ok: true, tool: typeof o["tool"] === "string" ? o["tool"] : "fsh", findings: list.map(finding) };
  } catch (e) {
    if (e instanceof Bad) return { ok: false, error: e.message };
    throw e;
  }
}
