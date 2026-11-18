import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { findModel } from "../src/models";
import { LIMITS } from "../src/policy";

// wrangler.jsonc allows comments; strip full-line ones before parsing.
const raw = readFileSync(new URL("../wrangler.jsonc", import.meta.url), "utf8").split("\n").filter((l) => !l.trim().startsWith("//")).join("\n");
const cfg = JSON.parse(raw) as { vars: Record<string, string>; ai?: { binding: string }; durable_objects: { bindings: { name: string }[] }; migrations: { new_sqlite_classes: string[] }[] };

describe("wrangler.jsonc as committed", () => {
  it("ships with AI off", () => expect(cfg.vars.AI_ENABLED).toBe("false"));
  it("names an allowlisted default model", () => expect(findModel(cfg.vars.DEFAULT_MODEL!)).toBeDefined());
  it("binds the model and the ledger, and ships no secret", () => {
    expect(cfg.ai?.binding).toBe("AI");
    expect(cfg.durable_objects.bindings.map((b) => b.name)).toContain("LEDGER");
    expect(cfg.migrations[0]!.new_sqlite_classes).toContain("BudgetLedger");
    expect(Object.keys(cfg.vars)).not.toContain("TOKEN_SECRET");
  });
  it("allows only https origins", () => {
    for (const o of cfg.vars.ALLOWED_ORIGINS!.split(",")) expect(o.trim()).toMatch(/^https:\/\//);
  });
});

const read = (rel: string) => readFileSync(new URL(rel, import.meta.url), "utf8");

describe("the pieces that live in other folders agree with this Worker", () => {
  it("the F# context windowing stays within this Worker's limits (and says the same numbers)", () => {
    const fs = read("../../src/FSharpened.Tooling/Completion/Context.fs");
    expect(Number(/WorkerPrefixLimit =\s*(\d+)/.exec(fs)?.[1])).toBe(LIMITS.prefix);
    expect(Number(/WorkerSuffixLimit =\s*(\d+)/.exec(fs)?.[1])).toBe(LIMITS.suffix);
  });
  it("the page's default endpoint host is allowed by the site's content security policy and by this Worker's origins", () => {
    const js = read("../../ide/web/src/aicomplete.js");
    const endpoint = /DEFAULT_ENDPOINT = "([^"]+)"/.exec(js)?.[1];
    expect(endpoint).toBeDefined();
    const host = new URL(endpoint!).origin;
    expect(read("../../site/static/_headers")).toContain("connect-src 'self' https://ide.fsharpened.workers.dev " + host);
    expect(new URL(endpoint!).pathname).toBe("/api/ai/complete");
  });
  it("the page's allowed origins are the ones the gateway allows", () => {
    const gw = /"ALLOWED_ORIGINS": "([^"]+)"/.exec(read("../../gateway/wrangler.jsonc"))?.[1];
    expect(cfg.vars.ALLOWED_ORIGINS).toBe(gw);
  });
});
