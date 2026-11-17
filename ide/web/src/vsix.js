// Reading VS Code content: a .vsix (an extension package), a colour theme, a snippets file. Only declared DATA is used,
// the themes and snippets an extension contributes. Extension code is never loaded or run (ADR-0022): a page that
// executes arbitrary extension code would defeat its own content security policy, and GitHub Copilot's code is not ours.
import { readZip, decodeText, LIMITS } from "./archive.js";
import { parseJsonc } from "./jsonc.js";

// A .vsix often holds thousands of files; only its small JSON files are read, so the entry count is not what costs.
const VSIX_LIMITS = Object.freeze({ ...LIMITS, entries: 20000, entryBytes: 2 * 1024 * 1024 });
export const MAX_THEMES_PER_PACK = 12;
const MAX_INCLUDE_DEPTH = 3;
const MAX_JSON_BYTES = 1024 * 1024;

const isJson = (p) => /\.json$/i.test(p) && !/(^|\/)node_modules\//i.test(p);

// package.json says "vs", "vs-dark", "hc-black" or "hc-light"; a theme file itself may say "light", "dark", "hc" or "hcLight".
const THEME_TYPE = { vs: "light", "vs-dark": "dark", "hc-black": "hc", "hc-light": "hcLight", light: "light", dark: "dark", hc: "hc", hcLight: "hcLight" };

const asText = (data) => (data.length > MAX_JSON_BYTES ? null : decodeText(data));
const isObj = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const dirOf = (p) => (p.includes("/") ? p.slice(0, p.lastIndexOf("/")) : "");

/** `a/b/../c` as `a/c`. A path that climbs out of the archive comes back as null. */
export function resolvePath(base, rel) {
  const out = base ? base.split("/") : [];
  for (const part of String(rel).replace(/\\/g, "/").split("/")) {
    if (part === "" || part === ".") continue;
    if (part === "..") { if (!out.length) return null; out.pop(); } else out.push(part);
  }
  return out.join("/");
}

/** The TextMate `scope` of a rule as a list: a string may hold several, separated by commas. */
function scopesOf(scope) {
  const list = Array.isArray(scope) ? scope : typeof scope === "string" ? [scope] : [];
  return list.filter((s) => typeof s === "string").flatMap((s) => s.split(",")).map((s) => s.trim()).filter(Boolean);
}

/** A parsed VS Code theme file, in the shape the F# core reads. Strings that are not colours are left for the core to refuse. */
export function themeInput(json, fallbackName, uiTheme) {
  const colors = isObj(json.colors) ? Object.entries(json.colors).filter(([, v]) => typeof v === "string").map(([key, value]) => ({ key, value })) : [];
  const rules = Array.isArray(json.tokenColors) ? json.tokenColors : [];
  const tokenColors = rules.filter(isObj).map((r) => ({ scopes: scopesOf(r.scope), foreground: isObj(r.settings) && typeof r.settings.foreground === "string" ? r.settings.foreground : "" })).filter((r) => r.foreground);
  const name = typeof json.name === "string" && json.name.trim() ? json.name : fallbackName;
  return { name, type: THEME_TYPE[uiTheme] ?? THEME_TYPE[json.type] ?? "", colors, tokenColors };
}

/**
 * One theme file from an archive, with the files it `include`s merged underneath it (a child overrides its parent).
 * `read(path)` returns the parsed JSON or null. Returns { json, notes }.
 */
export function loadTheme(path, read, notes, depth = 0) {
  const json = read(path);
  if (!isObj(json)) { notes.push(`${path}: not a JSON theme`); return null; }
  let base = null;
  if (typeof json.include === "string") {
    if (depth >= MAX_INCLUDE_DEPTH) notes.push(`${path}: includes nest deeper than ${MAX_INCLUDE_DEPTH}; the deepest were ignored`);
    else {
      const target = resolvePath(dirOf(path), json.include);
      base = target ? loadTheme(target, read, notes, depth + 1) : null;
      if (!base) notes.push(`${path}: its include "${json.include}" could not be read`);
    }
  }
  if (typeof json.tokenColors === "string") notes.push(`${path}: tokenColors points at a separate .tmTheme file, which is not read`);
  const merged = {
    name: json.name ?? base?.name,
    type: json.type ?? base?.type,
    colors: { ...(base?.colors ?? {}), ...(isObj(json.colors) ? json.colors : {}) },
    tokenColors: [...(base?.tokenColors ?? []), ...(Array.isArray(json.tokenColors) ? json.tokenColors : [])],
  };
  return merged;
}

