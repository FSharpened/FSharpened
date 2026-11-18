#!/usr/bin/env python3
"""FSharpened local CI/CD pipeline ("fsh-ci").

One entrypoint used identically on a developer machine, in a git hook, and in GitHub Actions
(Invariant: local and remote CI must not diverge). Standard library only; Windows, Linux, macOS.

  python tools/ci/fsh_ci.py run [--profile fast|full|all|release] [--only S ...] [--skip S ...]
                                [--no-cache] [--keep-going] [--jobs N] [--rid RID]
                                [--require-aot] [--require-rust] [--require-node] [--ignore-resources] [--allow-dirty]
  python tools/ci/fsh_ci.py preflight      # checks only
  python tools/ci/fsh_ci.py doctor [--fix] # the machine: memory, toolchains, state, hooks; --fix tidies what it safely can
  python tools/ci/fsh_ci.py status         # the last run, stage by stage
  python tools/ci/fsh_ci.py watch          # re-run the profile when files change (only stages whose inputs changed run)
  python tools/ci/fsh_ci.py verify [DIR]   # check a release bundle against its checksums and provenance
  python tools/ci/fsh_ci.py list | history | clean | install-hooks | uninstall-hooks

Features: preflight gate (toolchain, SDK pin, memory and commit headroom, disk, secrets, merge markers, large files,
network), resource governor (parallelism and MSBuild nodes follow free memory), dependency-ordered parallel scheduler,
content-addressed result cache with atomic writes, demand-driven restore/build (a docs-only change skips them),
process-tree-safe timeouts and Ctrl+C, one retry of build/test only when the failure is the machine's (OOM, MSBuild
node crash, locked file) and never for a real compile or test failure, hang-blamed tests, retry with backoff for
restore, JS/site/hub stages, local release bundles (SHA256SUMS + provenance; nothing is ever published), trx results,
JSON + Markdown reports, duration history with slow-stage warnings, honest per-RID coverage.
Skill: .agents/skills/run-local-ci.md   Docs: docs/operations/local-ci.md
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import datetime as dt
import hashlib
import json
import os
import pathlib
import platform
import re
import shutil
import signal
import socket
import statistics
import subprocess
import sys
import threading
import time
import urllib.request
from dataclasses import dataclass, field
from typing import Callable, Optional

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import fsh_release as rel  # noqa: E402
import fsh_runtime as rt  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parents[2]
STATE = ROOT / ".fsharpened" / "ci"
SIX_RIDS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]
EXCLUDE_DIRS = {".git", "bin", "obj", "artifacts", ".fsharpened", "node_modules", "__pycache__", ".vs", ".idea", "fable_out", "dist"}
INDEX = rt.FileIndex(ROOT, EXCLUDE_DIRS)

PASS, FAIL, SKIP, WARN, BLOCKED, CACHED = "pass", "fail", "skip", "warn", "blocked", "cached"
OK_STATES = {PASS, WARN, CACHED, SKIP}


# --------------------------------------------------------------------------- environment
def host_rid() -> str:
    sysname = platform.system().lower()
    machine = platform.machine().lower()
    arch = "arm64" if machine in ("arm64", "aarch64") else "x64"
    os_ = {"windows": "win", "linux": "linux", "darwin": "osx"}.get(sysname, sysname)
    return f"{os_}-{arch}"


def augment_path(env: dict) -> dict:
    """Make dotnet and the Windows native-AOT toolchain discoverable (vswhere is not on PATH by default)."""
    extra = []
    if os.name == "nt":
        pf = os.environ.get("ProgramFiles", r"C:\Program Files")
        pfx86 = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")
        extra += [os.path.join(pf, "dotnet"), os.path.join(pfx86, "Microsoft Visual Studio", "Installer")]
    else:
        extra += [os.path.expanduser("~/.dotnet"), "/usr/share/dotnet", "/usr/local/share/dotnet"]
    extra.append(os.path.join(os.path.expanduser("~"), ".cargo", "bin"))
    sep = os.pathsep
    current = env.get("PATH", "")
    for p in extra:
        if os.path.isdir(p) and p not in current.split(sep):
            current = current + sep + p
    env["PATH"] = current
    return env


def base_env() -> dict:
    env = augment_path(dict(os.environ))
    # a git hook runs with the repository's own variables set; tests that make a scratch repository must not inherit them
    for name in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES"):
        env.pop(name, None)
    env.update(
        {
            "CI": "true",  # strict mode: warnings are errors (Directory.Build.props)
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
            "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
            "MSBUILDDISABLENODEREUSE": "1",
            "NO_COLOR": "1",  # plain logs: parsed for counts, and readable in a file
        }
    )
    return env


def which(name: str, env: dict) -> Optional[str]:
    return shutil.which(name, path=env.get("PATH"))


def version_tuple(v: str) -> tuple:
    nums = re.findall(r"\d+", v.split("-")[0])
    return tuple(int(n) for n in nums[:3]) + (0,) * (3 - len(nums[:3]))


def pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if os.name == "nt":
        import ctypes

        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        h = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if h:
            ctypes.windll.kernel32.CloseHandle(h)
            return True
        return False
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


# --------------------------------------------------------------------------- hashing / files
def iter_files(patterns: list[str]):
    for rel_path in INDEX.select(patterns):
        yield ROOT / rel_path


def hash_inputs(patterns: list[str], extra: str = "") -> str:
    return INDEX.hash(patterns, extra)


RUNNER_FILES = ["tools/ci/fsh_ci.py", "tools/ci/fsh_runtime.py"]


def runner_hash() -> str:
    """The pipeline's own code, so a change to how a stage runs invalidates what that stage cached."""
    return INDEX.hash(RUNNER_FILES)[:16]


def git_lines(*args: str) -> list[str]:
    try:
        out = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, timeout=30)
        return [l for l in out.stdout.splitlines() if l]
    except Exception:
        return []


# --------------------------------------------------------------------------- results
@dataclass
class Result:
    status: str
    detail: str = ""
    notes: list[str] = field(default_factory=list)
    duration: float = 0.0
    metrics: dict = field(default_factory=dict)


@dataclass
class Ctx:
    run_dir: pathlib.Path
    env: dict
    rid: str
    require_aot: bool
    no_cache: bool
    sdk_version: str = ""
    require_rust: bool = False
    require_node: bool = False
    allow_dirty: bool = False
    ignore_resources: bool = False
    requested_jobs: int = 2
    profile: str = ""
    trigger: str = "manual"
    msbuild_nodes: int = 0  # -m:N for MSBuild; 0 leaves MSBuild's default (set by the resource governor)
    infra_retries: int = 1  # extra attempts for build/test when the failure is the machine's, never the code's
    hang_timeout_s: int = 300  # a single test running longer than this is a hang and is named
    plan: Optional[rt.Plan] = None
    gate_mb: int = 1200  # a dotnet stage waits (up to a minute) until this much memory is usable
    results: dict = field(default_factory=dict)

    def log(self, stage: str) -> pathlib.Path:
        return self.run_dir / f"{stage}.log"


def run_cmd(ctx: Ctx, stage: str, cmd: list[str], timeout: int = 900, cwd=None, append=False, env=None) -> tuple[int, str]:
    """Run a command, stream to the stage log, return (rc, the output of this command only). A timeout or Ctrl+C
    kills the whole process tree, not just the direct child."""
    mode = "ab" if append else "wb"
    resolved = cmd[:]
    env = env or ctx.env
    exe = which(cmd[0], env)
    if exe:
        resolved[0] = exe
    log = ctx.log(stage)
    start = log.stat().st_size if append and log.exists() else 0
    with open(log, mode) as f:
        f.write(("$ " + " ".join(cmd) + "\n").encode())
        f.flush()
        try:
            rc, timed_out = rt.run_tree(resolved, cwd or ROOT, env, f, timeout)
            if timed_out:
                f.write(f"\n[fsh-ci] TIMEOUT after {timeout}s (the process tree was killed)\n".encode())
        except FileNotFoundError as e:
            f.write(f"\n[fsh-ci] command not found: {e}\n".encode())
            rc = 127
    with open(log, "rb") as f:
        f.seek(start)
        return rc, f.read().decode("utf-8", errors="replace")


def run_resilient(ctx: Ctx, stage: str, cmd: list[str], timeout: int, retune: Optional[Callable[[list[str]], list[str]]] = None) -> tuple[int, str, list[str]]:
    """Run `cmd`; when it fails for a reason that is the machine's (out of memory, a crashed MSBuild node, a locked
    file) rather than the code's, stop the build servers, wait, and run it again with the machine eased. A real compile
    or test failure is never retried. Returns (rc, output of the last attempt, the reasons that caused retries)."""
    rc, out = run_cmd(ctx, stage, cmd, timeout=timeout)
    reasons: list[str] = []
    while rc != 0 and len(reasons) < ctx.infra_retries:
        why = rt.infra_failure(rc, out)
        if not why:
            break
        reasons.append(why)
        with open(ctx.log(stage), "ab") as f:
            f.write(f"\n[fsh-ci] infrastructure failure ({why}); stopping build servers and retrying once\n".encode())
        run_cmd(ctx, stage, ["dotnet", "build-server", "shutdown"], timeout=60, append=True)
        time.sleep(5)
        if retune:
            cmd = retune(cmd)
        rc, out = run_cmd(ctx, stage, cmd, timeout=timeout, append=True)
    return rc, out, reasons


