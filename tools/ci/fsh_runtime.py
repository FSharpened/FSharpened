"""Machine-facing helpers for fsh-ci: memory and the resource governor, process trees, failure classification,
atomic state, and a fast file index. Standard library only; Windows, Linux, macOS.

Everything here is either pure (so tools/ci/test_fsh_ci.py can test it with made-up inputs) or a thin wrapper over
one operating-system call. The pipeline itself lives in fsh_ci.py.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import threading
import time
from dataclasses import dataclass, field
from typing import Callable, Iterable, Optional


# --------------------------------------------------------------------------- memory and the resource governor
def memory_snapshot() -> Optional[dict]:
    """{total_mb, avail_mb, commit_free_mb}. Windows enforces a *commit limit* (RAM + page file) that is hit long
    before physical memory runs out when other processes reserve a lot, so both numbers are reported and the governor
    uses the smaller one. None when the platform gives no answer."""
    try:
        if os.name == "nt":
            import ctypes

            class MEMORYSTATUSEX(ctypes.Structure):
                _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                            ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                            ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                            ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                            ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]

            st = MEMORYSTATUSEX()
            st.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
            if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(st)):
                return None
            mb = 1024 * 1024
            return {"total_mb": st.ullTotalPhys // mb, "avail_mb": st.ullAvailPhys // mb,
                    "commit_free_mb": st.ullAvailPageFile // mb, "commit_total_mb": st.ullTotalPageFile // mb}
        if os.path.exists("/proc/meminfo"):
            kv = {}
            with open("/proc/meminfo", encoding="ascii", errors="ignore") as f:
                for line in f:
                    k, _, v = line.partition(":")
                    kv[k] = int(v.split()[0]) // 1024 if v.split() else 0
            avail = kv.get("MemAvailable", kv.get("MemFree", 0))
            return {"total_mb": kv.get("MemTotal", 0), "avail_mb": avail, "commit_free_mb": avail + kv.get("SwapFree", 0)}
        if os.uname().sysname == "Darwin":  # type: ignore[attr-defined]
            total = int(subprocess.run(["sysctl", "-n", "hw.memsize"], capture_output=True, text=True, timeout=10).stdout) // (1024 * 1024)
            vm = subprocess.run(["vm_stat"], capture_output=True, text=True, timeout=10).stdout
            page = int(re.search(r"page size of (\d+)", vm).group(1))  # type: ignore[union-attr]
            pages = sum(int(m.group(1)) for m in re.finditer(r"Pages (?:free|inactive|speculative):\s+(\d+)", vm))
            avail = pages * page // (1024 * 1024)
            return {"total_mb": total, "avail_mb": avail, "commit_free_mb": avail}
    except Exception:
        return None
    return None


def parse_processes(text: str, windows: bool) -> list[tuple[str, int]]:
    """(name, MB) per process from `tasklist /FO CSV /NH` (windows) or `ps -eo rss=,comm=` output."""
    rows = []
    for line in text.splitlines():
        line = line.strip()
        if not line:
            continue
        if windows:
            cells = [c.strip('"') for c in re.split(r'","', line.strip('"'))]
            if len(cells) >= 5:
                kb = re.sub(r"[^\d]", "", cells[4])
                if kb:
                    rows.append((cells[0], int(kb) // 1024))
        else:
            parts = line.split(None, 1)
            if len(parts) == 2 and parts[0].isdigit():
                rows.append((os.path.basename(parts[1]), int(parts[0]) // 1024))
    return rows


def top_consumers(n: int = 4, raw: Optional[list[tuple[str, int]]] = None) -> list[tuple[str, int, int]]:
    """The processes that hold the most memory, grouped by name: [(name, count, MB)] largest first."""
    if raw is None:
        try:
            if os.name == "nt":
                out = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, text=True, timeout=20).stdout
                raw = parse_processes(out, True)
            else:
                out = subprocess.run(["ps", "-eo", "rss=,comm="], capture_output=True, text=True, timeout=20).stdout
                raw = parse_processes(out, False)
        except Exception:
            return []
    grouped: dict[str, list[int]] = {}
    for name, mb in raw:
        grouped.setdefault(name, []).append(mb)
    rows = [(name, len(v), sum(v)) for name, v in grouped.items()]
    rows.sort(key=lambda r: -r[2])
    return rows[:n]


def describe_consumers(rows: list[tuple[str, int, int]]) -> str:
    return ", ".join(f"{name}{' x' + str(c) if c > 1 else ''} {mb / 1024:.1f} GB" for name, c, mb in rows if mb >= 200)


CRITICAL_MB = 1500  # below this a build or test run dies in confusing ways (MSBuild node crashes, OutOfMemoryException)
TIGHT_MB = 4000  # below this one dotnet process at a time, one MSBuild node
PER_NODE_MB = 1000


@dataclass
class Plan:
    level: str  # ok | tight | critical | unknown
    jobs: int
    nodes: int  # MSBuild node count for -m:N; 0 means "leave MSBuild's default"
    free_mb: Optional[int]
    reasons: list[str] = field(default_factory=list)

    def line(self) -> str:
        free = "unknown" if self.free_mb is None else f"{self.free_mb / 1024:.1f} GB"
        nodes = f"-m:{self.nodes}" if self.nodes else "-m default"
        return f"memory {self.level} (usable {free}): jobs {self.jobs}, msbuild {nodes}"


def usable_mb(snap: Optional[dict]) -> Optional[int]:
    if not snap:
        return None
    vals = [v for v in (snap.get("avail_mb"), snap.get("commit_free_mb")) if v is not None]
    return min(vals) if vals else None


def plan_resources(snap: Optional[dict], cpus: int, jobs: int, ignore_critical: bool = False) -> Plan:
    """How much parallelism this machine can take right now. Pure: the same snapshot always gives the same plan."""
    cpus = max(1, cpus)
    jobs = max(1, jobs)
    free = usable_mb(snap)
    if free is None:
        return Plan("unknown", jobs, 0, None, ["memory could not be read; using the requested parallelism"])
    if free < CRITICAL_MB:
        if ignore_critical:
            return Plan("tight", 1, 1, free, [f"only {free} MB usable, continuing because resources are ignored"])
        return Plan("critical", 1, 1, free, [f"only {free} MB usable (need >= {CRITICAL_MB})"])
    if free < TIGHT_MB:
        return Plan("tight", 1, 1 if free < 2500 else 2, free, [f"{free} MB usable (< {TIGHT_MB}); running one thing at a time"])
    nodes = max(1, min(cpus, 8, free // PER_NODE_MB))
    return Plan("ok", max(1, min(jobs, free // 2500)), nodes, free)


def wait_for_headroom(min_mb: int, timeout: float, poll: float = 5.0, snap_fn: Callable[[], Optional[dict]] = memory_snapshot,
                      sleep: Callable[[float], None] = time.sleep, clock: Callable[[], float] = time.time) -> tuple[bool, float, Optional[int]]:
    """Wait until at least `min_mb` is usable. Returns (enough, seconds waited, last reading). An unreadable machine is
    treated as having enough: the gate must never be the reason a run does not happen."""
    t0 = clock()
    free = usable_mb(snap_fn())
    while free is not None and free < min_mb and clock() - t0 < timeout:
        sleep(poll)
        free = usable_mb(snap_fn())
    return (free is None or free >= min_mb), clock() - t0, free


# --------------------------------------------------------------------------- failure classification
# Exit codes that mean a process was killed by the machine, not that the code is wrong (access violation, stack
# overflow, MSBuild's own "node crashed" codes), as signed and unsigned 32-bit values.
INFRA_RCS = {-1073741819, -1073741571, -2147450743, 3221225477, 3221225725, 2147516553}

INFRA_SIGNATURES: list[tuple[re.Pattern, str]] = [
    (re.compile(r"OutOfMemoryException|Insufficient memory|Cannot allocate memory|There is not enough memory"), "out of memory"),
    (re.compile(r"MSB4166|Child node \"?\d+\"? exited prematurely"), "MSBuild node crashed"),
    (re.compile(r"MSB4017|The build stopped unexpectedly"), "MSBuild stopped unexpectedly"),
    (re.compile(r"MSB6006:.*exited with code -?(1073741819|1073741571|2147450743|3221225477|3221225725)"), "build tool crashed"),
    (re.compile(r"MSB32(?:21|26|27):|being used by another process"), "file locked by another process"),
    (re.compile(r"Unable to load the service index|NU1301|TaskCanceledException.*HttpClient|Name or service not known"), "network blip"),
    (re.compile(r"Could not execute because the specified command or file was not found.*dotnet-fantomas"), "tool restore raced"),
]
# A real diagnostic means the code is wrong: never retry, never hide it behind an infrastructure explanation.
REAL_FAILURE = re.compile(r"\berror (?:FS|CS)\d+\b|^\s*Failed \S.*\[\d+|Failed:\s*[1-9]\d*,|Assert\.|Expected:", re.MULTILINE)


def infra_failure(rc: int, out: str) -> Optional[str]:
    """Why a failed command looks like the machine's fault (returns the reason), or None when it looks like the code's."""
    if rc == 0 or rc in (124, 127):  # a timeout is not retried: it would only repeat the wait
        return None
    if REAL_FAILURE.search(out):
        return None
    for pat, why in INFRA_SIGNATURES:
        if pat.search(out):
            return why
    if rc in INFRA_RCS:
        return f"process died with exit code {rc}"
    return None


def resource_hint(out: str) -> Optional[str]:
    """A failure that retrying cannot fix but that is worth naming."""
    if re.search(r"No space left on device|There is not enough space on the disk|ENOSPC", out):
        return "the disk is full"
    return None


ANSI = re.compile("\\x1b\\[[0-9;]*[A-Za-z]")


def strip_ansi(text: str) -> str:
    return ANSI.sub("", text)


def parse_test_output(out: str) -> dict:
    """Totals, failed test names and who to blame when a test run hangs or its host crashes."""
    passed = failed = skipped = 0
    for m in re.finditer(r"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)", out):
        failed += int(m.group(1))
        passed += int(m.group(2))
        skipped += int(m.group(3))
    names = []
    for m in re.finditer(r"^\s*Failed (\S.*?) \[\d+", out, re.MULTILINE):
        if m.group(1) not in names:
            names.append(m.group(1))
    blame = []
    for line in out.splitlines():
        s = line.strip()
        if re.search(r"Blame:|Test Run Aborted|The active test run was aborted|Hang timeout|test host process crashed|was running when", s, re.I):
            if s not in blame:
                blame.append(s)
    return {"passed": passed, "failed": failed, "skipped": skipped, "failed_names": names, "blame": blame}


def with_nodes(cmd: list[str], n: int) -> list[str]:
    """The same command with MSBuild's node count set to n (any earlier -m:N removed)."""
    kept = [c for c in cmd if not re.fullmatch(r"-(?:m|maxcpucount)(?::\d+)?", c, re.I)]
    return kept + [f"-m:{n}"] if n else kept


