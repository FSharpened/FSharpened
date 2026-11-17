// Moving projects in and out of the IDE (ADR-0021): .zip archives, single files, folders, and the manifest. The project
// model and every path rule live in the F# core; this file only translates between bytes and that model.
import { buildZip, readZip, decodeText, encodeText } from "./archive.js";

export const FORMAT = "fsharpened.project/1";

// The manifest's file name belongs to the core (`Core.manifestName`); `useManifestName` copies it here once at start-up.
let manifestFile = "fsharpened.project.json";
export function useManifestName(name) {
  manifestFile = name;
}

/** The manifest carries what the files cannot: the project's name, its entry file and its pipeline. */
export function manifestText(project) {
  return JSON.stringify({ format: FORMAT, name: project.name, entry: project.entry ?? null, pipeline: project.pipeline ?? [] }, null, 2) + "\n";
}

/** Tolerant: a manifest from a newer or hand-edited project never blocks an import. */
export function parseManifest(text) {
  try {
    const m = JSON.parse(text);
    if (!m || typeof m !== "object") return {};
    return {
      name: typeof m.name === "string" ? m.name : undefined,
      entry: typeof m.entry === "string" ? m.entry : undefined,
      pipeline: Array.isArray(m.pipeline) ? m.pipeline.filter((s) => typeof s === "string") : [],
    };
  } catch {
    return {};
  }
}

const safeFileName = (name) => (name.replace(/[^A-Za-z0-9._-]+/g, "-").replace(/^-+|-+$/g, "") || "project").slice(0, 60);

/** Every file of the project plus the manifest, inside one folder named after the project so unzipping never spills. */
export function exportProjectZip(project, date) {
  const root = safeFileName(project.name);
  const files = [...project.files.map((f) => ({ path: `${root}/${f.path}`, data: encodeText(f.text) })), { path: `${root}/${manifestFile}`, data: encodeText(manifestText(project)) }];
  return { name: `${root}.zip`, bytes: buildZip(files, date) };
}

/**
 * The project as a folder VS Code opens properly: its files, the extra files from `Core.vsCodeFiles` (.vscode/settings.json,
 * .vscode/extensions.json, a .code-workspace), and the manifest so the folder can be imported back. A file the project
 * already has is never replaced by a generated one.
 */
export function exportVsCodeZip(project, extra, date) {
  const root = safeFileName(project.name);
  const own = new Set(project.files.map((f) => f.path.toLowerCase()));
  const files = [
    ...project.files.map((f) => ({ path: `${root}/${f.path}`, data: encodeText(f.text) })),
    ...extra.filter((f) => !own.has(f.path.toLowerCase())).map((f) => ({ path: `${root}/${f.path}`, data: encodeText(f.text) })),
    { path: `${root}/${manifestFile}`, data: encodeText(manifestText(project)) },
  ];
  return { name: `${root}-vscode.zip`, bytes: buildZip(files, date) };
}

/** One folder of the project as its own archive; paths inside are relative to the folder. */
export function exportFolderZip(project, folder, date) {
  const prefix = folder.replace(/\/+$/, "") + "/";
  const inside = project.files.filter((f) => f.path.toLowerCase().startsWith(prefix.toLowerCase()));
  const root = safeFileName(folder.split("/").pop());
  return { name: `${root}.zip`, bytes: buildZip(inside.map((f) => ({ path: `${root}/${f.path.slice(prefix.length)}`, data: encodeText(f.text) })), date), count: inside.length };
}

const isManifest = (path) => {
  const parts = path.replace(/\\/g, "/").split("/").filter(Boolean);
  return parts.length >= 1 && parts.length <= 2 && parts[parts.length - 1] === manifestFile;
};

/**
 * Archive bytes -> { project, skipped }. `Core` is the compiled F# core. Binary files are skipped here (the project is
 * text); path, size and count rules are the core's, so an import and a manual add can never disagree.
 */
export async function importArchive(Core, bytes, fallbackName) {
  const { files, skipped } = await readZip(bytes);
  return entriesToProject(Core, files, skipped.map((s) => ({ path: s.path, reason: s.reason })), fallbackName);
}

/** Loose files (a file picker or a dropped folder): [{ path, data }]. */
export function importFiles(Core, files, fallbackName) {
  return entriesToProject(Core, files, [], fallbackName);
}

/**
 * Files already in memory ([{ path, text }]) -> a project, each judged by the core's rules. Unlike an import it keeps
 * paths exactly as given (a share link or a saved project must not lose its top-level folder).
 */
export function buildProject(Core, name, files, entry, pipeline) {
  let project = Core.projectCreate(name);
  const skipped = [];
  for (const f of files) {
    const r = Core.projectAddFile(project, f.path, f.text);
    if (r.ok) project = r.project;
    else skipped.push({ path: f.path, reason: r.error });
  }
  if (entry) {
    const r = Core.projectSetEntry(project, entry);
    if (r.ok) project = r.project;
    else skipped.push({ path: entry, reason: "entry file: " + r.error });
  }
  if (pipeline?.length) {
    const r = Core.projectSetPipeline(project, pipeline);
    if (r.ok) project = r.project;
    else skipped.push({ path: pipeline.join(", "), reason: "pipeline: " + r.error });
  }
  return { project, skipped };
}

function entriesToProject(Core, files, skipped, fallbackName) {
  let meta = {};
  const entries = [];
  for (const f of files) {
    if (isManifest(f.path)) {
      const text = decodeText(f.data);
      if (text !== null) meta = parseManifest(text);
      continue;
    }
    const text = decodeText(f.data);
    if (text === null) skipped.push({ path: f.path, reason: "binary file (the IDE holds text)" });
    else entries.push({ path: f.path, text });
  }
  const result = Core.projectImport(meta.name || fallbackName || "Imported", entries, meta.entry ?? "", meta.pipeline ?? []);
  return { project: result.project, skipped: [...skipped, ...result.skipped] };
}