/** VS Code snippet files hold { "Name": { prefix, body, description, scope } }. Returns defs for the F# core and a count of entries it could not use. */
export function snippetDefs(json, languageHint) {
  const defs = [];
  let skipped = 0;
  if (!isObj(json)) return { defs, skipped: 1 };
  for (const [name, v] of Object.entries(json)) {
    if (!isObj(v)) { skipped++; continue; }
    const prefixes = (Array.isArray(v.prefix) ? v.prefix : [v.prefix]).filter((p) => typeof p === "string" && p);
    const body = Array.isArray(v.body) ? v.body.filter((l) => typeof l === "string").join("\n") : typeof v.body === "string" ? v.body : null;
    if (!prefixes.length || body === null) { skipped++; continue; }
    const scope = typeof v.scope === "string" ? v.scope.split(",").map((s) => s.trim()).filter(Boolean) : languageHint ? [languageHint] : [];
    defs.push({ name, prefixes, body, description: typeof v.description === "string" ? v.description : "", scope });
  }
  return { defs, skipped };
}

const looksLikeTheme = (j) => isObj(j) && (isObj(j.colors) || Array.isArray(j.tokenColors) || typeof j.tokenColors === "string");
const looksLikeSnippets = (j) => isObj(j) && Object.keys(j).length > 0 && Object.values(j).every((v) => isObj(v) && ("body" in v));

/** What an extension's package.json says it contributes, for the "left out" report. Only names and counts. */
export function describeContributions(pkg) {
  const c = isObj(pkg.contributes) ? pkg.contributes : {};
  const used = [];
  const left = [];
  const count = (k) => (Array.isArray(c[k]) ? c[k].length : isObj(c[k]) ? Object.keys(c[k]).length : c[k] ? 1 : 0);
  if (count("themes")) used.push(`${count("themes")} colour theme${count("themes") === 1 ? "" : "s"}`);
  if (count("snippets")) used.push(`${count("snippets")} snippet file${count("snippets") === 1 ? "" : "s"}`);
  const labels = { commands: "commands", keybindings: "keyboard shortcuts", menus: "menus", configuration: "settings", languages: "language definitions", grammars: "syntax grammars", debuggers: "debuggers", views: "views", viewsContainers: "view containers", chatParticipants: "chat participants", languageModelTools: "language model tools", iconThemes: "icon themes", productIconThemes: "product icon themes", taskDefinitions: "task definitions", customEditors: "custom editors", walkthroughs: "walkthroughs", problemMatchers: "problem matchers", terminal: "terminal profiles", authentication: "authentication providers", mcpServerDefinitionProviders: "MCP servers", languageModelChatProviders: "language model providers" };
  for (const [k, label] of Object.entries(labels)) if (count(k)) left.push(`${count(k)} ${label}`);
  const runsCode = typeof pkg.main === "string" || typeof pkg.browser === "string";
  return { used, left, runsCode };
}

function nlsResolver(nls) {
  return (text) => {
    if (typeof text !== "string") return "";
    const m = /^%(.+)%$/.exec(text);
    if (!m) return text;
    const v = isObj(nls) ? nls[m[1]] : null;
    return typeof v === "string" ? v : isObj(v) && typeof v.message === "string" ? v.message : m[1];
  };
}

/**
 * Reads a .vsix (or a zipped extension folder). Returns
 *   { ok, error?, name, id, version, themes: [themeInput], themeNotes, snippets: [defs], snippetsSkipped, used, left, runsCode, readme? }
 * Nothing in the archive is executed.
 */
