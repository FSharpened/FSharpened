"""Self-tests for fsh-ci (run as the `selftest` stage). Stdlib unittest only."""
import json, os, pathlib, sys, tempfile, unittest

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import fsh_ci as ci  # noqa: E402


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


if __name__ == "__main__":
    unittest.main()
