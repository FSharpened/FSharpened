#!/usr/bin/env python3
"""Checks the built site (site/public) and the hub console (worker/admin) for the things that silently break a PWA.

    python tools/site/build.py --require-ide && python tools/site/check.py

Everything is deterministic and offline. A failure prints one line per problem and exits 1.
  - every internal link, script, stylesheet, image and manifest/icon target exists (Cloudflare "clean URL" rules applied)
  - no inline <script>, <style>, style="" or on*="" (the CSP forbids them, so they would fail only in production)
  - every page has the metadata a phone and a search engine need (viewport-fit, theme colours, icons, canonical, h1)
  - the web manifests are installable: 192/512 icons that really are that size, a maskable icon, start_url inside scope
  - every file a service worker precaches exists, and the site worker has no template placeholder left
  - the headers file keeps the cache and CSP rules the PWA depends on
Standard library only.
"""
from __future__ import annotations

import json
import re
import struct
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urldefrag, urlsplit

ROOT = Path(__file__).resolve().parents[2]
PUBLIC = ROOT / "site" / "public"
ADMIN = ROOT / "worker" / "admin"

problems: list[str] = []


def bad(msg: str) -> None:
    problems.append(msg)


class Page(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.refs: list[tuple[str, str]] = []  # (tag, url)
        self.meta: dict[str, list[dict[str, str]]] = {}
        self.links: list[dict[str, str]] = []
        self.inline_script = self.inline_style = 0
        self.inline_attrs: list[str] = []
        self.h1 = 0
        self.title = ""
        self.lang = ""
        self._in_title = False
        self._script_has_src = False

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        a = {k: (v or "") for k, v in attrs}
        for k in a:
            if k == "style" or k.startswith("on"):
                self.inline_attrs.append(f"<{tag} {k}=...>")
        if tag == "html":
            self.lang = a.get("lang", "")
        elif tag == "title":
            self._in_title = True
        elif tag == "h1":
            self.h1 += 1
        elif tag == "meta":
            self.meta.setdefault(a.get("name") or a.get("property") or "", []).append(a)
        elif tag == "link":
            self.links.append(a)
            if a.get("href"):
                self.refs.append(("link", a["href"]))
        elif tag == "script":
            self._script_has_src = "src" in a
            if "src" in a:
                self.refs.append(("script", a["src"]))
            elif a.get("type") not in ("application/ld+json",):
                self.inline_script += 1
        elif tag == "style":
            self.inline_style += 1
        elif tag in ("a", "img", "source"):
            u = a.get("href") or a.get("src")
            if u:
                self.refs.append((tag, u))

    def handle_endtag(self, tag: str) -> None:
        if tag == "title":
            self._in_title = False

    def handle_data(self, data: str) -> None:
        if self._in_title:
            self.title += data


def parse(path: Path) -> Page:
    p = Page()
    p.feed(path.read_text(encoding="utf-8"))
    return p


def resolve(root: Path, url: str, page_path: str) -> Path | None:
    """Map a URL to the file Cloudflare would serve for it, or None if it is external or not a file link."""
    url, _ = urldefrag(url)
    parts = urlsplit(url)
    if parts.scheme in ("http", "https", "mailto", "tel", "data", "javascript") or url.startswith("//") or not url:
        return None
    path = parts.path
    if not path.startswith("/"):
        base = page_path.rsplit("/", 1)[0] + "/"
        path = base + path
    rel = path.lstrip("/")
    for cand in ([root / "index.html"] if rel == "" else [root / rel, root / (rel + ".html"), root / rel / "index.html"]):
        if cand.is_file():
            return cand
    return root / rel  # does not exist; the caller reports it


def png_size(path: Path) -> tuple[int, int] | None:
    data = path.read_bytes()[:32]
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", data[16:24])


def check_manifest(root: Path, manifest_path: Path, label: str) -> dict:
    try:
        m = json.loads(manifest_path.read_text(encoding="utf-8"))
    except Exception as e:  # noqa: BLE001
        bad(f"{label}: manifest is not valid JSON ({e})")
        return {}
    for key in ("name", "short_name", "start_url", "scope", "display", "icons", "theme_color", "background_color", "id"):
        if not m.get(key):
            bad(f"{label}: manifest is missing '{key}'")
    if m.get("display") not in ("standalone", "fullscreen", "minimal-ui"):
        bad(f"{label}: manifest display must be standalone/minimal-ui/fullscreen to be installable")
    start, scope = urlsplit(m.get("start_url", "")).path, m.get("scope", "/")
    if not start.startswith(scope):
        bad(f"{label}: start_url '{start}' is outside scope '{scope}'")
    seen_any = seen_maskable = False
    for icon in m.get("icons", []):
        f = resolve(root, icon.get("src", ""), "/")
        if f is None or not f.is_file():
            bad(f"{label}: manifest icon {icon.get('src')} does not exist")
            continue
        if icon.get("type") == "image/png":
            dims = png_size(f)
            want = tuple(int(x) for x in icon["sizes"].split("x"))
            if dims != want:
                bad(f"{label}: icon {icon['src']} is {dims} but the manifest says {icon['sizes']}")
            if want[0] >= 512:
                seen_any |= "any" in icon.get("purpose", "any").split()
                seen_maskable |= "maskable" in icon.get("purpose", "").split()
    sizes = {i.get("sizes") for i in m.get("icons", [])}
    if not {"192x192", "512x512"} <= sizes:
        bad(f"{label}: need 192x192 and 512x512 icons (found {sorted(s for s in sizes if s)})")
    if not seen_any:
        bad(f"{label}: no 512px 'any' icon")
    if not seen_maskable:
        bad(f"{label}: no 512px 'maskable' icon (Android would letterbox the app icon)")
    for s in m.get("shortcuts", []):
        f = resolve(root, s.get("url", ""), "/")
        if f is None or not f.is_file():
            bad(f"{label}: shortcut {s.get('name')} points at {s.get('url')}, which does not exist")
    return m


def check_page_meta(label: str, p: Page, *, need_h1: bool = True) -> None:
    if not p.lang:
        bad(f"{label}: <html> has no lang")
    if not p.title.strip():
        bad(f"{label}: no <title>")
    vp = (p.meta.get("viewport") or [{}])[0].get("content", "")
    if "width=device-width" not in vp or "viewport-fit=cover" not in vp:
        bad(f"{label}: viewport must be width=device-width with viewport-fit=cover (found '{vp}')")
    if user_scalable := re.search(r"user-scalable\s*=\s*(no|0)|maximum-scale\s*=\s*1(\.0)?\b", vp):
        bad(f"{label}: viewport disables pinch zoom ({user_scalable.group(0)}); that fails WCAG 1.4.4")
    if len(p.meta.get("theme-color", [])) < 2:
        bad(f"{label}: needs light and dark theme-color metas")
    rels = {(l.get("rel") or "") for l in p.links}
    for rel in ("manifest", "icon", "apple-touch-icon", "stylesheet"):
        if rel not in rels:
            bad(f"{label}: no <link rel={rel}>")
    if p.inline_script or p.inline_style or p.inline_attrs:
        bad(f"{label}: inline code the CSP will block: {p.inline_script} <script>, {p.inline_style} <style>, {sorted(set(p.inline_attrs))}")
    if need_h1 and p.h1 != 1:
        bad(f"{label}: expected exactly one <h1>, found {p.h1}")


def precache_list(sw: Path) -> list[str]:
    m = re.search(r"const PRECACHE\s*=\s*(\[.*?\]);", sw.read_text(encoding="utf-8"), re.S)
    if not m:
        bad(f"{sw.relative_to(ROOT)}: cannot find the PRECACHE list")
        return []
    return json.loads(m.group(1))


def check_site() -> None:
    if not PUBLIC.is_dir() or not (PUBLIC / "index.html").is_file():
        bad("site/public is missing; run: python tools/site/build.py --require-ide")
        return
    pages = sorted(PUBLIC.rglob("*.html"))
    for f in pages:
        rel = "/" + f.relative_to(PUBLIC).as_posix()
        p = parse(f)
        check_page_meta(rel, p)
        for tag, url in p.refs:
            target = resolve(PUBLIC, url, rel)
            if target is not None and not target.is_file():
                bad(f"{rel}: <{tag}> points at {url}, which does not exist")

    m = check_manifest(PUBLIC, PUBLIC / "manifest.webmanifest", "site")
    if m and m.get("start_url", "").split("?")[0] != "/":
        bad("site: start_url should open the home page")

    sw = PUBLIC / "sw.js"
    text = sw.read_text(encoding="utf-8")
    if "__VERSION__" in text or "__PRECACHE__" in text or "__KNOWN__" in text:
        bad("site/public/sw.js still contains template placeholders")
    pre = precache_list(sw)
    for url in pre:
        t = resolve(PUBLIC, url, "/")
        if t is None or not t.is_file():
            bad(f"site sw.js precaches {url}, which does not exist")
    for must in ("/", "/offline", "/manifest.webmanifest"):
        if must not in pre:
            bad(f"site sw.js does not precache {must}")
    for a in (PUBLIC / "assets" / "h").glob("*"):
        if not re.fullmatch(r"[\w-]+\.[0-9a-f]{10}\.\w+", a.name):
            bad(f"assets/h/{a.name} is not content-fingerprinted")

    headers = (PUBLIC / "_headers").read_text(encoding="utf-8")
    for needle, why in (("immutable", "fingerprinted assets must be immutable"), ("/sw.js", "sw.js needs its own no-cache rule"), ("manifest-src", "CSP must name manifest-src"), ("worker-src", "CSP must name worker-src")):
        if needle not in headers:
            bad(f"site/_headers: {why} ('{needle}' not found)")
    if re.search(r"script-src[^;\n]*'unsafe-inline'", headers) or re.search(r"script-src[^;\n]*'unsafe-eval'", headers):
        bad("site/_headers: script-src must not allow unsafe-inline or unsafe-eval")

    site_map = (PUBLIC / "sitemap.xml").read_text(encoding="utf-8")
    for loc in re.findall(r"<loc>([^<]+)</loc>", site_map):
        t = resolve(PUBLIC, urlsplit(loc).path or "/", "/")
        if t is None or not t.is_file():
            bad(f"sitemap.xml lists {loc}, which does not exist")


def check_hub() -> None:
    index = ADMIN / "index.html"
    if not index.is_file():
        bad("worker/admin/index.html is missing")
        return
    p = parse(index)
    check_page_meta("hub /", p, need_h1=False)  # the h1 is rendered per view by admin.js
    for tag, url in p.refs:
        target = resolve(ADMIN, url, "/")
        if target is not None and not target.is_file():
            bad(f"hub /: <{tag}> points at {url}, which does not exist")
    check_manifest(ADMIN, ADMIN / "manifest.webmanifest", "hub")
    for f in ("sw.js", "pwa.js", "admin.js", "admin.css", "_headers"):
        if not (ADMIN / f).is_file():
            bad(f"worker/admin/{f} is missing")
    if (ADMIN / "sw.js").is_file():
        for url in precache_list(ADMIN / "sw.js"):
            t = resolve(ADMIN, url, "/")
            if t is None or not t.is_file():
                bad(f"hub sw.js precaches {url}, which does not exist")
        sw = (ADMIN / "sw.js").read_text(encoding="utf-8")
        if "/api/" not in sw:
            bad("hub sw.js must explicitly skip /api/ (private data and the GitHub sign-in redirect)")
    if "pwa.js" not in index.read_text(encoding="utf-8"):
        bad("hub index.html does not load pwa.js (no service worker registration)")
    headers = (ADMIN / "_headers").read_text(encoding="utf-8") if (ADMIN / "_headers").is_file() else ""
    for needle in ("worker-src", "manifest-src", "frame-ancestors"):
        if needle not in headers:
            bad(f"worker/admin/_headers: CSP is missing {needle}")


def main() -> int:
    check_site()
    check_hub()
    if problems:
        print(f"{len(problems)} problem(s):", file=sys.stderr)
        for line in problems:
            print("  - " + line, file=sys.stderr)
        return 1
    print("site + hub PWA checks: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
