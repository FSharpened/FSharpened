# Local CI/CD Pipeline (`fsh-ci`)

A custom, on-device, cross-platform pipeline in `tools/ci/fsh_ci.py` (Python stdlib only; Windows, Linux, macOS), with the machine-facing parts in `tools/ci/fsh_runtime.py` and the release bundle in `tools/ci/fsh_release.py`. It is **the** pipeline: GitHub Actions and git hooks call the same program, so local and remote results cannot diverge (ADR-0016). Skill: `.agents/skills/run-local-ci.md`.

## Use
```bash
python tools/ci/fsh_ci.py run                  # full profile (default)
./ci.sh run --profile fast                     # Linux/macOS/Git Bash wrapper
./ci.ps1 run --profile release                 # PowerShell wrapper
python tools/ci/fsh_ci.py run --only test      # a stage plus its dependencies
python tools/ci/fsh_ci.py run --profile all    # everything GitHub runs across its workflows
python tools/ci/fsh_ci.py watch                # re-run `fast` on every change; only affected stages run
python tools/ci/fsh_ci.py doctor               # is this machine fit for a run? (--fix tidies what it safely can)
python tools/ci/fsh_ci.py status               # the last run, stage by stage (exit 0 pass, 1 fail, 2 none)
python tools/ci/fsh_ci.py verify               # check the newest release bundle against its checksums
python tools/ci/fsh_ci.py preflight            # health checks only
python tools/ci/fsh_ci.py install-hooks        # pre-commit = fast, pre-push = full
```

## Profiles
| Profile | Stages | Purpose |
|---|---|---|
| `fast` | preflight, docs-drift, selftest, tools-restore, format | the pre-commit gate, about 10 s |
| `full` | fast + restore, build (strict), test, aot, rust, benchmarks | exactly what `ci.yml` runs on each RID. Keep it identical to that workflow |
| `all` | full + web, web-e2e, gateway, hub, site | everything GitHub runs across `ci.yml`, `ide.yml`, `hub.yml` and `site.yml`; the JavaScript stages skip with a warning when Node is missing |
| `release` | full + pack, bundle (requires NativeAOT; cache off) | a verified `artifacts/release/<commit>-<rid>/` bundle. Publishing it is a human step |

## Preflight (nothing else runs if it fails)
Python >= 3.10; git state; `dotnet` found and SDK >= the `global.json` pin (the runner adds the dotnet and Windows `vswhere` directories to `PATH` itself); **memory and commit headroom** (below); >= 2 GB free disk; the state folder is writable; `.config/dotnet-tools.json` parses; `registry/pillars.json` valid; **secret scan** (private keys, GitHub/AWS/Slack tokens); merge-conflict markers; files over 5 MB; NativeAOT toolchain (MSVC C++ tools / clang / Xcode CLT; a missing toolchain skips the AOT stage with a warning, or fails with `--require-aot`); Rust toolchain; Node 20+ (warning, or failure with `--require-node`); nuget.org reachability (warning only).

## Stages
| Stage | What it proves |
|---|---|
| `docs-drift` | registry <-> projects/docs/skills, routing, links, invariants, ADR index |
| `selftest` | the runner's own unit tests |
| `format` | `dotnet fantomas --check .` |
| `restore` | `dotnet restore` with 3 attempts and exponential backoff |
| `build` | Release build with `CI=true` (warnings are errors) |
| `test` | `dotnet test` with trx results; **zero tests executed counts as failure**; a test that runs longer than `--hang-timeout` (300 s) is named and killed instead of eating the stage timeout |
| `aot` | `PublishAot` for the host RID, then **runs the binary** and asserts it reports `aot: True`. This catches run-time AOT failures that a successful publish hides (the first run found `printf` crashing under NativeAOT; ADR-0014) |
| `rust` | the reference Rust crate for `Interop.Rust`: `cargo test --locked`, then `cargo fmt --check` and `cargo clippy -D warnings` when those components are installed. Zero Rust tests executed counts as failure. Skipped when `cargo` is missing; with `--require-rust` a missing `cargo` fails and the Interop.Rust tests fail instead of passing without calling the library ([Rust](rust.md), ADR-0019) |
| `benchmarks` | skipped until `pipelines/benchmarks/` has a harness |
| `pack` | NuGet packages for every project (release) |
| `bundle` | copies this run's packages and native binary into `artifacts/release/<commit>-<rid>/`, writes `SHA256SUMS`, `provenance.json` (commit, branch, SDK, RID, per-stage results, `"published": false`) and `RELEASE_NOTES.md` (from CHANGELOG `[Unreleased]`), then verifies the folder. Refuses a tree with uncommitted tracked changes (`--allow-dirty` overrides) |
| `web` | the browser IDE: Fable compiles the F# core, esbuild bundles it, `node --test` checks it reads compiler output like .NET does, and the bundle is over 100 KB (`ide.yml`, job `web`) |
| `web-e2e` | the built IDE page in headless Chromium (`ide/e2e`, Playwright): files, palette, search, themes, share links, `.zip`, multi-file runs and the pipeline, storage, and axe accessibility checks in four themes, 87 tests. The gateway is mocked and every other request is blocked, so no test touches the network. Needs the `site` stage's output and `npx playwright install chromium`; skipped with a warning when the browser is missing (the skip is not cached), failing with `--require-browser`. Zero tests executed counts as failure (`ide.yml`, job `e2e`) |
| `gateway` | the IDE gateway worker: `tsc --noEmit` and vitest (`ide.yml`, job `gateway`) |
| `hub` | the hub worker: `tsc --noEmit` and vitest (`hub.yml`) |
| `site` | icons current, site built, links/metadata/manifests/service-worker precache/CSP-safe markup checked, service-worker behaviour tests (`site.yml`) |

