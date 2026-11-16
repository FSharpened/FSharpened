import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { COMPILERS, normalize, originAllowed, sanitizeOptions, validate } from "../src/policy";

describe("allowlist stays in sync with the F# core", () => {
  it("every compiler id in Engines.fs is allowed, and nothing extra is", () => {
    const fs = readFileSync(new URL("../../tools/FSharpened.Ide/Engines.fs", import.meta.url), "utf8");
    const ids = [...fs.matchAll(/Id = "([^"]+)"/g)].map((m) => m[1]!).sort();
    expect(ids.length).toBeGreaterThan(5);
    expect(Object.keys(COMPILERS).sort()).toEqual(ids);
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
  it("drops options for .NET compilers", () => {
    const r = validate({ compiler: "dotnet90fsharpcoreclr", source: "printfn \"x\"", options: "--evil" });
    expect(r.ok && r.value.options).toBe("");
  });
});

describe("sanitizeOptions", () => {
  it("allows common flags", () => { expect(sanitizeOptions("-O2 -Wall -Wextra -std=c++20 -DFOO=1 -lm", "c++").ok).toBe(true); });
  it("rejects dangerous or unknown flags", () => {
    for (const bad of ["-fplugin=x.so", "-fuse-ld=gold", "-o /etc/x", "@file", "-include /etc/passwd", "-I/etc", "-Wl,--wrap=x", "-O2; rm -rf /"])
      expect(sanitizeOptions(bad, "c").ok, bad).toBe(false);
    expect(sanitizeOptions("-O2 ".repeat(100), "c").ok).toBe(false);
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
  it("maps a timeout", () => { expect(normalize({ code: 143, timedOut: true, buildResult: { code: 0 } }).timedOut).toBe(true); });
});

describe("originAllowed", () => {
  it("matches exactly", () => {
    expect(originAllowed("https://fsharpened.pages.dev", "https://fsharpened.pages.dev, https://x.dev")).toBe(true);
    expect(originAllowed("https://evil.pages.dev", "https://fsharpened.pages.dev")).toBe(false);
    expect(originAllowed(null, "https://fsharpened.pages.dev")).toBe(false);
  });
});
