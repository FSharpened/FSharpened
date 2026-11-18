// Opt-in AI completion (ADR-0026). Off until the person turns it on; nothing leaves the browser before that, and not
// even then without an endpoint. One command asks for a suggestion for the caret of the open file; it shows as ghost
// text that Tab accepts and Escape (or any edit) dismisses. The model's text is data: it is only ever inserted as text.
// What is sent is decided by the F# chain (FSharpened.Tooling.Completion): a window around the caret of the open file,
// withheld entirely when it looks like a secret or the file is one that usually holds secrets.
import * as Fable from "../fable_out/CompletionApi.js";
import { safe } from "./dom.js";

const KEY = "fsharpened.ide.ai.v1";
const DEFAULT_ENDPOINT = "https://ai.fsharpened.workers.dev/api/ai/complete"; // not deployed until a maintainer does it
const TIMEOUT_MS = 9000;
const MAX_CONTEXT = 6000;

export function createAiCompletion({ storage, editor, toast, snippets, project, activeLanguage, activePath, hasFile, announce = () => {}, api = Fable, fetchImpl = (...a) => fetch(...a) }) {
  let state = { enabled: false, endpoint: DEFAULT_ENDPOINT };
  const stored = safe(() => JSON.parse(storage.getItem(KEY)), null);
  if (stored && typeof stored === "object") {
    state = { enabled: stored.enabled === true, endpoint: typeof stored.endpoint === "string" && api.endpointOk(stored.endpoint) ? stored.endpoint : DEFAULT_ENDPOINT };
  }
  const save = () => safe(() => storage.setItem(KEY, JSON.stringify(state)));
  let generation = 0;
  let abort = null;
  const listeners = new Set();
  const emit = () => { for (const f of listeners) f({ ...state }); };

  /** The page's own network call, handed to the F# chain: a timeout and an abort signal, nothing else (no credentials, no cookies). */
  async function send(url, body, timeoutMs) {
    const ctrl = abort;
    const timer = setTimeout(() => { timedOut = true; ctrl.abort(); }, timeoutMs);
    let timedOut = false;
    try {
      const res = await fetchImpl(url, { method: "POST", headers: { "content-type": "application/json" }, body, signal: ctrl.signal, credentials: "omit", referrerPolicy: "no-referrer", cache: "no-store" });
      return { status: res.status, body: await res.text() };
    } catch (e) {
      return timedOut ? { timedOut: true } : { error: e?.name === "AbortError" ? "cancelled" : "network error" };
    } finally {
      clearTimeout(timer);
    }
  }

  return {
    get state() { return { ...state }; },
    onChange: (f) => { listeners.add(f); return () => listeners.delete(f); },
    setEnabled(on) { state = { ...state, enabled: !!on }; save(); emit(); },
    /** Returns an error message, or "" when the endpoint was accepted. */
    setEndpoint(url) {
      const u = String(url ?? "").trim();
      if (!api.endpointOk(u)) return "The endpoint must be an https address (http only for localhost).";
      state = { ...state, endpoint: u };
      save();
      emit();
      return "";
    },
    cancel() { generation++; abort?.abort(); abort = null; },
    /** Ask for a suggestion at the caret and show it. Resolves to what happened, for tests and for the status line. */
    async request() {
      if (!state.enabled) { toast("AI completion is off. Turn it on with “Enable AI Completion” in the command palette."); return { status: "off" }; }
      if (!hasFile()) return { status: "no-file" };
      const my = ++generation;
      abort?.abort();
      abort = new AbortController();
      const { text, cursor } = editor.context();
      const lang = activeLanguage();
      toast("Asking for a suggestion…");
      const r = await api.run(activePath(), lang, text, cursor, MAX_CONTEXT, state.endpoint, TIMEOUT_MS, snippets(lang), send);
      if (my !== generation) return { status: "superseded" };
      const first = r.candidates[0];
      if (!first) {
        const why = r.withheld ? `Nothing was sent: ${r.withheld}.` : r.skipped.length ? `No suggestion (${r.skipped.join("; ")}).` : "No suggestion here.";
        toast(why);
        return { status: "none", result: r };
      }
      editor.showGhost(first.text);
      const via = r.source === "workers-ai" ? "Workers AI (a model's suggestion, check it)" : "the built-in keyword list";
      const note = r.withheld ? ` Nothing was sent to Workers AI: ${r.withheld}.` : r.skipped.length ? ` Workers AI was not used: ${r.skipped.join("; ")}.` : "";
      announce(`Suggestion from ${via}. Press Tab to accept, Escape to dismiss.${note}`);
      return { status: "shown", result: r };
    },
    accept: () => editor.acceptGhost(),
    dismiss: () => { generation++; abort?.abort(); editor.clearGhost(); },
  };
}
