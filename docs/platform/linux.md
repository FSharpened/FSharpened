# Linux

- Install the SDK from the distro package or `dotnet-install.sh`; x64 and arm64 are both Tier 1.
- `inotify`: large repos hit `fs.inotify.max_user_watches`. The watcher must detect it, emit a structured diagnostic and fall back to polling. Raise the limit with `sysctl fs.inotify.max_user_watches=524288` for development.
- Sandboxing: cgroups / process groups via `ISandbox`.
- GPU: CUDA, ROCm/MIGraphX (AMD), OpenVINO/oneDNN (Intel) through ONNX Runtime; TorchSharp ROCm packages are planned community packages.
- Editors: Ionide, Rider, VS Code + SharpLsp; FsAutoComplete installs as a `dotnet tool` for headless/agent use.
- Containers: multi-arch OCI images in `pipelines/containers/` are a distribution channel only.
