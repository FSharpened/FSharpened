#!/usr/bin/env python3
"""Generate the static website (and its PWA files) into site/public. Standard library only.

    python tools/site/icons.py              # once, or when the mark changes (output is committed)
    cd ide/web && npm ci && npm run build   # the IDE bundle -> site/dist/ide.js
    python tools/site/build.py [--require-ide]
    python tools/site/check.py              # validates the output

Inputs:  site/src (hand-written CSS/JS/service worker), site/static (copied as is), branding/icons, site/dist/ide.js,
         registry/pillars.json (so the pillar list can never drift).
Outputs: site/public (git-ignored). Files under assets/h/ carry a content hash in their name, so they can be cached
         forever and a deploy can never mix an old script with new markup. HTML, the manifest and sw.js are revalidated.
Links use Cloudflare Pages "clean URLs" (/why, not /why.html), so no navigation pays for a redirect and the
service worker never has to cache a redirected response.
"""
import hashlib, html, json, os, pathlib, shutil, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC, STATIC, DIST = ROOT / "site" / "src", ROOT / "site" / "static", ROOT / "site" / "dist"
OUT = ROOT / "site" / "public"
ICONS = ROOT / "branding" / "icons"
REPO = "https://github.com/FSharpened/FSharpened"
SITE_URL = os.environ.get("SITE_URL", "https://fsharpened.pages.dev").rstrip("/")
GATEWAY = "https://ide.fsharpened.workers.dev"
reg = json.loads((ROOT / "registry/pillars.json").read_text(encoding="utf-8"))
e = html.escape

NAV = [("/", "Home"), ("/ide/", "IDE"), ("/why", "Why"), ("/how-it-works", "How it works"), ("/pillars", "Pillars"),
       ("/platforms", "Platforms"), ("/status", "Status"), ("/contribute", "Contribute")]
TAGLINE = "Sharpen your existing F#. Not a fork. An extension."


# ---------------------------------------------------------------------------------------------- output plumbing
def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write(rel: str, data) -> None:
    path = OUT / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(data, str):
        path.write_text(data, encoding="utf-8", newline="\n")
    else:
        path.write_bytes(data)


known_assets: dict[str, str] = {}  # url -> content digest, for everything fingerprinted


def fingerprint(source: pathlib.Path, stem: str, ext: str) -> str:
    """Copy `source` to assets/h/<stem>.<hash><ext> and return its URL."""
    data = source.read_bytes()
    name = f"{stem}.{digest(data)[:10]}{ext}"
    write(f"assets/h/{name}", data)
    url = f"/assets/h/{name}"
    known_assets[url] = digest(data)
    return url


# ---------------------------------------------------------------------------------------------- page chrome
ICON_THEME = (
    '<svg class="i-light" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>'
    '<svg class="i-dark" viewBox="0 0 24 24" aria-hidden="true"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>'
    '<svg class="i-auto" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="M12 3v18" /><path d="M12 3a9 9 0 0 1 0 18z" fill="currentColor"/></svg>'
)
ICON_MENU = (
    '<svg class="i-open" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16"/></svg>'
    '<svg class="i-close" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg>'
)