export async function readVsix(bytes) {
  let zip;
  try { zip = await readZip(bytes, VSIX_LIMITS, (p) => isJson(p) && p.split("/").length <= 8); }
  catch (e) { return { ok: false, error: String(e?.message ?? e) }; }
  const byPath = new Map(zip.files.map((f) => [f.path, f.data]));
  const base = byPath.has("extension/package.json") ? "extension/" : byPath.has("package.json") ? "" : null;
  if (base === null) return { ok: false, error: "No package.json was found, so this is not a VS Code extension package (.vsix)." };
  const cache = new Map();
  const read = (p) => {
    if (cache.has(p)) return cache.get(p);
    const data = byPath.get(p);
    let v = null;
    if (data) { const t = asText(data); if (t !== null) { try { v = parseJsonc(t); } catch { v = null; } } }
    cache.set(p, v);
    return v;
  };
  const pkg = read(base + "package.json");
  if (!isObj(pkg)) return { ok: false, error: "package.json could not be read as JSON." };
  const nls = nlsResolver(read(base + "package.nls.json"));
  const name = nls(pkg.displayName) || nls(pkg.name) || "VS Code extension";
  const id = typeof pkg.publisher === "string" && typeof pkg.name === "string" ? `${pkg.publisher}.${pkg.name}` : "";
  const desc = describeContributions(pkg);
  const c = isObj(pkg.contributes) ? pkg.contributes : {};

  const themes = [];
  const themeNotes = [];
  const themeDecls = Array.isArray(c.themes) ? c.themes.filter((t) => isObj(t) && typeof t.path === "string") : [];
  if (themeDecls.length > MAX_THEMES_PER_PACK) themeNotes.push(`${themeDecls.length} themes are declared; the first ${MAX_THEMES_PER_PACK} were read`);
  for (const t of themeDecls.slice(0, MAX_THEMES_PER_PACK)) {
    const path = resolvePath(base.replace(/\/$/, ""), t.path);
    if (!path) { themeNotes.push(`${t.path}: outside the package`); continue; }
    const merged = loadTheme(path, (p) => read(p), themeNotes);
    if (!merged) continue;
    const input = themeInput(merged, name, typeof t.uiTheme === "string" ? t.uiTheme : "");
    if (nls(t.label)) input.name = nls(t.label);
    themes.push(input);
  }

  const snippets = [];
  let snippetsSkipped = 0;
  const snipDecls = Array.isArray(c.snippets) ? c.snippets.filter((s) => isObj(s) && typeof s.path === "string") : [];
  for (const s of snipDecls.slice(0, 60)) {
    const path = resolvePath(base.replace(/\/$/, ""), s.path);
    const json = path ? read(path) : null;
    if (!json) { snippetsSkipped++; continue; }
    const r = snippetDefs(json, typeof s.language === "string" ? s.language : "");
    snippets.push(...r.defs);
    snippetsSkipped += r.skipped;
  }
  return { ok: true, name, id, version: typeof pkg.version === "string" ? pkg.version : "", themes, themeNotes, snippets, snippetsSkipped, used: desc.used, left: desc.left, runsCode: desc.runsCode };
}

/**
 * Reads one picked JSON file as a colour theme, a snippets file, or neither. `name` is the file name.
 * Returns { kind: "theme", input } | { kind: "snippets", defs, skipped } | { kind: "settings", pairs } | { kind: "unknown", error }.
 */
export function readJsonFile(name, text) {
  let json;
  try { json = parseJsonc(text); } catch (e) { return { kind: "unknown", error: `${name} is not valid JSON: ${e.message}` }; }
  const stem = name.replace(/^.*[\\/]/, "").replace(/\.(code-snippets|json)$/i, "");
  if (looksLikeTheme(json)) {
    const notes = [];
    if (typeof json.include === "string") notes.push(`It includes "${json.include}", which is another file: import that file as well, or use the .vsix`);
    if (typeof json.tokenColors === "string") notes.push("Its token colours are in a separate .tmTheme file, which is not read");
    return { kind: "theme", input: themeInput(json, stem, ""), notes };
  }
  if (looksLikeSnippets(json)) return { kind: "snippets", ...snippetDefs(json, /^[a-z][a-z0-9+#-]*$/.test(stem) && !/snippets?$/i.test(stem) ? stem : "") };
  if (isObj(json) && Object.keys(json).some((k) => k.includes("."))) return { kind: "settings", pairs: Object.entries(json).map(([key, value]) => ({ key, value })) };
  return { kind: "unknown", error: `${name} is not a colour theme, a snippets file or a settings file.` };
}

/** Plain text of a picked file, or null when it is binary or too large. */
export async function readPickedText(file) {
  if (file.size > MAX_JSON_BYTES) return null;
  return decodeText(new Uint8Array(await file.arrayBuffer()));
}