# --------------------------------------------------------------------------- process trees
_ACTIVE: dict[int, subprocess.Popen] = {}
_ACTIVE_LOCK = threading.Lock()


def kill_tree(pid: int) -> None:
    """Kill a process and everything it started. On Windows `taskkill /T`, elsewhere the process group."""
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, timeout=30)
        else:
            os.killpg(os.getpgid(pid), signal.SIGKILL)
    except Exception:
        try:
            os.kill(pid, 9)
        except Exception:
            pass


def kill_all() -> int:
    with _ACTIVE_LOCK:
        pids = list(_ACTIVE)
    for pid in pids:
        kill_tree(pid)
    return len(pids)


def run_tree(cmd: list[str], cwd, env: dict, out, timeout: float) -> tuple[int, bool]:
    """Run to completion, writing stdout+stderr to `out`. A timeout, Ctrl+C or a crash of this runner kills the whole
    process tree (MSBuild nodes, test hosts, compiler servers), not only the direct child. Returns (rc, timed_out)."""
    kwargs: dict = {}
    if os.name == "nt":
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP  # type: ignore[attr-defined]
    else:
        kwargs["start_new_session"] = True
    proc = subprocess.Popen(cmd, cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=out, stderr=subprocess.STDOUT, **kwargs)
    with _ACTIVE_LOCK:
        _ACTIVE[proc.pid] = proc
    try:
        try:
            return proc.wait(timeout=timeout), False
        except subprocess.TimeoutExpired:
            kill_tree(proc.pid)
            proc.wait()
            return 124, True
        except BaseException:
            kill_tree(proc.pid)
            raise
    finally:
        with _ACTIVE_LOCK:
            _ACTIVE.pop(proc.pid, None)


