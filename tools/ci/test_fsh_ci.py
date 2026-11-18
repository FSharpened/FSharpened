"""Self-tests for fsh-ci (run as the `selftest` stage). Stdlib unittest only."""
import json, os, pathlib, subprocess, sys, tempfile, threading, time, unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import fsh_ci as ci  # noqa: E402
import fsh_release as rel  # noqa: E402
import fsh_runtime as rt  # noqa: E402


class VersionTests(unittest.TestCase):
    def test_tuple_parsing(self):
        self.assertEqual(ci.version_tuple("9.0.321"), (9, 0, 321))
        self.assertEqual(ci.version_tuple("10.0.100-preview.1"), (10, 0, 100))
        self.assertLess(ci.version_tuple("8.0.400"), ci.version_tuple("9.0.200"))

    def test_host_rid_shape(self):
        os_, arch = ci.host_rid().split("-")
        self.assertIn(os_, {"win", "linux", "osx"})
        self.assertIn(arch, {"x64", "arm64"})
        self.assertIn(ci.host_rid(), ci.SIX_RIDS)


class HashTests(unittest.TestCase):
    def test_hash_is_deterministic_and_input_sensitive(self):
        a = ci.hash_inputs(["registry/pillars.json"], extra="x")
        self.assertEqual(a, ci.hash_inputs(["registry/pillars.json"], extra="x"))
        self.assertNotEqual(a, ci.hash_inputs(["registry/pillars.json"], extra="y"))

    def test_excluded_dirs_are_ignored(self):
        files = list(ci.iter_files(["**/*.fs"]))
        self.assertTrue(files)
        for f in files:
            self.assertFalse(set(f.relative_to(ci.ROOT).parts) & ci.EXCLUDE_DIRS)

    def test_generated_folders_are_not_inputs(self):
        # fable_out and dist are written by the very stages whose cache they would key
        self.assertIn("fable_out", ci.EXCLUDE_DIRS)
        self.assertIn("dist", ci.EXCLUDE_DIRS)


class LockTests(unittest.TestCase):
    def test_dead_pid_is_not_alive(self):
        self.assertTrue(ci.pid_alive(os.getpid()))
        self.assertFalse(ci.pid_alive(0))
        self.assertFalse(ci.pid_alive(2**22 + 12345))


class NullCache:
    def key(self, name, ctx): return None
    def hit(self, name, key): return False
    def store(self, name, key): pass
    def drop(self, name): pass


class HitCache(NullCache):
    """A cache in which the named stages are already satisfied."""
    def __init__(self, *hits): self.hits = set(hits)
    def key(self, name, ctx): return "k" if name in self.hits else None
    def hit(self, name, key): return bool(key)


class SchedulerTests(unittest.TestCase):
    def setUp(self):
        self.saved = dict(ci.STAGES)
        self.tmp = tempfile.TemporaryDirectory()
        self.ctx = ci.Ctx(run_dir=pathlib.Path(self.tmp.name), env={}, rid="linux-x64", require_aot=False, no_cache=True)

    def tearDown(self):
        ci.STAGES.clear(); ci.STAGES.update(self.saved); self.tmp.cleanup()

    def stages(self, **kw):
        ci.STAGES.clear()
        for name, (status, deps) in kw.items():
            ci.STAGES[name] = dict(fn=(lambda ctx, s=status: ci.Result(s, "x")), deps=deps, inputs=None, excl=False)

    def test_failure_blocks_dependents_but_not_independents(self):
        self.stages(a=(ci.PASS, []), b=(ci.FAIL, ["a"]), c=(ci.PASS, ["b"]), d=(ci.PASS, ["a"]))
        res = ci.execute(["a", "b", "c", "d"], self.ctx, NullCache(), 2, True, lambda n, r: None)
        self.assertEqual(res["b"].status, ci.FAIL)
        self.assertEqual(res["c"].status, ci.BLOCKED)
        self.assertEqual(res["d"].status, ci.PASS)

    def test_fail_fast_skips_remaining(self):
        self.stages(a=(ci.FAIL, []), b=(ci.PASS, []))
        res = ci.execute(["a", "b"], self.ctx, NullCache(), 1, False, lambda n, r: None)
        self.assertEqual(res["a"].status, ci.FAIL)
        self.assertIn(res["b"].status, (ci.SKIP, ci.PASS))  # b may already be running with jobs>1; with jobs=1 it is skipped

    def test_crashing_stage_becomes_failure(self):
        ci.STAGES.clear()
        def boom(ctx): raise RuntimeError("kaput")
        ci.STAGES["x"] = dict(fn=boom, deps=[], inputs=None, excl=False)
        res = ci.execute(["x"], self.ctx, NullCache(), 1, True, lambda n, r: None)
        self.assertEqual(res["x"].status, ci.FAIL)
        self.assertIn("kaput", res["x"].detail)

    def test_results_are_visible_to_later_stages(self):
        self.stages(a=(ci.PASS, []), b=(ci.PASS, ["a"]))
        ci.execute(["a", "b"], self.ctx, NullCache(), 1, True, lambda n, r: None)
        self.assertEqual(self.ctx.results["a"].status, ci.PASS)

    def test_start_callback_fires_for_stages_that_run_but_not_for_cached_ones(self):
        self.stages(a=(ci.PASS, []), b=(ci.PASS, ["a"]))
        self.ctx.no_cache = False
        started = []
        res = ci.execute(["a", "b"], self.ctx, HitCache("a"), 1, True, lambda n, r: None, started.append)
        self.assertEqual(res["a"].status, ci.CACHED)
        self.assertEqual(started, ["b"])

    def test_build_and_restore_are_skipped_when_every_consumer_is_cached(self):
        ran = []
        ci.STAGES.clear()
        for name, deps, extra in (("restore", [], {"lazy": True}), ("build", ["restore"], {"lazy": True}), ("test", ["build"], {}), ("aot", ["build"], {})):
            ci.STAGES[name] = dict(fn=(lambda ctx, n=name: (ran.append(n), ci.Result(ci.PASS, "x"))[1]), deps=deps, inputs=None, excl=False, **extra)
        self.ctx.no_cache = False
        res = ci.execute(["restore", "build", "test", "aot"], self.ctx, HitCache("test", "aot"), 2, True, lambda n, r: None)
        self.assertEqual(ran, [])
        self.assertEqual({n: r.status for n, r in res.items()}, {"restore": ci.SKIP, "build": ci.SKIP, "test": ci.CACHED, "aot": ci.CACHED})

    def test_build_runs_when_any_consumer_needs_it(self):
        ran = []
        ci.STAGES.clear()
        for name, deps, extra in (("restore", [], {"lazy": True}), ("build", ["restore"], {"lazy": True}), ("test", ["build"], {}), ("aot", ["build"], {})):
            ci.STAGES[name] = dict(fn=(lambda ctx, n=name: (ran.append(n), ci.Result(ci.PASS, "x"))[1]), deps=deps, inputs=None, excl=False, **extra)
        self.ctx.no_cache = False
        res = ci.execute(["restore", "build", "test", "aot"], self.ctx, HitCache("test"), 1, True, lambda n, r: None)
        self.assertEqual(sorted(ran), ["aot", "build", "restore"])
        self.assertEqual(res["test"].status, ci.CACHED)

    def test_nothing_is_skipped_when_the_cache_is_off(self):
        ci.STAGES.clear()
        ci.STAGES["build"] = dict(fn=lambda ctx: ci.Result(ci.PASS, "x"), deps=[], inputs=None, excl=False, lazy=True)
        ci.STAGES["test"] = dict(fn=lambda ctx: ci.Result(ci.PASS, "x"), deps=["build"], inputs=None, excl=False)
        res = ci.execute(["build", "test"], self.ctx, HitCache("test"), 1, True, lambda n, r: None)  # ctx.no_cache is True
        self.assertEqual(res["build"].status, ci.PASS)


