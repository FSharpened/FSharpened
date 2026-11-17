// Pure request policy: what the gateway is willing to forward. Unit-tested; no Workers APIs.

/** Where a language really runs. Compiler Explorer is the default; Wandbox serves what it does not have (ADR-0020). */
export type Backend = "ce" | "wandbox";

export type LanguageId =
  | "fsharp" | "csharp" | "vb" | "javascript" | "typescript" | "python" | "rust" | "dart"
  | "c" | "cpp" | "go" | "java" | "ruby" | "lua" | "php" | "r" | "haskell" | "ocaml";

/** Must stay in sync with tools/FSharpened.Ide/Engines.fs: same ids, same languages (a test enforces it). */
export const COMPILERS: Record<string, { language: LanguageId; backend: Backend }> = {
  dotnet90fsharpcoreclr: { language: "fsharp", backend: "ce" },
  dotnet80fsharpcoreclr: { language: "fsharp", backend: "ce" },
  dotnet90csharpcoreclr: { language: "csharp", backend: "ce" },
  dotnet80csharpcoreclr: { language: "csharp", backend: "ce" },
  dotnet90vbcoreclr: { language: "vb", backend: "ce" },
  dotnet80vbcoreclr: { language: "vb", backend: "ce" },
  "nodejs-20.17.0": { language: "javascript", backend: "wandbox" },
  "nodejs-18.20.4": { language: "javascript", backend: "wandbox" },
  "typescript-5.6.2": { language: "typescript", backend: "wandbox" },
  python314: { language: "python", backend: "ce" },
  python313: { language: "python", backend: "ce" },
  python312: { language: "python", backend: "ce" },
  r1990: { language: "rust", backend: "ce" },
  r1960: { language: "rust", backend: "ce" },
  r1900: { language: "rust", backend: "ce" },
  dart3136: { language: "dart", backend: "ce" },
  dart3122: { language: "dart", backend: "ce" },
  dart3116: { language: "dart", backend: "ce" },
  cg142: { language: "c", backend: "ce" },
  cclang1910: { language: "c", backend: "ce" },
  cg132: { language: "c", backend: "ce" },
  g142: { language: "cpp", backend: "ce" },
  clang1910: { language: "cpp", backend: "ce" },
  g132: { language: "cpp", backend: "ce" },
  gl1260: { language: "go", backend: "ce" },
  gl1257: { language: "go", backend: "ce" },
  gl12413: { language: "go", backend: "ce" },
  java2700: { language: "java", backend: "ce" },
  java2502: { language: "java", backend: "ce" },
  java2102: { language: "java", backend: "ce" },
  ruby405: { language: "ruby", backend: "ce" },
  ruby347: { language: "ruby", backend: "ce" },
  ruby334: { language: "ruby", backend: "ce" },
  lua550: { language: "lua", backend: "ce" },
  lua547: { language: "lua", backend: "ce" },
  lua536: { language: "lua", backend: "ce" },
  "php-8.3.12": { language: "php", backend: "wandbox" },
  "php-8.2.24": { language: "php", backend: "wandbox" },
  "r-4.4.1": { language: "r", backend: "wandbox" },
  ghc9122: { language: "haskell", backend: "ce" },
  ghc9102: { language: "haskell", backend: "ce" },
  ghc984: { language: "haskell", backend: "ce" },
  ocaml5502: { language: "ocaml", backend: "ce" },
  ocaml5401: { language: "ocaml", backend: "ce" },
  ocaml5300: { language: "ocaml", backend: "ce" },
};

/** Only these languages take compiler flags, each with its own allowlist. Every other language runs as written. */
export const FLAG_LANGUAGES: readonly LanguageId[] = ["c", "cpp", "rust"];

export const LIMITS = { source: 65_536, stdin: 4_096, options: 200 } as const;

export interface RunRequest { compiler: string; language: LanguageId; backend: Backend; options: string; source: string; stdin: string }

const FLAG = /^-(O[0-3sgz]?|W[a-z0-9=+-]*|std=[a-z0-9+]+|f[a-z0-9=-]+|m[a-z0-9=-]+|D[A-Za-z_][A-Za-z0-9_]*(=[A-Za-z0-9_.]*)?|g|pthread|lm|lpthread)$/;
const FORBIDDEN = /^-f(plugin|use-ld|passes|dump|file-prefix|sanitize-recover)/;