def summary_line(r: "Result") -> str:
    """One display line: for passes the last (most informative) line, for failures the first."""
    lines = [l for l in r.detail.splitlines() if l.strip() and not l.startswith("$ ")]
    if not lines:
        return ""
    return lines[-1] if r.status in (PASS, WARN, CACHED) else lines[0]


def tail(text: str, n: int = 12) -> str:
    lines = [l for l in text.splitlines() if l.strip()]
    return "\n".join(lines[-n:])


# --------------------------------------------------------------------------- preflight checks
def check_python(ctx):
    ok = sys.version_info >= (3, 10)
    return (PASS if ok else FAIL), f"python {platform.python_version()} (need >= 3.10)"


def check_git(ctx):
    if not (ROOT / ".git").exists():
        return WARN, "not a git checkout (hooks and tracked-file checks degraded)"
    if not which("git", ctx.env):
        return FAIL, "git not found"
    branch = (git_lines("rev-parse", "--abbrev-ref", "HEAD") or ["?"])[0]
    dirty = len(git_lines("status", "--porcelain"))
    return PASS, f"branch {branch}, {dirty} uncommitted change(s)"


def check_dotnet(ctx):
    exe = which("dotnet", ctx.env)
    if not exe:
        return FAIL, "dotnet not found on PATH (see docs/setup/environment.md)"
    try:
        v = subprocess.run([exe, "--version"], env=ctx.env, capture_output=True, text=True, timeout=60).stdout.strip()
    except Exception as e:
        return FAIL, f"dotnet --version failed: {e}"
    ctx.sdk_version = v
    pinned = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    if version_tuple(v) < version_tuple(pinned):
        return FAIL, f"SDK {v} is older than global.json pin {pinned}"
    return PASS, f"SDK {v} (pin >= {pinned})"


def check_disk(ctx):
    free = shutil.disk_usage(ROOT).free / 1e9
    return (PASS if free >= 2 else FAIL), f"{free:.1f} GB free (need >= 2)"


def check_network(ctx):
    try:
        socket.create_connection(("api.nuget.org", 443), timeout=4).close()
        return PASS, "nuget.org reachable"
    except OSError:
        return WARN, "nuget.org unreachable (restore relies on the local package cache)"


def check_secrets(ctx):
    pats = [
        re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
        re.compile(r"gh[pousr]_[A-Za-z0-9]{36,}"),
        re.compile(r"AKIA[0-9A-Z]{16}"),
        re.compile(r"xox[baprs]-[A-Za-z0-9-]{10,}"),
    ]
    hits = []
    for rel in git_lines("ls-files", "-co", "--exclude-standard"):
        p = ROOT / rel
        if rel.replace("\\", "/") == "tools/ci/fsh_ci.py" or not p.is_file() or p.stat().st_size > 1_000_000:
            continue
        try:
            text = p.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        for pat in pats:
            if pat.search(text):
                hits.append(rel)
                break
    return (FAIL, "possible secrets in: " + ", ".join(hits[:5])) if hits else (PASS, "no secret patterns found")


def check_markers(ctx):
    start, end = "<" * 7 + " ", ">" * 7 + " "
    hits = []
    for rel in git_lines("ls-files", "-co", "--exclude-standard"):
        if rel.replace("\\", "/") == "tools/ci/fsh_ci.py":
            continue
        p = ROOT / rel
        if p.is_file() and p.suffix in {".fs", ".fsproj", ".md", ".json", ".yml", ".py", ".props"} and p.stat().st_size < 1_000_000:
            for line in p.read_text(encoding="utf-8", errors="ignore").splitlines():
                if line.startswith(start) or line.startswith(end):
                    hits.append(rel)
                    break
    return (FAIL, "merge conflict markers in: " + ", ".join(hits[:5])) if hits else (PASS, "no merge conflict markers")


def check_large(ctx):
    big = []
    for rel in git_lines("ls-files", "-co", "--exclude-standard"):
        p = ROOT / rel
        if p.is_file() and p.stat().st_size > 5_000_000:
            big.append(f"{rel} ({p.stat().st_size // 1_000_000} MB)")
    return (FAIL, "files over 5 MB: " + ", ".join(big[:3])) if big else (PASS, "no oversized files")


def check_registry(ctx):
    try:
        json.loads((ROOT / "registry" / "pillars.json").read_text(encoding="utf-8"))
        return PASS, "registry/pillars.json parses"
    except Exception as e:
        return FAIL, f"registry/pillars.json invalid: {e}"


