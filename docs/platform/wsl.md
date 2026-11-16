# WSL (Tier 2)

WSL behaves as its Linux host, plus an interop boundary.
- Keep the repo on the Linux filesystem (`~/src`). Watching `/mnt/c` is slow and unreliable.
- Do not mix Windows `dotnet` and Linux `dotnet` on one working tree (obj/bin collisions).
- `dotnet fsh doctor --wsl` (planned) validates interop boundary, filesystem performance and `dotnet` path resolution.
- WSL2 + ROCm is AMD's recommended stable GPU path on Windows.
