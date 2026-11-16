# ADR-0016: One local-first CI pipeline, shared by hooks and GitHub Actions

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner, Tooling Builder persona

## Context
Agents and humans iterate faster and more safely when the full gate runs on their own machine, and CI that differs from local checks produces "works locally, fails in CI". The first real build also showed gates catch things reviews miss: FSharp.Core vanishing under central package management, a runtime/target mismatch, a `printf` crash that only appears in a NativeAOT binary, and pack failures.

## Decision
The pipeline is a single stdlib-only Python program, `tools/ci/fsh_ci.py`, with profiles (fast/full/release), a preflight gate, a dependency-ordered scheduler, a content-addressed cache, retries, logs, reports and history. Git hooks and GitHub Actions call it; workflows only install toolchains. The AOT stage must execute the published binary, not just publish it. Reports must state which RIDs were verified.

## Consequences
Local and remote results are comparable; the runner is itself tested (`tools/ci/test_fsh_ci.py`) and must be changed with its docs. Python 3.10+ becomes a required dev tool (already used by the scaffold and docs-drift scripts). A local run verifies only the host RID; the six-RID matrix remains the cross-platform authority.

## Alternatives considered
FAKE / Fun.Build (F#-native but needs a restored build before it can gate anything, and cannot run preflight on a broken toolchain); Makefile/shell scripts (not portable to Windows); GitHub-only workflows (no local parity, slow feedback).
