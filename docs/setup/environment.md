# Environment Setup

Canonical setup for every platform. Commands are copy-paste runnable.

## 1. Prerequisites (all platforms)
- Git, Python 3.10+ (for `tools/scripts/*.py`)
- .NET SDK **9.0.200 or newer** (see `global.json`; `.slnx` requires it). Projects target net8.0, so the net8.0 runtime is pulled automatically.

## 2. Install the SDK
**Windows**
```powershell
winget install Microsoft.DotNet.SDK.9
```
**macOS (Intel and Apple Silicon)**
```bash
brew install --cask dotnet-sdk
```
**Linux (Ubuntu/Debian)**
```bash
sudo apt-get update && sudo apt-get install -y dotnet-sdk-9.0
```
**Linux (other) / WSL**: use `dotnet-install.sh` from Microsoft, or your distro package. In WSL keep the repo on the Linux filesystem (`~/src`), not `/mnt/c`; file-watch performance is far worse across the boundary (`docs/platform/wsl.md`).

## 3. Clone and verify
```bash
git clone <repo-url> fsharpened && cd fsharpened
dotnet --version
python tools/scripts/check_docs.py
dotnet build FSharpened.slnx
dotnet test FSharpened.slnx
```
Expected on a fresh checkout: docs check passes; build and tests succeed (first run is the first real compile; see `HANDOVER.md`).

## 4. Editor
Any of: VS Code + Ionide, JetBrains Rider (all platforms), Visual Studio (Windows). SharpLsp is the cross-editor option. See `docs/platform/editors.md`.

## 5. Optional tooling
`dotnet tool restore` (Fantomas); Docker for the Linux container channel.
