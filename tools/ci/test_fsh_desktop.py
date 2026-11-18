"""Self-tests for the desktop packaging side of fsh-ci: the archive, the native-crate discovery, the self-test report
check, the provenance record and the files under packaging/ and .github/workflows. Stdlib unittest only."""
import contextlib, gzip, hashlib, io, json, os, pathlib, sys, tarfile, tempfile, time, unittest, zipfile
import xml.etree.ElementTree as ET
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import fsh_ci as ci  # noqa: E402
import fsh_desktop as desk  # noqa: E402
import fsh_release as rel  # noqa: E402
import realservers as rs  # noqa: E402

ROOT = ci.ROOT


def make_tree(root: pathlib.Path, mtime: float) -> None:
    (root / "languages").mkdir(parents=True)
    (root / "languages" / "b.json").write_text("{}", encoding="utf-8")
    (root / "languages" / "a.json").write_text("{}", encoding="utf-8")
    (root / "App").write_bytes(b"\x7fELF-binary")
    (root / "App").chmod(0o755)
    (root / "z.txt").write_text("last", encoding="utf-8")
    for p in root.rglob("*"):
        os.utime(p, (mtime, mtime))


class NamingTests(unittest.TestCase):
    def test_archive_names_and_extensions(self):
        self.assertEqual(desk.archive_name("0.0.1-skeleton", "win-x64"), "fsharpened-desktop-0.0.1-skeleton-win-x64.zip")
        self.assertEqual(desk.archive_name("1.2.3", "linux-arm64"), "fsharpened-desktop-1.2.3-linux-arm64.tar.gz")
        self.assertEqual(desk.archive_name("1.2.3", "osx-arm64"), "fsharpened-desktop-1.2.3-osx-arm64.tar.gz")

    def test_executable_and_library_names(self):
        self.assertEqual(desk.exe_name("win-arm64"), "FSharpened.Ide.Desktop.exe")
        self.assertEqual(desk.exe_name("linux-x64"), "FSharpened.Ide.Desktop")
        self.assertEqual(desk.native_library_file("win-x64", "fsh-watch"), "fsh_watch.dll")
        self.assertEqual(desk.native_library_file("linux-x64", "fsh-watch"), "libfsh_watch.so")
        self.assertEqual(desk.native_library_file("osx-arm64", "fsh_pty"), "libfsh_pty.dylib")

    def test_version_comes_from_directory_build_props(self):
        self.assertEqual(desk.read_version("<Project><PropertyGroup><Version>0.0.1-skeleton</Version></PropertyGroup></Project>"), "0.0.1-skeleton")
        self.assertEqual(desk.read_version("<Project/>"), "0.0.0")
        self.assertRegex(desk.read_version((ROOT / "Directory.Build.props").read_text(encoding="utf-8")), r"^\d")

    def test_commit_epoch_never_goes_below_1980(self):
        self.assertEqual(desk.commit_epoch(0), desk.ZIP_EPOCH_MIN)
        self.assertEqual(desk.commit_epoch(1_800_000_000), 1_800_000_000)


class NativeCrateTests(unittest.TestCase):
    def crate(self, base: pathlib.Path, name: str, body: str):
        d = base / "native" / "rust" / name
        d.mkdir(parents=True)
        (d / "Cargo.toml").write_text(f'[package]\nname = "{name}"\nversion = "0.1.0"\n{body}', encoding="utf-8")

    def test_only_cdylib_crates_that_are_not_excluded_are_shipped_in_name_order(self):
        with tempfile.TemporaryDirectory() as d:
            base = pathlib.Path(d)
            self.crate(base, "fsh-watch", '[lib]\ncrate-type = ["cdylib"]\n')
            self.crate(base, "fsh-pty", '[lib]\nname = "fsh_pty_native"\ncrate-type = ["cdylib", "rlib"]\n')
            self.crate(base, "fsh-example", '[lib]\ncrate-type = ["cdylib"]\n')
            self.crate(base, "fsh-tool", "[[bin]]\nname = \"x\"\n")
            found = desk.native_crates(base, ["fsh-example"])
            self.assertEqual([c.name for c in found], ["fsh-pty", "fsh-watch"])
            self.assertEqual([c.stem for c in found], ["fsh_pty_native", "fsh-watch"])

    def test_the_repository_config_excludes_only_the_reference_crate(self):
        config = desk.load_config(ROOT)
        self.assertEqual(config["nativeExclude"], ["fsh-interop-example"])
        for c in desk.native_crates(ROOT, config["nativeExclude"]):
            self.assertNotEqual(c.name, "fsh-interop-example")


