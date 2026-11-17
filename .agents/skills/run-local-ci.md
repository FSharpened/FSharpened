---
name: run-local-ci
description: Run, interpret and extend the local on-device CI/CD pipeline (fsh-ci), which is the same program GitHub Actions runs.
---

# run-local-ci

## Steps

1. Run `python tools/ci/fsh_ci.py run` (or `./ci.sh`, `./ci.ps1`). Use `--profile fast` (or `watch`) while iterating; `--profile all` to cover the JavaScript, gateway, hub and site workflows too; `--profile release` before tagging.
2. If preflight fails, fix the machine first (SDK pin, disk, memory, toolchain, secrets, conflict markers); no other stage runs until it passes. `python tools/ci/fsh_ci.py doctor` explains the machine in one screen; a `memory` failure names the processes holding the most memory. Free them rather than passing `--ignore-resources`.
3. On a failed stage open its log in the printed run directory (`.fsharpened/ci/runs/<ts>/<stage>.log`) or run `status`; reproduce with `--only <stage> --no-cache`.
4. A `WARN` on build or test after an "infrastructure retry" means the machine, not the code, broke the first attempt and the second passed. Note it; if it recurs, run `doctor`.
5. Never mark a stage green by skipping it or lowering a gate. A skipped AOT stage means the toolchain is missing, not that AOT passed. A skipped restore/build means every stage that needs them came from the cache.
6. Read the "Platform coverage" section of `report.md`; do not claim a RID verified unless the report lists it. The GitHub matrix covers the rest.
7. Before pushing: `full` must pass. Install the hooks once with `python tools/ci/fsh_ci.py install-hooks`.
8. Releases: `run --profile release`, then `verify`. The bundle in `artifacts/release/` is for a human to review and publish; never publish it yourself.
9. When changing the runner itself: update `tools/ci/test_fsh_ci.py` and `docs/operations/local-ci.md` in the same PR. Keep the `full` profile identical to what `ci.yml` runs.

## Done when

`full` passes locally, the self-tests pass, and the GitHub matrix is green (win-arm64 is experimental).