# --------------------------------------------------------------------------- atomic, thread-safe state
class JsonStore:
    """A small JSON object on disk, safe to update from several threads: every change is written to a temporary file
    and renamed over the old one, so a crash can never leave half a file."""

    def __init__(self, path):
        self.path = path
        self.lock = threading.Lock()
        self.data: dict = {}
        try:
            loaded = json.loads(path.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                self.data = loaded
        except Exception:
            pass

    def _save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_name(self.path.name + f".{os.getpid()}.{threading.get_ident()}.tmp")
        tmp.write_text(json.dumps(self.data, indent=1, sort_keys=True), encoding="utf-8")
        for attempt in range(5):  # Windows refuses to replace a file another process has open for a moment
            try:
                os.replace(tmp, self.path)
                return
            except OSError:
                if attempt == 4:
                    tmp.unlink(missing_ok=True)
                    raise
                time.sleep(0.05 * (attempt + 1))

    def get(self, key: str):
        with self.lock:
            return self.data.get(key)

    def set(self, key: str, value) -> None:
        with self.lock:
            self.data[key] = value
            self._save()

    def delete(self, key: str) -> None:
        with self.lock:
            if key in self.data:
                del self.data[key]
                self._save()


# --------------------------------------------------------------------------- the file index
def glob_regex(pattern: str) -> re.Pattern:
    """A glob as a regular expression over '/'-separated relative paths: `**/` is any number of folders (also none),
    a trailing `**` is everything below, `*` and `?` stay inside one folder."""
    out, i = [], 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif pattern.startswith("**", i):
            out.append(".*")
            i += 2
        elif pattern[i] == "*":
            out.append("[^/]*")
            i += 1
        elif pattern[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(pattern[i]))
            i += 1
    return re.compile("".join(out) + r"\Z")


class FileIndex:
    """One walk of the tree (pruning the folders that never matter) instead of one recursive glob per pattern, and a
    content digest per file that is only recomputed when the file's size or modification time changed."""

    def __init__(self, root, exclude: Iterable[str]):
        self.root = str(root)
        self.exclude = set(exclude)
        self._files: Optional[list[str]] = None
        self._digests: dict[str, tuple[int, int, str]] = {}
        self._lock = threading.Lock()

    def refresh(self) -> None:
        self._files = None

    def files(self) -> list[str]:
        if self._files is None:
            found = []
            for dirpath, dirnames, filenames in os.walk(self.root):
                dirnames[:] = [d for d in dirnames if d not in self.exclude]
                rel_dir = os.path.relpath(dirpath, self.root).replace("\\", "/")
                for f in filenames:
                    found.append(f if rel_dir == "." else f"{rel_dir}/{f}")
            found.sort()
            self._files = found
        return self._files

    def select(self, patterns: Iterable[str]) -> list[str]:
        regexes = [glob_regex(p) for p in patterns]
        return [f for f in self.files() if any(r.match(f) for r in regexes)]

    def digest(self, rel: str) -> str:
        path = os.path.join(self.root, rel)
        st = os.stat(path)
        with self._lock:
            hit = self._digests.get(rel)
            if hit and hit[0] == st.st_size and hit[1] == st.st_mtime_ns:
                return hit[2]
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for block in iter(lambda: f.read(1 << 20), b""):
                h.update(block)
        d = h.hexdigest()
        with self._lock:
            self._digests[rel] = (st.st_size, st.st_mtime_ns, d)
        return d

    def hash(self, patterns: Iterable[str], extra: str = "") -> str:
        h = hashlib.sha256()
        h.update(extra.encode())
        for rel in self.select(patterns):
            try:
                d = self.digest(rel)
            except OSError:  # deleted between the walk and the read: the next walk will not list it
                continue
            h.update(rel.encode())
            h.update(b"\0")
            h.update(d.encode())
            h.update(b"\0")
        return h.hexdigest()


# --------------------------------------------------------------------------- watching
def watch_loop(hash_fn: Callable[[], str], run_fn: Callable[[], int], poll: float = 2.0, settle: float = 1.5,
               sleep: Callable[[float], None] = time.sleep, max_runs: Optional[int] = None,
               idle_polls: Optional[int] = None, say: Callable[[str], None] = print) -> int:
    """Run `run_fn` once, then again each time the inputs change and stay unchanged for `settle` seconds. Returns the
    number of runs. `max_runs` and `idle_polls` exist so a test can stop the loop."""
    last = hash_fn()
    runs = 0
    idle = 0
    say("watching (Ctrl+C to stop)")
    run_fn()
    runs += 1
    while max_runs is None or runs < max_runs:
        sleep(poll)
        now = hash_fn()
        if now == last:
            idle += 1
            if idle_polls is not None and idle >= idle_polls:
                break
            continue
        idle = 0
        # wait for the editor or formatter to finish writing
        while True:
            sleep(settle)
            again = hash_fn()
            if again == now:
                break
            now = again
        last = now
        say("change detected")
        run_fn()
        runs += 1
    return runs


def dir_size(path) -> int:
    total = 0
    for dp, _dn, fn in os.walk(path):
        for f in fn:
            try:
                total += os.path.getsize(os.path.join(dp, f))
            except OSError:
                pass
    return total


def tool_version(exe: Optional[str], args: list[str], env: dict) -> str:
    if not exe:
        return "not found"
    try:
        out = subprocess.run([exe, *args], capture_output=True, text=True, timeout=30, env=env)
        return (out.stdout or out.stderr).strip().splitlines()[0] if (out.stdout or out.stderr).strip() else "?"
    except Exception as e:
        return f"error: {type(e).__name__}"


def which(name: str, env: dict) -> Optional[str]:
    return shutil.which(name, path=env.get("PATH"))
