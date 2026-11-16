---
name: run-local-ci
description: Run, interpret and extend the local on-device CI/CD pipeline (fsh-ci), which is the same program GitHub Actions runs.
---

# run-local-ci

## Steps

1. Run `python tools/ci/fsh_ci.py run` (or `./ci.sh`, `./ci.ps1`). Use `--profile fast` while iterating; `--profile release` before tagging.
2. If preflight fails, fix the machine first (SDK pin, disk, toolchain, secrets, conflict markers); no other stage runs until it passes.
3. On a failed stage open its log in the printed run directory (`.fsharpened/ci/runs/<ts>/<stage>.log`); reproduce with `--only <stage> --no-cache`.
4. Never mark a stage green by skipping it or lowering a gate. A skipped AOT stage means the toolchain is missing, not that AOT passed.
5. Read the "Platform coverage" section of `report.md`; do not claim a RID verified unless the report lists it. The GitHub matrix covers the rest.
6. Before pushing: `full` must pass. Install the hooks once with `python tools/ci/fsh_ci.py install-hooks`.
7. When changing the runner itself: update `tools/ci/test_fsh_ci.py` and `docs/operations/local-ci.md` in the same PR.

## Done when

`full` passes locally, the self-tests pass, and the GitHub matrix is green (win-arm64 is experimental).
