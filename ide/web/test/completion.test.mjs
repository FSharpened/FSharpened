// The AI completion chain (F#, FSharpened.Tooling.Completion) compiled by Fable, run with a mocked network.
// No real endpoint is ever called: `send` is a test double.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";

const entry = new URL("../fable_out/CompletionApi.js", import.meta.url);
const built = existsSync(entry);
const C = built ? await import(entry.href) : null;
const skip = built ? false : "Fable output missing: run `npm run fable` in ide/web first";

const ENDPOINT = "https://ai.example/api/ai/complete";
const reply = (body, status = 200) => async () => ({ status, body: JSON.stringify(body) });
const good = { ok: true, source: "workers-ai", degraded: null, model: "m", cached: false, candidates: [{ text: 'ntfn "hi"', kind: "inline", confidence: 0.5 }] };
const run = (send, over = {}) => {
  const a = { path: "a.fs", language: "fsharp", text: "let x = 1\n  pri", cursor: 15, maxContext: 6000, endpoint: ENDPOINT, timeoutMs: 1000, snippets: [], ...over };
  return C.run(a.path, a.language, a.text, a.cursor, a.maxContext, a.endpoint, a.timeoutMs, a.snippets, send);
};

test("endpoint rules", { skip }, () => {
  assert.equal(C.endpointOk(ENDPOINT), true);
  assert.equal(C.endpointOk("http://localhost:8791/api/ai/complete"), true);
  for (const bad of ["", "http://ai.example/x", "https://u:p@ai.example/x", "javascript:alert(1)", "https://ai.example/ x"]) assert.equal(C.endpointOk(bad), false, bad);
});

test("a Workers AI answer wins and carries its source", { skip }, async () => {
  const seen = [];
  const r = await run(async (url, body, ms) => { seen.push({ url, body: JSON.parse(body), ms }); return { status: 200, body: JSON.stringify(good) }; });
  assert.equal(r.source, "workers-ai");
  assert.equal(r.candidates[0].text, 'ntfn "hi"');
  assert.equal(seen.length, 1);
  assert.equal(seen[0].url, ENDPOINT);
  assert.equal(seen[0].ms, 1000);
  assert.deepEqual(Object.keys(seen[0].body), ["language", "prefix", "suffix", "path"]);
});

test("every degraded answer falls back to the static provider with the reason", { skip }, async () => {
  for (const [reason, text] of [["budget", "over budget, retry in 42 s"], ["disabled", "off"], ["timeout", "timed out"], ["error", "error"]]) {
    const r = await run(reply({ ok: true, source: "static", degraded: reason, retryAfterSeconds: 42, candidates: [] }));
    assert.equal(r.source, "static");
    assert.equal(r.candidates[0].kind, "keyword");
    assert.equal(r.skipped[0], "workers-ai: " + text);
  }
});

test("a transport timeout, a network error, an HTTP error and a throwing send all degrade", { skip }, async () => {
  for (const [send, why] of [
    [async () => ({ timedOut: true }), "workers-ai: timed out"],
    [async () => ({ error: "offline" }), "workers-ai: offline"],
    [async () => ({ status: 503, body: "" }), "workers-ai: HTTP 503"],
    [async () => ({ status: 429, body: "" }), "workers-ai: over budget"],
    [async () => { throw new Error("kaboom"); }, "workers-ai: kaboom"],
    [async () => ({ status: 200, body: "<html>" }), "workers-ai: bad response: invalid json"],
  ]) {
    const r = await run(send);
    assert.equal(r.source, "static", why);
    assert.ok(r.skipped[0].startsWith(why.split(":")[0]), why);
    assert.ok(r.skipped[0].includes(why.slice("workers-ai: ".length)) || r.skipped[0].startsWith("workers-ai: bad response"), `${r.skipped[0]} vs ${why}`);
  }
});

test("with no endpoint nothing is sent and the static provider answers (and snippets are offered)", { skip }, async () => {
  let called = 0;
  const r = await run(async () => { called++; return { status: 200, body: "{}" }; }, { endpoint: "", snippets: [{ label: "print", body: "printfn \"$1\"", languages: ["fsharp"] }] });
  assert.equal(called, 0);
  assert.equal(r.source, "static");
  assert.ok(r.candidates.some((c) => c.kind === "snippet"));
});

test("a bad endpoint is refused before any request", { skip }, async () => {
  let called = 0;
  const r = await run(async () => { called++; return { status: 200, body: "{}" }; }, { endpoint: "http://ai.example/x" });
  assert.equal(called, 0);
  assert.equal(r.source, "static");
});

test("text that looks like a secret is withheld from the network", { skip }, async () => {
  let called = 0;
  const key = "AK" + "IA" + "A".repeat(16);
  const text = `let k = "${key}"\n  pri`;
  const r = await run(async () => { called++; return { status: 200, body: JSON.stringify(good) }; }, { text, cursor: text.length });
  assert.equal(called, 0);
  assert.match(r.withheld, /looks like a secret/);
  assert.equal(r.source, "static");
});

test("a sensitive file is withheld", { skip }, async () => {
  let called = 0;
  const r = await run(async () => { called++; return { status: 200, body: JSON.stringify(good) }; }, { path: ".env", text: "A=1\nB", cursor: 5 });
  assert.equal(called, 0);
  assert.match(r.withheld, /usually holds secrets/);
});

test("only a window of a long file is sent", { skip }, async () => {
  let sent;
  const text = "x\n".repeat(20000);
  await run(async (_u, body) => { sent = JSON.parse(body); return { status: 200, body: JSON.stringify(good) }; }, { text, cursor: 20000, maxContext: 1000 });
  assert.ok(sent.prefix.length + sent.suffix.length <= 1000);
});

test("model text with control and bidi characters is cleaned", { skip }, async () => {
  const r = await run(reply({ ...good, candidates: [{ text: "a\u0000b\u202ec", kind: "inline", confidence: 3 }] }));
  assert.equal(r.candidates[0].text, "abc");
  assert.equal(r.candidates[0].confidence, 1);
});