## Robustness
- **Resource governor.** Windows enforces a *commit limit* (RAM plus page file) that is reached long before RAM runs out when other processes reserve memory; a build then fails with `MSB4166`, `OutOfMemoryException` or access violations instead of a clear error. Preflight reads the usable memory (the smaller of available RAM and commit headroom), and the run adapts: plenty of memory keeps `--jobs` and sets MSBuild to `-m:N` for the free memory; a tight machine (< 4 GB) runs one stage at a time with one or two MSBuild nodes and stops idle build servers; below 1.5 GB it fails preflight and names the largest consumers (`--ignore-resources` runs anyway). Each heavy stage also waits up to a minute for 1.2 GB before it starts. `doctor` shows all of this.
- **Infrastructure retry, never a real-failure retry.** When `build` or `test` fails with a signature of the machine failing (out of memory, a crashed MSBuild node, a locked file, a process killed by an access violation) and the output has no compiler error or failed test, the runner stops the build servers, waits and retries once with one MSBuild node. A pass after a retry is reported as `WARN`, never as a silent `PASS`. A compile error, a failed test, a timeout and a full disk are never retried.
- **Process-tree timeouts.** Every command runs in its own process group; a timeout, Ctrl+C or SIGTERM kills the whole tree (MSBuild nodes, test hosts, compiler servers), not only the direct child. The run exits 130 and releases its lock.
- **Hang and crash blame.** The test stage runs with `--blame-hang`; the failure names the failed tests and the hang or host-crash lines before the log tail.
- **Dependency-ordered parallel scheduler**; stages that touch the dotnet CLI are serialised by an exclusive lock, independent stages run concurrently (`--jobs`, lowered by the governor when memory is short).
- **Content-addressed cache**: a stage is skipped when a SHA-256 of its inputs, the SDK version, the RID, the runner's own code and the toolchain flags (`--require-rust`, cargo and node present) is unchanged since its last pass. The cache is written atomically from a lock, so a crash or parallel stages never leave a half file. Build is never cached. `--no-cache`, `--ci` and the `release` profile disable it.
- **Demand-driven restore and build.** `restore` and `build` are skipped when every stage that needs them (test, aot, pack) comes from the cache, so a docs-only change costs seconds instead of a full build.
- **Fast inputs.** One walk of the tree that skips `node_modules`, `bin`, `obj`, `dist`, `fable_out` and `artifacts`, and a per-file digest that is recomputed only when size or modification time changes.
- **Fail-fast** by default; `--keep-going` runs independent stages anyway. Dependents of a failed stage are reported `blocked`, never silently skipped.
- **Crash safety**: a stage that raises is a failed stage; per-stage timeouts; a single-run lock with stale-PID detection.
- **Artifacts** in `.fsharpened/ci/runs/<timestamp>/`: per-stage logs, `summary.json`, `report.md`, trx results, the AOT binary. The last 15 runs are kept.
- **History and regression warning**: `.fsharpened/ci/history.jsonl` (with what triggered each run); a passing stage more than 1.5x its recent median is flagged. `history` lists recent runs, `status` shows the last one in detail.
- **Honest platform coverage**: every report states which of the six RIDs this run verified (the host) and which are only covered by the GitHub matrix. A local pass never claims cross-platform parity.

## Automation
- **Hooks.** `install-hooks` sets `core.hooksPath` to `tools/ci/hooks` (version-controlled): `pre-commit` runs `fast`, `pre-push` runs `full`; a push that only deletes a remote branch runs nothing. Bypass once with `--no-verify`; CI still gates the merge.
- **Watch.** `watch` runs the profile (default `fast`) now and again after each settled change; stages whose inputs did not change come from the cache, so an edit re-runs only what it can affect. Ctrl+C stops it cleanly.
- **Unattended runs.** For a nightly full run, schedule `python tools/ci/fsh_ci.py run --profile all --keep-going --require-rust` yourself (Windows Task Scheduler, cron or launchd); `status` then reports it. The runner never installs a scheduler.
- **Hub telemetry.** With `FSH_HUB_URL` and `FSH_HUB_TOKEN` set, each run's summary is posted to the hub (best effort, never changes the exit code).

## Releases (local CD)
`run --profile release` builds from scratch, packs, runs the native binary, and writes the bundle described under `bundle`. `verify [DIR]` re-hashes every file against `SHA256SUMS`, fails on a changed, missing or extra file, checks the provenance lists the same files and says `published: false` (`--strict-head` also requires the bundle to be built from HEAD). **There is no publish command**: a person reviews the bundle and uploads it (AGENTS.md: never publish a package or model artifact without human approval). Only the host RID is built and verified locally; the bundle's provenance says so.

## GitHub Actions
`.github/workflows/ci.yml` runs `fsh_ci.py run --profile full --ci --require-aot --require-rust` on `win-x64`, `win-arm64` (experimental), `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, uploads each run's logs and writes `report.md` to the job summary. `ide.yml`, `hub.yml` and `site.yml` run the JavaScript checks that `--profile all` runs locally through the `web`, `web-e2e`, `gateway`, `hub` and `site` stages.

## Extending
Add a function returning `Result`, register it in `STAGES` (deps, cache inputs, exclusivity), add it to the profiles, add a self-test (`tools/ci/test_fsh_ci.py` checks every profile stage has its dependencies, the graph is acyclic and `full` still matches `ci.yml`), update this page. Pure logic that decides something (the governor, failure classification, lazy planning) belongs in `fsh_runtime.py` with a table test, not inside a stage.
