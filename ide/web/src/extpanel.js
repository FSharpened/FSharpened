// The Extensions panel: what the IDE can take from VS Code (themes, snippets, settings), what it cannot (extension code,
// GitHub Copilot), and the way to use both together (a project download that VS Code opens with the right extensions
// recommended). See ADR-0022. Nothing here executes extension code.
import { el, fill } from "./dom.js";
import { readVsix, readJsonFile, readPickedText, MAX_THEMES_PER_PACK } from "./vsix.js";

const slug = (s) => String(s).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 48) || "import";
const plural = (n, w) => `${n} ${w}${n === 1 ? "" : "s"}`;
const MARKET = "https://marketplace.visualstudio.com/items?itemName=";

/** host: { Core, prefs, root, project(), toast, afterSettings() } */
export function createExtensionsPanel({ Core, prefs, root, project, toast }) {
  const list = root.querySelector("#x-list"), recs = root.querySelector("#x-recs"), report = root.querySelector("#x-report");

  // ---- what is installed
  function drawPacks() {
    const packs = prefs.packs();
    fill(list, packs.length ? packs.map((p) => {
      const b = el("button", { type: "button", class: "mini", "aria-label": "Remove " + p.name }, "Remove");
      b.addEventListener("click", () => { prefs.removePack(p.id); toast(`Removed ${p.name}`); drawPacks(); });
      const bits = [p.themes.length ? plural(p.themes.length, "theme") : "", p.snippets.length ? plural(p.snippets.length, "snippet") : ""].filter(Boolean).join(", ");
      return el("li", { class: "x-pack" }, el("div", { class: "x-main" }, el("strong", {}, p.name), el("span", { class: "muted" }, ` ${p.version ? "v" + p.version + " · " : ""}${bits}`),
        p.left.length || p.runsCode ? el("div", { class: "muted x-left" }, "Not used: " + (p.left.length ? p.left.join(", ") : "its code") + (p.runsCode ? " · it runs code in VS Code, which is not run here" : "")) : null), b);
    }) : el("li", { class: "muted x-none" }, "Nothing imported yet."));
  }

  // ---- what VS Code should recommend for this project
  function drawRecs() {
    const ids = Core.vsCodeRecommendations(project());
    fill(recs, ids.map((id) => {
      const a = el("a", { href: MARKET + encodeURIComponent(id), target: "_blank", rel: "noopener noreferrer" }, id);
      return el("li", {}, a, id.toLowerCase().startsWith("github.copilot") ? el("span", { class: "tentry" }, "AI") : null);
    }));
  }

  // ---- the import report
  function show(title, rows) {
    const close = el("button", { type: "button", class: "mini" }, "Dismiss");
    close.addEventListener("click", () => { report.hidden = true; });
    fill(report, el("div", { class: "rhead" }, el("h3", {}, title), close), el("ul", {}, rows.map((r) => el("li", {}, r))));
    report.hidden = false;
  }

  /** The F# core turns each VS Code theme into a complete one; here they are collected with what it had to leave out. */
  function convertThemes(inputs) {
    const themes = [], notes = [];
    for (const input of inputs) {
      const r = Core.themeImport(input);
      themes.push(r.theme);
      for (const w of r.warnings.slice(0, 6)) notes.push(`${r.theme.name}: ${w}`);
      if (r.warnings.length > 6) notes.push(`${r.theme.name}: …and ${r.warnings.length - 6} more notes`);
    }
    return { themes, notes };
  }

  async function importVsix(file) {
    const r = await readVsix(new Uint8Array(await file.arrayBuffer()));
    if (!r.ok) return [`${file.name}: ${r.error}`];
    const { themes, notes } = convertThemes(r.themes);
    const snippets = Core.snippetsImport(r.snippets);
    const usable = snippets.filter((s) => !(s.languages.length === 1 && s.languages[0] === "unsupported"));
    const rows = [];
    const added = prefs.addPack({ id: "ext-" + slug(r.id || r.name), name: r.name, source: file.name, version: r.version, extId: r.id, themes, snippets: usable, notes: [...r.themeNotes, ...notes], left: r.left, runsCode: r.runsCode });
    const copilot = r.id.toLowerCase().startsWith("github.copilot");
    if (!added.ok) {
      rows.push(el("span", {}, el("strong", {}, r.name), ": ", added.error));
    } else {
      rows.push(el("span", {}, el("strong", {}, r.name), r.version ? " " + r.version : "", ": added ", [plural(themes.length, "colour theme"), plural(usable.length, "snippet")].join(" and "), "."));
      if (added.error) rows.push(added.error);
      if (r.snippets.length && usable.length < snippets.length) rows.push(`${plural(snippets.length - usable.length, "snippet")} are for languages this IDE does not have.`);
      if (r.snippetsSkipped) rows.push(`${plural(r.snippetsSkipped, "snippet entry")} could not be read.`);
    }
    for (const n of [...r.themeNotes, ...notes]) rows.push(n);
    if (r.left.length) rows.push(`Left out: ${r.left.join(", ")}.`);
    if (copilot) rows.push("GitHub Copilot's code runs in VS Code's extension host and talks to GitHub's service. A web page cannot host it, so nothing of it runs here. Use “Download for VS Code” below to open this project in VS Code with Copilot recommended.");
    else if (r.runsCode) rows.push("This extension also contains code that runs in VS Code. That code is never loaded here.");
    return rows;
  }

  async function importJson(file) {
    const text = await readPickedText(file);
    if (text === null) return [`${file.name}: not a text file, or larger than 1 MB.`];
    const r = readJsonFile(file.name, text);
    if (r.kind === "unknown") return [r.error];
    if (r.kind === "theme") {
      const { themes, notes } = convertThemes([r.input]);
      const added = prefs.addPack({ id: "theme-" + slug(file.name.replace(/\.json$/i, "")), name: themes[0].name, source: file.name, themes, snippets: [], notes: [...r.notes, ...notes], left: [], runsCode: false });
      return added.ok ? [`${themes[0].name}: added as a colour theme. Pick it in Settings or with “Color Theme…”.`, ...r.notes, ...notes] : [`${file.name}: ${added.error}`];
    }
    if (r.kind === "snippets") {
      const snippets = Core.snippetsImport(r.defs);
      const usable = snippets.filter((s) => !(s.languages.length === 1 && s.languages[0] === "unsupported"));
      if (!usable.length) return [`${file.name}: no snippets for the languages of this IDE (${r.defs.length} read).`];
      const added = prefs.addPack({ id: "snip-" + slug(file.name.replace(/\.(code-snippets|json)$/i, "")), name: file.name.replace(/\.json$/i, ""), source: file.name, snippets: usable, themes: [], notes: [], left: [], runsCode: false });
      return added.ok ? [`${file.name}: added ${plural(usable.length, "snippet")}${usable.length < snippets.length ? `; ${snippets.length - usable.length} were for other languages` : ""}${r.skipped ? `; ${plural(r.skipped, "entry")} could not be read` : ""}.`] : [`${file.name}: ${added.error}`];
    }
    // settings.json
    const res = Core.settingsFromVsCode(prefs.settings, r.pairs);
    let next = res.settings;
    const applied = [...res.applied];
    let ignored = res.ignored;
    if (ignored.includes("workbench.colorTheme")) { // the core knows VS Code's built-in theme names; an imported theme is found by its name here
      const id = importedThemeId(r.pairs);
      if (id) { next = { ...next, theme: id }; applied.push("workbench.colorTheme"); ignored = ignored.filter((k) => k !== "workbench.colorTheme"); }
    }
    const complex = r.pairs.filter((p) => p.value !== null && typeof p.value === "object").map((p) => p.key);
    prefs.update(next);
    const unused = [...ignored, ...complex];
    return [
      `Applied ${plural(applied.length, "setting")}${applied.length ? ": " + applied.join(", ") : ""}.`,
      unused.length ? `Not used (${unused.length === 1 ? "it is" : "they are"} VS Code's own, or hold lists or objects): ${unused.slice(0, 10).join(", ")}${unused.length > 10 ? ", …" : ""}.` : "Every setting in the file was used.",
    ];
  }

  /** The id of an imported theme that `workbench.colorTheme` names, or "". */
  function importedThemeId(pairs) {
    const wanted = pairs.find((p) => p.key === "workbench.colorTheme" && typeof p.value === "string");
    const name = wanted ? wanted.value.trim().toLowerCase() : "";
    return (name && prefs.themes().find((t) => !prefs.isBuiltin(t.id) && t.name.toLowerCase() === name)?.id) || "";
  }

  return {
    /** Read picked or dropped files: .vsix, a theme, a snippets file, or settings.json. */
    async importFiles(files) {
      const rows = [];
      for (const f of files) {
        try { rows.push(...(/\.vsix$/i.test(f.name) ? await importVsix(f) : await importJson(f))); }
        catch (e) { rows.push(`${f.name}: ${String(e?.message ?? e)}`); }
      }
      if (!rows.length) return;
      show("Import", rows);
      drawPacks();
      toast("Import finished: see the report");
    },
    refresh() { drawPacks(); drawRecs(); },
    drawRecs,
    isVsCodeFile: (name) => /\.(vsix)$/i.test(name),
    limit: MAX_THEMES_PER_PACK,
  };
}
