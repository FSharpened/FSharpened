// Editor settings, imported colour themes and snippets, and the list of VS Code content the person added. All of it is
// kept in this browser only. Everything read back from storage is re-validated, so a damaged or hand-edited entry can
// never put anything but a plain colour into a style rule or a plain string into a snippet.
import { safe } from "./dom.js";

const KEY = { settings: "fsharpened.ide.settings.v1", packs: "fsharpened.ide.packs.v1" };
export const LIMITS = Object.freeze({ themes: 40, snippets: 2000, packs: 30 });
const ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
const COLOR = /^#[0-9a-f]{6}(?:[0-9a-f]{2})?$/;

export function createPrefs(Core, storage) {
  const builtin = Core.themes();
  const roles = new Set(Object.keys(builtin.find((t) => t.id === "light").vars)); // every theme sets these and no others
  const listeners = new Set();
  let settings = Core.settingsDefaults();
  let packs = [];          // [{ id, name, source, version, extId, themes: [spec], snippets: [snippet], notes: [string], left: [string], runsCode }]
  let appliedVars = [];

  // ---- validation of stored content
  const cleanTheme = (t) => {
    if (!t || typeof t !== "object" || typeof t.id !== "string" || !ID.test(t.id) || builtin.some((b) => b.id === t.id)) return null;
    if (typeof t.name !== "string" || !t.vars || typeof t.vars !== "object") return null;
    const vars = {};
    for (const [k, v] of Object.entries(t.vars)) if (roles.has(k) && typeof v === "string" && COLOR.test(v)) vars[k] = v;
    if (Object.keys(vars).length !== roles.size) return null; // a theme is complete or it is not used
    return { id: t.id, name: t.name.slice(0, 80), scheme: t.scheme === "dark" ? "dark" : "light", contrast: t.contrast === "high" ? "high" : "normal", vars };
  };
  const cleanSnippet = (s) => {
    if (!s || typeof s.label !== "string" || typeof s.body !== "string" || s.body.length > 4000 || !s.label || s.label.length > 80) return null;
    return { label: s.label, detail: typeof s.detail === "string" ? s.detail.slice(0, 120) : "", body: s.body, languages: Array.isArray(s.languages) ? s.languages.filter((x) => typeof x === "string").slice(0, 20) : [] };
  };
  const cleanPack = (p) => {
    if (!p || typeof p.id !== "string" || !ID.test(p.id) || typeof p.name !== "string") return null;
    const strings = (a) => (Array.isArray(a) ? a.filter((x) => typeof x === "string").map((x) => x.slice(0, 300)).slice(0, 40) : []);
    return {
      id: p.id, name: p.name.slice(0, 120), source: typeof p.source === "string" ? p.source.slice(0, 200) : "", version: typeof p.version === "string" ? p.version.slice(0, 40) : "",
      extId: typeof p.extId === "string" ? p.extId.slice(0, 120) : "",
      themes: (Array.isArray(p.themes) ? p.themes : []).map(cleanTheme).filter(Boolean).slice(0, LIMITS.themes),
      snippets: (Array.isArray(p.snippets) ? p.snippets : []).map(cleanSnippet).filter(Boolean).slice(0, LIMITS.snippets),
      notes: strings(p.notes), left: strings(p.left), runsCode: !!p.runsCode,
    };
  };

  // ---- storage
  const read = (key) => safe(() => JSON.parse(storage.getItem(key)), null);
  const write = (key, value) => { try { storage.setItem(key, JSON.stringify(value)); return { ok: true }; } catch { return { ok: false, error: "This browser would not store that (its storage is full or blocked). It stays until you close the page." }; } };
  settings = Core.settingsSanitize(read(KEY.settings));
  const stored = read(KEY.packs);
  packs = (Array.isArray(stored) ? stored : []).map(cleanPack).filter(Boolean).slice(0, LIMITS.packs);

  const all = () => [...builtin, ...packs.flatMap((p) => p.themes)];
  const find = (id) => all().find((t) => t.id === id) ?? builtin[0];
  const importedSnippets = () => packs.flatMap((p) => p.snippets);

  // ---- applying
  /** Sets the theme's colours on the page (the root element's style, which the content security policy allows). */
  function apply() {
    const root = document.documentElement;
    for (const k of appliedVars) root.style.removeProperty("--" + k);
    appliedVars = [];
    const t = find(settings.theme);
    if (t.vars && Object.keys(t.vars).length) {
      for (const [k, v] of Object.entries(t.vars)) { root.style.setProperty("--" + k, v); appliedVars.push(k); }
      root.style.colorScheme = t.scheme;
      root.dataset.ideTheme = t.id;
    } else {
      root.style.removeProperty("color-scheme");
      delete root.dataset.ideTheme;
    }
    root.style.setProperty("--ed-fs", settings.fontSize + "px");
  }
  const emit = () => { for (const f of listeners) f(settings); };

  return {
    get settings() { return settings; },
    themes: all,
    theme: () => find(settings.theme),
    isBuiltin: (id) => builtin.some((t) => t.id === id),
    packs: () => packs,
    snippets: importedSnippets,
    /** The snippets offered in a language: built in, plus those imported. */
    snippetsFor: (lang) => Core.snippetsFor(lang, importedSnippets()),
    apply,
    onChange(f) { listeners.add(f); return () => listeners.delete(f); },
    /** Change some settings. Anything invalid is brought back into range by the core. */
    update(patch) {
      const next = Core.settingsSanitize({ ...settings, ...patch });
      next.theme = find(next.theme).id; // a theme that is gone (its import was removed) falls back to Auto
      settings = next;
      const r = write(KEY.settings, settings);
      apply();
      emit();
      return r;
    },
    reset() {
      settings = Core.settingsDefaults();
      safe(() => storage.removeItem(KEY.settings));
      apply();
      emit();
    },
    /** Add what an extension or a file contributed. Returns { ok, error? }. */
    addPack(pack) {
      const made = cleanPack(pack);
      if (!made) return { ok: false, error: "That content could not be used." };
      // clean-up truncates; an import that is over the limits is refused whole instead of silently cut short
      if ((pack.snippets?.length ?? 0) > LIMITS.snippets) return { ok: false, error: `${pack.snippets.length} snippets are more than the ${LIMITS.snippets} this browser keeps.` };
      if ((pack.themes?.length ?? 0) > LIMITS.themes) return { ok: false, error: `${pack.themes.length} themes are more than the ${LIMITS.themes} this browser keeps.` };
      if (!made.themes.length && !made.snippets.length) return { ok: false, error: "It has no colour themes or snippets this IDE can use." };
      const others = packs.filter((p) => p.id !== made.id);
      const themeCount = others.reduce((n, p) => n + p.themes.length, 0) + made.themes.length;
      const snippetCount = others.reduce((n, p) => n + p.snippets.length, 0) + made.snippets.length;
      if (others.length >= LIMITS.packs) return { ok: false, error: `${LIMITS.packs} imports are the most this browser keeps. Remove one first.` };
      if (themeCount > LIMITS.themes) return { ok: false, error: `${LIMITS.themes} imported themes are the most this browser keeps. Remove some first.` };
      if (snippetCount > LIMITS.snippets) return { ok: false, error: `${LIMITS.snippets} imported snippets are the most this browser keeps. Remove some first.` };
      // ids stay unique across packs and never collide with a built-in theme
      const taken = new Set([...builtin.map((t) => t.id), ...others.flatMap((p) => p.themes.map((t) => t.id))]);
      made.themes = made.themes.map((t) => { let id = t.id, n = 2; while (taken.has(id)) id = `${t.id.slice(0, 58)}-${n++}`; taken.add(id); return { ...t, id }; });
      const next = [...others, made];
      const r = write(KEY.packs, next);
      packs = next;
      apply();
      emit();
      return { ok: true, saved: r.ok, error: r.ok ? undefined : r.error, pack: made };
    },
    removePack(id) {
      const gone = packs.find((p) => p.id === id);
      packs = packs.filter((p) => p.id !== id);
      write(KEY.packs, packs);
      if (gone && gone.themes.some((t) => t.id === settings.theme)) this.update({ theme: "auto" });
      else emit();
      return !!gone;
    },
  };
}
