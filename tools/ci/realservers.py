#!/usr/bin/env python3
"""Opt-in verification of the desktop IDE's language-server and debug-adapter clients against REAL servers.

  python tools/ci/realservers.py [--only NAME ...] [--path DIR ...] [--no-build] [--list] [--json FILE]

It detects which of these programs are installed, runs the .NET checks in tests/FSharpened.Ide.Host.Tests for those
(the checks are skipped, not passed, unless FSH_REAL_SERVERS=1, which this script sets), and prints one table: per
server, per check, passed / failed / skipped with an install hint. A server that is not installed is skipped, never
reported as passed. Exit 0 when every server that could be tested passed (or none was installed), 1 when one failed.

Standard library only. Installs nothing. Used by the `realservers` stage of fsh_ci.py (profile `verify`, never `full`,
`all` or `release`) and by .github/workflows/realservers.yml. Docs: docs/operations/desktop.md.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from typing import Optional

ROOT = pathlib.Path(__file__).resolve().parents[2]
TEST_PROJECT = "tests/FSharpened.Ide.Host.Tests"
WINDOWS = os.name == "nt"


@dataclass(frozen=True)
class Server:
    name: str  # the program on PATH
    kind: str  # "lsp" or "dap"
    fixture: str  # folder under tests/fixtures/realservers
    install: str  # what a person runs to get it


SERVERS = [
    Server("gopls", "lsp", "go", "go install golang.org/x/tools/gopls@latest"),
    Server("rust-analyzer", "lsp", "rust", "rustup component add rust-analyzer rust-src"),
    Server("pyright-langserver", "lsp", "python", "npm install -g pyright"),
    Server("typescript-language-server", "lsp", "typescript", "npm install -g typescript@5 typescript-language-server"),
    Server("clangd", "lsp", "c", "install LLVM (clangd)"),
    Server("lua-language-server", "lsp", "lua", "https://luals.github.io"),
    Server("fsautocomplete", "lsp", "fsharp", "dotnet tool install -g fsautocomplete"),
    Server("netcoredbg", "dap", "dotnet", "https://github.com/Samsung/netcoredbg"),
    Server("dlv", "dap", "go", "go install github.com/go-delve/delve/cmd/dlv@latest"),
    Server("lldb-dap", "dap", "c", "install LLVM (lldb-dap)"),
    Server("debugpy", "dap", "python", "pip install debugpy"),
]
CHECKS = {"lsp": ["start", "initialize", "diagnostics", "completion", "definition", "hover"],
          "dap": ["start", "initialize", "breakpoint", "variable", "step", "terminate"]}


def env_name(server: str) -> str:
    return "FSH_REALSERVER_CMD_" + server.upper().replace("-", "_")


def find(program: str, search_path: str) -> Optional[str]:
    return shutil.which(program, path=search_path)


def shim_command(shim: pathlib.Path) -> Optional[list[str]]:
    """The `node script.js` an npm `.cmd` shim runs. The host starts programs without a shell, so it cannot start the shim
    itself (a documented limit); this lets the check still use the real server, through the same client."""
    try:
        text = shim.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
    m = re.search(r'"%dp0%\\([^"]+?\.(?:js|mjs|cjs))"', text)
    node = shutil.which("node")
    if not m or not node:
        return None
    script = (shim.parent / m.group(1).replace("\\", "/")).resolve()
    return [node, str(script)] if script.is_file() else None


@dataclass
class Detected:
    server: Server
    path: Optional[str]
    override: Optional[list[str]] = None
    note: str = ""


def dap_tests_exist() -> bool:
    return (ROOT / TEST_PROJECT / "RealDapServers.fs").is_file()


def detect(search_path: str) -> list[Detected]:
    found = []
    for s in SERVERS:
        if s.kind == "dap" and not dap_tests_exist():
            found.append(Detected(s, None, None, "the debug-adapter client (Dap.fs, PR #13) and its checks are not on this branch yet"))
            continue
        path = find(s.name, search_path)
        override = None
        note = ""
        if WINDOWS and path and path.lower().endswith((".cmd", ".bat")):
            cmd = shim_command(pathlib.Path(path))
            if cmd:
                override = cmd + (["--stdio"] if s.kind == "lsp" and s.name != "fsautocomplete" else [])
                note = "npm .cmd shim: run as `node <script>` (the host cannot start .cmd files)"
            else:
                note = "only a .cmd shim that could not be resolved: the host cannot start it"
                path = None
        if s.name == "debugpy":
            python = shutil.which("python3") or shutil.which("python")
            ok = bool(python) and subprocess.run([python, "-c", "import debugpy"], capture_output=True, env=dict(os.environ, PATH=search_path), timeout=60).returncode == 0
            path = python if ok else None
        found.append(Detected(s, path, override, note))
    return found


def run_tests(selected: list[Detected], search_path: str, report: pathlib.Path, no_build: bool, timeout: int) -> tuple[int, str]:
    env = dict(os.environ)
    env["PATH"] = search_path
    env["FSH_REAL_SERVERS"] = "1"
    env["FSH_REAL_REPORT"] = str(report)
    for d in selected:
        if d.override:
            env[env_name(d.server.name)] = json.dumps(d.override)
    names = "|".join(f"DisplayName~{d.server.name}:" for d in selected)  # a test's display name starts with its server
    cmd = ["dotnet", "test", TEST_PROJECT, "-c", "Release", "-nologo", "--filter", f"FullyQualifiedName~RealServers&({names})"]
    if no_build:
        cmd.append("--no-build")
    try:
        p = subprocess.run(cmd, cwd=ROOT, env=env, capture_output=True, text=True, timeout=timeout)
        return p.returncode, p.stdout + p.stderr
    except subprocess.TimeoutExpired as e:
        return 124, f"timed out after {timeout}s\n{(e.stdout or b'')[-2000:]}"


def read_report(report: pathlib.Path, fixture: str, kind: str) -> Optional[list[dict]]:
    f = report / (fixture + ("-dap" if kind == "dap" else "") + ".json")
    if not f.is_file():
        return None
    return json.loads(f.read_text(encoding="utf-8"))["checks"]


def summarize(detected: list[Detected], report: pathlib.Path, test_rc: int, test_out: str) -> tuple[list[dict], bool]:
    rows: list[dict] = []
    failed = False
    for d in detected:
        s = d.server
        row = {"server": s.name, "kind": s.kind, "fixture": s.fixture, "installed": d.path is not None, "install": s.install, "note": d.note, "checks": []}
        if d.path is None:
            row["status"] = "skipped"
        else:
            checks = read_report(report, s.fixture, s.kind)
            if checks is None:
                row["status"] = "failed"
                row["checks"] = [{"name": "run", "ok": False, "detail": "the test wrote no report (see the dotnet test output)"}]
                failed = True
            else:
                row["checks"] = checks
                row["status"] = "passed" if all(c["ok"] for c in checks) else "failed"
                failed = failed or row["status"] == "failed"
        rows.append(row)
    return rows, failed


def render(rows: list[dict]) -> str:
    out = []
    for r in rows:
        head = f"{r['status'].upper():8} {r['server']:28} ({r['kind']}, fixture {r['fixture']})"
        out.append(head)
        if r["status"] == "skipped":
            out.append(f"         {r['note'] or 'not installed'}. Install: {r['install']}")
        else:
            if r["note"]:
                out.append(f"         {r['note']}")
            for c in r["checks"]:
                out.append(f"         {'ok  ' if c['ok'] else 'FAIL'} {c['name']:<12} {c['detail']}")
    return "\n".join(out)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--only", nargs="*", help="test only these servers (program names)")
    ap.add_argument("--path", nargs="*", default=[], help="extra folders to look for servers in (put first on PATH)")
    ap.add_argument("--no-build", action="store_true", help="the test project is already built")
    ap.add_argument("--list", action="store_true", help="only report what is installed")
    ap.add_argument("--json", help="write the per-server result here")
    ap.add_argument("--timeout", type=int, default=1800, help="seconds for the whole dotnet test run")
    ap.add_argument("--kind", choices=["lsp", "dap"], help="only language servers or only debug adapters")
    args = ap.parse_args(argv)

    extra = [str(pathlib.Path(p).resolve()) for p in args.path] + [p for p in os.environ.get("FSH_REALSERVERS_PATH", "").split(os.pathsep) if p]
    search_path = os.pathsep.join(extra + [os.environ.get("PATH", "")])
    detected = detect(search_path)
    if args.only:
        detected = [d for d in detected if d.server.name in args.only]
    if args.kind:
        detected = [d for d in detected if d.server.kind == args.kind]
    installed = [d for d in detected if d.path]
    print(f"real servers: {len(installed)} of {len(detected)} installed" + (" (" + ", ".join(d.server.name for d in installed) + ")" if installed else ""))

    report = pathlib.Path(tempfile.mkdtemp(prefix="fsh-realservers-"))
    test_rc, test_out = 0, ""
    try:
        if installed and not args.list:
            test_rc, test_out = run_tests(installed, search_path, report, args.no_build, args.timeout)
        rows, failed = summarize(detected, report, test_rc, test_out) if not args.list else (
            [{"server": d.server.name, "kind": d.server.kind, "fixture": d.server.fixture, "installed": d.path is not None, "install": d.server.install,
              "note": d.note, "checks": [], "status": "installed" if d.path else "skipped"} for d in detected], False)
    finally:
        shutil.rmtree(report, ignore_errors=True)
    print(render(rows))
    tested = [r for r in rows if r["status"] in ("passed", "failed")]
    print(f"\n{sum(r['status'] == 'passed' for r in rows)} passed, {sum(r['status'] == 'failed' for r in rows)} failed, {sum(r['status'] == 'skipped' for r in rows)} skipped (not installed)")
    if not tested and not args.list:
        print("nothing was exercised: install a server from the hints above to verify it")
    if failed and test_out:
        print("\n--- dotnet test output (tail) ---\n" + "\n".join(test_out.splitlines()[-25:]))
    if args.json:
        pathlib.Path(args.json).write_text(json.dumps({"servers": rows, "exercised": [r["server"] for r in tested]}, indent=1), encoding="utf-8")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