class ArchiveTests(unittest.TestCase):
    def build(self, kind: str, mtime: float, epoch: int = 1_800_000_000) -> bytes:
        with tempfile.TemporaryDirectory() as d:
            src = pathlib.Path(d) / "src"
            make_tree(src, mtime)
            dest = pathlib.Path(d) / f"out.{kind}"
            desk.write_archive(src, dest, "top", epoch)
            return dest.read_bytes()

    def test_two_builds_with_different_file_times_are_byte_identical(self):
        for kind in ("zip", "tar.gz"):
            a = self.build(kind, 1_000_000_000)
            time.sleep(0.01)
            b = self.build(kind, 1_700_000_000)
            self.assertEqual(hashlib.sha256(a).hexdigest(), hashlib.sha256(b).hexdigest(), kind)

    def test_a_different_commit_time_changes_the_archive(self):
        self.assertNotEqual(self.build("zip", 1, 1_800_000_000), self.build("zip", 1, 1_800_000_100))

    def test_entries_are_sorted_with_fixed_owner_and_time(self):
        with tempfile.TemporaryDirectory() as d:
            src = pathlib.Path(d) / "src"
            make_tree(src, 1_000_000_000)
            tgz = pathlib.Path(d) / "a.tar.gz"
            desk.write_archive(src, tgz, "top", 1_800_000_000)
            self.assertEqual(desk.archive_members(tgz), ["top/App", "top/languages/a.json", "top/languages/b.json", "top/z.txt"])
            with tarfile.open(tgz) as t:
                for m in t.getmembers():
                    self.assertEqual((m.uid, m.gid, m.uname, m.gname, m.mtime), (0, 0, "", "", 1_800_000_000))
            self.assertEqual(tgz.read_bytes()[4:8], b"\0\0\0\0", "the gzip header carries no time")
            z = pathlib.Path(d) / "a.zip"
            desk.write_archive(src, z, "top", 1_800_000_000)
            with zipfile.ZipFile(z) as zf:
                self.assertEqual(zf.namelist(), ["top/App", "top/languages/a.json", "top/languages/b.json", "top/z.txt"])
                self.assertEqual(len({i.date_time for i in zf.infolist()}), 1)
                self.assertIsNone(zf.testzip())

    @unittest.skipIf(os.name == "nt", "the executable bit exists only on POSIX file systems")
    def test_the_executable_bit_survives_in_the_tarball(self):
        with tempfile.TemporaryDirectory() as d:
            src = pathlib.Path(d) / "src"
            make_tree(src, 1_000_000_000)
            tgz = pathlib.Path(d) / "a.tar.gz"
            desk.write_archive(src, tgz, "top", 1_800_000_000)
            with tarfile.open(tgz) as t:
                self.assertEqual(t.getmember("top/App").mode, 0o755)
                self.assertEqual(t.getmember("top/z.txt").mode, 0o644)

    def test_an_empty_folder_or_unknown_type_is_refused(self):
        with tempfile.TemporaryDirectory() as d:
            empty = pathlib.Path(d) / "e"
            empty.mkdir()
            with self.assertRaises(ValueError):
                desk.write_archive(empty, pathlib.Path(d) / "x.zip", "top", 1_800_000_000)
            make_tree(pathlib.Path(d) / "s", 1)
            with self.assertRaises(ValueError):
                desk.write_archive(pathlib.Path(d) / "s", pathlib.Path(d) / "x.rar", "top", 1_800_000_000)


