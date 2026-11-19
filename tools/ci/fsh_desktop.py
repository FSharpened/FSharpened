"""Desktop packaging helpers for fsh-ci (ADR-0023): which Rust crates the desktop app ships, the deterministic archive,
and the check of what the headless self-test reported. Standard library only.

Nothing here signs, notarises, uploads or publishes anything: those are human gates (packaging/README.md).
"""
from __future__ import annotations

import datetime as dt
import gzip
import io
import json
import os
import pathlib
import re
import tarfile
import zipfile
from dataclasses import dataclass
from typing import Optional

APP = "FSharpened.Ide.Desktop"
ARCHIVE_PREFIX = "fsharpened-desktop"
PACKAGING_CONFIG = "packaging/desktop.json"
COMPRESS_LEVEL = 6
ZIP_EPOCH_MIN = 315532800  # 1980-01-01: the earliest time a zip entry can carry


def exe_name(rid: str) -> str:
    return APP + (".exe" if rid.startswith("win") else "")


def archive_ext(rid: str) -> str:
    """zip on Windows (what Explorer opens), tar.gz on Linux and macOS (keeps the executable bit)."""
    return "zip" if rid.startswith("win") else "tar.gz"


def archive_name(version: str, rid: str) -> str:
    return f"{ARCHIVE_PREFIX}-{version}-{rid}.{archive_ext(rid)}"


def read_version(props_text: str) -> str:
    m = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", props_text)
    return m.group(1) if m else "0.0.0"


def native_library_file(rid: str, stem: str) -> str:
    """The file name cargo gives a cdylib for this RID (Rust turns `-` in a crate name into `_`)."""
    stem = stem.replace("-", "_")
    if rid.startswith("win"):
        return f"{stem}.dll"
    if rid.startswith("osx"):
        return f"lib{stem}.dylib"
    return f"lib{stem}.so"


@dataclass(frozen=True)
class Crate:
    name: str
    manifest: pathlib.Path
    stem: str


def load_config(root: pathlib.Path) -> dict:
    p = root / PACKAGING_CONFIG
    if not p.is_file():
        return {"nativeExclude": []}
    return json.loads(p.read_text(encoding="utf-8"))


def native_crates(root: pathlib.Path, exclude: list[str]) -> list[Crate]:
    """Every cdylib crate under native/rust except the excluded names (reference crates that the app does not load).
    Sorted by name so the archive does not depend on directory order."""
    found: list[Crate] = []
    base = root / "native" / "rust"
    if not base.is_dir():
        return found
    for manifest in sorted(base.glob("*/Cargo.toml")):
        text = manifest.read_text(encoding="utf-8")
        pkg = re.search(r'^\s*name\s*=\s*"([^"]+)"', text, re.M)
        if not pkg or not re.search(r'crate-type\s*=\s*\[[^\]]*"cdylib"', text):
            continue
        name = pkg.group(1)
        if name in exclude:
            continue
        lib = re.search(r"^\[lib\][^\[]*?^\s*name\s*=\s*\"([^\"]+)\"", text, re.M | re.S)
        found.append(Crate(name, manifest, lib.group(1) if lib else name))
    return found


def commit_epoch(unix_seconds: Optional[int]) -> int:
    """The timestamp every archive entry carries: the commit time (so two builds of one commit agree), never below 1980."""
    return max(int(unix_seconds or 0), ZIP_EPOCH_MIN)


def _entries(src: pathlib.Path) -> list[tuple[str, pathlib.Path]]:
    """Files only, sorted by their '/'-separated relative path (byte order, not the file system's)."""
    out = [(p.relative_to(src).as_posix(), p) for p in src.rglob("*") if p.is_file()]
    out.sort(key=lambda e: e[0].encode("utf-8"))
    return out


def _executable(path: pathlib.Path) -> bool:
    return bool(path.stat().st_mode & 0o111) and os.name != "nt"


def write_archive(src: pathlib.Path, dest: pathlib.Path, top: str, epoch: int) -> int:
    """Archive `src` as `top/...` with sorted entries, fixed timestamps, owner and modes. Returns the entry count.
    The bytes depend only on the file contents, the names and `epoch`, plus the zlib version that compresses them."""
    entries = _entries(src)
    if not entries:
        raise ValueError(f"{src} has no files to archive")
    dest.parent.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.fromtimestamp(epoch, dt.timezone.utc)
    if dest.name.endswith(".zip"):
        with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED, compresslevel=COMPRESS_LEVEL) as z:
            for rel, path in entries:
                info = zipfile.ZipInfo(f"{top}/{rel}", (stamp.year, stamp.month, stamp.day, stamp.hour, stamp.minute, stamp.second - stamp.second % 2))
                info.create_system = 3
                info.external_attr = (0o755 if _executable(path) else 0o644) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                z.writestr(info, path.read_bytes(), compresslevel=COMPRESS_LEVEL)
    elif dest.name.endswith(".tar.gz"):
        with open(dest, "wb") as raw:
            # filename="" and mtime=0 keep the gzip header free of the output name and the current time
            with gzip.GzipFile(filename="", mode="wb", fileobj=raw, compresslevel=COMPRESS_LEVEL, mtime=0) as gz:
                with tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as tar:
                    for rel, path in entries:
                        info = tarfile.TarInfo(f"{top}/{rel}")
                        data = path.read_bytes()
                        info.size = len(data)
                        info.mtime = epoch
                        info.mode = 0o755 if _executable(path) else 0o644
                        info.uid = info.gid = 0
                        info.uname = info.gname = ""
                        tar.addfile(info, io.BytesIO(data))
    else:
        raise ValueError(f"unknown archive type: {dest.name}")
    return len(entries)


def archive_members(path: pathlib.Path) -> list[str]:
    if path.name.endswith(".zip"):
        with zipfile.ZipFile(path) as z:
            return z.namelist()
    with tarfile.open(path, "r:gz") as t:
        return t.getnames()


def check_selftest(report: dict, rid: str, expected_packs: int, expected_grammars: int) -> list[str]:
    """Problems in what `--selftest` printed (empty = the packaged app is whole)."""
    problems: list[str] = []
    if report.get("ok") is not True:
        problems.append(f"selftest did not report ok: {report.get('failures') or report}")
    if report.get("packs", 0) < expected_packs:
        problems.append(f"only {report.get('packs', 0)} language packs loaded (languages/ has {expected_packs})")
    if report.get("packProblems", 0) != 0:
        problems.append(f"{report.get('packProblems')} language pack(s) failed to load")
    if report.get("grammars", 0) < expected_grammars:
        problems.append(f"only {report.get('grammars', 0)} shipped grammars found (grammars/ has {expected_grammars})")
    if report.get("nativeFailed", 0) != 0:
        problems.append(f"{report.get('nativeFailed')} native library file(s) failed to load: {report.get('nativeErrors')}")
    if report.get("workspaceEntries", 0) < 1:
        problems.append("the temporary folder was not listed")
    if report.get("rid") and report["rid"] != rid:
        problems.append(f"selftest ran as {report['rid']}, expected {rid}")
    return problems


def parse_selftest(output: str) -> Optional[dict]:
    """The JSON object on the selftest's last line that starts with '{' (log lines may precede it)."""
    for line in reversed(output.splitlines()):
        line = line.strip()
        if line.startswith("{") and line.endswith("}"):
            try:
                return json.loads(line)
            except ValueError:
                return None
    return None
