// The page's opt-in AI completion controller (src/aicomplete.js) with the real Fable-compiled chain and a mocked network.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";

const built = existsSync(new URL("../fable_out/CompletionApi.js", import.meta.url));
const skip = built ? false : "Fable output missing: run `npm run fable` in ide/web first";
const { createAiCompletion } = built ? await import("../src/aicomplete.js") : {};

const memory = () => { const m = new Map(); return { getItem: (k) => m.get(k) ?? null, setItem: (k, v) => m.set(k, String(v)), m }; };
const good = { ok: true, source: "workers-ai", degraded: null, model: "m", cached: false, candidates: [{ text: "ntfn", kind: "inline", confidence: 0.5 }] };

function setup({ text = "let a = 1\n  pri", path = "a.fs", fetchImpl } = {}) {
  const storage = memory(), toasts = [], ghosts = [], fetches = [];
  const editor = { context: () => ({ text, cursor: text.length }), showGhost: (t) => ghosts.push(t), acceptGhost: () => true, clearGhost: () => ghosts.push(null) };
  const f = fetchImpl ?? (async () => ({ status: 200, text: async () => JSON.stringify(good) }));
  const ai = createAiCompletion({
    storage, editor, toast: (m) => toasts.push(m), snippets: () => [], activeLanguage: () => "fsharp", activePath: () => path, hasFile: () => true, announce: (m) => toasts.push(m),
    fetchImpl: async (url, init) => { fetches.push({ url, init }); return f(url, init); },
  });
  return { ai, storage, toasts, ghosts, fetches };
}

test("it is off by default and does not touch the network", { skip }, async () => {
  const { ai, fetches, toasts, ghosts } = setup();
  assert.equal(ai.state.enabled, false);
  const r = await ai.request();
  assert.equal(r.status, "off");
  assert.equal(fetches.length, 0);
  assert.equal(ghosts.length, 0);
  assert.match(toasts[0], /off/);
});

test("once enabled it asks the endpoint with credentials omitted and shows the suggestion", { skip }, async () => {
  const { ai, fetches, ghosts } = setup();
  assert.equal(ai.setEndpoint("https://ai.example/api/ai/complete"), "");
  ai.setEnabled(true);
  const r = await ai.request();
  assert.equal(r.status, "shown");
  assert.deepEqual(ghosts, ["ntfn"]);
  assert.equal(fetches.length, 1);
  assert.equal(fetches[0].url, "https://ai.example/api/ai/complete");
  assert.equal(fetches[0].init.credentials, "omit");
  assert.equal(fetches[0].init.method, "POST");
});

test("the setting and endpoint persist, and a damaged stored endpoint is ignored", { skip }, () => {
  const a = setup();
  a.ai.setEnabled(true);
  a.ai.setEndpoint("https://ai.example/x");
  const stored = a.storage.getItem("fsharpened.ide.ai.v1");
  assert.deepEqual(JSON.parse(stored), { enabled: true, endpoint: "https://ai.example/x" });
  const b = createAiCompletion({ storage: { getItem: () => JSON.stringify({ enabled: true, endpoint: "javascript:alert(1)" }), setItem() {} }, editor: {}, toast() {}, snippets: () => [], activeLanguage: () => "", activePath: () => "", hasFile: () => true });
  assert.equal(b.state.enabled, true);
  assert.match(b.state.endpoint, /^https:\/\/ai\.fsharpened\.workers\.dev\//);
  const c = createAiCompletion({ storage: { getItem: () => "not json", setItem() {} }, editor: {}, toast() {}, snippets: () => [], activeLanguage: () => "", activePath: () => "", hasFile: () => true });
  assert.equal(c.state.enabled, false);
});

test("an endpoint that is not https (or localhost http) is refused", { skip }, () => {
  const { ai } = setup();
  assert.notEqual(ai.setEndpoint("http://ai.example/x"), "");
  assert.notEqual(ai.setEndpoint("ftp://x"), "");
  assert.equal(ai.setEndpoint("http://localhost:8791/api/ai/complete"), "");
});

test("an error from the network degrades to the built-in keywords", { skip }, async () => {
  const { ai, ghosts, toasts } = setup({ fetchImpl: async () => { throw new TypeError("offline"); } });
  ai.setEnabled(true);
  const r = await ai.request();
  assert.equal(r.status, "shown");
  assert.deepEqual(ghosts, ["ntfn"]); // from the static provider: "pri" continues to "printfn"
  assert.equal(r.result.source, "static");
  assert.match(toasts.at(-1), /built-in keyword list/);
});

test("cancelling while a request is in flight shows nothing", { skip }, async () => {
  const { ai, ghosts } = setup({ fetchImpl: (url, init) => new Promise((_, reject) => init.signal.addEventListener("abort", () => reject(Object.assign(new Error("aborted"), { name: "AbortError" })))) });
  ai.setEnabled(true);
  const p = ai.request();
  setTimeout(() => ai.cancel(), 20);
  const r = await p;
  assert.equal(r.status, "superseded");
  assert.deepEqual(ghosts, []);
});

test("a newer request supersedes an older one", { skip }, async () => {
  let n = 0;
  const { ai, ghosts } = setup({ fetchImpl: async () => { const mine = ++n; if (mine === 1) await new Promise((r) => setTimeout(r, 50)); return { status: 200, text: async () => JSON.stringify({ ...good, candidates: [{ text: "r" + mine, kind: "inline", confidence: 0.5 }] }) }; } });
  ai.setEnabled(true);
  const first = ai.request();
  const second = ai.request();
  const [a, b] = await Promise.all([first, second]);
  assert.equal(a.status, "superseded");
  assert.equal(b.status, "shown");
  assert.deepEqual(ghosts, ["r2"]);
});

test("text that looks like a secret is never sent", { skip }, async () => {
  const key = "AK" + "IA" + "A".repeat(16);
  const { ai, fetches, toasts } = setup({ text: `let k = "${key}"\n  pri` });
  ai.setEnabled(true);
  await ai.request();
  assert.equal(fetches.length, 0);
  assert.match(toasts.at(-1), /Nothing was sent to Workers AI: the text near the cursor looks like a secret/);
});
