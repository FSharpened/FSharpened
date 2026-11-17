import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { COMPILERS, FLAG_LANGUAGES, normalize, normalizeWandbox, originAllowed, sanitizeOptions, validate } from "../src/policy";

/** The F# case name of a language, mapped to the id the gateway and the client share (Language.id in Languages.fs). */
const LANGUAGE_IDS: Record<string, string> = {
  FSharp: "fsharp", CSharp: "csharp", VisualBasic: "vb", JavaScript: "javascript", TypeScript: "typescript", Python: "python", Rust: "rust", Dart: "dart",
  C: "c", Cpp: "cpp", Go: "go", Java: "java", Ruby: "ruby", Lua: "lua", Php: "php", R: "r", Haskell: "haskell", OCaml: "ocaml",
};

const engines = readFileSync(new URL("../../tools/FSharpened.Ide/Engines.fs", import.meta.url), "utf8");
const languages = readFileSync(new URL("../../tools/FSharpened.Ide/Languages.fs", import.meta.url), "utf8");

describe("allowlist stays in sync with the F# core", () => {
  it("every compiler id in Engines.fs is allowed, and nothing extra is", () => {
    const ids = [...engines.matchAll(/Id = "([^"]+)"/g)].map((m) => m[1]!).sort();
    expect(ids.length).toBeGreaterThan(40);
    expect(Object.keys(COMPILERS).sort()).toEqual(ids);
  });
  it("every compiler is allowed for the language Engines.fs gives it", () => {
    const pairs = [...engines.matchAll(/Id = "([^"]+)"\s+Label = "[^"]*"\s+Language = (\w+)/g)].map((m) => [m[1]!, LANGUAGE_IDS[m[2]!]] as const);
    expect(pairs.length).toBe(Object.keys(COMPILERS).length);
    for (const [id, language] of pairs) expect(COMPILERS[id]?.language, id).toBe(language);
  });
  it("the language ids are exactly the ones the F# language table declares, and each has a compiler", () => {
    const declared = [...languages.matchAll(/info \w+ "([a-z]+)"/g)].map((m) => m[1]!).sort();
    expect(declared).toEqual(Object.values(LANGUAGE_IDS).sort());
    const served = new Set(Object.values(COMPILERS).map((c) => c.language));
    expect([...served].sort()).toEqual(declared);
  });
  it("only the languages the F# table marks as taking flags are allowed flags", () => {
    const flagged = [...languages.matchAll(/info \w+ "([a-z]+)" \[[^\]]*\] "[^"]*" "[^"]*" "[a-z]+" (true|false)/g)].filter((m) => m[2] === "true").map((m) => m[1]!).sort();
    expect(flagged).toEqual([...FLAG_LANGUAGES].sort());
  });
  it("routes each language to the backend that has it", () => {
    const backends = (language: string) => new Set(Object.values(COMPILERS).filter((c) => c.language === language).map((c) => c.backend));
    for (const l of ["javascript", "typescript", "php", "r"]) expect([...backends(l)], l).toEqual(["wandbox"]);
    for (const l of ["fsharp", "csharp", "vb", "python", "rust", "dart", "c", "cpp", "go", "java", "ruby", "lua", "haskell", "ocaml"]) expect([...backends(l)], l).toEqual(["ce"]);
  });
});