class SelftestReportTests(unittest.TestCase):
    good = {"ok": True, "rid": "win-x64", "packs": 37, "packProblems": 0, "grammars": 7, "workspaceEntries": 1, "nativeLibraries": 4, "nativeFailed": 0}

    def test_a_whole_package_has_no_problems(self):
        self.assertEqual(desk.check_selftest(self.good, "win-x64", 37, 7), [])

    def test_each_kind_of_damage_is_named(self):
        for change, text in (({"ok": False, "failures": "x"}, "did not report ok"), ({"packs": 3}, "language packs"), ({"packProblems": 2}, "failed to load"),
                             ({"grammars": 0}, "grammars"), ({"workspaceEntries": 0}, "temporary folder"), ({"nativeFailed": 1, "nativeErrors": "a.dll"}, "native library"),
                             ({"rid": "linux-x64"}, "expected win-x64")):
            problems = "\n".join(desk.check_selftest({**self.good, **change}, "win-x64", 37, 7))
            self.assertIn(text, problems)

    def test_the_report_is_the_last_json_line(self):
        out = "log line\n" + json.dumps(self.good) + "\n"
        self.assertEqual(desk.parse_selftest(out), self.good)
        self.assertIsNone(desk.parse_selftest("no json here"))
        self.assertIsNone(desk.parse_selftest("{broken}"))


