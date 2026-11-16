#!/usr/bin/env python3
"""Generate the static website into site/public from registry/pillars.json (stdlib only).
Pages are templates here so the pillar list can never drift from the registry."""
import html, json, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "site" / "public"
REPO = "https://github.com/FSharpened/FSharpened"
reg = json.loads((ROOT / "registry/pillars.json").read_text(encoding="utf-8"))
e = html.escape

NAV = [("index.html", "Home"), ("ide/index.html", "IDE"), ("why.html", "Why"), ("how-it-works.html", "How it works"),
       ("pillars.html", "Pillars"), ("platforms.html", "Platforms"), ("status.html", "Status"),
       ("contribute.html", "Contribute")]


def page(fname, title, desc, body, head="", foot="", bodyclass=""):
    nav = "".join(f'<a href="/{f.replace("index.html", "")}"{" aria-current=page" if f==fname else ""}>{t}</a>' for f, t in NAV)
    t = "FSharpened: Sharpen your existing F#" if fname == "index.html" else f"{title} · FSharpened"
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{e(t)}</title><meta name="description" content="{e(desc)}">
<meta property="og:title" content="{e(t)}"><meta property="og:description" content="{e(desc)}"><meta property="og:type" content="website">
<meta property="og:image" content="/assets/og.png"><meta name="twitter:card" content="summary_large_image">
<meta name="theme-color" content="#4338ca"><link rel="icon" href="/favicon.svg" type="image/svg+xml"><link rel="apple-touch-icon" href="/assets/apple-touch-icon.png">
<link rel="stylesheet" href="/assets/site.css">{head}</head><body class="{bodyclass}">
<a class="skip" href="#main">Skip to content</a>
<header class="site"><div class="wrap"><a class="brand" href="/"><img src="/assets/mark.svg" alt="" width="30" height="30">FSharpened</a>
<nav class="main" aria-label="Main">{nav}<a class="btn" href="{REPO}">GitHub</a></nav></div></header>
<main id="main">{body}</main>
<footer class="site"><div class="wrap"><div><strong>FSharpened</strong><br>Sharpen your existing F#. Not a fork. An extension.</div>
<div><a href="{REPO}">Source</a> · <a href="{REPO}/blob/main/docs/spec/SPEC.md">Specification</a> · <a href="{REPO}/tree/main/docs/adr">ADRs</a> · <a href="{REPO}/blob/main/SECURITY.md">Security</a></div>
<div>Apache-2.0. Maintained by humans and agents together.</div></div></footer>
<script src="/assets/site.js" defer></script>{foot}</body></html>"""


def head(h, p):
    return f'<div class="page-head wrap"><h1>{h}</h1><p class="sub">{p}</p></div>'


pillars = reg["pillars"]
by_layer = {"substrate": "Substrate", "core": "Product", "ml": "Machine learning"}

# ---------------- home
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
<div class="cta"><a class="btn" href="/ide/">Open the IDE</a><a class="btn ghost" href="/how-it-works.html">How it works</a><a class="btn ghost" href="/why.html">Why it exists</a><a class="btn ghost" href="{REPO}">View on GitHub</a></div>
</div></section>
<section><div class="wrap"><h2>What you get</h2><p class="sub">Six principles shape every pillar.</p>
<div class="grid">{''.join(f'<div class="card"><h3>{e(a)}</h3><p>{e(b)}</p></div>' for a, b in cards)}</div></div></section>
<section class="alt"><div class="wrap"><h2>Try the CLI</h2><p class="sub">The <code>fsh</code> command line is the front door. Today it ships <code>doctor</code>; the rest of the surface is on the roadmap.</p>
<pre><code>git clone {REPO}.git
cd FSharpened
python tools/ci/fsh_ci.py run      # the same pipeline CI runs
dotnet run --project tools/FSharpened.Cli -- doctor</code></pre></div></section>
<section><div class="wrap"><h2>{len(pillars)} pillars, one registry</h2><p class="sub">Substrates, product pillars and machine-learning pillars, all declared in one machine-checked registry.</p>
<a class="btn" href="/pillars.html">Explore the pillars</a></div></section>"""

