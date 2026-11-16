#!/usr/bin/env python3
"""FSharpened local CI/CD pipeline ("fsh-ci").

One entrypoint used identically on a developer machine, in a git hook, and in GitHub Actions
(Invariant: local and remote CI must not diverge). Standard library only; Windows, Linux, macOS.

  python tools/ci/fsh_ci.py run [--profile fast|full|release] [--only S ...] [--skip S ...]
                                [--no-cache] [--keep-going] [--jobs N] [--rid RID] [--require-aot]
  python tools/ci/fsh_ci.py preflight      # checks only
  python tools/ci/fsh_ci.py list           # stages and profiles
  python tools/ci/fsh_ci.py history        # recent runs and stage timings
  python tools/ci/fsh_ci.py clean          # drop cache and old runs
  python tools/ci/fsh_ci.py install-hooks  # pre-commit (fast) and pre-push (full)
  python tools/ci/fsh_ci.py uninstall-hooks

Features: preflight gate (toolchain, SDK pin, disk, secrets, merge markers, large files, network),
dependency-ordered parallel scheduler, content-addressed result cache, retry with backoff for
restore, per-stage timeouts and logs, single-run lock with stale detection, trx test results,
JSON + Markdown reports, duration history with slow-stage regression warnings, honest per-RID
coverage reporting. Skill: .agents/skills/run-local-ci.md   Docs: docs/operations/local-ci.md
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
import socket
import statistics
import subprocess
import sys
import threading
import time
import urllib.request
from dataclasses import dataclass, field
from typing import Callable, Optional

ROOT = pathlib.Path(__file__).resolve().parents[2]
STATE = ROOT / ".fsharpened" / "ci"
SIX_RIDS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]
EXCLUDE_DIRS = {".git", "bin", "obj", "artifacts", ".fsharpened", "node_modules", "__pycache__", ".vs", ".idea"}

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
    sep = os.pathsep
    current = env.get("PATH", "")
    for p in extra:
        if os.path.isdir(p) and p not in current.split(sep):
            current = current + sep + p
    env["PATH"] = current
    return env


def base_env() -> dict:
    env = augment_path(dict(os.environ))
    env.update(
        {
            "CI": "true",  # strict mode: warnings are errors (Directory.Build.props)
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
            "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
            "MSBUILDDISABLENODEREUSE": "1",
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
    seen = set()
    for pat in patterns:
        for p in ROOT.glob(pat):
            if p.is_file() and not (set(p.relative_to(ROOT).parts) & EXCLUDE_DIRS):
                if p not in seen:
                    seen.add(p)
                    yield p


def hash_inputs(patterns: list[str], extra: str = "") -> str:
    h = hashlib.sha256()
    h.update(extra.encode())
    for p in sorted(iter_files(patterns)):
        h.update(str(p.relative_to(ROOT)).replace("\\", "/").encode())
        h.update(b"\0")
        h.update(p.read_bytes())
        h.update(b"\0")
    return h.hexdigest()


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
    results: dict = field(default_factory=dict)

    def log(self, stage: str) -> pathlib.Path:
        return self.run_dir / f"{stage}.log"


def run_cmd(ctx: Ctx, stage: str, cmd: list[str], timeout: int = 900, cwd=None, append=False) -> tuple[int, str]:
    """Run a command, stream to the stage log, return (rc, combined output)."""
    mode = "ab" if append else "wb"
    resolved = cmd[:]
    exe = which(cmd[0], ctx.env)
    if exe:
        resolved[0] = exe
    with open(ctx.log(stage), mode) as f:
        f.write(("$ " + " ".join(cmd) + "\n").encode())
        f.flush()
        try:
            proc = subprocess.run(
                resolved, cwd=cwd or ROOT, env=ctx.env, stdout=f, stderr=subprocess.STDOUT, timeout=timeout
            )
            rc = proc.returncode
        except subprocess.TimeoutExpired:
            f.write(f"\n[fsh-ci] TIMEOUT after {timeout}s\n".encode())
            rc = 124
        except FileNotFoundError as e:
            f.write(f"\n[fsh-ci] command not found: {e}\n".encode())
            rc = 127
    return rc, ctx.log(stage).read_text(encoding="utf-8", errors="replace")


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


PREFLIGHT_CHECKS: list[tuple[str, Callable]] = [
    ("python", check_python),
    ("git", check_git),
    ("dotnet-sdk", check_dotnet),
    ("disk-space", check_disk),
    ("registry", check_registry),
    ("secrets", check_secrets),
    ("conflict-markers", check_markers),
    ("large-files", check_large),
    ("aot-toolchain", check_aot_toolchain),
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


def retry(ctx: Ctx, stage: str, cmd: list[str], attempts: int = 3, timeout: int = 600) -> tuple[int, str]:
    rc, out = 1, ""
    for i in range(1, attempts + 1):
        rc, out = run_cmd(ctx, stage, cmd, timeout=timeout, append=(i > 1))
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


def stage_build(ctx: Ctx) -> Result:
    rc, out = run_cmd(ctx, "build", ["dotnet", "build", "FSharpened.slnx", "-c", "Release", "--no-restore", "-p:CI=true", "-v:m", "-nologo"], timeout=1200)
    warns = len(re.findall(r": warning ", out))
    m = re.search(r"(\d+) Warning\(s\)", out)
    if m:
        warns = int(m.group(1))
    return Result(PASS if rc == 0 else FAIL, f"{warns} warning(s)" if rc == 0 else tail(out, 15), metrics={"warnings": warns})


def stage_test(ctx: Ctx) -> Result:
    results = ctx.run_dir / "tests"
    results.mkdir(exist_ok=True)
    cmd = ["dotnet", "test", "FSharpened.slnx", "-c", "Release", "--no-build", "-p:CI=true", "--logger", "trx;LogFileName=results.trx",
           "--results-directory", str(results), "-nologo"]
    rc, out = run_cmd(ctx, "test", cmd, timeout=1200)
    failed = passed = skipped = 0
    for m in re.finditer(r"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)", out):
        failed += int(m.group(1)); passed += int(m.group(2)); skipped += int(m.group(3))
    if rc == 0 and passed == 0:
        return Result(FAIL, "zero tests executed (a green run with no tests is a failure)", metrics={"passed": 0})
    return Result(PASS if rc == 0 else FAIL, f"{passed} passed, {failed} failed, {skipped} skipped" if rc == 0 else tail(out, 15),
                  metrics={"passed": passed, "failed": failed, "skipped": skipped})


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


# stage name -> (function, deps, input globs for the cache key, exclusive dotnet lock, cacheable)
CODE = ["src/**/*.fs", "src/**/*.fsproj", "tools/**/*.fs", "tools/**/*.fsproj", "tests/**/*.fs", "tests/**/*.fsproj",
        "Directory.Build.props", "Directory.Packages.props", "global.json", "FSharpened.slnx"]
STAGES: dict[str, dict] = {
    "preflight": dict(fn=stage_preflight, deps=[], inputs=None, excl=False),
    "docs-drift": dict(fn=stage_docs_drift, deps=["preflight"], inputs=["**/*.md", "registry/**", "src/**/*.fsproj", ".agents/**", "tools/scripts/*.py"], excl=False),
    "selftest": dict(fn=stage_selftest, deps=["preflight"], inputs=["tools/ci/*.py"], excl=False),
    "tools-restore": dict(fn=stage_tools_restore, deps=["preflight"], inputs=None, excl=True),
    "format": dict(fn=stage_format, deps=["tools-restore"], inputs=["**/*.fs", ".editorconfig", ".config/dotnet-tools.json"], excl=False),
    "restore": dict(fn=stage_restore, deps=["preflight"], inputs=None, excl=True),
    "build": dict(fn=stage_build, deps=["restore"], inputs=CODE, excl=True),
    "test": dict(fn=stage_test, deps=["build"], inputs=CODE, excl=True),
    "aot": dict(fn=stage_aot, deps=["build"], inputs=CODE, excl=True),
    "benchmarks": dict(fn=stage_benchmarks, deps=["build"], inputs=None, excl=True),
    "pack": dict(fn=stage_pack, deps=["build", "test"], inputs=CODE, excl=True),
}
PROFILES = {
    "fast": ["preflight", "docs-drift", "selftest", "tools-restore", "format"],
    "full": ["preflight", "docs-drift", "selftest", "tools-restore", "format", "restore", "build", "test", "aot", "benchmarks"],
    "release": ["preflight", "docs-drift", "selftest", "tools-restore", "format", "restore", "build", "test", "aot", "benchmarks", "pack"],
}
# A cache hit is only trusted for stages whose outputs are not consumed by a later stage in the same run.
CACHEABLE = {"docs-drift", "selftest", "format", "test", "aot"}


# --------------------------------------------------------------------------- scheduler
class Cache:
    def __init__(self):
        self.path = STATE / "cache.json"
        self.data = {}
        try:
            self.data = json.loads(self.path.read_text(encoding="utf-8"))
        except Exception:
            pass

    def key(self, name: str, ctx: Ctx) -> Optional[str]:
        spec = STAGES[name]
        if spec["inputs"] is None or name not in CACHEABLE:
            return None
        return hash_inputs(spec["inputs"], extra=f"{name}|{ctx.sdk_version}|{ctx.rid}|{sys.version_info[:2]}")

    def hit(self, name: str, key: Optional[str]) -> bool:
        return bool(key) and self.data.get(name) == key

    def store(self, name: str, key: Optional[str]):
        if key:
            self.data[name] = key
            STATE.mkdir(parents=True, exist_ok=True)
            self.path.write_text(json.dumps(self.data, indent=1), encoding="utf-8")

    def drop(self, name: str):
        if name in self.data:
            del self.data[name]
            self.path.write_text(json.dumps(self.data, indent=1), encoding="utf-8")


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


def execute(names: list[str], ctx: Ctx, cache: Cache, jobs: int, keep_going: bool, log_fn) -> dict[str, Result]:
    selected = set(names)
    results: dict[str, Result] = {}
    keys = {n: cache.key(n, ctx) for n in names}
    dotnet_lock = threading.Lock()
    failed_dep: set[str] = set()

    def run_one(name: str) -> Result:
        spec = STAGES[name]
        key = keys[name]
        if not ctx.no_cache and cache.hit(name, key):
            return Result(CACHED, "inputs unchanged since last pass")
        t0 = time.time()
        try:
            if spec["excl"]:
                with dotnet_lock:
                    t0 = time.time()
                    r = spec["fn"](ctx)
            else:
                r = spec["fn"](ctx)
        except Exception as e:  # never let a stage crash the runner silently
            r = Result(FAIL, f"stage crashed: {type(e).__name__}: {e}")
        r.duration = time.time() - t0
        if r.status in (PASS, WARN):
            cache.store(name, key)
        else:
            cache.drop(name)
        return r

    pending = list(names)
    stop = False
    with cf.ThreadPoolExecutor(max_workers=max(1, jobs)) as pool:
        running: dict[cf.Future, str] = {}
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
                    log_fn(name, results[name])
                    if results[name].status == FAIL:
                        failed_dep.add(name)
                        stop = True
            elif not progressed:
                break
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
    verified = [ctx.rid] if all(results.get(s, Result(SKIP)).status in (PASS, CACHED) for s in ("build", "test") if s in results) and "build" in results else []
    summary = {
        "ok": ok,
        "profile": profile,
        "rid": ctx.rid,
        "sdk": ctx.sdk_version,
        "commit": (git_lines("rev-parse", "--short", "HEAD") or ["?"])[0],
        "dirty": bool(git_lines("status", "--porcelain")),
        "started": ctx.run_dir.name,
        "duration_s": round(total, 1),
        "stages": {n: {"status": r.status, "seconds": round(r.duration, 1), "detail": r.detail, "metrics": r.metrics} for n, r in results.items()},
        "rids_verified_locally": verified,
        "rids_not_verified_locally": [r for r in SIX_RIDS if r not in verified],
    }
    (ctx.run_dir / "summary.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    lines = [f"# fsh-ci report ({profile})", "", f"- result: **{'PASS' if ok else 'FAIL'}** in {total:.1f}s",
             f"- host rid: `{ctx.rid}`  SDK `{ctx.sdk_version}`  commit `{summary['commit']}`{' (dirty)' if summary['dirty'] else ''}", "",
             "| stage | status | seconds | detail |", "|---|---|---|---|"]
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
        f.write(json.dumps({"t": ctx.run_dir.name, "profile": profile, "ok": ok, "rid": ctx.rid, "total": summary["duration_s"],
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
def cmd_run(args) -> int:
    names = PROFILES[args.profile][:]
    if args.only:
        wanted = set(args.only)
        unknown = wanted - set(STAGES)
        if unknown:
            print("unknown stage(s): " + ", ".join(sorted(unknown)), file=sys.stderr)
            return 2

        def closure(s: str, acc: set):
            acc.add(s)
            for d in STAGES[s]["deps"]:
                closure(d, acc)
            return acc

        need = set()
        for s in wanted:
            closure(s, need)
        names = [s for s in STAGES if s in need]
    if args.skip:
        names = [s for s in names if s not in set(args.skip) or s == "preflight"]
    if args.profile == "release":
        args.require_aot = True

    if not acquire_lock():
        print("another fsh-ci run is active (see .fsharpened/ci/lock)", file=sys.stderr)
        return 3
    try:
        run_id = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
        run_dir = STATE / "runs" / run_id
        run_dir.mkdir(parents=True, exist_ok=True)
        ctx = Ctx(run_dir=run_dir, env=base_env(), rid=args.rid or host_rid(), require_aot=args.require_aot, no_cache=args.no_cache or args.ci)
        cache = Cache()
        print(f"fsh-ci profile={args.profile} rid={ctx.rid} jobs={args.jobs} cache={'off' if ctx.no_cache else 'on'}")
        print(f"logs: {run_dir}")
        t0 = time.time()

        def log_fn(name: str, r: Result):
            print(f"  [{ICON[r.status]:5}] {name:14} {r.duration:6.1f}s  {summary_line(r)}", flush=True)

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
        rest = [n for n in names if n != "preflight"]
        results = dict(pre)
        results.update(execute(rest, ctx, cache, args.jobs, args.keep_going, log_fn) if rest else {})
        # Re-create the dependency view so blocked stages that depended on preflight still resolve.
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
    finally:
        release_lock()


def cmd_preflight(args) -> int:
    ctx = Ctx(run_dir=STATE / "preflight", env=base_env(), rid=host_rid(), require_aot=args.require_aot, no_cache=True)
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
        print(f"{n:14} deps={spec['deps']} cache={'yes' if n in CACHEABLE else 'no'}")
    return 0


def cmd_history(args) -> int:
    hist = STATE / "history.jsonl"
    if not hist.exists():
        print("no runs yet")
        return 0
    for line in hist.read_text(encoding="utf-8").splitlines()[-15:]:
        row = json.loads(line)
        print(f"{row['t']}  {row['profile']:7} {'ok  ' if row['ok'] else 'FAIL'} {row['total']:7.1f}s  {row['rid']}")
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


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="fsh-ci", description=__doc__.split("\n")[1])
    sub = ap.add_subparsers(dest="cmd")
    r = sub.add_parser("run")
    r.add_argument("--profile", choices=PROFILES, default="full")
    r.add_argument("--only", nargs="+")
    r.add_argument("--skip", nargs="+")
    r.add_argument("--no-cache", action="store_true")
    r.add_argument("--keep-going", action="store_true", help="run independent stages after a failure")
    r.add_argument("--jobs", type=int, default=min(4, os.cpu_count() or 2))
    r.add_argument("--rid", help="override host RID for the AOT stage")
    r.add_argument("--require-aot", action="store_true", help="fail (not skip) when the NativeAOT toolchain is missing")
    r.add_argument("--ci", action="store_true", help="CI mode: cache off")
    r.add_argument("--hook", action="store_true", help="invoked from a git hook")
    p = sub.add_parser("preflight")
    p.add_argument("--require-aot", action="store_true")
    for name in ("list", "history", "clean", "install-hooks", "uninstall-hooks"):
        sub.add_parser(name)
    args = ap.parse_args(argv)
    if args.cmd is None:
        args = ap.parse_args(["run"])
    table = {"run": cmd_run, "preflight": cmd_preflight, "list": cmd_list, "history": cmd_history, "clean": cmd_clean,
             "install-hooks": cmd_install_hooks, "uninstall-hooks": cmd_uninstall_hooks}
    return table[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
