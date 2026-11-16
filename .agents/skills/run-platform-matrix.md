---
name: run-platform-matrix
description: Run the full six-RID matrix: build, test, analyzer gate, per-RID NativeAOT publish and ONNX smoke test.
---

# run-platform-matrix

Run the full six-RID matrix: build, test, analyzer gate, per-RID NativeAOT publish and ONNX smoke test.

## Steps

1. Locally run `dotnet fsh doctor` to record the host RID and available backends.
2. Push a branch; the `ci.yml` matrix covers win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64.
3. For a RID you cannot reach, record it as unverified in the PR; do not claim parity.
4. AOT is validated per RID, never inferred from another RID.

## Done when

CI is green on all six RIDs, docs-drift passes, and the Reviewer persona has signed off.
