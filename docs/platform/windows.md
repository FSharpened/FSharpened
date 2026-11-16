# Windows

- Enable long paths (`LongPathsEnabled`) and keep the repo path short.
- File watching: `ReadDirectoryChangesW` can overflow under load and silently drop events. `IFileWatcher` must emit `Overflow` and fall back to polling; a silent loss is a "hot reload stopped working" bug.
- IPC: Unix domain sockets by default (Windows 10+); named pipes where ACLs matter.
- Sandboxing: Job Objects.
- GPU: DirectML covers AMD, Intel, NVIDIA through ONNX Runtime. ROCm on Windows needs the HIP SDK; WSL2 + ROCm is the stable alternative. Windows containers are a separate, ADR-gated track.
- Editors: Visual Studio (Windows only), Rider, VS Code.
- Line endings: LF in repo (`.gitattributes`), native on checkout.