class ProvenanceTests(unittest.TestCase):
    def bundle(self, root: pathlib.Path, **change):
        (root / "desktop").mkdir(parents=True)
        archive = root / "desktop" / "fsharpened-desktop-1.0.0-linux-x64.tar.gz"
        archive.write_bytes(b"archive")
        meta = {"archive": archive.name, "archive_bytes": 7, "archive_sha256": rel.sha256_file(archive), "rid": "linux-x64", "version": "1.0.0",
                "selftest": "passed", "window_smoke": "skipped (no display)", "native_libraries": [], "trimmed": False, "native_aot": False,
                "path": f"desktop/{archive.name}", **change}
        files = {r: (root / r).stat().st_size for r in rel.bundle_files(root)}
        prov = rel.build_provenance(commit="a" * 40, branch="main", dirty=False, sdk="9.0.321", rid="linux-x64", python="3.11", profile="release",
                                    stages={}, rids_verified=["linux-x64"], files=files, desktop=meta)
        (root / rel.PROVENANCE).write_text(json.dumps(prov), encoding="utf-8")
        rel.write_checksums(root)
        return prov

    def test_the_desktop_record_says_unsigned_and_verifies(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            prov = self.bundle(root)
            self.assertEqual((prov["desktop"]["signed"], prov["desktop"]["notarised"], prov["desktop"]["installer"]), (False, False, False))
            self.assertEqual(rel.verify_bundle(root), [])

    def test_a_wrong_hash_or_a_missing_selftest_is_caught(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            self.bundle(root, archive_sha256="0" * 64, selftest="skipped")
            problems = "\n".join(rel.verify_bundle(root))
            self.assertIn("different SHA-256", problems)
            self.assertIn("self-test", problems)

    def test_the_bundle_stage_needs_the_desktop_archive(self):
        with tempfile.TemporaryDirectory() as d:
            run = pathlib.Path(d)
            (run / "aot").mkdir()
            (run / "aot" / "fsh").write_bytes(b"x")
            ctx = ci.Ctx(run_dir=run, env={}, rid="linux-x64", require_aot=False, no_cache=True)
            ctx.results["desktop-package"] = ci.Result(ci.FAIL, "boom")
            nupkg = ROOT / "artifacts" / "nupkg"
            with mock.patch.object(ci, "git_lines", lambda *a: [] if "status" in a else ["a" * 40]), \
                    mock.patch.object(ci.shutil, "copy2", lambda *a, **k: None), mock.patch.object(ci.shutil, "rmtree", lambda *a, **k: None), \
                    mock.patch.object(pathlib.Path, "mkdir", lambda *a, **k: None), mock.patch.object(pathlib.Path, "glob", lambda self, pat: [nupkg / "A.nupkg"] if self == nupkg else []):
                r = ci.stage_bundle(ctx)
            self.assertEqual(r.status, ci.FAIL)
            self.assertIn("desktop archive is missing", r.detail)


class StageWiringTests(unittest.TestCase):
    def test_desktop_package_is_release_only_and_after_build_and_test(self):
        self.assertIn("desktop-package", ci.PROFILES["release"])
        self.assertLess(ci.PROFILES["release"].index("test"), ci.PROFILES["release"].index("desktop-package"))
        self.assertLess(ci.PROFILES["release"].index("desktop-package"), ci.PROFILES["release"].index("bundle"))
        self.assertEqual(ci.STAGES["desktop-package"]["deps"], ["build", "test"])
        self.assertIn("desktop-package", ci.STAGES["bundle"]["deps"])
        self.assertNotIn("desktop-package", ci.CACHEABLE)

    def test_the_stage_fails_loudly_when_the_publish_fails(self):
        with tempfile.TemporaryDirectory() as d:
            ctx = ci.Ctx(run_dir=pathlib.Path(d), env={}, rid="linux-x64", require_aot=False, no_cache=True)
            with mock.patch.object(ci, "run_cmd", lambda *a, **k: (1, "error NU1101: unable to find package")):
                r = ci.stage_desktop_package(ctx)
            self.assertEqual(r.status, ci.FAIL)
            self.assertIn("NU1101", r.detail)

    def test_the_desktop_project_ships_the_packs_and_the_selftest(self):
        proj = (ROOT / "tools" / "FSharpened.Ide.Desktop" / "FSharpened.Ide.Desktop.fsproj").read_text(encoding="utf-8")
        self.assertIn("languages", proj)
        self.assertIn("grammars", proj)
        self.assertIn('Compile Include="Selftest.fs"', proj)
        self.assertIn("--selftest", (ROOT / "tools" / "FSharpened.Ide.Desktop" / "Program.fs").read_text(encoding="utf-8"))


class PackagingInputTests(unittest.TestCase):
    def test_the_installer_inputs_are_well_formed_and_name_the_right_program(self):
        for rel_path in ("windows/FSharpened.wxs", "windows/AppxManifest.xml", "linux/fsharpened.metainfo.xml", "macos/Info.plist"):
            text = (ROOT / "packaging" / rel_path).read_text(encoding="utf-8")
            ET.fromstring(text.encode("utf-8"))
            self.assertIn("{{VERSION}}", text, rel_path)
        for rel_path in ("windows/FSharpened.wxs", "windows/AppxManifest.xml", "macos/Info.plist", "linux/fsharpened.desktop"):
            self.assertIn("FSharpened.Ide.Desktop", (ROOT / "packaging" / rel_path).read_text(encoding="utf-8"), rel_path)
        entry = (ROOT / "packaging" / "linux" / "fsharpened.desktop").read_text(encoding="utf-8")
        for key in ("Type=Application", "Name=", "Exec=", "Icon=", "Categories="):
            self.assertIn(key, entry)

    def test_nothing_in_packaging_holds_a_secret_or_signs_or_uploads(self):
        for p in (ROOT / "packaging").rglob("*"):
            if not p.is_file() or p.suffix == ".md":
                continue
            text = p.read_text(encoding="utf-8")
            for word in ("signtool sign", "notarytool submit", "gh release", "curl ", "twine", ".pfx", "BEGIN PRIVATE KEY"):
                self.assertNotIn(word, text, f"{p.name}: {word}")
        config = json.loads((ROOT / "packaging" / "desktop.json").read_text(encoding="utf-8"))
        self.assertEqual(config["format"], "fsharpened.desktop-package/1")

    def test_the_readme_names_every_human_gate(self):
        text = (ROOT / "packaging" / "README.md").read_text(encoding="utf-8").lower()
        for word in ("signing", "notaris", "never", "human"):
            self.assertIn(word, text)


class WorkflowTests(unittest.TestCase):
    def read(self, name: str) -> str:
        return (ROOT / ".github" / "workflows" / name).read_text(encoding="utf-8")

    def test_release_dry_run_is_manual_read_only_pinned_and_never_publishes(self):
        text = self.read("release-dry-run.yml")
        self.assertIn("workflow_dispatch:", text)
        for trigger in ("pull_request", "push:", "schedule:", "release:"):
            self.assertNotIn(trigger, text)
        self.assertIn("contents: read", text)
        self.assertNotRegex(text, r":\s*write", "no write permission")
        self.assertIn("--profile release", text)
        for rid in ci.SIX_RIDS:
            self.assertIn(f"rid: {rid}", text)
        for line in text.splitlines():
            if "uses:" in line:
                self.assertRegex(line, r"uses: [\w./-]+@[0-9a-f]{40}\b", line)
        for word in ("secrets.", "gh release", "nuget push", "twine", "docker push", "npm publish", "signtool", "notarytool"):
            self.assertNotIn(word, text, word)

    def test_realservers_workflow_is_manual_weekly_linux_read_only_and_pinned(self):
        text = self.read("realservers.yml")
        self.assertIn("workflow_dispatch:", text)
        self.assertIn("schedule:", text)
        self.assertIn("cron:", text)
        self.assertIn("contents: read", text)
        self.assertIn("ubuntu-latest", text)
        self.assertNotIn("windows", text)
        self.assertNotIn("macos", text)
        self.assertIn("--profile verify", text)
        self.assertNotIn("pull_request", text)
        for line in text.splitlines():
            if "uses:" in line:
                self.assertRegex(line, r"uses: [\w./-]+@[0-9a-f]{40}\b", line)
        for server in ("gopls", "rust-analyzer", "pyright", "typescript-language-server", "clangd", "lua-language-server"):
            self.assertIn(server, text)

class RealServersTests(unittest.TestCase):
    def test_the_verify_profile_is_opt_in_and_never_part_of_the_others(self):
        self.assertEqual(ci.PROFILES["verify"], ["preflight", "restore", "build", "realservers"])
        for name in ("fast", "full", "all", "release"):
            self.assertNotIn("realservers", ci.PROFILES[name], name)
        self.assertEqual(ci.STAGES["realservers"]["deps"], ["build"])
        self.assertNotIn("realservers", ci.CACHEABLE)
        for stages in ci.PROFILES.values():
            for s in stages:
                for d in ci.STAGES[s]["deps"]:
                    self.assertIn(d, stages)

    def test_every_server_has_a_fixture_an_install_hint_and_a_pack(self):
        names = [s.name for s in rs.SERVERS]
        self.assertEqual(len(names), len(set(names)))
        for want in ("gopls", "rust-analyzer", "pyright-langserver", "typescript-language-server", "clangd", "lua-language-server",
                     "fsautocomplete", "netcoredbg", "dlv", "lldb-dap", "debugpy"):
            self.assertIn(want, names)
        for s in rs.SERVERS:
            fixture = ROOT / "tests" / "fixtures" / "realservers" / s.fixture / "fixture.json"
            self.assertTrue(fixture.is_file(), s.name)
            data = json.loads(fixture.read_text(encoding="utf-8"))
            self.assertTrue(s.install, s.name)
            if s.kind == "lsp":
                self.assertEqual(data["server"], s.name)
                pack = json.loads((ROOT / "languages" / f"{data['pack']}.json").read_text(encoding="utf-8"))
                self.assertEqual(pack["server"]["command"], s.name, "the fixture starts the server the language pack names")
            else:
                self.assertEqual(data["debug"]["adapter"], s.name)

    def test_a_npm_cmd_shim_is_resolved_to_node_and_the_script(self):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            (root / "node_modules" / "pyright").mkdir(parents=True)
            (root / "node_modules" / "pyright" / "langserver.index.js").write_text("//", encoding="utf-8")
            shim = root / "pyright-langserver.cmd"
            shim.write_text('@ECHO off\r\nendLocal & goto #_undefined_# 2>NUL || & "%_prog%"  "%dp0%\\node_modules\\pyright\\langserver.index.js" %*\r\n', encoding="utf-8")
            with mock.patch.object(rs.shutil, "which", lambda name, path=None: "C:/node/node.exe"):
                cmd = rs.shim_command(shim)
            self.assertEqual(cmd[0], "C:/node/node.exe")
            self.assertTrue(cmd[1].endswith("langserver.index.js"))
            shim.write_text("@echo off\r\nsomething else\r\n", encoding="utf-8")
            with mock.patch.object(rs.shutil, "which", lambda name, path=None: "C:/node/node.exe"):
                self.assertIsNone(rs.shim_command(shim))

    def test_a_server_that_is_not_installed_is_skipped_never_passed(self):
        detected = [rs.Detected(rs.SERVERS[0], None), rs.Detected(rs.SERVERS[1], "/bin/rust-analyzer")]
        report = pathlib.Path(tempfile.mkdtemp())
        (report / "rust.json").write_text(json.dumps({"id": "rust", "checks": [{"name": "initialize", "ok": True, "detail": "x"}, {"name": "hover", "ok": False, "detail": "none"}]}), encoding="utf-8")
        rows, failed = rs.summarize(detected, report, 1, "")
        self.assertEqual([r["status"] for r in rows], ["skipped", "failed"])
        self.assertTrue(failed)
        text = rs.render(rows)
        self.assertIn("Install: " + rs.SERVERS[0].install, text)
        self.assertIn("FAIL hover", text)

    def test_an_installed_server_with_no_report_is_a_failure_not_a_pass(self):
        rows, failed = rs.summarize([rs.Detected(rs.SERVERS[0], "/bin/gopls")], pathlib.Path(tempfile.mkdtemp()), 0, "")
        self.assertEqual(rows[0]["status"], "failed")
        self.assertTrue(failed)

    def test_nothing_installed_is_a_clean_skip(self):
        with mock.patch.object(rs, "detect", lambda p: [rs.Detected(s, None) for s in rs.SERVERS]), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(rs.main(["--no-build"]), 0)

    def test_the_stage_reports_skip_when_nothing_was_exercised_and_fail_on_a_failure(self):
        for rows, want in (([{"server": "gopls", "status": "skipped"}], ci.SKIP), ([{"server": "gopls", "status": "passed"}], ci.PASS),
                           ([{"server": "gopls", "status": "failed"}], ci.FAIL)):
            with tempfile.TemporaryDirectory() as d:
                run = pathlib.Path(d)
                (run / "realservers.json").write_text(json.dumps({"servers": rows}), encoding="utf-8")
                ctx = ci.Ctx(run_dir=run, env={}, rid="win-x64", require_aot=False, no_cache=True)
                with mock.patch.object(ci, "run_cmd", lambda *a, **k: (0, "real servers: 0 of 1 installed\nSKIPPED gopls")):
                    self.assertEqual(ci.stage_realservers(ctx).status, want)

    def test_the_real_server_tests_are_skipped_unless_asked_for(self):
        src = (ROOT / "tests" / "FSharpened.Ide.Host.Tests" / "RealServers.fs").read_text(encoding="utf-8")
        self.assertIn('"FSH_REAL_SERVERS"', src)
        self.assertIn("this.Skip <-", src)
        self.assertNotIn("[<Fact>]", src, "every real-server test uses the skipping attribute")


if __name__ == "__main__":
    unittest.main()