def aot_toolchain(ctx) -> tuple[bool, str]:
    sysname = platform.system()
    if sysname == "Windows":
        vswhere = which("vswhere", ctx.env)
        if not vswhere:
            return False, "vswhere not found (install Visual Studio Build Tools, C++ workload)"
        out = subprocess.run(
            [vswhere, "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
            capture_output=True, text=True, env=ctx.env,
        ).stdout.strip()
        return (bool(out), f"MSVC at {out}" if out else "MSVC C++ tools missing (install the 'Desktop development with C++' workload)")
    if sysname == "Darwin":
        ok = subprocess.run(["xcode-select", "-p"], capture_output=True).returncode == 0
        return ok, "Xcode command line tools" if ok else "run: xcode-select --install"
    cc = which("clang", ctx.env) or which("gcc", ctx.env)
    return bool(cc), (f"native compiler {cc}" if cc else "install clang (apt install clang zlib1g-dev)")


def check_aot_toolchain(ctx):
    ok, msg = aot_toolchain(ctx)
    if ok:
        return PASS, msg
    return (FAIL if ctx.require_aot else WARN), f"NativeAOT toolchain: {msg} (aot stage will be skipped)"


def check_rust_toolchain(ctx):
    cargo = which("cargo", ctx.env)
    if not cargo:
        msg = "cargo not found (install with rustup; the rust stage and the Interop.Rust integration tests need it)"
        return (FAIL if ctx.require_rust else WARN), msg
    out = subprocess.run([cargo, "--version"], capture_output=True, text=True, env=ctx.env).stdout.strip()
    return PASS, out or cargo


def check_memory(ctx):
    """Free memory and, on Windows, commit headroom. A build on a machine that is out of commit does not fail cleanly:
    MSBuild nodes crash, node exits with access violations, the compiler throws OutOfMemoryException. Say so up front,
    with who holds the memory."""
    snap = rt.memory_snapshot()
    plan = rt.plan_resources(snap, os.cpu_count() or 2, ctx.requested_jobs, ctx.ignore_resources)
    ctx.plan = plan
    if plan.level == "unknown":
        return WARN, "memory could not be read"
    detail = f"{plan.free_mb / 1024:.1f} GB usable of {snap['total_mb'] / 1024:.0f} GB"
    if plan.level == "ok":
        return PASS, f"{detail}; jobs {plan.jobs}, msbuild -m:{plan.nodes}"
    who = rt.describe_consumers(rt.top_consumers())
    hint = f"; largest: {who}" if who else ""
    if plan.level == "tight":
        return WARN, f"{detail}; running one stage at a time with msbuild -m:{plan.nodes}{hint}"
    return FAIL, f"{detail} (need >= {rt.CRITICAL_MB / 1024:.1f}); close something or pass --ignore-resources{hint}"


def check_node(ctx):
    node = which("node", ctx.env)
    if not node:
        msg = "node not found (the web, gateway, hub and site stages need Node 20+)"
        return (FAIL if ctx.require_node else WARN), msg
    v = rt.tool_version(node, ["--version"], ctx.env)
    major = int(re.sub(r"[^\d].*", "", v.lstrip("v")) or 0)
    if major < 20:
        return (FAIL if ctx.require_node else WARN), f"node {v} is older than 20"
    return PASS, f"node {v}"


def check_state_dir(ctx):
    try:
        STATE.mkdir(parents=True, exist_ok=True)
        probe = STATE / ".write-probe"
        probe.write_text("x", encoding="utf-8")
        probe.unlink()
        return PASS, f"{STATE.relative_to(ROOT)} is writable"
    except OSError as e:
        return FAIL, f"cannot write to {STATE}: {e}"


def check_tool_manifest(ctx):
    try:
        data = json.loads((ROOT / ".config" / "dotnet-tools.json").read_text(encoding="utf-8"))
        return PASS, "dotnet tools: " + ", ".join(sorted(data.get("tools", {}))) or "none"
    except Exception as e:
        return FAIL, f".config/dotnet-tools.json unreadable: {e}"


PREFLIGHT_CHECKS: list[tuple[str, Callable]] = [
    ("python", check_python),
    ("git", check_git),
    ("dotnet-sdk", check_dotnet),
    ("memory", check_memory),
    ("disk-space", check_disk),
    ("state-dir", check_state_dir),
    ("tool-manifest", check_tool_manifest),
    ("registry", check_registry),
    ("secrets", check_secrets),
    ("conflict-markers", check_markers),
    ("large-files", check_large),
    ("aot-toolchain", check_aot_toolchain),
    ("rust-toolchain", check_rust_toolchain),
    ("node", check_node),
    ("network", check_network),
]


def stage_preflight(ctx: Ctx) -> Result:
    notes, worst = [], PASS
    for name, fn in PREFLIGHT_CHECKS:
        try:
            status, msg = fn(ctx)
        except Exception as e:  # a crashing check is a failing check, never a skipped one
            status, msg = FAIL, f"check crashed: {e}"
        notes.append(f"{status.upper():5} {name}: {msg}")
        if status == FAIL:
            worst = FAIL
        elif status == WARN and worst == PASS:
            worst = WARN
    ctx.log("preflight").write_text("\n".join(notes) + "\n", encoding="utf-8")
    fails = sum(1 for n in notes if n.startswith("FAIL"))
    return Result(worst, f"{len(notes)} checks, {fails} failed", notes)


# --------------------------------------------------------------------------- stages
def stage_docs_drift(ctx: Ctx) -> Result:
    rc, out = run_cmd(ctx, "docs-drift", [sys.executable, "tools/scripts/check_docs.py"], timeout=120)
    return Result(PASS if rc == 0 else FAIL, tail(out, 6))


def stage_selftest(ctx: Ctx) -> Result:
    rc, out = run_cmd(ctx, "selftest", [sys.executable, "-m", "unittest", "discover", "-s", "tools/ci", "-p", "test_*.py"], timeout=120)
    return Result(PASS if rc == 0 else FAIL, tail(out, 6))


def retry(ctx: Ctx, stage: str, cmd: list[str], attempts: int = 3, timeout: int = 600, cwd=None) -> tuple[int, str]:
    rc, out = 1, ""
    for i in range(1, attempts + 1):
        rc, out = run_cmd(ctx, stage, cmd, timeout=timeout, append=(i > 1), cwd=cwd)
        if rc == 0:
            return rc, out
        if i < attempts:
            time.sleep(2 ** i)
    return rc, out


def stage_tools_restore(ctx: Ctx) -> Result:
    rc, out = retry(ctx, "tools-restore", ["dotnet", "tool", "restore"])
    return Result(PASS if rc == 0 else FAIL, tail(out, 4))


def stage_format(ctx: Ctx) -> Result:
    rc, out = run_cmd(ctx, "format", ["dotnet", "fantomas", "--check", "."], timeout=300)
    detail = "formatted" if rc == 0 else "unformatted files; run: dotnet fantomas ."
    return Result(PASS if rc == 0 else FAIL, detail + ("" if rc == 0 else "\n" + tail(out, 8)))


def stage_restore(ctx: Ctx) -> Result:
    rc, out = retry(ctx, "restore", ["dotnet", "restore", "FSharpened.slnx", "-v:q"])
    return Result(PASS if rc == 0 else FAIL, tail(out, 6))


def eased(cmd: list[str]) -> list[str]:
    """The retry of a build or test command: one MSBuild node, so it needs the least memory it can."""
    return rt.with_nodes(cmd, 1)


def recovered(result: Result, reasons: list[str]) -> Result:
    """A pass that needed an infrastructure retry is still a pass, but never a silent one."""
    if reasons and result.status == PASS:
        result.status = WARN
        result.detail += f" (passed after an infrastructure retry: {', '.join(reasons)})"
    result.metrics["infra_retries"] = len(reasons)
    return result


def stage_build(ctx: Ctx) -> Result:
    cmd = ["dotnet", "build", "FSharpened.slnx", "-c", "Release", "--no-restore", "-p:CI=true", "-v:m", "-nologo"]
    cmd = rt.with_nodes(cmd, ctx.msbuild_nodes)
    rc, out, reasons = run_resilient(ctx, "build", cmd, timeout=1200, retune=eased)
    warns = len(re.findall(r": warning ", out))
    m = re.search(r"(\d+) Warning\(s\)", out)
    if m:
        warns = int(m.group(1))
    if rc != 0:
        head = []
        why = rt.infra_failure(rc, out)
        if why:
            head.append(f"infrastructure: {why} (the retry failed too; see the memory line in preflight)")
        hint = rt.resource_hint(out)
        if hint:
            head.append(hint)
        return Result(FAIL, "\n".join(head + [tail(out, 15)]), metrics={"infra_retries": len(reasons)})
    return recovered(Result(PASS, f"{warns} warning(s)", metrics={"warnings": warns}), reasons)


def stage_test(ctx: Ctx) -> Result:
    results = ctx.run_dir / "tests"
    results.mkdir(exist_ok=True)
    cmd = ["dotnet", "test", "FSharpened.slnx", "-c", "Release", "--no-build", "-p:CI=true", "--logger", "trx;LogFileName=results.trx",
           "--results-directory", str(results), "-nologo",
           # a test that never returns is named and killed instead of eating the stage timeout
           "--blame-hang", "--blame-hang-timeout", f"{ctx.hang_timeout_s}s", "--blame-hang-dump-type", "none"]
    cmd = rt.with_nodes(cmd, ctx.msbuild_nodes)
    rc, out, reasons = run_resilient(ctx, "test", cmd, timeout=1500, retune=eased)
    t = rt.parse_test_output(out)
    metrics = {"passed": t["passed"], "failed": t["failed"], "skipped": t["skipped"], "infra_retries": len(reasons)}
    if rc == 0 and t["passed"] == 0:
        return Result(FAIL, "zero tests executed (a green run with no tests is a failure)", metrics=metrics)
    if rc != 0:
        head = []
        if t["failed_names"]:
            head.append("failed: " + ", ".join(t["failed_names"][:8]) + (" ..." if len(t["failed_names"]) > 8 else ""))
        head += t["blame"][:3]
        why = rt.infra_failure(rc, out)
        if why:
            head.append(f"infrastructure: {why}")
        return Result(FAIL, "\n".join(head + [tail(out, 15)]), metrics=metrics)
    return recovered(Result(PASS, f"{t['passed']} passed, {t['failed']} failed, {t['skipped']} skipped", metrics=metrics), reasons)


def stage_aot(ctx: Ctx) -> Result:
    ok, msg = aot_toolchain(ctx)
    if not ok:
        if ctx.require_aot:
            return Result(FAIL, "NativeAOT toolchain missing: " + msg)
        return Result(SKIP, "NativeAOT toolchain missing: " + msg)
    outdir = ctx.run_dir / "aot"
    rc, out = run_cmd(ctx, "aot", ["dotnet", "publish", "tools/FSharpened.Cli", "-c", "Release", "-r", ctx.rid, "-p:PublishAot=true", "-p:CI=true", "-o", str(outdir), "-nologo"], timeout=1800)
    if rc != 0:
        return Result(FAIL, tail(out, 15))
    exe = outdir / ("fsh.exe" if ctx.rid.startswith("win") else "fsh")
    if not exe.exists():
        return Result(FAIL, f"published binary missing: {exe}")
    # Smoke: the AOT binary must run and report that it is actually AOT. This catches reflection/printf
    # failures that only appear at run time (ADR-0014, gap K).
    rc, out = run_cmd(ctx, "aot", [str(exe), "doctor"], timeout=60, append=True)
    if rc != 0 or "aot:        True" not in out:
        return Result(FAIL, "AOT smoke failed:\n" + tail(out, 10))
    rc, out = run_cmd(ctx, "aot", [str(exe), "--help"], timeout=60, append=True)
    if rc != 0:
        return Result(FAIL, "AOT --help failed:\n" + tail(out, 10))
    size = exe.stat().st_size / 1e6
    return Result(PASS, f"{ctx.rid} binary {size:.1f} MB runs", metrics={"binary_mb": round(size, 2)})


RUST_CRATE = "native/rust/fsh-interop-example/Cargo.toml"


def stage_rust(ctx: Ctx) -> Result:
    """The reference Rust crate for Interop.Rust (ADR-0019): unit tests, plus rustfmt and clippy when installed.
    The F# side is exercised by the Interop.Rust tests in the test stage, which fail instead of passing quietly
    when FSH_REQUIRE_RUST=1 (set by --require-rust)."""
    if not which("cargo", ctx.env):
        if ctx.require_rust:
            return Result(FAIL, "cargo not found (--require-rust)")
        return Result(SKIP, "cargo not found; install Rust with rustup to run the Interop.Rust stage")
    env = dict(ctx.env)
    env["CARGO_TARGET_DIR"] = str(ctx.run_dir / "rust-target")
    manifest = ["--manifest-path", RUST_CRATE]
    rc, out = run_cmd(ctx, "rust", ["cargo", "test", "--locked", *manifest], timeout=900, env=env)
    if rc != 0:
        return Result(FAIL, tail(out, 15))
    passed = sum(int(m.group(1)) for m in re.finditer(r"test result: ok\. (\d+) passed", out))
    if passed == 0:
        return Result(FAIL, "zero Rust tests executed (a green run with no tests is a failure)")
    notes = []
    for component, args in (("fmt", ["cargo", "fmt", "--check", *manifest]),
                            ("clippy", ["cargo", "clippy", "--locked", *manifest, "--all-targets", "--", "-D", "warnings"])):
        probe, _ = run_cmd(ctx, "rust", ["cargo", component, "--version"], timeout=60, append=True, env=env)
        if probe != 0:
            notes.append(f"{component} not installed (skipped)")
            continue
        rc, out = run_cmd(ctx, "rust", args, timeout=900, append=True, env=env)
        if rc != 0:
            return Result(FAIL, f"cargo {component} failed:\n" + tail(out, 12))
    return Result(PASS, f"{passed} Rust tests passed" + ("; " + "; ".join(notes) if notes else ""), metrics={"rust_tests": passed})


def stage_benchmarks(ctx: Ctx) -> Result:
    harness = ROOT / "pipelines" / "benchmarks"
    if not any(harness.glob("*.fsproj")) and not any(harness.glob("**/*.fsproj")):
        return Result(SKIP, "no benchmark harness yet (pipelines/benchmarks/README.md)")
    return Result(SKIP, "benchmark harness present but not wired to fsh-ci yet")


def stage_pack(ctx: Ctx) -> Result:
    out_dir = ROOT / "artifacts" / "nupkg"
    shutil.rmtree(out_dir, ignore_errors=True)
    rc, out = run_cmd(ctx, "pack", ["dotnet", "pack", "FSharpened.slnx", "-c", "Release", "-p:CI=true", "-o", str(out_dir), "-nologo"], timeout=900)
    n = len(list(out_dir.glob("*.nupkg"))) if out_dir.exists() else 0
    if rc != 0:
        return Result(FAIL, tail(out, 15))
    return Result(PASS if n else FAIL, f"{n} package(s) in artifacts/nupkg", metrics={"packages": n})


def node_ready(ctx: Ctx) -> Optional[Result]:
    """None when node and npm are usable, otherwise the skip (or, with --require-node, the failure) for the stage."""
    if which("node", ctx.env) and which("npm", ctx.env):
        return None
    if ctx.require_node:
        return Result(FAIL, "node/npm not found (--require-node)")
    return Result(SKIP, "node not found; install Node 20+ to run this stage")


def npm_install(ctx: Ctx, stage: str, folder: str) -> Optional[Result]:
    """`npm ci --ignore-scripts`, but only when package-lock.json is not the one node_modules was installed from.
    Returns a failing Result, or None when the dependencies are in place."""
    base = ROOT / folder
    stamp = base / "node_modules" / ".fsh-lock-sha"
    want = hashlib.sha256((base / "package-lock.json").read_bytes()).hexdigest()
    try:
        have = stamp.read_text(encoding="utf-8").strip()
    except OSError:
        have = ""
    if have == want:
        with open(ctx.log(stage), "ab") as f:
            f.write(f"[fsh-ci] node_modules in {folder} already match package-lock.json\n".encode())
        return None
    rc, out = retry(ctx, stage, ["npm", "ci", "--ignore-scripts"], attempts=2, timeout=600, cwd=base)
    if rc != 0:
        return Result(FAIL, "npm ci failed:\n" + tail(out, 10))
    try:
        stamp.write_text(want, encoding="utf-8")
    except OSError:
        pass
    return None


def npm_steps(ctx: Ctx, stage: str, folder: str, scripts: list[list[str]]) -> tuple[Optional[Result], str]:
    """Install, then run each npm command in order. Returns (failure or None, the output of the last command)."""
    bad = npm_install(ctx, stage, folder)
    if bad:
        return bad, ""
    out = ""
    for cmd in scripts:
        rc, out = run_cmd(ctx, stage, ["npm", *cmd], timeout=900, cwd=ROOT / folder, append=True)
        if rc != 0:
            return Result(FAIL, f"npm {' '.join(cmd)} failed:\n" + tail(out, 15)), out
    return None, out


def stage_web(ctx: Ctx) -> Result:
    """The browser IDE: Fable compiles the F# core to JS, esbuild bundles it, node's own tests check it reads real
    compiler output the same way .NET does. Mirrors the `web` job of ide.yml."""
    skip = node_ready(ctx)
    if skip:
        return skip
    bad, out = npm_steps(ctx, "web", "ide/web", [["run", "build"], ["test"]])
    if bad:
        return bad
    passed = re.search(r"# pass (\d+)", out)
    bundle = ROOT / "site" / "dist" / "ide.js"
    size = bundle.stat().st_size if bundle.exists() else 0
    if size <= 100000:
        return Result(FAIL, f"site/dist/ide.js is {size} bytes; a real bundle is over 100000")
    return Result(PASS, f"{passed.group(1) if passed else '?'} node tests passed, bundle {size // 1000} KB", metrics={"node_tests": int(passed.group(1)) if passed else 0})


def stage_gateway(ctx: Ctx) -> Result:
    """The IDE gateway worker: type-check and vitest. Mirrors the `gateway` job of ide.yml."""
    skip = node_ready(ctx)
    if skip:
        return skip
    bad, out = npm_steps(ctx, "gateway", "gateway", [["run", "typecheck"], ["test"]])
    if bad:
        return bad
    m = re.search(r"Tests\s+(\d+) passed", rt.strip_ansi(out))
    return Result(PASS, f"{m.group(1)} tests passed" if m else "type-check and tests passed", metrics={"vitest": int(m.group(1)) if m else 0})


def stage_hub(ctx: Ctx) -> Result:
    """The hub worker: type-check and vitest. Mirrors hub.yml."""
    skip = node_ready(ctx)
    if skip:
        return skip
    bad, out = npm_steps(ctx, "hub", "worker", [["run", "typecheck"], ["test"]])
    if bad:
        return bad
    m = re.search(r"Tests\s+(\d+) passed", rt.strip_ansi(out))
    return Result(PASS, f"{m.group(1)} tests passed" if m else "type-check and tests passed", metrics={"vitest": int(m.group(1)) if m else 0})


def stage_site(ctx: Ctx) -> Result:
    """The public site and hub console as they are deployed: icons up to date, the build, links and CSP-safe markup, and
    the service-worker behaviour tests. Mirrors site.yml."""
    steps = [
        ("icons", [sys.executable, "tools/site/icons.py", "--check"]),
        ("build", [sys.executable, "tools/site/build.py", "--require-ide"]),
        ("check", [sys.executable, "tools/site/check.py"]),
    ]
    for label, cmd in steps:
        rc, out = run_cmd(ctx, "site", cmd, timeout=300, append=(label != "icons"))
        if rc != 0:
            return Result(FAIL, f"site {label} failed:\n" + tail(out, 12))
    skip = node_ready(ctx)
    if skip:
        return Result(WARN if skip.status == SKIP else skip.status, "site build and checks passed; service-worker tests skipped (no node)")
    rc, out = run_cmd(ctx, "site", ["node", "--test", "site/test/*.test.mjs"], timeout=300, append=True)
    if rc != 0:
        return Result(FAIL, "service-worker tests failed:\n" + tail(out, 15))
    m = re.search(r"# pass (\d+)", out)
    return Result(PASS, f"site built and checked, {m.group(1) if m else '?'} service-worker tests passed")


def stage_bundle(ctx: Ctx) -> Result:
    """Collect this run's packages and native binary into artifacts/release/<commit>-<rid>/ with SHA256SUMS,
    provenance.json and release notes, then verify the folder. Publishes nothing."""
    if git_lines("status", "--porcelain", "--untracked-files=no") and not ctx.allow_dirty:
        return Result(FAIL, "tracked files have uncommitted changes, so the provenance would name a commit that is not what was built (commit, or pass --allow-dirty)")
    commit = (git_lines("rev-parse", "HEAD") or [""])[0]
    branch = (git_lines("rev-parse", "--abbrev-ref", "HEAD") or ["?"])[0]
    if not commit:
        return Result(FAIL, "not a git checkout; a bundle needs a commit to name")
    out_dir = ROOT / "artifacts" / "release" / f"{commit[:10]}-{ctx.rid}"
    shutil.rmtree(out_dir, ignore_errors=True)
    (out_dir / "packages").mkdir(parents=True)
    packages = sorted((ROOT / "artifacts" / "nupkg").glob("*.nupkg"))
    if not packages:
        return Result(FAIL, "no packages in artifacts/nupkg (the pack stage did not produce any)")
    for p in packages:
        shutil.copy2(p, out_dir / "packages" / p.name)
    exe_name = "fsh.exe" if ctx.rid.startswith("win") else "fsh"
    exe = ctx.run_dir / "aot" / exe_name
    if not exe.exists():
        return Result(FAIL, f"the native binary is missing ({exe}); the bundle needs this run's aot stage")
    (out_dir / ctx.rid).mkdir()
    shutil.copy2(exe, out_dir / ctx.rid / exe_name)
    changelog = ROOT / "CHANGELOG.md"
    notes = rel.changelog_section(changelog.read_text(encoding="utf-8")) if changelog.exists() else ""
    (out_dir / rel.NOTES).write_text(f"# Release notes for {commit[:10]}\n\n{notes or '(nothing under [Unreleased] in CHANGELOG.md)'}\n", encoding="utf-8", newline="\n")
    files = {r: (out_dir / r).stat().st_size for r in rel.bundle_files(out_dir)}
    stages = {n: {"status": r.status, "seconds": round(r.duration, 1), "metrics": r.metrics} for n, r in ctx.results.items()}
    prov = rel.build_provenance(commit=commit, branch=branch, dirty=bool(git_lines("status", "--porcelain", "--untracked-files=no")), sdk=ctx.sdk_version,
                                rid=ctx.rid, python=platform.python_version(), profile=ctx.profile, stages=stages,
                                rids_verified=[ctx.rid], files=files)
    (out_dir / rel.PROVENANCE).write_text(json.dumps(prov, indent=1), encoding="utf-8", newline="\n")
    rel.write_checksums(out_dir)
    problems = rel.verify_bundle(out_dir, commit)
    if problems:
        return Result(FAIL, "the bundle failed its own verification:\n" + "\n".join(problems))
    total = sum(files.values()) / 1e6
    return Result(PASS, f"{len(packages)} package(s) + {ctx.rid} binary, {total:.1f} MB, in {out_dir.relative_to(ROOT).as_posix()}", metrics={"files": len(files) + 2})


# stage name -> (function, deps, input globs for the cache key, exclusive dotnet lock, cacheable)
# lazy: skipped when every stage that consumes it is satisfied from the cache. trivial: does not count as a consumer.
CODE = ["src/**/*.fs", "src/**/*.fsproj", "tools/**/*.fs", "tools/**/*.fsproj", "tests/**/*.fs", "tests/**/*.fsproj",
        "Directory.Build.props", "Directory.Packages.props", "global.json", "FSharpened.slnx",
        "native/rust/**/*.rs", "native/rust/**/Cargo.toml", "native/rust/**/Cargo.lock"]
WEB_INPUTS = ["ide/web/**", "tools/FSharpened.Ide.Web/**", "tools/FSharpened.Ide/**", "global.json", ".config/dotnet-tools.json",
              "Directory.Build.props", "Directory.Packages.props"]
STAGES: dict[str, dict] = {
    "preflight": dict(fn=stage_preflight, deps=[], inputs=None, excl=False),
    "docs-drift": dict(fn=stage_docs_drift, deps=["preflight"], inputs=["**/*.md", "registry/**", "src/**/*.fsproj", ".agents/**", "tools/scripts/*.py"], excl=False),
    "selftest": dict(fn=stage_selftest, deps=["preflight"], inputs=["tools/ci/*.py"], excl=False),
    "tools-restore": dict(fn=stage_tools_restore, deps=["preflight"], inputs=None, excl=True),
    "format": dict(fn=stage_format, deps=["tools-restore"], inputs=["**/*.fs", ".editorconfig", ".config/dotnet-tools.json"], excl=False),
    "restore": dict(fn=stage_restore, deps=["preflight"], inputs=None, excl=True, lazy=True),
    "build": dict(fn=stage_build, deps=["restore"], inputs=CODE, excl=True, lazy=True),
    "test": dict(fn=stage_test, deps=["build"], inputs=CODE, excl=True),
    "aot": dict(fn=stage_aot, deps=["build"], inputs=CODE, excl=True),
    "rust": dict(fn=stage_rust, deps=["preflight"], inputs=None, excl=False),
    "benchmarks": dict(fn=stage_benchmarks, deps=["build"], inputs=None, excl=True, trivial=True),
    "pack": dict(fn=stage_pack, deps=["build", "test"], inputs=CODE, excl=True),
    "bundle": dict(fn=stage_bundle, deps=["pack", "aot", "test"], inputs=None, excl=False),
    "web": dict(fn=stage_web, deps=["tools-restore"], inputs=WEB_INPUTS, excl=True),
    "gateway": dict(fn=stage_gateway, deps=["preflight"], inputs=["gateway/**"], excl=False),
    "hub": dict(fn=stage_hub, deps=["preflight"], inputs=["worker/**"], excl=False),
    "site": dict(fn=stage_site, deps=["web"], inputs=["site/**", "tools/site/**", "branding/**", "worker/admin/**", *WEB_INPUTS], excl=False),
}
FULL = ["preflight", "docs-drift", "selftest", "tools-restore", "format", "restore", "build", "test", "aot", "rust", "benchmarks"]
PROFILES = {
    "fast": ["preflight", "docs-drift", "selftest", "tools-restore", "format"],
    # what ci.yml runs on each of the six RIDs: keep this list identical to what GitHub's matrix expects
    "full": FULL,
    # everything GitHub runs across all its workflows: full, plus the browser IDE, gateway, hub and site jobs
    "all": FULL + ["web", "gateway", "hub", "site"],
    "release": FULL + ["pack", "bundle"],
}
# A cache hit is only trusted for stages whose outputs are not consumed by a later stage in the same run.
CACHEABLE = {"docs-drift", "selftest", "format", "test", "aot", "web", "gateway", "hub", "site"}
# Stages that start dotnet or compile a lot: they wait (briefly) for memory before they begin.
HEAVY = {"restore", "build", "test", "aot", "pack", "web"}


# --------------------------------------------------------------------------- scheduler
class Cache:
    """stage -> the input hash of its last pass. Updated from worker threads, so every change is atomic."""

    def __init__(self):
        self.store_ = rt.JsonStore(STATE / "cache.json")
        self.runner = runner_hash()

    def key(self, name: str, ctx: Ctx) -> Optional[str]:
        spec = STAGES[name]
        if spec["inputs"] is None or name not in CACHEABLE:
            return None
        flags = f"rust={int(ctx.require_rust)}:{int(bool(which('cargo', ctx.env)))}|aot={int(ctx.require_aot)}|node={int(bool(which('node', ctx.env)))}"
        return hash_inputs(spec["inputs"], extra=f"{name}|{ctx.sdk_version}|{ctx.rid}|{sys.version_info[:2]}|{self.runner}|{flags}")

    def hit(self, name: str, key: Optional[str]) -> bool:
        return bool(key) and self.store_.get(name) == key

    def store(self, name: str, key: Optional[str]):
        if key:
            self.store_.set(name, key)

    def drop(self, name: str):
        self.store_.delete(name)


def plan_lazy(names: list[str], hits: set[str]) -> dict[str, str]:
    """Stages that need not run because everything that consumes them is already satisfied (cache hit, or itself not
    needed). A docs-only change then skips restore and build instead of paying for them to feed cached tests."""
    skipped: dict[str, str] = {}
    changed = True
    while changed:
        changed = False
        for n in names:
            if n in skipped or not STAGES[n].get("lazy"):
                continue
            consumers = [c for c in names if n in STAGES[c]["deps"] and not STAGES[c].get("trivial")]
            if consumers and all(c in hits or c in skipped for c in consumers):
                skipped[n] = "not needed: " + ", ".join(consumers) + " came from the cache"
                changed = True
    return skipped


def acquire_lock() -> bool:
    STATE.mkdir(parents=True, exist_ok=True)
    lock = STATE / "lock"
    if lock.exists():
        try:
            pid = int(lock.read_text().split()[0])
        except Exception:
            pid = 0
        if pid and pid_alive(pid) and pid != os.getpid():
            return False
        lock.unlink(missing_ok=True)  # stale
    lock.write_text(f"{os.getpid()} {dt.datetime.now().isoformat()}\n")
    return True


def release_lock():
    (STATE / "lock").unlink(missing_ok=True)


def execute(names: list[str], ctx: Ctx, cache: Cache, jobs: int, keep_going: bool, log_fn, start_fn=None) -> dict[str, Result]:
    selected = set(names)
    results: dict[str, Result] = {}
    keys = {n: cache.key(n, ctx) for n in names}
    hits = {n for n in names if not ctx.no_cache and cache.hit(n, keys[n])}
    lazy = {} if ctx.no_cache else plan_lazy(names, hits)
    dotnet_lock = threading.Lock()
    failed_dep: set[str] = set()

    def run_one(name: str) -> Result:
        spec = STAGES[name]
        key = keys[name]
        if name in lazy:
            return Result(SKIP, lazy[name])
        if name in hits:
            return Result(CACHED, "inputs unchanged since last pass")
        if start_fn:
            start_fn(name)
        t0 = time.time()
        note = ""
        try:
            if spec["excl"]:
                with dotnet_lock:
                    if ctx.plan and ctx.plan.level != "unknown" and name in HEAVY:
                        enough, waited, free = rt.wait_for_headroom(ctx.gate_mb, 60)
                        if waited >= 1:
                            note = f"waited {waited:.0f}s for memory ({'enough' if enough else 'still low'}: {0 if free is None else free} MB usable)"
                    t0 = time.time()
                    r = spec["fn"](ctx)
            else:
                r = spec["fn"](ctx)
        except Exception as e:  # never let a stage crash the runner silently
            r = Result(FAIL, f"stage crashed: {type(e).__name__}: {e}")
        r.duration = time.time() - t0
        if note:
            r.notes.append(note)
        if r.status in (PASS, WARN):
            cache.store(name, key)
        else:
            cache.drop(name)
        return r

    pending = list(names)
    stop = False
    with cf.ThreadPoolExecutor(max_workers=max(1, jobs)) as pool:
        running: dict[cf.Future, str] = {}
        try:
            while pending or running:
                progressed = False
                for name in list(pending):
                    deps = [d for d in STAGES[name]["deps"] if d in selected]
                    if any(d in failed_dep for d in deps):
                        results[name] = Result(BLOCKED, "blocked by failed dependency: " + ", ".join(d for d in deps if d in failed_dep))
                        failed_dep.add(name)
                        pending.remove(name)
                        log_fn(name, results[name])
                        progressed = True
                    elif stop and not keep_going:
                        results[name] = Result(SKIP, "fail-fast: earlier stage failed")
                        pending.remove(name)
                        log_fn(name, results[name])
                        progressed = True
                    elif all(d in results for d in deps):
                        pending.remove(name)
                        running[pool.submit(run_one, name)] = name
                        progressed = True
                if running:
                    done, _ = cf.wait(list(running), timeout=0.2, return_when=cf.FIRST_COMPLETED)
                    for fut in done:
                        name = running.pop(fut)
                        results[name] = fut.result()
                        ctx.results[name] = results[name]
                        log_fn(name, results[name])
                        if results[name].status == FAIL:
                            failed_dep.add(name)
                            stop = True
                elif not progressed:
                    break
        except BaseException:
            # Ctrl+C or a crash of the runner itself: take every child process tree down with us, or they keep running
            # (and holding files and memory) after the pipeline has gone.
            rt.kill_all()
            raise
    return results


def publish_to_hub(summary: dict) -> None:
    """Best-effort: POST the run summary to the FSharpened hub (worker/). Never changes the exit code.
    Enabled only when FSH_HUB_URL and FSH_HUB_TOKEN are set; the token is never logged."""
    url, token = os.environ.get("FSH_HUB_URL"), os.environ.get("FSH_HUB_TOKEN")
    if not url or not token:
        return
    payload = dict(summary)
    payload["source"] = "github-actions" if os.environ.get("GITHUB_ACTIONS") else "local"
    payload["branch"] = os.environ.get("GITHUB_REF_NAME") or (git_lines("rev-parse", "--abbrev-ref", "HEAD") or [""])[0]
    payload["run_url"] = (f"{os.environ['GITHUB_SERVER_URL']}/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
                          if os.environ.get("GITHUB_RUN_ID") else "")
    req = urllib.request.Request(url.rstrip("/") + "/api/ingest/ci", data=json.dumps(payload).encode("utf-8"), method="POST",
                                 headers={"content-type": "application/json", "authorization": "Bearer " + token, "user-agent": "fsh-ci"})
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            print(f"hub: published ({resp.status})")
    except Exception as ex:  # telemetry must never break the build
        print(f"hub: publish skipped ({type(ex).__name__})")


# --------------------------------------------------------------------------- reporting
ICON = {PASS: "PASS", FAIL: "FAIL", SKIP: "skip", WARN: "WARN", BLOCKED: "blkd", CACHED: "cache"}


def write_reports(ctx: Ctx, profile: str, results: dict[str, Result], order: list[str], total: float):
    ok = all(r.status in OK_STATES for r in results.values())
    # A passing (or cached-for-identical-inputs) test stage on this RID is what verification means; a build that was
    # skipped because every consumer came from the cache does not unverify it.
    verified = [ctx.rid] if results.get("test", Result(SKIP)).status in (PASS, WARN, CACHED) and results.get("build", Result(SKIP)).status != FAIL else []
    retries = sum(int(r.metrics.get("infra_retries", 0)) for r in results.values())
    summary = {
        "ok": ok,
        "profile": profile,
        "trigger": ctx.trigger,
        "rid": ctx.rid,
        "sdk": ctx.sdk_version,
        "commit": (git_lines("rev-parse", "--short", "HEAD") or ["?"])[0],
        "dirty": bool(git_lines("status", "--porcelain")),
        "started": ctx.run_dir.name,
        "duration_s": round(total, 1),
        "resources": ctx.plan.line() if ctx.plan else "",
        "infra_retries": retries,
        "runner": runner_hash(),
        "stages": {n: {"status": r.status, "seconds": round(r.duration, 1), "detail": r.detail, "metrics": r.metrics, "notes": r.notes if n != "preflight" else []}
                   for n, r in results.items()},
        "rids_verified_locally": verified,
        "rids_not_verified_locally": [r for r in SIX_RIDS if r not in verified],
    }
    (ctx.run_dir / "summary.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    lines = [f"# fsh-ci report ({profile})", "", f"- result: **{'PASS' if ok else 'FAIL'}** in {total:.1f}s ({ctx.trigger})",
             f"- host rid: `{ctx.rid}`  SDK `{ctx.sdk_version}`  commit `{summary['commit']}`{' (dirty)' if summary['dirty'] else ''}"]
    if summary["resources"]:
        lines.append(f"- resources: {summary['resources']}")
    if retries:
        lines.append(f"- infrastructure retries: {retries} (a pass after a retry is reported as WARN; see the stage log)")
    lines += ["", "| stage | status | seconds | detail |", "|---|---|---|---|"]
    for n in order:
        r = results.get(n)
        if r:
            lines.append(f"| {n} | {ICON[r.status]} | {r.duration:.1f} | {summary_line(r)} |")
    lines += ["", "## Platform coverage",
              f"Verified by this run: {', '.join(verified) or 'none (build/test did not both pass)'}.",
              "Not verified by this run (covered only by the GitHub six-RID matrix): " + ", ".join(summary["rids_not_verified_locally"]) + "."]
    (ctx.run_dir / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    hist = STATE / "history.jsonl"
    with open(hist, "a", encoding="utf-8") as f:
        f.write(json.dumps({"t": ctx.run_dir.name, "profile": profile, "ok": ok, "rid": ctx.rid, "total": summary["duration_s"], "trigger": ctx.trigger,
                            "stages": {n: round(r.duration, 1) for n, r in results.items() if r.status == PASS}}) + "\n")
    latest = STATE / "latest"
    try:
        latest.write_text(str(ctx.run_dir), encoding="utf-8")
    except OSError:
        pass
    return summary


def regression_warnings(results: dict[str, Result]) -> list[str]:
    hist = STATE / "history.jsonl"
    if not hist.exists():
        return []
    rows = []
    for line in hist.read_text(encoding="utf-8").splitlines()[-40:]:
        try:
            rows.append(json.loads(line))
        except Exception:
            pass
    out = []
    for name, r in results.items():
        if r.status != PASS or r.duration < 5:
            continue
        prev = [row["stages"][name] for row in rows if row.get("ok") and name in row.get("stages", {})][-10:]
        if len(prev) >= 3:
            med = statistics.median(prev)
            if r.duration > 1.5 * med:
                out.append(f"stage '{name}' took {r.duration:.1f}s, 1.5x the recent median {med:.1f}s")
    return out


def prune_runs(keep: int = 15):
    runs = STATE / "runs"
    if runs.exists():
        for d in sorted(runs.iterdir())[:-keep]:
            shutil.rmtree(d, ignore_errors=True)


# --------------------------------------------------------------------------- commands
def select_stages(profile: str, only: Optional[list[str]] = None, skip: Optional[list[str]] = None) -> list[str]:
    """The stages of a run, in a stable order. --only adds each named stage's dependencies; --skip never drops preflight."""
    names = PROFILES[profile][:]
    if only:
        unknown = set(only) - set(STAGES)
        if unknown:
            raise ValueError("unknown stage(s): " + ", ".join(sorted(unknown)))

        def closure(s: str, acc: set):
            acc.add(s)
            for d in STAGES[s]["deps"]:
                closure(d, acc)
            return acc

        need: set = set()
        for s in only:
            closure(s, need)
        names = [s for s in STAGES if s in need]
    if skip:
        names = [s for s in names if s not in set(skip) or s == "preflight"]
    return names


def _stop_signal(signum, frame):  # SIGTERM / SIGBREAK become Ctrl+C so the same cleanup runs
    raise KeyboardInterrupt


def cmd_run(args) -> int:
    try:
        names = select_stages(args.profile, args.only, args.skip)
    except ValueError as e:
        print(str(e), file=sys.stderr)
        return 2
    if args.profile == "release":
        args.require_aot = True
        args.no_cache = True  # a release is built from scratch, and the bundle needs this run's own outputs

    if not acquire_lock():
        print("another fsh-ci run is active (see .fsharpened/ci/lock)", file=sys.stderr)
        return 3
    for sig in ("SIGTERM", "SIGBREAK"):
        if hasattr(signal, sig):
            try:
                signal.signal(getattr(signal, sig), _stop_signal)
            except (ValueError, OSError):
                pass
    try:
        run_id = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
        run_dir = STATE / "runs" / run_id
        run_dir.mkdir(parents=True, exist_ok=True)
        ctx = Ctx(run_dir=run_dir, env=base_env(), rid=args.rid or host_rid(), require_aot=args.require_aot, no_cache=args.no_cache or args.ci,
                  require_rust=args.require_rust, require_node=args.require_node, allow_dirty=args.allow_dirty,
                  ignore_resources=args.ignore_resources, requested_jobs=args.jobs, profile=args.profile,
                  trigger="hook" if args.hook else ("ci" if args.ci else "manual"), hang_timeout_s=args.hang_timeout)
        if ctx.require_rust:
            ctx.env["FSH_REQUIRE_RUST"] = "1"  # the Interop.Rust tests then fail, not pass quietly, without cargo
        INDEX.refresh()
        cache = Cache()
        print(f"fsh-ci profile={args.profile} rid={ctx.rid} jobs<={args.jobs} cache={'off' if ctx.no_cache else 'on'} trigger={ctx.trigger}")
        print(f"logs: {run_dir}")
        t0 = time.time()

        def log_fn(name: str, r: Result):
            print(f"  [{ICON[r.status]:5}] {name:14} {r.duration:6.1f}s  {summary_line(r)}", flush=True)

        def start_fn(name: str):
            if name != "preflight":
                print(f"  [run  ] {name}", flush=True)

        # Preflight first and alone: nothing else runs on an unhealthy machine.
        pre = execute(["preflight"], ctx, cache, 1, False, log_fn)
        if pre["preflight"].status == FAIL:
            for n in pre["preflight"].notes:
                print("    " + n)
            summary = write_reports(ctx, args.profile, pre, ["preflight"], time.time() - t0)
            print("preflight failed; nothing else was run")
            publish_to_hub(summary)
            return 1
        for n in pre["preflight"].notes:
            if not n.startswith("PASS"):
                print("    " + n)
        jobs = args.jobs
        if ctx.plan and ctx.plan.level != "unknown":
            jobs = min(args.jobs, ctx.plan.jobs)
            ctx.msbuild_nodes = ctx.plan.nodes
            print("  " + ctx.plan.line())
            if ctx.plan.level == "tight":  # idle build servers hold memory a tight machine needs
                run_cmd(ctx, "preflight", ["dotnet", "build-server", "shutdown"], timeout=60, append=True)
        rest = [n for n in names if n != "preflight"]
        results = dict(pre)
        results.update(execute(rest, ctx, cache, jobs, args.keep_going, log_fn, start_fn) if rest else {})
        total = time.time() - t0
        summary = write_reports(ctx, args.profile, results, names, total)
        for w in regression_warnings(results):
            print("  WARN " + w)
        for n, r in results.items():
            if r.status in (FAIL, BLOCKED) and r.detail:
                print(f"\n--- {n} ---\n{r.detail}\n(full log: {ctx.log(n)})")
        print(f"\n{'PASS' if summary['ok'] else 'FAIL'} in {total:.1f}s  report: {run_dir / 'report.md'}")
        if summary["rids_not_verified_locally"] and args.profile != "fast":
            print("platform coverage: verified " + (", ".join(summary["rids_verified_locally"]) or "none") + "; the rest rely on the GitHub matrix")
        publish_to_hub(summary)
        prune_runs()
        return 0 if summary["ok"] else 1
    except KeyboardInterrupt:
        killed = rt.kill_all()
        print(f"\ninterrupted; stopped {killed} running process tree(s). Logs so far: {STATE / 'runs'}")
        return 130
    finally:
        release_lock()


def cmd_preflight(args) -> int:
    ctx = Ctx(run_dir=STATE / "preflight", env=base_env(), rid=host_rid(), require_aot=args.require_aot, no_cache=True,
              require_rust=args.require_rust, require_node=args.require_node, ignore_resources=args.ignore_resources)
    ctx.run_dir.mkdir(parents=True, exist_ok=True)
    r = stage_preflight(ctx)
    for n in r.notes:
        print(n)
    return 0 if r.status != FAIL else 1


def cmd_list(args) -> int:
    for p, s in PROFILES.items():
        print(f"profile {p}: {' -> '.join(s)}")
    print()
    for n, spec in STAGES.items():
        flags = [f for f, on in (("cached", n in CACHEABLE), ("lazy", spec.get("lazy")), ("dotnet-lock", spec["excl"])) if on]
        print(f"{n:14} deps={spec['deps']} {' '.join(flags)}")
    return 0


def cmd_history(args) -> int:
    hist = STATE / "history.jsonl"
    if not hist.exists():
        print("no runs yet")
        return 0
    for line in hist.read_text(encoding="utf-8").splitlines()[-15:]:
        row = json.loads(line)
        print(f"{row['t']}  {row['profile']:7} {'ok  ' if row['ok'] else 'FAIL'} {row['total']:7.1f}s  {row['rid']}  {row.get('trigger', '')}")
    return 0


def last_summary() -> Optional[dict]:
    try:
        run = pathlib.Path((STATE / "latest").read_text(encoding="utf-8").strip())
        return json.loads((run / "summary.json").read_text(encoding="utf-8"))
    except Exception:
        return None


def cmd_status(args) -> int:
    """The last run, stage by stage. Exit code: 0 it passed, 1 it failed, 2 there has been no run."""
    s = last_summary()
    lock = STATE / "lock"
    if args.json:
        print(json.dumps({"last": s, "running": lock.exists()}, indent=1))
        return 2 if s is None else (0 if s["ok"] else 1)
    if lock.exists():
        print(f"a run is active (lock: {lock.read_text().strip()})")
    if s is None:
        print("no runs yet; start with: python tools/ci/fsh_ci.py run --profile fast")
        return 2
    print(f"last run {s['started']}  profile {s['profile']}  {'PASS' if s['ok'] else 'FAIL'} in {s['duration_s']}s  commit {s['commit']}{' (dirty)' if s['dirty'] else ''}  [{s.get('trigger', '')}]")
    if s.get("resources"):
        print("  " + s["resources"])
    for n, st in s["stages"].items():
        line = (st["detail"].splitlines() or [""])[-1] if st["status"] in (PASS, WARN, CACHED) else (st["detail"].splitlines() or [""])[0]
        print(f"  {ICON[st['status']]:5} {n:14} {st['seconds']:6.1f}s  {line[:90]}")
    if s.get("infra_retries"):
        print(f"  {s['infra_retries']} infrastructure retr{'y' if s['infra_retries'] == 1 else 'ies'}")
    return 0 if s["ok"] else 1


def cmd_doctor(args) -> int:
    """Everything about the machine that decides whether a run will go well, with what to do about each problem."""
    env = base_env()
    snap = rt.memory_snapshot()
    plan = rt.plan_resources(snap, os.cpu_count() or 2, args.jobs)
    ctx = Ctx(run_dir=STATE / "preflight", env=env, rid=host_rid(), require_aot=False, no_cache=True)
    rows: list[tuple[str, str, str]] = []  # (level, topic, message)

    def add(level: str, topic: str, msg: str):
        rows.append((level, topic, msg))

    add("info", "host", f"{host_rid()}, {platform.platform()}, {os.cpu_count()} CPUs, python {platform.python_version()}")
    if snap:
        add("ok" if plan.level == "ok" else ("warn" if plan.level == "tight" else "fail"), "memory",
            f"{snap['total_mb'] / 1024:.1f} GB RAM, {snap['avail_mb'] / 1024:.1f} GB available"
            + (f", {snap['commit_free_mb'] / 1024:.1f} GB commit headroom" if "commit_total_mb" in snap else "") + f" -> {plan.line()}")
        if plan.level != "ok":
            who = rt.describe_consumers(rt.top_consumers())
            if who:
                add("info", "memory", "largest: " + who)
    else:
        add("warn", "memory", "could not be read")
    free = shutil.disk_usage(ROOT).free / 1e9
    add("ok" if free >= 5 else ("warn" if free >= 2 else "fail"), "disk", f"{free:.1f} GB free")
    dotnet = which("dotnet", env)
    pinned = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    v = rt.tool_version(dotnet, ["--version"], env)
    add("ok" if dotnet and version_tuple(v) >= version_tuple(pinned) else "fail", "dotnet", f"{v} (global.json pin {pinned})")
    for label, exe, a in (("git", "git", ["--version"]), ("node", "node", ["--version"]), ("npm", "npm", ["--version"]), ("cargo", "cargo", ["--version"])):
        found = which(exe, env)
        add("ok" if found else "warn", label, rt.tool_version(found, a, env))
    aot_ok, aot_msg = aot_toolchain(ctx)
    add("ok" if aot_ok else "warn", "native aot", aot_msg)
    # fsh-ci state
    runs = len(list((STATE / "runs").iterdir())) if (STATE / "runs").exists() else 0
    cache = rt.JsonStore(STATE / "cache.json")
    add("info", "state", f"{rt.dir_size(STATE) / 1e6:.0f} MB in .fsharpened/ci, {runs} run(s), {len(cache.data)} cached stage(s)") if STATE.exists() else add("info", "state", "no runs yet")
    lock = STATE / "lock"
    stale = False
    if lock.exists():
        try:
            pid = int(lock.read_text().split()[0])
        except Exception:
            pid = 0
        stale = not (pid and pid_alive(pid))
        add("warn" if stale else "info", "lock", f"{'stale ' if stale else ''}run lock (pid {pid})")
    hooks = (git_lines("config", "core.hooksPath") or [""])[0]
    add("ok" if hooks == "tools/ci/hooks" else "warn", "hooks", "pre-commit (fast) and pre-push (full) installed" if hooks == "tools/ci/hooks" else "not installed: python tools/ci/fsh_ci.py install-hooks")
    leftovers = [r for r in rt.top_consumers(200) if r[0].lower().startswith(("msbuild", "testhost", "vbcscompiler", "fsc"))]
    if leftovers and not lock.exists():
        add("warn", "processes", "build processes with no run active: " + rt.describe_consumers(leftovers) + " (doctor --fix stops build servers)")
    if args.fix:
        if stale:
            lock.unlink(missing_ok=True)
            add("info", "fix", "removed the stale lock")
        if dotnet:
            subprocess.run([dotnet, "build-server", "shutdown"], capture_output=True, env=env, timeout=120)
            add("info", "fix", "stopped dotnet build servers")
        prune_runs()
        add("info", "fix", "pruned old runs")
    if args.json:
        print(json.dumps([{"level": l, "topic": t, "message": m} for l, t, m in rows], indent=1))
    else:
        mark = {"ok": "ok  ", "info": "    ", "warn": "WARN", "fail": "FAIL"}
        for l, t, m in rows:
            print(f"{mark[l]} {t:11} {m}")
    return 1 if any(l == "fail" for l, _, _ in rows) else 0


def cmd_verify(args) -> int:
    """Check a release bundle: every file against SHA256SUMS, nothing added or removed, provenance consistent."""
    if args.dir:
        target = pathlib.Path(args.dir)
    else:
        found = sorted((ROOT / "artifacts" / "release").glob("*/"), key=lambda p: p.stat().st_mtime) if (ROOT / "artifacts" / "release").exists() else []
        if not found:
            print("no bundle found in artifacts/release (build one with: run --profile release)", file=sys.stderr)
            return 2
        target = found[-1]
    head = (git_lines("rev-parse", "HEAD") or [""])[0]
    problems = rel.verify_bundle(target, head if args.strict_head else None)
    if problems:
        print(f"{target}: NOT intact")
        for p in problems:
            print("  " + p)
        return 1
    prov = json.loads((target / rel.PROVENANCE).read_text(encoding="utf-8"))
    n = len(rel.parse_checksums((target / rel.SUMS).read_text(encoding="utf-8")))
    print(f"{target}: intact ({n} files hashed)")
    print(f"  built from {prov['commit'][:10]} on {prov['branch']}, rid {prov['rid']}, SDK {prov['sdk']}, {prov['generated_utc']}")
    if head and not head.startswith(prov["commit"][:10]):
        print(f"  note: HEAD is now {head[:10]}")
    print("  published: " + ("no (a person reviews and uploads it)" if prov.get("published") is False else "UNKNOWN") + (", built from a dirty tree" if prov.get("dirty") else ""))
    return 0


WATCH_PATTERNS = ["**/*.fs", "**/*.fsproj", "**/*.md", "**/*.props", "**/*.json", "tools/ci/*.py", "tools/scripts/*.py", "native/rust/**",
                  "ide/**", "gateway/**", "worker/**", "site/**", "tools/site/*.py", ".editorconfig", "global.json"]


def cmd_watch(args) -> int:
    """Run the profile now and again whenever a source file changes. Stages whose inputs did not change come from the cache,
    so editing one file re-runs only what it can affect."""
    def inputs() -> str:
        INDEX.refresh()
        return INDEX.hash(WATCH_PATTERNS)

    def once() -> int:
        rc = cmd_run(args)
        print(f"-- {'PASS' if rc == 0 else 'FAIL'}; waiting for changes --", flush=True)
        return rc

    try:
        rt.watch_loop(inputs, once, poll=args.poll)
    except KeyboardInterrupt:
        print("\nstopped watching")
    return 0


def cmd_clean(args) -> int:
    shutil.rmtree(STATE, ignore_errors=True)
    print("removed .fsharpened/ci (cache, runs, history)")
    return 0


def cmd_install_hooks(args) -> int:
    if not (ROOT / ".git").exists():
        print("not a git checkout", file=sys.stderr)
        return 1
    subprocess.run(["git", "config", "core.hooksPath", "tools/ci/hooks"], cwd=ROOT, check=True)
    for h in ("pre-commit", "pre-push"):
        subprocess.run(["git", "update-index", "--chmod=+x", f"tools/ci/hooks/{h}"], cwd=ROOT, capture_output=True)
        try:
            os.chmod(ROOT / "tools" / "ci" / "hooks" / h, 0o755)
        except OSError:
            pass
    print("hooks installed: pre-commit runs the 'fast' profile, pre-push runs 'full'. Bypass once with --no-verify.")
    return 0


def cmd_uninstall_hooks(args) -> int:
    subprocess.run(["git", "config", "--unset", "core.hooksPath"], cwd=ROOT)
    print("hooks uninstalled")
    return 0


def add_run_options(r: argparse.ArgumentParser, default_profile: str = "full") -> None:
    r.add_argument("--profile", choices=PROFILES, default=default_profile)
    r.add_argument("--only", nargs="+")
    r.add_argument("--skip", nargs="+")
    r.add_argument("--no-cache", action="store_true")
    r.add_argument("--keep-going", action="store_true", help="run independent stages after a failure")
    r.add_argument("--jobs", type=int, default=min(4, os.cpu_count() or 2), help="upper bound; the resource governor may lower it")
    r.add_argument("--rid", help="override host RID for the AOT stage")
    r.add_argument("--require-aot", action="store_true", help="fail (not skip) when the NativeAOT toolchain is missing")
    r.add_argument("--require-rust", action="store_true", help="fail (not skip) when cargo is missing; also makes the Interop.Rust tests fail without it")
    r.add_argument("--require-node", action="store_true", help="fail (not skip) when node is missing for the web, gateway, hub and site stages")
    r.add_argument("--ignore-resources", action="store_true", help="run even when free memory is below the safe minimum")
    r.add_argument("--allow-dirty", action="store_true", help="let the release bundle name a commit although tracked files are modified")
    r.add_argument("--hang-timeout", type=int, default=300, help="seconds one test may run before it is named as a hang and killed")
    r.add_argument("--ci", action="store_true", help="CI mode: cache off")
    r.add_argument("--hook", action="store_true", help="invoked from a git hook")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="fsh-ci", description=__doc__.split("\n")[1])
    sub = ap.add_subparsers(dest="cmd")
    add_run_options(sub.add_parser("run"))
    w = sub.add_parser("watch")
    add_run_options(w, "fast")
    w.add_argument("--poll", type=float, default=2.0, help="seconds between looks at the files")
    p = sub.add_parser("preflight")
    for flag in ("--require-aot", "--require-rust", "--require-node", "--ignore-resources"):
        p.add_argument(flag, action="store_true")
    d = sub.add_parser("doctor")
    d.add_argument("--fix", action="store_true", help="remove a stale lock, stop build servers, prune old runs")
    d.add_argument("--json", action="store_true")
    d.add_argument("--jobs", type=int, default=min(4, os.cpu_count() or 2))
    st = sub.add_parser("status")
    st.add_argument("--json", action="store_true")
    v = sub.add_parser("verify")
    v.add_argument("dir", nargs="?", help="a bundle folder (default: the newest in artifacts/release)")
    v.add_argument("--strict-head", action="store_true", help="also fail when the bundle was built from another commit than HEAD")
    for name in ("list", "history", "clean", "install-hooks", "uninstall-hooks"):
        sub.add_parser(name)
    args = ap.parse_args(argv)
    if args.cmd is None:
        args = ap.parse_args(["run"])
    table = {"run": cmd_run, "watch": cmd_watch, "preflight": cmd_preflight, "doctor": cmd_doctor, "status": cmd_status, "verify": cmd_verify,
             "list": cmd_list, "history": cmd_history, "clean": cmd_clean, "install-hooks": cmd_install_hooks, "uninstall-hooks": cmd_uninstall_hooks}
    return table[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