// rustc takes flags joined to their value (-Copt-level=2, not -C opt-level=2) so each token can be checked on its own.
// Everything that could read or write the sandbox, pick a linker, load code, change the target or reach unstable
// internals (-Z, --emit, -o, -L, -l, --extern, --sysroot, -Clinker, -Clink-arg, -Cllvm-args, -Cpasses, --print, --target)
// is simply absent from the allowlist.
const RUST_FLAG = new RegExp(
  "^(?:" + [
    "--edition=20(?:15|18|21|24)",
    "-O",
    "-g",
    "--cap-lints=(?:allow|warn|deny|forbid)",
    "-[WADF][A-Za-z0-9_:]+",
    "-Copt-level=(?:[0-3sz])",
    "-Cdebuginfo=[0-2]",
    "-Coverflow-checks=(?:on|off|yes|no)",
    "-Cdebug-assertions=(?:on|off|yes|no)",
    "-Cpanic=(?:unwind|abort)",
    "-Ccodegen-units=[0-9]{1,2}",
    "-Clto=(?:off|thin|fat|true|false|yes|no)",
    "-Cstrip=(?:none|debuginfo|symbols)",
    "-Ctarget-cpu=[a-z0-9_-]{1,30}",
    "-Ctarget-feature=[+-][a-z0-9_.]{1,20}(?:,[+-][a-z0-9_.]{1,20}){0,10}",
  ].join("|") + ")$",
);

/** Compiler flags are allowlisted token by token. .NET compilers take no flags. */
export function sanitizeOptions(options: string, language: string): { ok: true; value: string } | { ok: false; error: string } {
  if (!FLAG_LANGUAGES.includes(language as LanguageId)) return { ok: true, value: "" };
  if (options.length > LIMITS.options) return { ok: false, error: "options too long" };
  const tokens = options.split(/\s+/).filter(Boolean);
  const allowed = language === "rust" ? (t: string) => RUST_FLAG.test(t) : (t: string) => FLAG.test(t) && !FORBIDDEN.test(t);
  for (const t of tokens) if (!allowed(t)) return { ok: false, error: "flag not allowed: " + t.slice(0, 40) };
  return { ok: true, value: tokens.join(" ") };
}

export function validate(body: unknown): { ok: true; value: RunRequest } | { ok: false; error: string } {
  if (typeof body !== "object" || body === null || Array.isArray(body)) return { ok: false, error: "body must be an object" };
  const b = body as Record<string, unknown>;
  const compiler = typeof b.compiler === "string" ? b.compiler : "";
  const entry = Object.hasOwn(COMPILERS, compiler) ? COMPILERS[compiler] : undefined;
  if (!entry) return { ok: false, error: "unknown compiler" };
  if (typeof b.source !== "string" || b.source.length === 0) return { ok: false, error: "source required" };
  if (b.source.length > LIMITS.source) return { ok: false, error: "source too large" };
  const stdin = typeof b.stdin === "string" ? b.stdin : "";
  if (stdin.length > LIMITS.stdin) return { ok: false, error: "stdin too large" };
  const o = sanitizeOptions(typeof b.options === "string" ? b.options : "", entry.language);
  if (!o.ok) return o;
  return { ok: true, value: { compiler, language: entry.language, backend: entry.backend, options: o.value, source: b.source, stdin } };
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

/** The subset of Wandbox's POST /api/compile.json response the gateway reads. */
export interface WandboxUpstream {
  status?: string | number; signal?: string;
  compiler_output?: string; compiler_error?: string; compiler_message?: string;
  program_output?: string; program_error?: string;
}

/**
 * Wandbox has no separate build status, so a build failure is inferred: the run never produced any output of its own,
 * it exited non-zero, and the compiler said something. Interpreted languages (Node.js, PHP, R) have no compile step, so
 * for them the compiler fields are empty and a failing script is always a run failure. PHP prints its errors on
 * stdout and exits 255; the client reads that (Protocol.interpret), so nothing is rewritten here.
 */
export function normalizeWandbox(u: WandboxUpstream, durationMs: number): RunResult {
  const compiler = u.compiler_message ?? [u.compiler_output, u.compiler_error].filter(Boolean).join("\n");
  const stdout = u.program_output ?? "";
  const stderr = u.program_error ?? "";
  const killed = /kill|timeout|timed out/i.test(u.signal ?? "");
  const status = u.status === undefined || u.status === "" ? NaN : Number(u.status);
  const exitCode = Number.isFinite(status) ? status : killed ? 137 : -1;
  const buildFailed = !killed && exitCode !== 0 && compiler.trim() !== "" && stdout === "" && stderr === "";
  return {
    buildFailed,
    buildOutput: compiler,
    stdout: buildFailed ? "" : stdout,
    stderr: buildFailed ? "" : stderr,
    exitCode,
    timedOut: killed,
    durationMs: Math.round(durationMs),
  };
}

export async function cacheKey(r: RunRequest): Promise<string> {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(JSON.stringify([r.compiler, r.options, r.source, r.stdin])));
  return [...new Uint8Array(d)].map((x) => x.toString(16).padStart(2, "0")).join("");
}

export function originAllowed(origin: string | null, allowed: string): boolean {
  return !!origin && allowed.split(",").map((s) => s.trim()).filter(Boolean).includes(origin);
}
