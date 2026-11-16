# Local CI/CD Pipeline (`fsh-ci`)

A custom, on-device, cross-platform pipeline in `tools/ci/fsh_ci.py` (Python stdlib only; Windows, Linux, macOS). It is **the** pipeline: GitHub Actions and git hooks call the same program, so local and remote results cannot diverge (ADR-0016). Skill: `.agents/skills/run-local-ci.md`.

## Use
```bash
python tools/ci/fsh_ci.py run                  # full profile (default)
./ci.sh run --profile fast                     # Linux/macOS/Git Bash wrapper
./ci.ps1 run --profile release                 # PowerShell wrapper
python tools/ci/fsh_ci.py run --only test      # a stage plus its dependencies
python tools/ci/fsh_ci.py preflight            # health checks only
python tools/ci/fsh_ci.py install-hooks        # pre-commit = fast, pre-push = full
```

## Profiles
| Profile | Stages | Typical time (win-x64) |
|---|---|---|
| `fast` | preflight, docs-drift, selftest, tools-restore, format | ~3 s |
| `full` | fast + restore, build (strict), test, aot, benchmarks | ~50 s cold, ~30 s warm |
| `release` | full + pack (requires NativeAOT) | ~70 s |

## Preflight (nothing else runs if it fails)
Python >= 3.10; git state; `dotnet` found and SDK >= the `global.json` pin (the runner adds the dotnet and Windows `vswhere` directories to `PATH` itself); >= 2 GB free disk; `registry/pillars.json` valid; **secret scan** (private keys, GitHub/AWS/Slack tokens); merge-conflict markers; files over 5 MB; NativeAOT toolchain (MSVC C++ tools / clang / Xcode CLT; a missing toolchain skips the AOT stage with a warning, or fails with `--require-aot`); nuget.org reachability (warning only).

## Stages
| Stage | What it proves |
|---|---|
| `docs-drift` | registry <-> projects/docs/skills, routing, links, invariants, ADR index |
| `selftest` | the runner's own unit tests |
| `format` | `dotnet fantomas --check .` |
| `restore` | `dotnet restore` with 3 attempts and exponential backoff |
| `build` | Release build with `CI=true` (warnings are errors) |
| `test` | `dotnet test` with trx results; **zero tests executed counts as failure** |
| `aot` | `PublishAot` for the host RID, then **runs the binary** and asserts it reports `aot: True`. This catches run-time AOT failures that a successful publish hides (the first run found `printf` crashing under NativeAOT; ADR-0014) |
| `benchmarks` | skipped until `pipelines/benchmarks/` has a harness |
| `pack` | NuGet packages for every project (release) |

## Engine features
- **Dependency-ordered parallel scheduler**; stages that touch the dotnet CLI are serialised by an exclusive lock, independent stages run concurrently (`--jobs`).
- **Content-addressed cache**: a stage is skipped when a SHA-256 of its inputs, the SDK version, the RID and the stage name is unchanged since its last pass. Build is never cached (later stages consume its outputs). `--no-cache` and `--ci` disable it.
- **Fail-fast** by default; `--keep-going` runs independent stages anyway. Dependents of a failed stage are reported `blocked`, never silently skipped.
- **Crash safety**: a stage that raises is a failed stage; per-stage timeouts; a single-run lock with stale-PID detection.
- **Artifacts** in `.fsharpened/ci/runs/<timestamp>/`: per-stage logs, `summary.json`, `report.md`, trx results, the AOT binary. The last 15 runs are kept.
- **History and regression warning**: `.fsharpened/ci/history.jsonl`; a passing stage more than 1.5x its recent median is flagged. `python tools/ci/fsh_ci.py history` lists recent runs.
- **Honest platform coverage**: every report states which of the six RIDs this run verified (the host) and which are only covered by the GitHub matrix. A local pass never claims cross-platform parity.

## Hooks
`install-hooks` sets `core.hooksPath` to `tools/ci/hooks` (version-controlled): `pre-commit` runs `fast`, `pre-push` runs `full`. Bypass once with `--no-verify`; CI still gates the merge.

## GitHub Actions
`.github/workflows/ci.yml` runs `fsh_ci.py run --profile full --ci --require-aot` on `win-x64`, `win-arm64` (experimental), `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, uploads each run's logs and writes `report.md` to the job summary.

## Extending
Add a function returning `Result`, register it in `STAGES` (deps, cache inputs, exclusivity), add it to the profiles, add a self-test (`tools/ci/test_fsh_ci.py` checks every profile stage has its dependencies and the graph is acyclic), update this page.