describe("validate", () => {
  const ok = { compiler: "cg142", source: "int main(void){return 0;}", options: "-O2 -Wall -std=c17", stdin: "" };
  it("accepts a normal request", () => { const r = validate(ok); expect(r.ok && r.value.language).toBe("c"); });
  it("rejects unknown compilers, empty and oversized input", () => {
    expect(validate({ ...ok, compiler: "evil" }).ok).toBe(false);
    expect(validate({ ...ok, source: "" }).ok).toBe(false);
    expect(validate({ ...ok, source: "x".repeat(70_000) }).ok).toBe(false);
    expect(validate({ ...ok, stdin: "x".repeat(5_000) }).ok).toBe(false);
    expect(validate(null).ok).toBe(false);
  });
  it("accepts a Rust request and keeps its flags", () => {
    const r = validate({ compiler: "r1990", source: "fn main() {}", options: "-Copt-level=2 --edition=2024", stdin: "" });
    expect(r.ok && r.value.language).toBe("rust");
    expect(r.ok && r.value.options).toBe("-Copt-level=2 --edition=2024");
  });
  it("drops options for .NET compilers", () => {
    const r = validate({ compiler: "dotnet90fsharpcoreclr", source: "printfn \"x\"", options: "--evil" });
    expect(r.ok && r.value.options).toBe("");
  });
  it("drops options for every language that takes no flags, and never lets them reach Wandbox", () => {
    for (const [compiler, entry] of Object.entries(COMPILERS)) {
      if ((FLAG_LANGUAGES as readonly string[]).includes(entry.language)) continue;
      const r = validate({ compiler, source: "x", options: "-O2 --evil", stdin: "" });
      expect(r.ok && r.value.options, compiler).toBe("");
    }
  });
  it("resolves the backend from the compiler id and ignores whatever language the client claims", () => {
    const js = validate({ compiler: "nodejs-20.17.0", language: "rust", source: "console.log(1)" });
    expect(js.ok && js.value).toMatchObject({ language: "javascript", backend: "wandbox" });
    const py = validate({ compiler: "python313", language: "javascript", source: "print(1)" });
    expect(py.ok && py.value).toMatchObject({ language: "python", backend: "ce" });
  });
  it("does not treat inherited object keys as compilers", () => {
    for (const compiler of ["constructor", "__proto__", "toString", "hasOwnProperty"]) expect(validate({ compiler, source: "x" }).ok, compiler).toBe(false);
  });
});

describe("sanitizeOptions", () => {
  it("allows common flags", () => { expect(sanitizeOptions("-O2 -Wall -Wextra -std=c++20 -DFOO=1 -lm", "cpp")).toEqual({ ok: true, value: "-O2 -Wall -Wextra -std=c++20 -DFOO=1 -lm" }); });
  it("rejects dangerous or unknown flags", () => {
    for (const bad of ["-fplugin=x.so", "-fuse-ld=gold", "-o /etc/x", "@file", "-include /etc/passwd", "-I/etc", "-Wl,--wrap=x", "-O2; rm -rf /"])
      expect(sanitizeOptions(bad, "c").ok, bad).toBe(false);
    expect(sanitizeOptions("-O2 ".repeat(100), "c").ok).toBe(false);
  });
});

describe("sanitizeOptions for rustc", () => {
  it("allows edition, optimisation, lint and codegen flags written as single tokens", () => {
    const goods = ["--edition=2021 -O -g -Copt-level=3 -Cdebuginfo=1 -Coverflow-checks=on -Cpanic=abort", "-Ccodegen-units=1 -Clto=thin -Ctarget-cpu=native -Ctarget-feature=+avx2,+fma", "-Wunused -Aclippy::all -Dwarnings --cap-lints=warn"];
    for (const good of goods) expect(sanitizeOptions(good, "rust").ok, good).toBe(true);
  });
  it("rejects everything that could touch the sandbox, the linker, the target or unstable internals", () => {
    for (const bad of [
      "-Clinker=/bin/sh", "-Clink-arg=-lfoo", "-Clink-args=x", "-Cllvm-args=-x", "-Cpasses=x", "-Csave-temps", "-Cextra-filename=x", "-Cincremental=/tmp/x",
      "-Zunpretty=hir", "-Zbuild-std", "--emit=asm", "--emit", "-o", "-o/etc/x", "--out-dir=/tmp", "-L/etc", "-lc", "--extern=x=/x", "--sysroot=/", "--print=sysroot",
      "--target=wasm32-unknown-unknown", "--cfg=x", "--remap-path-prefix=a=b", "--edition=1999", "-Copt-level=9", "-C", "opt-level=2", "@file", "-Wunused;rm", "--crate-type=cdylib",
    ])
      expect(sanitizeOptions(bad, "rust").ok, bad).toBe(false);
  });
  it("does not let C flags through for Rust, or Rust flags through for C", () => {
    expect(sanitizeOptions("-O2 -std=c17", "rust").ok).toBe(false);
    expect(sanitizeOptions("--edition=2021", "c").ok).toBe(false);
  });
});

