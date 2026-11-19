// A tiny static server for site/public that applies the headers of site/static/_headers (Cloudflare Pages syntax),
// so the tests run under the same Content-Security-Policy as production.
import http from "node:http";
import { readFile, stat } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
export const SITE = process.env.IDE_E2E_SITE ?? path.resolve(here, "../../../site/public");

const TYPES = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".json": "application/json",
  ".webmanifest": "application/manifest+json", ".svg": "image/svg+xml", ".png": "image/png", ".ico": "image/x-icon", ".txt": "text/plain; charset=utf-8",
};

/** Parses a Cloudflare `_headers` file: a path pattern, then indented `Name: value` lines; `! Name` detaches an inherited header. */
export function parseHeaders(text) {
  const blocks = [];
  for (const raw of text.split(/\r?\n/)) {
    if (!raw.trim() || raw.trim().startsWith("#")) continue;
    if (!/^\s/.test(raw)) { blocks.push({ pattern: raw.trim(), lines: [] }); continue; }
    blocks.at(-1)?.lines.push(raw.trim());
  }
  return blocks;
}

const SPECIAL = /[.+?^${}()|[\]\\]/g;
const globToRegExp = (g) => new RegExp("^" + g.split("*").map((s) => s.replace(SPECIAL, (c) => "\\" + c)).join(".*") + "$");

export function headersFor(blocks, pathname) {
  const out = new Map();
  for (const b of blocks) {
    if (!globToRegExp(b.pattern).test(pathname)) continue;
    for (const l of b.lines) {
      if (l.startsWith("!")) { out.delete(l.slice(1).trim().toLowerCase()); continue; }
      const i = l.indexOf(":");
      out.set(l.slice(0, i).trim().toLowerCase(), l.slice(i + 1).trim());
    }
  }
  return out;
}

export async function startServer() {
  const blocks = parseHeaders(await readFile(path.join(SITE, "_headers"), "utf8"));
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, "http://localhost");
      let rel = decodeURIComponent(url.pathname);
      if (rel.includes("..")) { res.writeHead(400).end(); return; }
      let file = path.join(SITE, rel);
      let st = await stat(file).catch(() => null);
      if (st?.isDirectory()) { file = path.join(file, "index.html"); st = await stat(file).catch(() => null); }
      if (!st && !path.extname(file)) { file += ".html"; st = await stat(file).catch(() => null); }
      if (!st) { res.writeHead(404, { "content-type": "text/plain" }).end("not found"); return; }
      const headers = Object.fromEntries(headersFor(blocks, rel.endsWith("/") ? rel : rel));
      headers["content-type"] = headers["content-type"] ?? TYPES[path.extname(file)] ?? "application/octet-stream";
      delete headers["strict-transport-security"]; // plain http on localhost
      res.writeHead(200, headers).end(await readFile(file));
    } catch (e) {
      res.writeHead(500).end(String(e));
    }
  });
  await new Promise((ok) => server.listen(0, "127.0.0.1", ok));
  const { port } = server.address();
  return { origin: `http://127.0.0.1:${port}`, close: () => new Promise((ok) => server.close(ok)) };
}
