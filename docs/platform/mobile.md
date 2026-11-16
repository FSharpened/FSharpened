# Mobile (Tier 3: Android, iOS)

Not an active target, but **must not be architecturally precluded** (Invariant 4). Constraints: NativeAOT-only on iOS, no JIT, MAUI/Fabulous.Avalonia packaging, ONNX Runtime with QNN/NNAPI/CoreML providers.

Gating criteria for promotion to active: accepted ADR; the AOT-only lane (`tests/aot-mobile-sim/`) green for a full release; symbolic fallback layer verified under AOT-only; ONNX inference smoke on simulator. Until then, authoring and training are desktop/CI concerns; serving artifacts are validated for the mobile constraint.