describe("normalize", () => {
  it("maps a build failure", () => {
    const r = normalize({ code: -1, didExecute: false, stderr: [{ text: "Build failed" }], buildResult: { code: 1, stderr: [{ text: "<source>:1:1: error: x" }] } });
    expect(r.buildFailed).toBe(true); expect(r.buildOutput).toContain("error: x"); expect(r.stderr).toBe("");
  });
  it("maps a normal run with exit code and timing", () => {
    const r = normalize({ code: 3, didExecute: true, execTime: "28", stdout: [{ text: "42" }], buildResult: { code: 0 } });
    expect(r).toMatchObject({ buildFailed: false, stdout: "42", exitCode: 3, durationMs: 28, timedOut: false });
  });
  it("maps a Rust build failure and a panic exit", () => {
    const f = normalize({ code: -1, didExecute: false, buildResult: { code: 1, stderr: [{ text: "error[E0425]: cannot find value `y` in this scope" }, { text: " --> <source>:3:20" }] } });
    expect(f.buildFailed).toBe(true); expect(f.buildOutput).toContain("E0425");
    const p = normalize({ code: 101, didExecute: true, execTime: "12", stdout: [{ text: "start" }], stderr: [{ text: "thread 'main' (2) panicked at /app/example.rs:5:21:" }], buildResult: { code: 0 } });
    expect(p).toMatchObject({ buildFailed: false, exitCode: 101, stdout: "start" }); expect(p.stderr).toContain("panicked");
  });
  it("maps a timeout", () => { expect(normalize({ code: 143, timedOut: true, buildResult: { code: 0 } }).timedOut).toBe(true); });
});

describe("originAllowed", () => {
  it("matches exactly", () => {
    expect(originAllowed("https://fsharpened.pages.dev", "https://fsharpened.pages.dev, https://x.dev")).toBe(true);
    expect(originAllowed("https://evil.pages.dev", "https://fsharpened.pages.dev")).toBe(false);
    expect(originAllowed(null, "https://fsharpened.pages.dev")).toBe(false);
  });
});

describe("normalizeWandbox", () => {
  it("maps a clean run, using the wall time the gateway measured", () => {
    const r = normalizeWandbox({ status: "0", program_output: "hi\n", program_message: "hi\n" } as never, 812.4);
    expect(r).toMatchObject({ buildFailed: false, stdout: "hi\n", stderr: "", exitCode: 0, timedOut: false, durationMs: 812 });
  });
  it("treats a non-zero exit with program output as a run failure, not a build failure", () => {
    const r = normalizeWandbox({ status: "1", compiler_message: "warning: x", program_output: "start\n", program_error: "Error: boom\n" }, 5);
    expect(r).toMatchObject({ buildFailed: false, exitCode: 1, stdout: "start\n", stderr: "Error: boom\n" });
  });
  it("treats a non-zero exit that only the compiler spoke about as a build failure", () => {
    const r = normalizeWandbox({ status: "1", compiler_output: "", compiler_error: "prog.ts(1,7): error TS2322: Type 'string' is not assignable to type 'number'." }, 5);
    expect(r.buildFailed).toBe(true);
    expect(r.buildOutput).toContain("TS2322");
    expect(r).toMatchObject({ stdout: "", stderr: "", exitCode: 1 });
  });
  it("keeps PHP's errors on stdout where PHP writes them", () => {
    const r = normalizeWandbox({ status: "255", program_output: "1PHP Fatal error:  Uncaught Exception: boom in /x.php:3\n" }, 5);
    expect(r).toMatchObject({ buildFailed: false, exitCode: 255 });
    expect(r.stdout).toContain("Fatal error");
  });
  it("reports a killed program as a timeout", () => {
    const r = normalizeWandbox({ signal: "Killed", program_output: "partial" }, 10_000);
    expect(r).toMatchObject({ timedOut: true, exitCode: 137, buildFailed: false });
  });
  it("copes with numeric status, a missing status, and empty responses", () => {
    expect(normalizeWandbox({ status: 3 }, 1).exitCode).toBe(3);
    expect(normalizeWandbox({}, 1)).toMatchObject({ exitCode: -1, buildFailed: false, stdout: "", stderr: "" });
  });
});