class LazyPlanTests(unittest.TestCase):
    def test_the_real_graph_skips_build_for_a_docs_only_change(self):
        names = ci.PROFILES["full"]
        hits = {"docs-drift", "selftest", "format", "test", "aot"}
        skipped = ci.plan_lazy(names, hits)
        self.assertEqual(set(skipped), {"restore", "build"})

    def test_a_changed_test_input_brings_the_build_back(self):
        skipped = ci.plan_lazy(ci.PROFILES["full"], {"docs-drift", "selftest", "format", "aot"})
        self.assertEqual(skipped, {})

    def test_pack_always_needs_the_build(self):
        skipped = ci.plan_lazy(ci.PROFILES["release"], {"test", "aot"})
        self.assertNotIn("build", skipped)

    def test_a_stage_asked_for_by_name_with_no_consumers_is_never_skipped(self):
        self.assertEqual(ci.plan_lazy(["preflight", "restore", "build"], set()), {})


class ProfileTests(unittest.TestCase):
    def test_profiles_reference_known_stages_and_start_with_preflight(self):
        for name, stages in ci.PROFILES.items():
            self.assertEqual(stages[0], "preflight", name)
            for s in stages:
                self.assertIn(s, ci.STAGES)

    def test_stage_deps_exist_and_are_acyclic(self):
        def visit(n, seen):
            self.assertNotIn(n, seen)
            for d in ci.STAGES[n]["deps"]:
                self.assertIn(d, ci.STAGES)
                visit(d, seen | {n})
        for n in ci.STAGES:
            visit(n, set())

    def test_every_profile_stage_has_its_deps_in_the_profile(self):
        for name, stages in ci.PROFILES.items():
            for s in stages:
                for d in ci.STAGES[s]["deps"]:
                    self.assertIn(d, stages, f"{name}: {s} needs {d}")

    def test_full_is_what_the_github_matrix_runs_and_all_adds_only_the_javascript_stages(self):
        self.assertEqual(ci.PROFILES["full"], ["preflight", "docs-drift", "selftest", "tools-restore", "format", "restore", "build",
                                              "test", "aot", "rust", "benchmarks"])
        self.assertEqual(ci.PROFILES["all"][:len(ci.PROFILES["full"])], ci.PROFILES["full"])
        self.assertEqual(set(ci.PROFILES["all"]) - set(ci.PROFILES["full"]), {"web", "gateway", "hub", "site", "web-e2e"})
        workflow = (ci.ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
        self.assertIn("--profile full", workflow)

    def test_release_ends_with_the_bundle_and_only_the_release_profile_has_it(self):
        self.assertEqual(ci.PROFILES["release"][-2:], ["pack", "bundle"])
        for name in ("fast", "full", "all"):
            self.assertNotIn("bundle", ci.PROFILES[name])

    def test_only_cached_stages_declare_inputs(self):
        for name, spec in ci.STAGES.items():
            if name in ci.CACHEABLE:
                self.assertIsNotNone(spec["inputs"], name)

    def test_the_javascript_stages_match_the_folders_the_workflows_build(self):
        wf = ci.ROOT / ".github" / "workflows"
        text = "\n".join(p.read_text(encoding="utf-8") for p in wf.glob("*.yml"))
        for folder in ("gateway", "ide/web", "ide/e2e", "worker"):
            self.assertIn(f"working-directory: {folder}", text)
        self.assertIn("gateway/**", ci.STAGES["gateway"]["inputs"])
        self.assertIn("worker/**", ci.STAGES["hub"]["inputs"])
        self.assertIn("ide/web/**", ci.STAGES["web"]["inputs"])


class SelectStagesTests(unittest.TestCase):
    def test_only_brings_its_dependencies_in_graph_order(self):
        names = ci.select_stages("full", only=["test"])
        self.assertEqual(names, ["preflight", "restore", "build", "test"])

    def test_skip_never_drops_preflight(self):
        names = ci.select_stages("fast", skip=["preflight", "format"])
        self.assertIn("preflight", names)
        self.assertNotIn("format", names)

    def test_unknown_stage_is_rejected(self):
        with self.assertRaises(ValueError):
            ci.select_stages("full", only=["nope"])


class CacheTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.saved = ci.STATE
        ci.STATE = pathlib.Path(self.tmp.name)

    def tearDown(self):
        ci.STATE = self.saved
        self.tmp.cleanup()

    def ctx(self, **kw):
        return ci.Ctx(run_dir=ci.STATE, env={"PATH": ""}, rid="linux-x64", require_aot=False, no_cache=False, sdk_version="9.0.321", **kw)

    def test_key_changes_with_the_runner_and_with_flags_that_change_what_a_stage_checks(self):
        c = ci.Cache()
        base = c.key("test", self.ctx())
        self.assertEqual(base, ci.Cache().key("test", self.ctx()))
        other = ci.Cache()
        other.runner = "different"
        self.assertNotEqual(base, other.key("test", self.ctx()))
        self.assertNotEqual(base, c.key("test", self.ctx(require_rust=True)))

    def test_stages_without_inputs_or_not_cacheable_have_no_key(self):
        c = ci.Cache()
        self.assertIsNone(c.key("build", self.ctx()))
        self.assertIsNone(c.key("preflight", self.ctx()))

    def test_concurrent_stores_from_many_threads_all_land_and_the_file_is_valid_json(self):
        c = ci.Cache()
        threads = [threading.Thread(target=lambda i=i: c.store(f"stage{i}", f"key{i}")) for i in range(40)]
        for t in threads: t.start()
        for t in threads: t.join()
        again = ci.Cache()
        for i in range(40):
            self.assertTrue(again.hit(f"stage{i}", f"key{i}"))
        json.loads((ci.STATE / "cache.json").read_text(encoding="utf-8"))
        self.assertEqual(list(ci.STATE.glob("*.tmp")), [])

    def test_a_dropped_stage_stays_dropped(self):
        c = ci.Cache()
        c.store("test", "k")
        c.drop("test")
        self.assertFalse(ci.Cache().hit("test", "k"))

    def test_a_corrupt_cache_file_is_treated_as_empty(self):
        (ci.STATE / "cache.json").write_text("{ not json", encoding="utf-8")
        self.assertFalse(ci.Cache().hit("test", "k"))


class RustStageTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def ctx(self, require_rust):
        # an empty PATH means cargo cannot be found, whatever is installed on this machine
        return ci.Ctx(run_dir=pathlib.Path(self.tmp.name), env={"PATH": ""}, rid="linux-x64", require_aot=False,
                      no_cache=True, require_rust=require_rust)

    def test_missing_cargo_skips_by_default(self):
        self.assertEqual(ci.stage_rust(self.ctx(False)).status, ci.SKIP)

    def test_missing_cargo_fails_when_required(self):
        self.assertEqual(ci.stage_rust(self.ctx(True)).status, ci.FAIL)

    def test_preflight_warns_or_fails_without_cargo(self):
        self.assertEqual(ci.check_rust_toolchain(self.ctx(False))[0], ci.WARN)
        self.assertEqual(ci.check_rust_toolchain(self.ctx(True))[0], ci.FAIL)

    def test_the_reference_crate_is_an_input_of_the_test_stage(self):
        self.assertTrue(any(p.startswith("native/rust") for p in ci.CODE))
        self.assertTrue((ci.ROOT / ci.RUST_CRATE).is_file())

    def test_rust_is_in_the_full_profile_but_not_the_fast_one(self):
        self.assertIn("rust", ci.PROFILES["full"])
        self.assertNotIn("rust", ci.PROFILES["fast"])


class NodeStageTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def ctx(self, require_node):
        return ci.Ctx(run_dir=pathlib.Path(self.tmp.name), env={"PATH": ""}, rid="linux-x64", require_aot=False, no_cache=True, require_node=require_node)

    def test_without_node_the_javascript_stages_skip_or_fail_when_required(self):
        for stage in (ci.stage_web, ci.stage_gateway, ci.stage_hub):
            self.assertEqual(stage(self.ctx(False)).status, ci.SKIP)
            self.assertEqual(stage(self.ctx(True)).status, ci.FAIL)

    def test_preflight_node_check_follows_the_same_rule(self):
        self.assertEqual(ci.check_node(self.ctx(False))[0], ci.WARN)
        self.assertEqual(ci.check_node(self.ctx(True))[0], ci.FAIL)


class WebE2eStageTests(unittest.TestCase):
    """The browser tests (ADR-0024): skip with a warning without Chromium, fail with --require-browser, never cache a skip."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.tmp.name)
        (self.root / "site" / "public" / "ide").mkdir(parents=True)
        (self.root / "site" / "public" / "ide" / "index.html").write_text("<!doctype html>", encoding="utf-8")

    def tearDown(self):
        self.tmp.cleanup()

    def ctx(self, **kw):
        return ci.Ctx(run_dir=self.root, env={"PATH": ""}, rid="linux-x64", require_aot=False, no_cache=True, **kw)

    def run_stage(self, ctx, outputs):
        """outputs: what run_cmd answers, in order: (exit code, text)."""
        calls = []

        def fake_run(c, stage, cmd, **kw):
            calls.append(cmd)
            return outputs[len(calls) - 1]

        patches = (mock.patch.object(ci, "ROOT", self.root), mock.patch.object(ci, "node_ready", return_value=None),
                   mock.patch.object(ci, "npm_install", return_value=None), mock.patch.object(ci, "run_cmd", side_effect=fake_run))
        with patches[0], patches[1], patches[2], patches[3]:
            return ci.stage_web_e2e(ctx), calls

    def test_it_is_part_of_all_after_the_site_it_serves_and_not_part_of_full(self):
        self.assertIn("web-e2e", ci.PROFILES["all"])
        self.assertNotIn("web-e2e", ci.PROFILES["full"])
        self.assertLess(ci.PROFILES["all"].index("site"), ci.PROFILES["all"].index("web-e2e"))
        self.assertEqual(ci.STAGES["web-e2e"]["deps"], ["site"])
        self.assertIn("web-e2e", ci.CACHEABLE)
        for glob in ("ide/e2e/**", "site/**", "ide/web/**"):
            self.assertIn(glob, ci.STAGES["web-e2e"]["inputs"])

    def test_without_node_it_skips_or_fails_when_node_is_required(self):
        self.assertEqual(ci.stage_web_e2e(self.ctx()).status, ci.SKIP)
        self.assertEqual(ci.stage_web_e2e(self.ctx(require_node=True)).status, ci.FAIL)

    def test_without_chromium_it_skips_with_a_warning_and_fails_when_required(self):
        r, calls = self.run_stage(self.ctx(), [(3, "not installed")])
        self.assertEqual(r.status, ci.SKIP)  # a SKIP is never stored in the cache, a WARN would be
        self.assertIn("npx playwright install chromium", r.detail)
        self.assertEqual(len(calls), 1)
        r, _ = self.run_stage(self.ctx(require_browser=True), [(3, "not installed")])
        self.assertEqual(r.status, ci.FAIL)
        self.assertIn("--require-browser", r.detail)

    def test_a_missing_page_is_a_failure_that_names_the_site_build(self):
        (self.root / "site" / "public" / "ide" / "index.html").unlink()
        r, _ = self.run_stage(self.ctx(), [])
        self.assertEqual(r.status, ci.FAIL)
        self.assertIn("build.py", r.detail)

    def test_it_counts_passed_tests_and_a_green_run_with_none_is_a_failure(self):
        r, calls = self.run_stage(self.ctx(), [(0, "chromium: x"), (0, "Running 18 tests" + chr(10) + "  18 passed (11.7s)" + chr(10))])
        self.assertEqual((r.status, r.metrics["browser_tests"]), (ci.PASS, 18))
        self.assertEqual(calls[1], ["npm", "test"])
        r, _ = self.run_stage(self.ctx(), [(0, "chromium: x"), (0, "No tests found")])
        self.assertEqual(r.status, ci.FAIL)

    def test_a_failing_browser_test_fails_the_stage_with_the_output(self):
        r, _ = self.run_stage(self.ctx(), [(0, "chromium: x"), (1, "1 failed" + chr(10) + "  1 passed")])
        self.assertEqual(r.status, ci.FAIL)
        self.assertIn("1 failed", r.detail)


class ResilientRunTests(unittest.TestCase):
    """run_resilient retries only what the machine broke."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.ctx = ci.Ctx(run_dir=pathlib.Path(self.tmp.name), env={}, rid="linux-x64", require_aot=False, no_cache=True)
        self.calls = []

    def tearDown(self):
        self.tmp.cleanup()

    def script(self, *answers):
        queue = list(answers)

        def fake(ctx, stage, cmd, timeout=900, cwd=None, append=False, env=None):
            self.calls.append(cmd)
            if cmd[:2] == ["dotnet", "build-server"]:
                return 0, ""
            return queue.pop(0)

        return mock.patch.object(ci, "run_cmd", fake), mock.patch.object(ci.time, "sleep", lambda s: None)

    def test_a_real_compile_error_is_never_retried(self):
        p1, p2 = self.script((1, "src/A.fs(3,1): error FS0039: The value 'x' is not defined"))
        with p1, p2:
            rc, out, reasons = ci.run_resilient(self.ctx, "build", ["dotnet", "build"], 10)
        self.assertEqual((rc, reasons), (1, []))
        self.assertEqual(len(self.calls), 1)

    def test_an_out_of_memory_failure_is_retried_once_with_one_msbuild_node(self):
        p1, p2 = self.script((1, "MSBUILD : error MSB4166: Child node \"2\" exited prematurely. System.OutOfMemoryException"), (0, "ok"))
        with p1, p2:
            rc, out, reasons = ci.run_resilient(self.ctx, "build", ["dotnet", "build", "-m:8"], 10, retune=ci.eased)
        self.assertEqual(rc, 0)
        self.assertEqual(reasons, ["out of memory"])
        self.assertEqual(self.calls[0], ["dotnet", "build", "-m:8"])
        self.assertEqual(self.calls[1], ["dotnet", "build-server", "shutdown"])
        self.assertEqual(self.calls[2], ["dotnet", "build", "-m:1"])

    def test_it_gives_up_after_the_configured_retries(self):
        crash = (1, "MSB4017: The build stopped unexpectedly")
        p1, p2 = self.script(crash, crash, crash)
        with p1, p2:
            rc, out, reasons = ci.run_resilient(self.ctx, "build", ["dotnet", "build"], 10)
        self.assertEqual(rc, 1)
        self.assertEqual(len(reasons), 1)  # one retry by default
        self.assertEqual(len([c for c in self.calls if c[:2] == ["dotnet", "build"]]), 2)

    def test_a_pass_after_a_retry_is_a_warning_not_a_silent_pass(self):
        r = ci.recovered(ci.Result(ci.PASS, "12 passed"), ["out of memory"])
        self.assertEqual(r.status, ci.WARN)
        self.assertIn("infrastructure retry", r.detail)
        self.assertEqual(ci.recovered(ci.Result(ci.PASS, "x"), []).status, ci.PASS)

    def test_a_timeout_is_not_retried(self):
        p1, p2 = self.script((124, "[fsh-ci] TIMEOUT after 10s\nOutOfMemoryException"))
        with p1, p2:
            rc, out, reasons = ci.run_resilient(self.ctx, "build", ["dotnet", "build"], 10)
        self.assertEqual((rc, reasons), (124, []))

    def test_the_test_stage_names_the_failed_tests_and_the_hang(self):
        out = ("  Failed Parser handles tabs [3 ms]\n  Failed Index survives restart [2 ms]\n"
               "Blame: Hang timeout of 300000ms was exceeded in test run.\nFailed!  - Failed:     2, Passed:    40, Skipped:     0, Total:    42\n")
        with mock.patch.object(ci, "run_resilient", lambda *a, **k: (1, out, [])):
            r = ci.stage_test(self.ctx)
        self.assertEqual(r.status, ci.FAIL)
        self.assertIn("Parser handles tabs", r.detail)
        self.assertIn("Hang timeout", r.detail)
        self.assertEqual(r.metrics["failed"], 2)

    def test_green_with_no_tests_is_still_a_failure(self):
        with mock.patch.object(ci, "run_resilient", lambda *a, **k: (0, "Build succeeded.\n", [])):
            self.assertEqual(ci.stage_test(self.ctx).status, ci.FAIL)

    def test_the_test_command_carries_the_hang_options_and_the_governor_node_count(self):
        seen = []
        self.ctx.msbuild_nodes = 3
        self.ctx.hang_timeout_s = 77

        def fake(ctx, stage, cmd, timeout, retune=None):
            seen.append(cmd)
            return 0, "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5", []

        with mock.patch.object(ci, "run_resilient", fake):
            r = ci.stage_test(self.ctx)
        self.assertEqual(r.status, ci.PASS)
        self.assertIn("--blame-hang", seen[0])
        self.assertIn("77s", seen[0])
        self.assertIn("-m:3", seen[0])


class RuntimeTests(unittest.TestCase):
    def test_governor_table(self):
        def plan(avail, commit, jobs=4, cpus=12, ignore=False):
            return rt.plan_resources({"total_mb": 32000, "avail_mb": avail, "commit_free_mb": commit}, cpus, jobs, ignore)

        roomy = plan(20000, 30000)
        self.assertEqual((roomy.level, roomy.jobs, roomy.nodes), ("ok", 4, 8))
        # the smaller of RAM and commit decides: plenty of RAM but no commit headroom is a tight machine
        commit_bound = plan(17000, 2400)
        self.assertEqual((commit_bound.level, commit_bound.jobs, commit_bound.nodes), ("tight", 1, 1))
        mid = plan(3500, 3500)
        self.assertEqual((mid.level, mid.jobs, mid.nodes), ("tight", 1, 2))
        low = plan(900, 5000)
        self.assertEqual((low.level, low.jobs, low.nodes), ("critical", 1, 1))
        forced = plan(900, 5000, ignore=True)
        self.assertEqual((forced.level, forced.nodes), ("tight", 1))
        self.assertEqual(plan(8000, 8000, jobs=1, cpus=2).nodes, 2)
        self.assertEqual(rt.plan_resources(None, 4, 3).level, "unknown")
        self.assertEqual(rt.plan_resources(None, 4, 3).jobs, 3)

    def test_wait_for_headroom_waits_then_gives_up_and_never_blocks_on_an_unreadable_machine(self):
        readings = iter([{"avail_mb": 500, "commit_free_mb": 500}, {"avail_mb": 800, "commit_free_mb": 800}, {"avail_mb": 5000, "commit_free_mb": 5000}])
        now = [0.0]
        ok, waited, free = rt.wait_for_headroom(1200, 60, 5, snap_fn=lambda: next(readings), sleep=lambda s: now.__setitem__(0, now[0] + s), clock=lambda: now[0])
        self.assertTrue(ok)
        self.assertEqual((waited, free), (10.0, 5000))
        ok, waited, _ = rt.wait_for_headroom(1200, 20, 5, snap_fn=lambda: {"avail_mb": 100, "commit_free_mb": 100}, sleep=lambda s: now.__setitem__(0, now[0] + s), clock=lambda: now[0])
        self.assertFalse(ok)
        ok, _, _ = rt.wait_for_headroom(1200, 20, 5, snap_fn=lambda: None)
        self.assertTrue(ok)

    def test_memory_snapshot_is_sane_on_this_machine(self):
        snap = rt.memory_snapshot()
        if snap is not None:
            self.assertGreater(snap["total_mb"], 100)
            self.assertGreaterEqual(snap["avail_mb"], 0)

    def test_process_listing_parsers_and_grouping(self):
        win = '"llama-server.exe","101","Console","1","6,291,456 K"\n"llama-server.exe","102","Console","1","6,291,456 K"\n"claude.exe","7","Console","1","100,000 K"\n'
        rows = rt.parse_processes(win, True)
        self.assertEqual(rows[0], ("llama-server.exe", 6144))
        top = rt.top_consumers(raw=rows)
        self.assertEqual(top[0][:2], ("llama-server.exe", 2))
        self.assertIn("llama-server.exe x2 12.0 GB", rt.describe_consumers(top))
        posix = "  6291456 llama-server\n   204800 /usr/bin/node\n"
        self.assertEqual(rt.parse_processes(posix, False), [("llama-server", 6144), ("node", 200)])

    def test_infra_failures_are_told_apart_from_real_ones(self):
        self.assertEqual(rt.infra_failure(1, "System.OutOfMemoryException: Exception of type"), "out of memory")
        self.assertEqual(rt.infra_failure(1, 'MSBUILD : error MSB4166: Child node "3" exited prematurely.'), "MSBuild node crashed")
        self.assertEqual(rt.infra_failure(1, "error MSB6006: \"csc.dll\" exited with code -1073741819."), "build tool crashed")
        self.assertEqual(rt.infra_failure(1, "The process cannot access the file because it is being used by another process"), "file locked by another process")
        self.assertIn("exit code", rt.infra_failure(-1073741819, "") or "")
        self.assertIn("exit code", rt.infra_failure(3221225477, "") or "")
        self.assertIsNone(rt.infra_failure(0, "OutOfMemoryException"))
        self.assertIsNone(rt.infra_failure(1, ""))
        self.assertIsNone(rt.infra_failure(124, "OutOfMemoryException"))
        self.assertIsNone(rt.infra_failure(1, "a.fs(1,1): error FS0001: type mismatch\nMSB4166"))
        self.assertIsNone(rt.infra_failure(1, "  Failed Some test [4 ms]\nSystem.OutOfMemoryException"))
        self.assertIsNone(rt.infra_failure(1, "Failed!  - Failed:     3, Passed:     9, Skipped:     0, Total:    12"))

    def test_disk_full_is_named_but_not_a_retry(self):
        self.assertEqual(rt.resource_hint("error: No space left on device"), "the disk is full")
        self.assertIsNone(rt.resource_hint("fine"))
        self.assertIsNone(rt.infra_failure(1, "No space left on device"))

    def test_test_output_parsing(self):
        out = ("Passed!  - Failed:     0, Passed:    27, Skipped:     1, Total:    28, Duration: 4 s - A.dll\n"
               "Failed!  - Failed:     2, Passed:    10, Skipped:     0, Total:    12 - B.dll\n"
               "  Failed Alpha works [12 ms]\n  Failed Beta works [3 ms]\n  Failed Alpha works [1 ms]\n"
               "The active test run was aborted. Reason: Test host process crashed\n")
        t = rt.parse_test_output(out)
        self.assertEqual((t["passed"], t["failed"], t["skipped"]), (37, 2, 1))
        self.assertEqual(t["failed_names"], ["Alpha works", "Beta works"])
        self.assertTrue(any("aborted" in b for b in t["blame"]))

    def test_colour_codes_are_stripped_so_counts_can_be_read(self):
        coloured = "\x1b[2m      Tests \x1b[22m \x1b[1m\x1b[32m28 passed\x1b[39m\x1b[90m (28)\x1b[39m"
        self.assertEqual(rt.strip_ansi(coloured), "      Tests  28 passed (28)")
        self.assertRegex(rt.strip_ansi(coloured), r"Tests\s+(\d+) passed")

    def test_with_nodes_replaces_any_earlier_setting(self):
        self.assertEqual(rt.with_nodes(["dotnet", "build", "-m:8"], 1), ["dotnet", "build", "-m:1"])
        self.assertEqual(rt.with_nodes(["dotnet", "build", "-maxcpucount:4", "-m"], 2), ["dotnet", "build", "-m:2"])
        self.assertEqual(rt.with_nodes(["dotnet", "build"], 0), ["dotnet", "build"])

    def test_glob_regex_agrees_with_pathlib_for_the_patterns_the_pipeline_uses(self):
        patterns = ["src/**/*.fsproj", "tools/ci/*.py", "**/*.md", "Directory.Build.props", "native/rust/**/Cargo.toml", "tests/**/*.fs"]
        for pat in patterns:
            expected = {p.relative_to(ci.ROOT).as_posix() for p in ci.ROOT.glob(pat) if p.is_file() and not (set(p.relative_to(ci.ROOT).parts) & ci.EXCLUDE_DIRS)}
            got = set(ci.INDEX.select([pat]))
            self.assertEqual(got, expected, pat)

    def test_a_trailing_double_star_matches_every_file_below(self):
        r = rt.glob_regex("ide/web/**")
        self.assertTrue(r.match("ide/web/src/a.fs"))
        self.assertTrue(r.match("ide/web/package.json"))
        self.assertFalse(r.match("ide/webx/a.fs"))
        self.assertFalse(rt.glob_regex("*.md").match("docs/a.md"))

    def test_file_index_hashes_change_with_content_and_ignore_excluded_folders(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            (root / "src").mkdir()
            (root / "node_modules").mkdir()
            (root / "src" / "a.fs").write_text("one", encoding="utf-8")
            (root / "node_modules" / "b.fs").write_text("noise", encoding="utf-8")
            idx = rt.FileIndex(root, {"node_modules"})
            self.assertEqual(idx.select(["**/*.fs"]), ["src/a.fs"])
            h1 = idx.hash(["**/*.fs"])
            self.assertEqual(h1, idx.hash(["**/*.fs"]))
            time.sleep(0.02)
            (root / "src" / "a.fs").write_text("two!", encoding="utf-8")
            self.assertNotEqual(h1, idx.hash(["**/*.fs"]))
            (root / "src" / "c.fs").write_text("new", encoding="utf-8")
            idx.refresh()
            self.assertEqual(idx.select(["**/*.fs"]), ["src/a.fs", "src/c.fs"])

    def test_with_the_same_files_two_indexes_give_the_same_hash(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            (root / "x.md").write_text("hello", encoding="utf-8")
            self.assertEqual(rt.FileIndex(root, set()).hash(["*.md"], "e"), rt.FileIndex(root, set()).hash(["*.md"], "e"))

    def test_watch_loop_runs_once_then_after_each_settled_change_and_stops_when_idle(self):
        states = iter(["a", "a", "b", "b", "b", "b", "b", "b", "b", "b", "b", "b"])
        seen = []
        runs = rt.watch_loop(lambda: next(states), lambda: seen.append(1) or 0, poll=0, settle=0, sleep=lambda s: None, idle_polls=2, say=lambda m: None)
        self.assertEqual(runs, 2)

    def test_watch_waits_for_a_burst_of_edits_to_finish(self):
        # a, then the files keep changing for two looks (b, c), then settle on d
        states = iter(["a", "b", "c", "d", "d", "d", "d", "d", "d"])
        seen = []
        runs = rt.watch_loop(lambda: next(states), lambda: seen.append(1) or 0, poll=0, settle=0, sleep=lambda s: None, idle_polls=1, say=lambda m: None)
        self.assertEqual(runs, 2)


class ProcessTreeTests(unittest.TestCase):
    def test_a_timeout_kills_the_children_too(self):
        with tempfile.TemporaryDirectory() as d:
            pidfile = pathlib.Path(d) / "child.pid"
            code = ("import subprocess,sys,time\n"
                    f"p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)'])\n"
                    f"open(r'{pidfile}','w').write(str(p.pid))\n"
                    "time.sleep(60)\n")
            with open(pathlib.Path(d) / "out.log", "wb") as out:
                t0 = time.time()
                rc, timed_out = rt.run_tree([sys.executable, "-c", code], d, dict(os.environ), out, 3)
            self.assertTrue(timed_out)
            self.assertEqual(rc, 124)
            self.assertLess(time.time() - t0, 30)
            child = int(pidfile.read_text())
            for _ in range(50):
                if not ci.pid_alive(child):
                    break
                time.sleep(0.1)
            self.assertFalse(ci.pid_alive(child), "the grandchild survived the timeout")

    def test_exit_code_and_output_are_returned(self):
        with tempfile.TemporaryDirectory() as d:
            log = pathlib.Path(d) / "o.log"
            with open(log, "wb") as out:
                rc, timed_out = rt.run_tree([sys.executable, "-c", "print('hi'); raise SystemExit(7)"], d, dict(os.environ), out, 30)
            self.assertEqual((rc, timed_out), (7, False))
            self.assertIn("hi", log.read_text())

    def test_run_cmd_returns_only_the_output_of_its_own_command_when_appending(self):
        with tempfile.TemporaryDirectory() as d:
            ctx = ci.Ctx(run_dir=pathlib.Path(d), env=dict(os.environ), rid="linux-x64", require_aot=False, no_cache=True)
            ci.run_cmd(ctx, "s", [sys.executable, "-c", "print('first')"])
            rc, out = ci.run_cmd(ctx, "s", [sys.executable, "-c", "print('second')"], append=True)
            self.assertIn("second", out)
            self.assertNotIn("first", out)
            self.assertIn("first", ctx.log("s").read_text())

    def test_a_missing_command_is_a_clear_127(self):
        with tempfile.TemporaryDirectory() as d:
            ctx = ci.Ctx(run_dir=pathlib.Path(d), env=dict(os.environ), rid="linux-x64", require_aot=False, no_cache=True)
            rc, out = ci.run_cmd(ctx, "s", ["definitely-not-a-real-program-xyz"])
            self.assertEqual(rc, 127)
            self.assertIn("not found", out)


class ReleaseTests(unittest.TestCase):
    def make(self, root: pathlib.Path):
        (root / "packages").mkdir(parents=True)
        (root / "packages" / "A.1.0.0.nupkg").write_bytes(b"pkg")
        (root / "linux-x64").mkdir()
        (root / "linux-x64" / "fsh").write_bytes(b"bin")
        (root / rel.NOTES).write_text("notes", encoding="utf-8")
        files = {r: (root / r).stat().st_size for r in rel.bundle_files(root)}
        prov = rel.build_provenance(commit="a" * 40, branch="main", dirty=False, sdk="9.0.321", rid="linux-x64", python="3.11", profile="release",
                                    stages={"test": {"status": "pass"}}, rids_verified=["linux-x64"], files=files)
        (root / rel.PROVENANCE).write_text(json.dumps(prov), encoding="utf-8")
        rel.write_checksums(root)

    def test_an_untouched_bundle_verifies_and_says_it_is_not_published(self):
        with tempfile.TemporaryDirectory() as d:
            self.make(pathlib.Path(d))
            self.assertEqual(rel.verify_bundle(pathlib.Path(d)), [])
            self.assertIs(json.loads((pathlib.Path(d) / rel.PROVENANCE).read_text())["published"], False)

    def test_a_changed_removed_or_added_file_is_caught(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            self.make(root)
            (root / "packages" / "A.1.0.0.nupkg").write_bytes(b"tampered")
            (root / "extra.txt").write_text("x", encoding="utf-8")
            (root / rel.NOTES).unlink()
            problems = "\n".join(rel.verify_bundle(root))
            self.assertIn("changed: packages/A.1.0.0.nupkg", problems)
            self.assertIn("not in SHA256SUMS: extra.txt", problems)
            self.assertIn(f"missing: {rel.NOTES}", problems)

    def test_missing_manifest_and_wrong_commit(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            self.make(root)
            self.assertTrue(any("built from" in p for p in rel.verify_bundle(root, "b" * 40)))
            self.assertEqual(rel.verify_bundle(root, "a" * 40), [])
            (root / rel.SUMS).unlink()
            self.assertEqual(rel.verify_bundle(root), ["SHA256SUMS is missing"])

    def test_provenance_claiming_to_be_published_is_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            self.make(root)
            prov = json.loads((root / rel.PROVENANCE).read_text())
            prov["published"] = True
            (root / rel.PROVENANCE).write_text(json.dumps(prov), encoding="utf-8")
            rel.write_checksums(root)
            self.assertTrue(any("published=false" in p for p in rel.verify_bundle(root)))

    def test_changelog_section(self):
        text = "# Changelog\n\n## [Unreleased]\n- one\n- two\n\n## [0.1.0]\n- old\n"
        self.assertEqual(rel.changelog_section(text), "- one\n- two")
        self.assertEqual(rel.changelog_section(text, "0.1.0"), "- old")
        self.assertEqual(rel.changelog_section("# x\n", "Unreleased"), "")

    def test_there_is_no_publish_command(self):
        # AGENTS.md: never publish a package or model artifact; a human approves and uploads a bundle
        src = (pathlib.Path(__file__).parent / "fsh_ci.py").read_text(encoding="utf-8")
        self.assertNotIn("nuget push", src)
        self.assertNotIn("dotnet nuget push", src)
        self.assertNotIn("sub.add_parser(\"publish\")", src)

    def test_bundle_stage_refuses_a_dirty_tree_and_a_missing_aot_binary(self):
        with tempfile.TemporaryDirectory() as d:
            ctx = ci.Ctx(run_dir=pathlib.Path(d), env={}, rid="linux-x64", require_aot=False, no_cache=True)
            with mock.patch.object(ci, "git_lines", lambda *a: [" M src/A.fs"] if "status" in a else ["a" * 40]):
                r = ci.stage_bundle(ctx)
            self.assertEqual(r.status, ci.FAIL)
            self.assertIn("uncommitted", r.detail)


class ReportingTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.saved = ci.STATE
        ci.STATE = pathlib.Path(self.tmp.name)
        self.run_dir = ci.STATE / "runs" / "20260101-000000"
        self.run_dir.mkdir(parents=True)

    def tearDown(self):
        ci.STATE = self.saved
        self.tmp.cleanup()

    def ctx(self):
        return ci.Ctx(run_dir=self.run_dir, env={}, rid="linux-x64", require_aot=False, no_cache=True, sdk_version="9.0.321", profile="full", trigger="hook")

    def test_report_and_status_round_trip(self):
        results = {"preflight": ci.Result(ci.PASS, "11 checks"), "build": ci.Result(ci.PASS, "0 warning(s)", duration=12.0),
                   "test": ci.Result(ci.WARN, "40 passed (passed after an infrastructure retry: out of memory)", metrics={"infra_retries": 1}, duration=30.0)}
        with mock.patch.object(ci, "git_lines", lambda *a: ["abc1234"] if "rev-parse" in a else []):
            summary = ci.write_reports(self.ctx(), "full", results, list(results), 50.0)
        self.assertTrue(summary["ok"])
        self.assertEqual(summary["infra_retries"], 1)
        self.assertEqual(summary["trigger"], "hook")
        self.assertEqual(summary["rids_verified_locally"], ["linux-x64"])
        self.assertIn("infrastructure retries: 1", (self.run_dir / "report.md").read_text())
        self.assertEqual(ci.last_summary()["commit"], "abc1234")
        args = mock.Mock(json=False)
        with mock.patch("builtins.print"):
            self.assertEqual(ci.cmd_status(args), 0)

    def test_a_failed_run_makes_status_exit_nonzero_and_no_run_is_exit_2(self):
        args = mock.Mock(json=False)
        with mock.patch("builtins.print"):
            self.assertEqual(ci.cmd_status(args), 2)
        results = {"preflight": ci.Result(ci.PASS, "ok"), "build": ci.Result(ci.FAIL, "boom")}
        with mock.patch.object(ci, "git_lines", lambda *a: []):
            ci.write_reports(self.ctx(), "full", results, list(results), 5.0)
        with mock.patch("builtins.print"):
            self.assertEqual(ci.cmd_status(args), 1)

    def test_verified_rids_need_a_passing_test_stage(self):
        results = {"build": ci.Result(ci.PASS, "ok")}
        with mock.patch.object(ci, "git_lines", lambda *a: []):
            summary = ci.write_reports(self.ctx(), "full", results, ["build"], 1.0)
        self.assertEqual(summary["rids_verified_locally"], [])


class HookTests(unittest.TestCase):
    def test_hooks_call_the_pipeline_with_the_hook_flag(self):
        for hook, profile in (("pre-commit", "fast"), ("pre-push", "full")):
            text = (ci.ROOT / "tools" / "ci" / "hooks" / hook).read_text(encoding="utf-8")
            self.assertIn(f"--profile {profile}", text)
            self.assertIn("--hook", text)
            self.assertNotIn("\r", text)  # a CRLF in a shell script breaks it on Linux and macOS

    def test_check_memory_fails_only_below_the_critical_minimum_unless_ignored(self):
        ctx = ci.Ctx(run_dir=pathlib.Path("."), env={}, rid="linux-x64", require_aot=False, no_cache=True)
        low = {"total_mb": 16000, "avail_mb": 900, "commit_free_mb": 900}
        with mock.patch.object(rt, "memory_snapshot", lambda: low), mock.patch.object(rt, "top_consumers", lambda *a, **k: []):
            self.assertEqual(ci.check_memory(ctx)[0], ci.FAIL)
            ctx.ignore_resources = True
            self.assertEqual(ci.check_memory(ctx)[0], ci.WARN)
        roomy = {"total_mb": 32000, "avail_mb": 20000, "commit_free_mb": 30000}
        ctx.ignore_resources = False
        with mock.patch.object(rt, "memory_snapshot", lambda: roomy):
            self.assertEqual(ci.check_memory(ctx)[0], ci.PASS)
            self.assertEqual(ctx.plan.level, "ok")


if __name__ == "__main__":
    unittest.main()
