# macOS

- Both `osx-arm64` and `osx-x64` are Tier 1 (a matrix entry, once `Platform` exists, is cheaper than a second-class path).
- FSEvents coalesces events and delivers with latency; `IFileWatcher` must debounce without losing changes.
- APFS is case-insensitive by default; `IPathNormalizer` owns path identity.
- Visual Studio for Mac is retired; use Rider or VS Code (Ionide / SharpLsp).
- GPU: CoreML (ONNX Runtime) for inference, MPS (TorchSharp) for training on Apple Silicon. Intel Mac GPU training is not supported by libtorch; use CPU/oneDAL.
- Distribution: signing and notarisation steps belong in the release skill.