# ---------------- why
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
<p><a class="btn" href="/how-it-works.html">See how it works</a></p>
</div></section>"""

# ---------------- how it works
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

# ---------------- pillars
def pcard(p):
    wraps = f'<p>Wraps: {e(", ".join(p["wraps"]))}</p>' if p["wraps"] else ""
    return (f'<div class="card" data-layer="{p["layer"]}" data-stab="{p["stability"]}"><span class="tag {"p0" if p["priority"]=="P0" else ""}">{p["priority"]}</span>'
            f'<span class="tag">{by_layer[p["layer"]]}</span><span class="tag">{p["stability"]}</span><h3>{e(p["title"])}</h3>'
            f'<p><code>FSharpened.{e(p["id"])}</code></p><p>{e(p["summary"])}</p>{wraps}</div>')
filt = '<div class="filters" role="group" aria-label="Filter pillars"><button aria-pressed="true" data-f="all">All</button>' + "".join(f'<button aria-pressed="false" data-f="{k}">{v}</button>' for k, v in by_layer.items()) + "</div>"
pil = head("Pillars", f"{len(pillars)} independently versioned packages plus tools, declared in registry/pillars.json.") + f"""
<section><div class="wrap">{filt}<div class="grid">{''.join(pcard(p) for p in pillars)}</div>
<h2 class="pad">Tools</h2><div class="grid">{''.join(f'<div class="card"><h3>{e(t["title"])}</h3><p><code>{e(t.get("command", t["project"]))}</code></p><p>{e(t["summary"])}</p></div>' for t in reg["tools"])}</div></div></section>"""

# ---------------- platforms
rids = [("Windows", "x64, arm64"), ("Linux", "x64, arm64"), ("macOS", "x64, arm64 (Apple silicon)")]
plat = head("Platforms", "Nothing is Linux-first. Everything must work on every tier.") + f"""
<section><div class="wrap"><h2>Tier 1: gated on every change</h2><div class="grid">{''.join(f'<div class="card"><h3>{a}</h3><p>{b}</p></div>' for a, b in rids)}</div>
<h2 class="pad">Tier 2 and 3</h2><div class="grid"><div class="card"><h3>Tier 2</h3><p>WSL, containers, Windows Server. Supported, tested on a schedule.</p></div><div class="card"><h3>Tier 3</h3><p>Android and iOS. A stated goal; contracts exist so they can be added without rewrites.</p></div></div>
<p class="sub pad">Every pull request runs the six-RID matrix, including a NativeAOT publish that executes the produced binary.</p></div></section>"""

# ---------------- status
status = head("Status", "Honest and current: this is an early skeleton, not a finished product.") + f"""
<section><div class="wrap prose"><p class="notice">All {len(pillars)} pillars exist as buildable projects. Platform, Statepoints, Effects, Diagnostics, ML.Portability, ML.Hardware and Models contain real code; the rest are typed markers awaiting implementation.</p>
<h2>What works today</h2><ul><li>Solution builds and 9 contract tests pass</li><li><code>fsh doctor</code> runs, including as a NativeAOT binary</li><li>Local CI pipeline with preflight, caching, reports and git hooks</li><li>Six-RID GitHub matrix</li><li>Docs-drift checks keep the registry, docs and skills consistent</li></ul>
<h2>What is next</h2><p>See the <a href="{REPO}/blob/main/PLAN.md">plan</a> and <a href="{REPO}/blob/main/TODO.md">todo</a>. The canonical status lives in <a href="{REPO}/blob/main/docs/STATUS.md">docs/STATUS.md</a>.</p></div></section>"""

# ---------------- contribute
contrib = head("Contribute", "Built by humans and agents together, governed by written rules.") + f"""
<section><div class="wrap prose"><div class="steps">
<div class="card"><h3>Set up</h3><p>Install the .NET SDK 9.0.200+ and Python 3, then run <code>python tools/ci/fsh_ci.py install-hooks</code>.</p></div>
<div class="card"><h3>Pick work</h3><p>Choose an item from the <a href="{REPO}/blob/main/TODO.md">TODO</a>, branch as <code>feat/&lt;pillar&gt;-&lt;short&gt;</code>.</p></div>
<div class="card"><h3>Follow the order</h3><p>ADR, canonical doc, contract tests, implementation, platform matrix, docs map.</p></div>
<div class="card"><h3>Open a PR</h3><p>Say which RIDs you could not verify. A reviewer other than the author approves; merging is human-gated.</p></div></div>
<p><a class="btn" href="{REPO}/blob/main/CONTRIBUTING.md">Contributing guide</a> <a class="btn ghost" href="{REPO}/issues">Issues</a></p>
<p>Security issues: please do not open a public issue, see <a href="{REPO}/blob/main/SECURITY.md">SECURITY.md</a>.</p></div></section>"""

notfound = head("Page not found", "That page does not exist.") + '<section><div class="wrap"><a class="btn" href="/">Back home</a></div></section>'

pages = {"index.html": ("Home", "A satellite suite of libraries, analyzers and tools that extend F# without forking the compiler.", home),
         "why.html": ("Why", "Why F# needs FSharpened: one state model, grounded agents, true cross-platform parity.", why),
         "how-it-works.html": ("How it works", "A layered satellite architecture around FCS, analyzers, NuGet and LSP.", how),
         "pillars.html": ("Pillars", "Every FSharpened pillar and tool, generated from the registry.", pil),
         "platforms.html": ("Platforms", "Windows, Linux and macOS on x64 and arm64 are gated on every change.", plat),
         "status.html": ("Status", "What works today and what is next.", status),
         "contribute.html": ("Contribute", "How to contribute to FSharpened.", contrib),
         "404.html": ("Not found", "Page not found.", notfound)}

# ---------------- IDE
ide_body = """<div class="ide" id="ide">
<div class="bar">
  <div class="tabs" id="langs" role="tablist" aria-label="Language"></div>
  <label>Compiler <select id="compiler" aria-label="Compiler"></select></label>
  <label id="options-wrap">Flags <input id="options" type="text" spellcheck="false" aria-label="Compiler flags"></label>
  <label>Examples <select id="template" aria-label="Load an example"></select></label>
  <span class="grow"></span>
  <button id="btn-reset" class="ghost" title="Reset all buffers to the starter examples">Reset</button>
  <button id="btn-snap" class="ghost" title="Save a statepoint (Ctrl+S)">Snapshot</button>
  <button id="btn-share" class="ghost" title="Copy a link containing this program">Share</button>
  <button id="btn-run" class="primary" title="Compile and run (Ctrl+Enter)">&#9654; Run</button>
