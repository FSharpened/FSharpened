"""Local release bundles for fsh-ci: a folder of build outputs with a checksum manifest and a provenance record, and
the check that proves a bundle is still what was built. Standard library only.

Nothing here publishes anything. A bundle is the thing a human approves and then uploads (AGENTS.md: never publish a
package or model artifact without human approval).
"""
from __future__ import annotations

import datetime as dt
import hashlib
import json
import pathlib
import re
from typing import Optional

SUMS = "SHA256SUMS"
PROVENANCE = "provenance.json"
NOTES = "RELEASE_NOTES.md"
SCHEMA = 1


def sha256_file(path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def bundle_files(root: pathlib.Path) -> list[str]:
    """Every file in the bundle except the manifest itself, as sorted '/'-separated relative paths."""
    return sorted(p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file() and p.name != SUMS)


def write_checksums(root: pathlib.Path) -> str:
    lines = [f"{sha256_file(root / rel)}  {rel}" for rel in bundle_files(root)]
    text = "\n".join(lines) + "\n"
    (root / SUMS).write_text(text, encoding="utf-8", newline="\n")
    return text


def parse_checksums(text: str) -> dict[str, str]:
    out = {}
    for line in text.splitlines():
        m = re.fullmatch(r"([0-9a-f]{64})  (.+)", line.strip())
        if m:
            out[m.group(2)] = m.group(1)
    return out


def changelog_section(text: str, heading: str = "Unreleased") -> str:
    """The body under `## [heading]` (or `## heading`) up to the next `## `; empty when there is none."""
    lines = text.splitlines()
    start = None
    for i, line in enumerate(lines):
        if re.match(rf"##\s*\[?{re.escape(heading)}\]?", line, re.I):
            start = i + 1
            break
    if start is None:
        return ""
    body = []
    for line in lines[start:]:
        if line.startswith("## "):
            break
        body.append(line)
    return "\n".join(body).strip()


def build_provenance(*, commit: str, branch: str, dirty: bool, sdk: str, rid: str, python: str, profile: str,
                     stages: dict, rids_verified: list[str], files: dict[str, int], now: Optional[dt.datetime] = None) -> dict:
    now = now or dt.datetime.now(dt.timezone.utc)
    return {
        "schema": SCHEMA,
        "generated_utc": now.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "commit": commit,
        "branch": branch,
        "dirty": dirty,
        "profile": profile,
        "rid": rid,
        "sdk": sdk,
        "python": python,
        "stages": stages,
        "rids_verified_locally": rids_verified,
        "files": files,
        "published": False,
        "note": "Built and checked locally by fsh-ci. Not published. Publishing needs human approval.",
    }


def verify_bundle(root: pathlib.Path, head: Optional[str] = None) -> list[str]:
    """Problems found in a bundle (empty list = intact). Checks the manifest against the files, that nothing was added
    or removed, that the provenance parses and lists the same files, and, when `head` is given, whether the bundle was
    built from that commit."""
    problems: list[str] = []
    sums_path = root / SUMS
    if not root.is_dir():
        return [f"{root} is not a folder"]
    if not sums_path.is_file():
        return [f"{SUMS} is missing"]
    expected = parse_checksums(sums_path.read_text(encoding="utf-8"))
    if not expected:
        problems.append(f"{SUMS} lists no files")
    actual = set(bundle_files(root))
    for rel, digest in expected.items():
        p = root / rel
        if not p.is_file():
            problems.append(f"missing: {rel}")
        elif sha256_file(p) != digest:
            problems.append(f"changed: {rel}")
    for rel in sorted(actual - set(expected)):
        problems.append(f"not in {SUMS}: {rel}")
    prov_path = root / PROVENANCE
    if not prov_path.is_file():
        problems.append(f"{PROVENANCE} is missing")
    else:
        try:
            prov = json.loads(prov_path.read_text(encoding="utf-8"))
        except ValueError as e:
            problems.append(f"{PROVENANCE} is not JSON: {e}")
        else:
            if prov.get("published") is not False:
                problems.append(f"{PROVENANCE} does not say published=false")
            for rel in prov.get("files", {}):
                if rel not in expected:
                    problems.append(f"{PROVENANCE} lists {rel}, which is not in {SUMS}")
            if head and prov.get("commit") and not head.startswith(prov["commit"]) and not prov["commit"].startswith(head):
                problems.append(f"built from {prov['commit'][:10]}, HEAD is {head[:10]}")
    return problems