def page(path, title, desc, body, assets, *, ide=False, noindex=False):
    """`path` is the canonical URL path. `assets` maps logical names to fingerprinted URLs."""
    nav = "".join(
        f'<a href="{p}"{" aria-current=page" if p == path else ""}>{t}</a>' for p, t in NAV
    ) + f'<a class="btn" href="{REPO}">GitHub</a>'
    full_title = "FSharpened: Sharpen your existing F#" if path == "/" else f"{title} · FSharpened"
    canonical = SITE_URL + path
    viewport = "width=device-width,initial-scale=1,viewport-fit=cover" + (",interactive-widget=resizes-content" if ide else "")
    robots = '<meta name="robots" content="noindex">' if noindex else ""
    head_extra = f'<link rel="stylesheet" href="{assets["ide.css"]}">' if ide and "ide.css" in assets else ""
    foot_extra = f'<script type="module" src="{assets["ide.js"]}"></script>' if ide and "ide.js" in assets else ""
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="{viewport}">
<title>{e(full_title)}</title><meta name="description" content="{e(desc)}">{robots}
<link rel="canonical" href="{canonical}">
<meta name="color-scheme" content="light dark">
<meta name="theme-color" content="#ffffff" media="(prefers-color-scheme: light)" data-c="#ffffff" data-m="(prefers-color-scheme: light)">
<meta name="theme-color" content="#0b1020" media="(prefers-color-scheme: dark)" data-c="#0b1020" data-m="(prefers-color-scheme: dark)">
<link rel="manifest" href="/manifest.webmanifest">
<link rel="icon" href="/favicon.svg" type="image/svg+xml"><link rel="icon" href="/assets/favicon-32.png" type="image/png" sizes="32x32"><link rel="icon" href="/favicon.ico" sizes="48x48">
<link rel="apple-touch-icon" href="/assets/icon-180.png">
<meta name="application-name" content="FSharpened"><meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-capable" content="yes"><meta name="apple-mobile-web-app-title" content="FSharpened"><meta name="apple-mobile-web-app-status-bar-style" content="default">
<meta property="og:site_name" content="FSharpened"><meta property="og:title" content="{e(full_title)}"><meta property="og:description" content="{e(desc)}"><meta property="og:type" content="website"><meta property="og:url" content="{canonical}">
<meta property="og:image" content="{SITE_URL}/assets/og.png"><meta property="og:image:width" content="1200"><meta property="og:image:height" content="630"><meta property="og:image:alt" content="FSharpened: an F with a sharpened diagonal arm and a cyan diamond">
<meta name="twitter:card" content="summary_large_image"><meta name="twitter:image:alt" content="FSharpened: an F with a sharpened diagonal arm and a cyan diamond">
<script src="{assets["theme.js"]}"></script>
<link rel="stylesheet" href="{assets["site.css"]}">{head_extra}</head><body class="{"ide-page" if ide else ""}">
<a class="skip" href="#main">Skip to content</a>
<header class="site"><div class="wrap"><a class="brand" href="/"><img src="/assets/mark.svg" alt="" width="30" height="30">FSharpened</a>
<nav class="main" id="nav" aria-label="Main">{nav}</nav>
<div class="tools"><button class="install" id="install" type="button" hidden>Install</button>
<button class="icon-btn" id="theme" type="button" data-mode="auto" aria-label="Theme: automatic. Switch to light" title="Theme">{ICON_THEME}</button>
<button class="icon-btn menu-btn" id="menu" type="button" aria-expanded="false" aria-controls="nav" aria-label="Open menu">{ICON_MENU}</button></div></div></header>
<main id="main" tabindex="-1">{body}</main>
<footer class="site"><div class="wrap"><div><strong>FSharpened</strong><br>{TAGLINE}</div>
<div><a href="{REPO}">Source</a> · <a href="{REPO}/blob/main/docs/spec/SPEC.md">Specification</a> · <a href="{REPO}/tree/main/docs/adr">ADRs</a> · <a href="{REPO}/blob/main/SECURITY.md">Security</a></div>
<div>Apache-2.0. Maintained by humans and agents together.</div></div></footer>
<script src="{assets["site.js"]}" defer></script>{foot_extra}</body></html>
"""


def head(h, p):
    return f'<div class="page-head wrap"><h1>{h}</h1><p class="sub">{p}</p></div>'


# ---------------------------------------------------------------------------------------------- page content
pillars = reg["pillars"]
by_layer = {"substrate": "Substrate", "core": "Product", "ml": "Machine learning"}

cards = [
    ("Not a fork", "Zero changes to the F# compiler. Every capability routes through FSharp.Compiler.Service, the analyzers SDK, NuGet or LSP. When F# ships a fix, you get it for free."),
    ("Everywhere, first-class", "Windows, Linux and macOS on x64 and arm64 are equal citizens. No component is privileged; platform behaviour lives in exactly one pillar."),
    ("Rollback is a primitive", "Statepoints give every tool one way to snapshot, fork, roll back and merge state. Hot reload, AI edits and training all use the same substrate."),
    ("Agents-ready by design", "Semantic grounding over MCP, deterministic fallbacks and machine-checked invariants, so agents and humans maintain it side by side."),
    ("Trustworthy ML", "ONNX-first inference, signed model cards, frozen judges and a five-layer fallback chain. Models never bypass telemetry, security or statepoints."),
    ("Compose, don't rebuild", "Fantomas, FSharp.Analyzers.SDK, netcoredbg, Ionide: we wrap and connect the best tools instead of competing with them."),
]
home = f"""<section class="hero"><div class="wrap">
<span class="pill">Open source · Apache-2.0 · Early skeleton</span>
<h1>Sharpen your existing <em>F#</em>.</h1>
<p class="lead">A suite of libraries, analyzers, tools and pipelines that plug into the F# compiler services, the .NET SDK and your editor. Not a fork. An extension.</p>
<div class="cta"><a class="btn" href="/ide/">Open the IDE</a><a class="btn ghost" href="/how-it-works">How it works</a><a class="btn ghost" href="/why">Why it exists</a><a class="btn ghost" href="{REPO}">View on GitHub</a></div>
</div></section>
<section><div class="wrap"><h2>What you get</h2><p class="sub">Six principles shape every pillar.</p>
<div class="grid">{''.join(f'<div class="card"><h3>{e(a)}</h3><p>{e(b)}</p></div>' for a, b in cards)}</div></div></section>
<section class="alt"><div class="wrap"><h2>Try the CLI</h2><p class="sub">The <code>fsh</code> command line is the front door. Today it ships <code>doctor</code>; the rest of the surface is on the roadmap.</p>
<pre tabindex="0"><code>git clone {REPO}.git
cd FSharpened
python tools/ci/fsh_ci.py run      # the same pipeline CI runs
dotnet run --project tools/FSharpened.Cli -- doctor</code></pre></div></section>
<section><div class="wrap"><h2>{len(pillars)} pillars, one registry</h2><p class="sub">Substrates, product pillars and machine-learning pillars, all declared in one machine-checked registry.</p>
<a class="btn" href="/pillars">Explore the pillars</a></div></section>"""

why = head("Why FSharpened is needed", "F# is excellent. The tooling around it has gaps that every team ends up patching privately.") + """
<section><div class="wrap prose">
<h2>The problem</h2>
<p>F# has a superb compiler and a thoughtful language, but the surrounding developer experience is thinner than in the ecosystems it competes with. Teams rebuild the same glue again and again: analyzers, reliable hot reload, safe refactoring, reproducible state, editor-agnostic tooling and, increasingly, a safe way to let AI agents work on code.</p>
<h2>Why not a fork?</h2>
<p>A compiler fork is a permanent maintenance debt and splits the community. FSharpened takes the opposite stance: <strong>never fork</strong>. Everything routes through public extension points (FCS, FSharp.Analyzers.SDK, NuGet, LSP), so improvements upstream flow in and ours can flow back.</p>
<h2>What is missing today</h2>
<ul><li><strong>One state model.</strong> Hot reload, debugging, refactoring and AI edits each invent their own checkpoints. Statepoints give them one.</li>
<li><strong>Trustworthy agent grounding.</strong> Agents guess at symbols. Semantic resolution over MCP gives them facts.</li>
<li><strong>True cross-platform parity.</strong> Tooling is too often Linux-first or Windows-only. Here every tier-1 RID is a gate.</li>
<li><strong>Safe ML in the dev loop.</strong> Models need signing, frozen judges, budgets and deterministic fallbacks to be allowed near your build.</li></ul>
<h2>Who it is for</h2>
<p>F# developers who want better tooling without leaving the ecosystem, teams that want agents to help safely, and contributors who want a well-governed project with written invariants and recorded decisions.</p>
<p><a class="btn" href="/how-it-works">See how it works</a></p>
</div></section>"""

steps = [
    ("Wrap the platform", "FSharpened.Platform hides every OS difference (file watching, IPC, paths, sandboxing, terminals) behind interfaces and a capability manifest. Nothing else may use <code>#if</code> on a platform."),
    ("Share one state substrate", "FSharpened.Statepoints stores content-addressed snapshots with fork, rollback and merge. Hot reload sessions, AI edits and training runs are all statepoint consumers."),
    ("Ground everything semantically", "FSharpened.Semantic exposes symbol resolution over the F# compiler services and MCP, so tools and agents work from facts, not text guesses."),
    ("Compose the best tools", "Product pillars connect Fantomas, analyzers, debuggers and editors. We write glue and contracts, not replacements."),
    ("Add ML behind contracts", "ML pillars reach frameworks only through ML.Portability, pick a backend per device through ML.Hardware, and always end in a deterministic fallback chain."),
    ("Prove it on every RID", "One local-first pipeline (<code>fsh_ci.py</code>) runs identically in hooks, on your machine and in the six-RID GitHub matrix, including NativeAOT."),
]
how = head("How it works", "A layered satellite architecture around the F# toolchain.") + f"""
<section><div class="wrap"><div class="steps">{''.join(f'<div class="card"><h3>{a}</h3><p>{b}</p></div>' for a, b in steps)}</div></div></section>
<section class="alt"><div class="wrap prose"><h2>The fallback chain</h2>
<p>Any model-backed feature resolves through five deterministic layers, so the tool keeps working when a model is missing, slow or untrusted:</p>
<ol><li>Specialised model</li><li>Sibling model</li><li>Symbolic rules</li><li>Warm checkpoint</li><li>Structured escalation to a human</li></ol>
<h2>Invariants, not vibes</h2><p>Fourteen constitutional invariants are written down, mirrored in the registry and checked mechanically. They are what lets agents and humans change the project safely for years.</p>
<p><a class="btn" href="{REPO}/blob/main/MAINTAINER.md">Read the invariants</a></p></div></section>"""


def pcard(p):
    wraps = f'<p>Wraps: {e(", ".join(p["wraps"]))}</p>' if p["wraps"] else ""
    return (f'<div class="card" data-layer="{p["layer"]}" data-stab="{p["stability"]}"><span class="tag {"p0" if p["priority"]=="P0" else ""}">{p["priority"]}</span>'
            f'<span class="tag">{by_layer[p["layer"]]}</span><span class="tag">{p["stability"]}</span><h3>{e(p["title"])}</h3>'
            f'<p><code>FSharpened.{e(p["id"])}</code></p><p>{e(p["summary"])}</p>{wraps}</div>')


filt = '<div class="filters" role="group" aria-label="Filter pillars"><button aria-pressed="true" data-f="all">All</button>' + "".join(f'<button aria-pressed="false" data-f="{k}">{v}</button>' for k, v in by_layer.items()) + "</div>"
pil = head("Pillars", f"{len(pillars)} independently versioned packages plus tools, declared in registry/pillars.json.") + f"""
<section><div class="wrap">{filt}<div class="grid">{''.join(pcard(p) for p in pillars)}</div>
<h2 class="pad">Tools</h2><div class="grid">{''.join(f'<div class="card"><h3>{e(t["title"])}</h3><p><code>{e(t.get("command", t["project"]))}</code></p><p>{e(t["summary"])}</p></div>' for t in reg["tools"])}</div></div></section>"""

rids = [("Windows", "x64, arm64"), ("Linux", "x64, arm64"), ("macOS", "x64, arm64 (Apple silicon)")]
plat = head("Platforms", "Nothing is Linux-first. Everything must work on every tier.") + f"""
<section><div class="wrap"><h2>Tier 1: gated on every change</h2><div class="grid">{''.join(f'<div class="card"><h3>{a}</h3><p>{b}</p></div>' for a, b in rids)}</div>
<h2 class="pad">Tier 2 and 3</h2><div class="grid"><div class="card"><h3>Tier 2</h3><p>WSL, containers, Windows Server. Supported, tested on a schedule.</p></div><div class="card"><h3>Tier 3</h3><p>Android and iOS. A stated goal; contracts exist so they can be added without rewrites.</p></div></div>
<p class="sub pad">Every pull request runs the six-RID matrix, including a NativeAOT publish that executes the produced binary. This website and the IDE are installable apps that work in any current browser on any of these platforms.</p></div></section>"""

status = head("Status", "Honest and current: this is an early skeleton, not a finished product.") + f"""
<section><div class="wrap prose"><p class="notice">All {len(pillars)} pillars exist as buildable projects. Platform, Statepoints, Effects, Diagnostics, ML.Portability, ML.Hardware and Models contain real code; the rest are typed markers awaiting implementation.</p>
<h2>What works today</h2><ul><li>Solution builds and the contract and IDE tests pass</li><li><code>fsh doctor</code> runs, including as a NativeAOT binary</li><li>A browser <a href="/ide/">IDE</a> for 18 languages (F#, C#, VB.NET, every Fable target and more) built on the F# core and Statepoints</li><li>Local CI pipeline with preflight, caching, reports and git hooks</li><li>Six-RID GitHub matrix</li><li>Docs-drift checks keep the registry, docs and skills consistent</li></ul>
<h2>What is next</h2><p>See the <a href="{REPO}/blob/main/PLAN.md">plan</a> and <a href="{REPO}/blob/main/TODO.md">todo</a>. The canonical status lives in <a href="{REPO}/blob/main/docs/STATUS.md">docs/STATUS.md</a>.</p></div></section>"""

contrib = head("Contribute", "Built by humans and agents together, governed by written rules.") + f"""
<section><div class="wrap prose"><div class="steps">
<div class="card"><h3>Set up</h3><p>Install the .NET SDK 9.0.200+ and Python 3, then run <code>python tools/ci/fsh_ci.py install-hooks</code>.</p></div>
<div class="card"><h3>Pick work</h3><p>Choose an item from the <a href="{REPO}/blob/main/TODO.md">TODO</a>, branch as <code>feat/&lt;pillar&gt;-&lt;short&gt;</code>.</p></div>
<div class="card"><h3>Follow the order</h3><p>ADR, canonical doc, contract tests, implementation, platform matrix, docs map.</p></div>
<div class="card"><h3>Open a PR</h3><p>Say which RIDs you could not verify. A reviewer other than the author approves; merging is human-gated.</p></div></div>
<p><a class="btn" href="{REPO}/blob/main/CONTRIBUTING.md">Contributing guide</a> <a class="btn ghost" href="{REPO}/issues">Issues</a></p>
<p>Security issues: please do not open a public issue, see <a href="{REPO}/blob/main/SECURITY.md">SECURITY.md</a>.</p></div></section>"""

notfound = head("Page not found", "That page does not exist.") + '<section><div class="wrap"><a class="btn" href="/">Back home</a></div></section>'

offline = """<section class="offline-page"><div class="wrap"><h1>You are offline</h1>
<p class="sub">This page is not saved on your device yet. Pages you have already opened still work without a connection, and the IDE still runs its offline checks.</p>
<div class="cta"><button class="btn" type="button" data-retry>Try again</button><a class="btn ghost" href="/">Home</a><a class="btn ghost" href="/ide/">IDE</a><a class="btn ghost" href="/pillars">Pillars</a></div></div></section>"""

ide_body = """<div class="ide" id="ide">
<h1 class="sr-only">FSharpened IDE: edit, compile and run F# and 17 other languages in polyglot projects</h1>
<div class="bar" id="bar">
  <button id="btn-side" class="ghost side-toggle" type="button" aria-expanded="true" aria-controls="side" title="Show or hide the files and projects (Ctrl+B)">Files</button>
  <div class="tabs" id="ftabs" role="tablist" aria-label="Open files"></div>
  <div class="opts" id="opts">
    <label id="compiler-wrap">Compiler <select id="compiler" aria-label="Compiler"></select></label>
    <label id="options-wrap">Flags <input id="options" type="text" spellcheck="false" autocapitalize="off" autocomplete="off" aria-label="Compiler flags"></label>
    <label id="template-wrap">Examples <select id="template" aria-label="Load an example"></select></label>
  </div>
  <span class="grow"></span>
  <div class="acts" id="acts">
    <button id="btn-palette" class="ghost" type="button" aria-haspopup="dialog" title="Command palette: every command by name (F1 or Ctrl+Shift+P)">Commands</button>
    <button id="btn-snap" class="ghost" type="button" title="Save a statepoint of the whole project (Ctrl+S)">Snapshot</button>
    <button id="btn-share" class="ghost" type="button" title="Copy a link containing this project">Share</button>
    <button id="btn-settings" class="ghost" type="button" aria-haspopup="dialog" title="Settings, colour theme and keyboard shortcuts (Ctrl+,)">Settings</button>
  </div>
  <button id="btn-opts" class="ghost opts-toggle" type="button" aria-expanded="false" aria-controls="opts acts">Options</button>
  <button id="btn-run" class="primary" type="button" title="Compile and run (Ctrl+Enter)">&#9654; Run</button>
</div>
<div class="work" id="work">
<aside class="side" id="side" aria-label="Project">
  <div class="stabs" role="tablist" aria-label="Sidebar">
    <button role="tab" type="button" data-s="files" aria-selected="true">Files</button>
    <button role="tab" type="button" data-s="search" aria-selected="false" tabindex="-1" title="Search in the project (Ctrl+Shift+F)">Search</button>
    <button role="tab" type="button" data-s="outline" aria-selected="false" tabindex="-1" title="Declarations in the open file (Ctrl+Shift+O)">Outline</button>
    <button role="tab" type="button" data-s="projects" aria-selected="false" tabindex="-1">Projects</button>
    <button role="tab" type="button" data-s="extensions" aria-selected="false" tabindex="-1" title="VS Code themes, snippets and settings; using VS Code with this project">VS Code</button>
    <button id="btn-side-close" class="x side-close" type="button" aria-label="Close the sidebar">&times;</button>
  </div>
  <form id="askbar" class="askbar" role="group" aria-labelledby="ask-label" hidden><label id="ask-label" for="ask-input"></label><input id="ask-input" type="text" spellcheck="false" autocapitalize="off" autocomplete="off" maxlength="200"><span class="ask-btns"><button type="submit" class="mini" id="ask-ok">OK</button><button type="button" class="mini" id="ask-cancel">Cancel</button></span><p id="ask-error" class="ask-error" role="alert" hidden></p></form>
  <div class="spanel" data-s="files">
    <div class="shead"><strong id="proj-name" class="pname"></strong><span id="proj-meta" class="muted"></span></div>
    <div class="toolbar" role="toolbar" aria-label="File actions">
      <button id="f-new" class="mini" type="button" title="Create a file; folders in its path are made as needed">New file</button>
      <button id="f-rename" class="mini" type="button" title="Rename or move the selected file or folder (F2)">Rename</button>
      <button id="f-delete" class="mini" type="button" title="Delete the selected file or folder (undo from the Timeline)">Delete</button>
      <button id="f-entry" class="mini" type="button" title="Make the selected file the one Run starts from">Set entry</button>
      <button id="f-copy" class="mini" type="button" title="Copy the open file to the clipboard">Copy</button>
      <button id="f-download" class="mini" type="button" title="Download the selected file, or the selected folder as a .zip">Download</button>
    </div>
    <p class="muted hint">Right-click a file, a folder or a tab for more.</p>
    <div id="tree" class="tree"></div>
  </div>
  <div class="spanel" data-s="search" hidden>
    <form id="sr-form" class="srform" role="search" aria-label="Search the project">
      <div class="srrow"><input id="sr-q" type="text" placeholder="Search" aria-label="Search the project for" spellcheck="false" autocapitalize="off" autocomplete="off" maxlength="500">
        <span class="srtoggles"><button id="sr-case" type="button" aria-pressed="false" title="Match case (Alt+C)" aria-label="Match case" data-glyph="Aa"></button><button id="sr-word" type="button" aria-pressed="false" title="Whole word (Alt+W)" aria-label="Whole word" data-glyph="ab"></button><button id="sr-re" type="button" aria-pressed="false" title="Regular expression (Alt+R)" aria-label="Regular expression" data-glyph=".*"></button></span></div>
      <div class="srrow"><input id="sr-r" type="text" placeholder="Replace" aria-label="Replace with" spellcheck="false" autocapitalize="off" autocomplete="off" maxlength="500"><button id="sr-replace-all" class="mini" type="button" disabled title="Replace every match in the project">Replace all</button></div>
      <details id="sr-more" class="srmore"><summary>Files to include or exclude</summary>
        <label>Include <input id="sr-inc" type="text" placeholder="src/**, *.rs" spellcheck="false" autocapitalize="off" autocomplete="off"></label>
        <label>Exclude <input id="sr-exc" type="text" placeholder="**/test/**, *.md" spellcheck="false" autocapitalize="off" autocomplete="off"></label>
      </details>
    </form>
    <p id="sr-sum" class="muted hint" role="status" aria-live="polite"></p>
    <div id="sr-results" class="srresults"></div>
  </div>
  <div class="spanel" data-s="outline" hidden>
    <div class="shead"><strong class="pname">Outline</strong><span id="ol-note" class="muted">A quick index of declarations, not a full parse.</span></div>
    <div class="srrow olrow"><input id="ol-filter" type="text" placeholder="Filter symbols" aria-label="Filter the symbols of the open file" spellcheck="false" autocapitalize="off" autocomplete="off"></div>
    <ul id="ol-list" class="ollist"></ul>
  </div>
  <div class="spanel" data-s="projects" hidden>
    <div class="toolbar" role="toolbar" aria-label="Project actions">
      <button id="p-new" class="mini" type="button">New project</button>
      <button id="p-starter" class="mini" type="button" title="A new project with one starter program per language">Examples</button>
      <button id="p-dup" class="mini" type="button">Duplicate</button>
      <button id="p-rename" class="mini" type="button">Rename</button>
      <button id="p-delete" class="mini" type="button">Delete</button>
      <button id="p-zip" class="mini" type="button" title="Download the whole project as a .zip you can import again">Download .zip</button>
    </div>
    <div class="toolbar" role="toolbar" aria-label="Import">
      <button id="p-import-zip" class="mini" type="button" title="Open a .zip as a new project">Import .zip</button>
      <button id="p-import-folder" class="mini" type="button" title="Open a folder from your computer as a new project">Import folder</button>
      <button id="p-add" class="mini" type="button" title="Add files to the open project">Add files</button>
    </div>
    <p class="muted hint">Drop a .zip, files or a folder anywhere on the editor to import it.</p>
    <p id="p-storage" class="muted hint"></p>
    <section class="pipe" aria-labelledby="pipe-h">
      <h3 id="pipe-h">Pipeline</h3>
      <p id="pipe-hint" class="muted hint"></p>
      <ol id="pipe-list" class="pipelist"></ol>
      <div class="toolbar" role="toolbar" aria-label="Pipeline actions">
        <button id="pipe-add" class="mini" type="button" title="Add the open file as the next step">Add open file</button>
        <button id="pipe-run" class="mini" type="button" title="Run every step in order">Run pipeline</button>
        <button id="pipe-clear" class="mini" type="button">Clear</button>
      </div>
      <details class="pipe-support"><summary>Which languages can run several files</summary><div id="pipe-support"></div></details>
    </section>
    <ul id="plist" class="plist"></ul>
    <div id="report" class="report" hidden></div>
  </div>
  <div class="spanel xpanel" data-s="extensions" hidden>
    <div class="shead"><strong class="pname">VS Code and this IDE</strong></div>
    <div class="xtext">
      <p><strong>Works here:</strong> colour themes, snippets and settings from VS Code, read as plain data. Drop or pick a <code>.vsix</code>, a theme <code>.json</code>, a snippets file or <code>settings.json</code>.</p>
      <p><strong>Works in VS Code:</strong> download this project with its VS Code settings and recommended extensions, including <strong>GitHub Copilot</strong> and Copilot Chat.</p>
      <details class="xwhy"><summary>Why Copilot and other extensions do not run in this page</summary>
        <p>A VS Code extension is a program written against VS Code's own API, and it runs inside VS Code's extension host. GitHub Copilot is one: it is GitHub's proprietary extension, it needs your GitHub sign-in, and its suggestions come from GitHub's service. A web page cannot host that API, and this page deliberately refuses to run code it did not ship (its content security policy), which is also what keeps your projects safe from a bad extension.</p>
        <p>So the IDE does not pretend. It takes what is data, and for everything else it hands you a project that VS Code opens correctly, with the extensions to install already recommended. Planned and not built yet: a language-server bridge and an AI completion provider (ADR-0022, proposed).</p>
      </details>
    </div>
    <div class="toolbar" role="toolbar" aria-label="VS Code import and export">
      <button id="x-import" class="mini" type="button" title="Import a .vsix, a colour theme, a snippets file or settings.json">Import…</button>
      <button id="x-download" class="mini" type="button" title="Download the project as a .zip with .vscode/extensions.json, settings and a .code-workspace file">Download for VS Code</button>
    </div>
    <div id="x-report" class="report" hidden></div>
    <h3 class="xh">Imported</h3>
    <ul id="x-list" class="xlist"></ul>
    <h3 class="xh">Recommended for this project in VS Code</h3>
    <ul id="x-recs" class="xlist xrecs"></ul>
  </div>
  <input id="pick-zip" type="file" accept=".zip,application/zip" hidden>
  <input id="pick-folder" type="file" webkitdirectory hidden>
  <input id="pick-files" type="file" multiple hidden>
  <input id="pick-ext" type="file" accept=".vsix,.json,.code-snippets,application/json" multiple hidden>
</aside>
<div class="split" id="split">
  <div class="edcol">
    <nav id="crumbs" class="crumbs" aria-label="Breadcrumbs: file path and the symbols around the caret" hidden></nav>
    <div id="editor" class="editor" role="group" aria-label="Code editor. Press Escape, then Tab, to leave the editor."><div id="empty" class="empty" hidden><p><strong>This project has no files.</strong></p><p class="muted">Create one with New file, or drop a .zip or a folder here.</p></div></div>
  </div>
  <div id="sep" class="sep" role="separator" aria-orientation="vertical" aria-label="Resize the editor and output panels" tabindex="0"></div>
  <div class="bottom">
    <div class="ptabs" role="tablist" aria-label="Panels">
      <button data-p="output" aria-selected="true" role="tab" type="button">Output</button>
      <button data-p="problems" aria-selected="false" role="tab" type="button">Problems <span id="pcount" class="badge"></span></button>
      <button data-p="timeline" aria-selected="false" role="tab" type="button">Timeline <span id="tcount" class="badge"></span></button>
      <button data-p="stdin" aria-selected="false" role="tab" type="button">Input</button>
    </div>
    <p id="livenote" class="muted livenote" role="status" hidden></p>
    <div class="panel" data-p="output"><div id="summary" role="status"></div><div id="runinfo" class="runinfo"></div><ol id="steps" class="steps" hidden></ol><pre id="stdout" tabindex="0"></pre><pre id="stderr" class="err" tabindex="0"></pre></div>
    <div class="panel" data-p="problems" hidden><div id="problems"></div></div>
    <div class="panel" data-p="timeline" hidden><p class="muted">Every run and snapshot is a <strong>statepoint</strong> of the whole project, from FSharpened.Statepoints. Restore rolls back; Fork branches without touching the source.</p><ul id="timeline"></ul><div id="diff" hidden></div></div>
    <div class="panel" data-p="stdin" hidden><label for="stdin">Standard input for your program</label><textarea id="stdin" rows="6" spellcheck="false"></textarea></div>
  </div>
</div>
</div>
<div class="statusbar"><a class="home-link" href="/">&larr; FSharpened</a><span id="status" role="status" aria-live="polite">Loading…</span><span class="grow"></span>
  <span class="stitems" id="stitems" role="group" aria-label="Editor status" hidden>
    <button id="st-pos" class="stbtn" type="button" title="Go to line (Ctrl+G)">Ln 1, Col 1</button>
    <span id="st-sel" class="stsel"></span>
    <button id="st-indent" class="stbtn" type="button" title="Change indentation">Spaces: 4</button>
    <span id="st-lang" class="stlang"></span>
    <button id="st-eol" class="stbtn" type="button" title="Change the end-of-line sequence of this file">LF</button>
    <span class="stenc">UTF-8</span>
  </span>
  <span class="muted note">Runs on <a href="https://godbolt.org" rel="noopener">Compiler Explorer</a> and <a href="https://wandbox.org" rel="noopener">Wandbox</a> sandboxes. Your code is sent to the one that serves your language when you press Run, and, only if you turn on Live check in Settings, after you pause typing; do not paste secrets.</span></div>
<dialog id="palette" class="pal" aria-labelledby="pal-title">
  <h2 id="pal-title" class="sr-only">Command palette</h2>
  <input id="pal-input" type="text" role="combobox" aria-expanded="true" aria-controls="pal-list" aria-autocomplete="list" autocomplete="off" autocapitalize="off" spellcheck="false" aria-label="Type to search">
  <p id="pal-hint" class="pal-hint muted"></p>
  <ul id="pal-list" role="listbox" aria-label="Results"></ul>
</dialog>
<dialog id="settings" class="dlg" aria-labelledby="set-title">
  <form method="dialog" class="dlgform">
    <div class="dlghead"><h2 id="set-title">Settings</h2><button class="x" value="close" aria-label="Close settings">&times;</button></div>
    <div class="dlgbody">
      <label class="setrow">Colour theme <select id="set-theme" autofocus></select></label>
      <p id="set-theme-note" class="muted hint"></p>
      <label class="setrow">Editor font size <input id="set-font" type="number" inputmode="numeric" step="1"></label>
      <label class="setrow">Tab size <input id="set-tab" type="number" inputmode="numeric" step="1"></label>
      <label class="setcheck"><input id="set-spaces" type="checkbox"> Indent with spaces (otherwise with tabs)</label>
      <label class="setcheck"><input id="set-wrap" type="checkbox"> Word wrap</label>
      <label class="setcheck"><input id="set-lines" type="checkbox"> Line numbers</label>
      <label class="setcheck"><input id="set-ws" type="checkbox"> Show whitespace</label>
      <label class="setcheck"><input id="set-close" type="checkbox"> Close brackets and quotes automatically</label>
      <label class="setcheck"><input id="set-crumbs" type="checkbox"> Breadcrumbs above the editor</label>
      <label class="setcheck"><input id="set-live" type="checkbox" aria-describedby="set-live-note"> Live check: ask the compiler as I type</label>
      <p id="set-live-note" class="muted hint">Off by default. When on, a moment after you stop typing your code is sent to Compiler Explorer or Wandbox and <em>run</em> there, the same as pressing Run, so errors show as you write. At most 4 checks a minute share the gateway's limit with Run; it pauses when asked to, and an edit cancels the check in progress.</p>
      <p class="muted hint">Settings stay in this browser. Colour themes from VS Code can be added in the VS Code tab.</p>
    </div>
    <div class="dlgfoot"><button type="button" id="set-reset" class="mini">Reset to defaults</button><button type="button" id="set-shortcuts" class="mini">Keyboard shortcuts</button><button value="close" class="mini">Close</button></div>
  </form>
</dialog>
<dialog id="shortcuts" class="dlg wide" aria-labelledby="sc-title">
  <form method="dialog" class="dlgform">
    <div class="dlghead"><h2 id="sc-title">Keyboard shortcuts</h2><button class="x" value="close" aria-label="Close the shortcut list">&times;</button></div>
    <div class="dlgbody" id="sc-body"></div>
    <div class="dlgfoot"><span class="muted hint">Hold Shift while right-clicking to get the browser's own menu.</span><button value="close" class="mini">Close</button></div>
  </form>
</dialog>
</div><div id="toast" role="status" aria-live="polite"></div>
<noscript><p class="wrap">The IDE needs JavaScript.</p></noscript>"""

PAGES = {  # path -> (file, title, description, body, options)
    "/": ("index.html", "Home", "A satellite suite of libraries, analyzers and tools that extend F# without forking the compiler.", home, {}),
    "/ide/": ("ide/index.html", "IDE", "A browser IDE for F# and 17 other languages, built on FSharpened: edit, compile, run, and time-travel with statepoints.", ide_body, {"ide": True}),
    "/why": ("why.html", "Why", "Why F# needs FSharpened: one state model, grounded agents, true cross-platform parity.", why, {}),
    "/how-it-works": ("how-it-works.html", "How it works", "A layered satellite architecture around FCS, analyzers, NuGet and LSP.", how, {}),
    "/pillars": ("pillars.html", "Pillars", "Every FSharpened pillar and tool, generated from the registry.", pil, {}),
    "/platforms": ("platforms.html", "Platforms", "Windows, Linux and macOS on x64 and arm64 are gated on every change.", plat, {}),
    "/status": ("status.html", "Status", "What works today and what is next.", status, {}),
    "/contribute": ("contribute.html", "Contribute", "How to contribute to FSharpened.", contrib, {}),
    "/offline": ("offline.html", "Offline", "You are offline.", offline, {"noindex": True}),
    "/404.html": ("404.html", "Not found", "Page not found.", notfound, {"noindex": True}),
}
NOT_PRECACHED = {"/404.html"}
NOT_IN_SITEMAP = {"/offline", "/404.html"}


# ---------------------------------------------------------------------------------------------- build
def build(require_ide):
    OUT.mkdir(parents=True, exist_ok=True)
    for child in OUT.iterdir():  # empty the folder, not the folder: on Windows another process may hold it open
        shutil.rmtree(child) if child.is_dir() else child.unlink()
    shutil.copytree(STATIC, OUT, dirs_exist_ok=True)

    for name in ("icon-192.png", "icon-512.png", "icon-maskable-512.png", "icon-180.png", "favicon-32.png"):
        shutil.copyfile(ICONS / name, OUT / "assets" / name)
    shutil.copyfile(ICONS / "favicon.ico", OUT / "favicon.ico")

    assets = {
        "site.css": fingerprint(SRC / "site.css", "site", ".css"),
        "site.js": fingerprint(SRC / "site.js", "site", ".js"),
        "theme.js": fingerprint(SRC / "theme.js", "theme", ".js"),
    }
    ide_js = DIST / "ide.js"
    assets["ide.css"] = fingerprint(SRC / "ide.css", "ide", ".css")
    if ide_js.exists():
        assets["ide.js"] = fingerprint(ide_js, "ide", ".js")
    elif require_ide:
        print("error: site/dist/ide.js is missing. Build it: cd ide/web && npm ci && npm run build", file=sys.stderr)
        sys.exit(1)
    else:
        print("warning: site/dist/ide.js is missing; the IDE page will not start. Build it: cd ide/web && npm ci && npm run build")

    for path, (fname, title, desc, body, opts) in PAGES.items():
        write(fname, page(path, title, desc, body, assets, **opts))

    # ---- PWA: manifest, service worker, sitemap, robots
    manifest = {
        "id": "/",
        "name": "FSharpened",
        "short_name": "FSharpened",
        "description": "Sharpen your existing F#. Docs and a browser IDE for F# and 17 other languages. Works offline.",
        "start_url": "/?source=pwa",
        "scope": "/",
        "display": "standalone",
        "display_override": ["standalone", "minimal-ui", "browser"],
        "orientation": "any",
        "theme_color": "#4338ca",
        "background_color": "#0b1020",
        "lang": "en",
        "dir": "ltr",
        "categories": ["developer", "productivity", "education"],
        "prefer_related_applications": False,
        "launch_handler": {"client_mode": ["navigate-existing", "auto"]},
        "icons": [
            {"src": "/assets/icon-192.png", "sizes": "192x192", "type": "image/png", "purpose": "any"},
            {"src": "/assets/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any"},
            {"src": "/assets/icon-maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable"},
            {"src": "/favicon.svg", "sizes": "any", "type": "image/svg+xml", "purpose": "any"},
        ],
        "shortcuts": [
            {"name": "Open the IDE", "short_name": "IDE", "description": "Write and run F#, C#, VB.NET, JavaScript, Python, Rust and more", "url": "/ide/?source=shortcut", "icons": [{"src": "/assets/icon-192.png", "sizes": "192x192", "type": "image/png"}]},
            {"name": "Pillars", "short_name": "Pillars", "description": "Every package in the suite", "url": "/pillars?source=shortcut", "icons": [{"src": "/assets/icon-192.png", "sizes": "192x192", "type": "image/png"}]},
            {"name": "Project status", "short_name": "Status", "description": "What works today", "url": "/status?source=shortcut", "icons": [{"src": "/assets/icon-192.png", "sizes": "192x192", "type": "image/png"}]},
        ],
    }
    write("manifest.webmanifest", json.dumps(manifest, indent=2) + "\n")

    precache = [p for p in PAGES if p not in NOT_PRECACHED]
    precache += [assets[k] for k in ("site.css", "site.js", "theme.js")]
    precache += ["/assets/mark.svg", "/assets/icon-192.png", "/favicon.svg", "/manifest.webmanifest"]
    known = sorted(u for u in known_assets)
    parts = []
    for u in precache:
        f = OUT / (PAGES[u][0] if u in PAGES else u.lstrip("/"))
        parts.append(u + ":" + digest(f.read_bytes()))
    parts += [u + ":" + known_assets[u] for u in known]
    version = digest("\n".join(parts).encode())[:12]
    sw = (SRC / "sw.js").read_text(encoding="utf-8")
    sw = sw.replace('"__VERSION__"', json.dumps(version)).replace("__PRECACHE__", json.dumps(precache, indent=2)).replace("__KNOWN__", json.dumps(known, indent=2))
    write("sw.js", sw)

    urls = [f"{SITE_URL}{p}" for p in PAGES if p not in NOT_IN_SITEMAP]
    write("sitemap.xml", '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n' + "".join(f"  <url><loc>{u}</loc></url>\n" for u in urls) + "</urlset>\n")
    write("robots.txt", f"User-agent: *\nAllow: /\nDisallow: /offline\n\nSitemap: {SITE_URL}/sitemap.xml\n")
    print(f"site: {len(PAGES)} pages, {len(known_assets)} fingerprinted assets, sw version {version} -> {OUT}")


if __name__ == "__main__":
    build("--require-ide" in sys.argv)