</div>
<div class="split">
  <div id="editor" class="editor"></div>
  <div class="bottom">
    <div class="ptabs" role="tablist" aria-label="Panels">
      <button data-p="output" aria-selected="true" role="tab">Output</button>
      <button data-p="problems" aria-selected="false" role="tab">Problems <span id="pcount" class="badge"></span></button>
      <button data-p="timeline" aria-selected="false" role="tab">Timeline <span id="tcount" class="badge"></span></button>
      <button data-p="stdin" aria-selected="false" role="tab">Input</button>
    </div>
    <div class="panel" data-p="output"><div id="summary" role="status"></div><pre id="stdout"></pre><pre id="stderr" class="err"></pre></div>
    <div class="panel" data-p="problems" hidden><div id="problems"></div></div>
    <div class="panel" data-p="timeline" hidden><p class="muted">Every run and snapshot is a <strong>statepoint</strong> from FSharpened.Statepoints. Restore rolls back; Fork branches without touching the source.</p><ul id="timeline"></ul><div id="diff" hidden></div></div>
    <div class="panel" data-p="stdin" hidden><label for="stdin">Standard input for your program</label><textarea id="stdin" rows="6" spellcheck="false"></textarea></div>
  </div>
</div>
<div class="statusbar"><span id="status">Loading…</span><span class="grow"></span><span class="muted">Runs on <a href="https://godbolt.org" rel="noopener">Compiler Explorer</a> sandboxes. Your code is sent there when you press Run; do not paste secrets.</span></div>
</div><div id="toast" role="status" aria-live="polite"></div>
<noscript><p class="wrap">The IDE needs JavaScript.</p></noscript>"""
pages["ide/index.html"] = ("IDE", "A browser IDE for F#, C, C# and C++ built on FSharpened: edit, compile, run, and time-travel with statepoints.", ide_body)

for f, (t, d, b) in pages.items():
    extra = {"head": '<link rel="stylesheet" href="/ide/ide.css">', "foot": '<script type="module" src="/ide/ide.js"></script>', "bodyclass": "ide-page"} if f == "ide/index.html" else {}
    (OUT / f).parent.mkdir(parents=True, exist_ok=True)
    (OUT / f).write_text(page(f, t, d, b, **extra), encoding="utf-8", newline="\n")
(OUT / "robots.txt").write_text("User-agent: *\nAllow: /\n", encoding="utf-8")
if not (OUT / "ide" / "ide.js").exists():
    print("warning: site/public/ide/ide.js is missing; build it first: cd ide/web && npm ci && npm run build (docs/operations/ide.md)")
print(f"site: {len(pages)} pages -> {OUT}")
