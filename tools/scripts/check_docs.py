#!/usr/bin/env python3
"""FSharpened docs-drift check (mechanical, no network, stdlib only).

Checks:
  1. Every registry pillar has: src project, package doc, architecture doc, skill.
  2. Registry dependencies exist and are acyclic.
  3. Every repo path routed in docs/README.md and AGENTS.md exists (skill coverage).
  4. Every relative markdown link in *.md resolves.
  5. MAINTAINER.md lists every registry invariant number.
  6. AGENTS.md respects the 100-line hard cap.
  7. Every ADR in docs/adr/ is indexed in docs/adr/README.md.

Exit 0 = pass, 1 = failures printed. Skill: .agents/skills/update-docs-map.md
"""
import json, os, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
errors = []


def err(msg):
    errors.append(msg)


def exists_exact(path: pathlib.Path) -> bool:
    """Case-exact existence on every OS (Windows/macOS are case-insensitive; Linux is not).
    A link that only resolves on a case-insensitive filesystem is a bug (Invariant 1)."""
    path = pathlib.Path(path)
    if not path.exists():
        return False
    # Do NOT use Path.resolve(): on Windows/macOS it normalises to the on-disk case and hides the bug.
    absolute = pathlib.Path(os.path.normpath(os.path.abspath(path)))
    try:
        rel = absolute.relative_to(pathlib.Path(os.path.abspath(ROOT)))
    except ValueError:
        return True
    cur = pathlib.Path(os.path.abspath(ROOT))
    for part in rel.parts:
        if part not in {c.name for c in cur.iterdir()}:
            return False
        cur = cur / part
    return True


def read(p):
    return (ROOT / p).read_text(encoding="utf-8")


reg = json.loads(read("registry/pillars.json"))
ids = {p["id"] for p in reg["pillars"]}

# 1 + 2
for p in reg["pillars"]:
    pid = p["id"]
    for rel in (
        f"src/FSharpened.{pid}/FSharpened.{pid}.fsproj",
        f"docs/packages/{pid}.md",
        f"docs/architecture/pillars/{pid}.md",
        f".agents/skills/{p['skill']}.md",
    ):
        if not exists_exact(ROOT / rel):
            err(f"[registry] missing {rel}")
    for d in p["depends"]:
        if d not in ids:
            err(f"[registry] {pid} depends on unknown pillar {d}")

graph = {p["id"]: p["depends"] for p in reg["pillars"]}
state = {}


def visit(n, stack):
    if state.get(n) == 2:
        return
    if state.get(n) == 1:
        err("[registry] dependency cycle: " + " -> ".join(stack + [n]))
        return
    state[n] = 1
    for d in graph.get(n, []):
        if d in graph:
            visit(d, stack + [n])
    state[n] = 2


for n in graph:
    visit(n, [])

# 3 routed paths
path_re = re.compile(r"(?<![\w/.-])((?:docs|\.agents|pipelines|registry|tools|tests)/[\w./<>-]+\.(?:md|json|yml|py|sh|ps1))")
for doc in ("docs/README.md", "AGENTS.md"):
    for m in path_re.finditer(read(doc)):
        target = m.group(1)
        if "<" in target:
            continue
        if not exists_exact(ROOT / target):
            err(f"[routing] {doc} routes to missing {target}")

# 4 relative markdown links
link_re = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")
for md in ROOT.rglob("*.md"):
    if any(part in (".git", "node_modules", "bin", "obj") for part in md.parts):
        continue
    for m in link_re.finditer(md.read_text(encoding="utf-8")):
        href = m.group(1)
        if href.startswith(("http://", "https://", "mailto:", "#")):
            continue
        target = pathlib.Path(os.path.normpath(md.parent / href.split("#")[0]))
        if not exists_exact(target):
            err(f"[link] {md.relative_to(ROOT)} -> {href}")

# 5 invariants
maint = read("MAINTAINER.md")
for inv in reg["invariants"]:
    if not re.search(rf"^{inv['n']}\. \*\*", maint, re.M):
        err(f"[invariants] MAINTAINER.md missing invariant {inv['n']}")

# 6 AGENTS.md cap
if len(read("AGENTS.md").splitlines()) > 100:
    err("[agents] AGENTS.md exceeds the 100-line hard cap")

# 7 ADR index
adr_index = read("docs/adr/README.md")
for f in sorted((ROOT / "docs/adr").glob("[0-9][0-9][0-9][0-9]-*.md")):
    if f.name[:4] not in adr_index:
        err(f"[adr] {f.name} not indexed in docs/adr/README.md")

if errors:
    print(f"docs-drift: {len(errors)} problem(s)")
    for e in errors:
        print("  " + e)
    sys.exit(1)
print(f"docs-drift: OK ({len(reg['pillars'])} pillars, {len(reg['invariants'])} invariants)")
