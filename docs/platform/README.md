# Platform Policy

**Invariant 1: no component is platform-privileged.** A feature that works on one Tier 1 platform and degrades on another is a bug, not a backlog item. Platform behaviour is isolated in `FSharpened.Platform` and declared in the capability manifest, never inferred at call sites. Changes to this policy need an ADR (ADR-0003).

| Tier | Platforms | RIDs | Requirement |
|---|---|---|---|
| 1 | Windows, Linux, macOS | `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` | Build, test, AOT-validate and ship on every CI run |
| 2 | WSL, Linux containers, Windows Server | inherit Tier 1 | Same behaviour as host; explicit interop tests |
| 3 | Android, iOS, future | `android-arm64`, `ios-arm64` | ADR-gated; **must not be architecturally precluded**; AOT-only lane |

Containers are a Linux distribution channel (multi-arch `linux/amd64`, `linux/arm64`), not a platform hierarchy. Windows containers are a separate ADR-gated track.

## What differs, and where it is handled
| Concern | Windows | Linux | macOS | Owner |
|---|---|---|---|---|
| File events | `ReadDirectoryChangesW` can overflow and drop events | `inotify` limited by `max_user_watches` | FSEvents coalesces with latency | `IFileWatcher` (debounce, overflow diagnostic, polling fallback) |
| IPC | Unix domain sockets (Win10+) default; named pipes for ACL cases | UDS | UDS | `ITransport` |
| Path identity | case-insensitive, 260-char legacy limit | case-sensitive | APFS case-insensitive by default | `IPathNormalizer` |
| Fork storage | NTFS no CoW | btrfs/XFS reflinks | APFS clonefile | `IOverlayStore` is in-memory; CoW is an optimisation only |
| Sandbox kill semantics | Job Objects | cgroups / process groups | process groups | `ISandbox` |
| Terminal | legacy consoles lack ANSI | ANSI | ANSI | `ITerminal` |

## Per-platform documents
`windows.md`, `linux.md`, `macos.md`, `wsl.md`, `containers.md`, `mobile.md`, `editors.md`, `performance.md` in this folder. Per-platform performance numbers are published in `performance.md`; nobody quotes a Linux benchmark as universal.

Skill: `.agents/skills/add-platform-abstraction.md`. A `#if` on a platform in product code fails review.
