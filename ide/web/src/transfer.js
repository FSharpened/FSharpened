// Getting files out of and into the page: clipboard, downloads, file pickers, drag and drop. Browser-only glue; the
// archive format is archive.js and the project rules are the F# core's.
import { LIMITS } from "./archive.js";

/** Hands bytes or text to the browser as a download. */
export function download(name, data, type = "application/octet-stream") {
  const url = URL.createObjectURL(new Blob([data], { type }));
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.rel = "noopener";
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}

export async function copyText(text) {
  if (navigator.clipboard?.writeText) {
    try { await navigator.clipboard.writeText(text); return true; } catch { /* fall through to the older way */ }
  }
  const t = document.createElement("textarea");
  t.value = text;
  t.setAttribute("readonly", "");
  t.style.position = "fixed";
  t.style.opacity = "0";
  document.body.append(t);
  t.select();
  let ok = false;
  try { ok = document.execCommand("copy"); } catch { ok = false; }
  t.remove();
  return ok;
}

const isZipFile = (f) => /\.zip$/i.test(f.name) || f.type === "application/zip" || f.type === "application/x-zip-compressed";

/** Reads picked or dropped files into [{ path, data }] and a list of what was left out and why. */
export async function readFiles(files) {
  const out = [];
  const skipped = [];
  let total = 0;
  for (const f of files) {
    const path = f.webkitRelativePath || f.path || f.name;
    if (out.length >= LIMITS.entries) { skipped.push({ path, reason: `more than ${LIMITS.entries} files at once` }); continue; }
    if (f.size > LIMITS.entryBytes) { skipped.push({ path, reason: "larger than " + Math.round(LIMITS.entryBytes / 1048576) + " MB" }); continue; }
    if (total + f.size > LIMITS.totalBytes) { skipped.push({ path, reason: "the selection is larger than " + Math.round(LIMITS.totalBytes / 1048576) + " MB in total" }); continue; }
    try {
      out.push({ path, data: new Uint8Array(await f.arrayBuffer()) });
      total += f.size;
    } catch { skipped.push({ path, reason: "the browser could not read it" }); }
  }
  return { files: out, skipped };
}

/** A single archive is one project; anything else is a set of loose files. */
export function classify(files) {
  const list = [...files];
  return list.length === 1 && isZipFile(list[0]) ? { zip: list[0] } : { loose: list };
}

// Dropped folders arrive as directory entries, not files; walk them (bounded, so a huge tree cannot hang the page).
function entryFile(entry) { return new Promise((res, rej) => entry.file(res, rej)); }
function readDir(reader) { return new Promise((res, rej) => reader.readEntries(res, rej)); }

export async function filesFromDrop(dt) {
  const roots = [...(dt.items ?? [])].map((i) => i.webkitGetAsEntry?.()).filter(Boolean);
  if (!roots.length) return [...(dt.files ?? [])];
  const out = [];
  const walk = async (entry, prefix, depth) => {
    if (out.length >= LIMITS.entries || depth > 12) return;
    if (entry.isFile) {
      const f = await entryFile(entry).catch(() => null);
      if (f) out.push(Object.defineProperty(f, "path", { value: prefix + entry.name }));
    } else if (entry.isDirectory) {
      const reader = entry.createReader();
      for (;;) {
        const batch = await readDir(reader).catch(() => []);
        if (!batch.length) break;
        for (const child of batch) await walk(child, prefix + entry.name + "/", depth + 1);
      }
    }
  };
  for (const r of roots) await walk(r, "", 0);
  return out;
}

/** Opens a hidden file input and resolves with the chosen files ([] if the person cancels). */
export function pick(input) {
  return new Promise((resolve) => {
    const done = () => { input.removeEventListener("change", done); input.removeEventListener("cancel", cancel); const files = [...input.files]; input.value = ""; resolve(files); };
    const cancel = () => { input.removeEventListener("change", done); input.removeEventListener("cancel", cancel); resolve([]); };
    input.addEventListener("change", done);
    input.addEventListener("cancel", cancel);
    input.click();
  });
}
