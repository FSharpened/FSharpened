// Pure request policy: what the gateway is willing to forward. Unit-tested; no Workers APIs.

/** Must stay in sync with tools/FSharpened.Ide/Engines.fs (a test enforces it). */
export const COMPILERS: Record<string, "fsharp" | "c" | "csharp" | "c++"> = {
  dotnet90fsharpcoreclr: "fsharp",
  dotnet80fsharpcoreclr: "fsharp",
  cg142: "c",
  cclang1910: "c",
  cg132: "c",
  dotnet90csharpcoreclr: "csharp",
  dotnet80csharpcoreclr: "csharp",
  g142: "c++",
  clang1910: "c++",
  g132: "c++",
};

export const LIMITS = { source: 65_536, stdin: 4_096, options: 200 } as const;

export interface RunRequest { compiler: string; language: string; options: string; source: string; stdin: string }

const FLAG = /^-(O[0-3sgz]?|W[a-z0-9=+-]*|std=[a-z0-9+]+|f[a-z0-9=-]+|m[a-z0-9=-]+|D[A-Za-z_][A-Za-z0-9_]*(=[A-Za-z0-9_.]*)?|g|pthread|lm|lpthread)$/;
const FORBIDDEN = /^-f(plugin|use-ld|passes|dump|file-prefix|sanitize-recover)/;

/** Compiler flags are allowlisted token by token. .NET compilers take no flags. */
export function sanitizeOptions(options: string, language: string): { ok: true; value: string } | { ok: false; error: string } {
  if (language === "fsharp" || language === "csharp") return { ok: true, value: "" };
  if (options.length > LIMITS.options) return { ok: false, error: "options too long" };
  const tokens = options.split(/\s+/).filter(Boolean);
  for (const t of tokens) if (!FLAG.test(t) || FORBIDDEN.test(t)) return { ok: false, error: "flag not allowed: " + t.slice(0, 40) };
  return { ok: true, value: tokens.join(" ") };
}

export function validate(body: unknown): { ok: true; value: RunRequest } | { ok: false; error: string } {
  if (typeof body !== "object" || body === null || Array.isArray(body)) return { ok: false, error: "body must be an object" };
  const b = body as Record<string, unknown>;
  const compiler = typeof b.compiler === "string" ? b.compiler : "";
  const lang = COMPILERS[compiler];
  if (!lang) return { ok: false, error: "unknown compiler" };
  if (typeof b.source !== "string" || b.source.length === 0) return { ok: false, error: "source required" };
  if (b.source.length > LIMITS.source) return { ok: false, error: "source too large" };
  const stdin = typeof b.stdin === "string" ? b.stdin : "";
  if (stdin.length > LIMITS.stdin) return { ok: false, error: "stdin too large" };
  const o = sanitizeOptions(typeof b.options === "string" ? b.options : "", lang);
  if (!o.ok) return o;
  return { ok: true, value: { compiler, language: lang, options: o.value, source: b.source, stdin } };
}

interface Line { text?: string }
export interface Upstream {
  code?: number; didExecute?: boolean; timedOut?: boolean; execTime?: number | string;
  stdout?: Line[]; stderr?: Line[];
  buildResult?: { code?: number; stdout?: Line[]; stderr?: Line[] };
}
const join = (l?: Line[]) => (l ?? []).map((x) => x.text ?? "").join("\n");

export interface RunResult {
  buildFailed: boolean; buildOutput: string; stdout: string; stderr: string; exitCode: number; timedOut: boolean; durationMs: number;
}

/** Compiler Explorer reports build failures as code -1 with a build result; normalise to one shape. */
export function normalize(u: Upstream): RunResult {
  const b = u.buildResult;
  const buildFailed = (b?.code ?? 0) !== 0;
  return {
    buildFailed,
    buildOutput: [join(b?.stdout), join(b?.stderr)].filter(Boolean).join("\n"),
    stdout: buildFailed ? "" : join(u.stdout),
    stderr: buildFailed ? "" : join(u.stderr),
    exitCode: typeof u.code === "number" ? u.code : -1,
    timedOut: u.timedOut === true,
    durationMs: Math.round(Number(u.execTime) || 0),
  };
}

export async function cacheKey(r: RunRequest): Promise<string> {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(JSON.stringify([r.compiler, r.options, r.source, r.stdin])));
  return [...new Uint8Array(d)].map((x) => x.toString(16).padStart(2, "0")).join("");
}

export function originAllowed(origin: string | null, allowed: string): boolean {
  return !!origin && allowed.split(",").map((s) => s.trim()).filter(Boolean).includes(origin);
}
