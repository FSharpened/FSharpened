namespace FSharpened.Platform

open System
open System.Collections.Generic
open System.Runtime.InteropServices

type Os =
    | Windows
    | Linux
    | MacOs
    | Android
    | Ios
    | UnknownOs

type Arch =
    | X64
    | Arm64
    | OtherArch of string

/// Everything models, telemetry and the analyzer harness may know about the host.
/// Published once at startup as the capability manifest (Invariant 1).
type RuntimeIdentity =
    { Rid: string
      Os: Os
      Arch: Arch
      IsAot: bool
      IsContainer: bool
      IsWsl: bool }

module RuntimeIdentity =
    let private detectOs () =
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            Windows
        elif RuntimeInformation.IsOSPlatform OSPlatform.OSX then
            MacOs
        elif RuntimeInformation.IsOSPlatform OSPlatform.Linux then
            Linux
        else
            UnknownOs

    let private detectArch () =
        match RuntimeInformation.ProcessArchitecture with
        | Architecture.X64 -> X64
        | Architecture.Arm64 -> Arm64
        | other -> OtherArch(string other)

    let private ridOf os arch =
        let o =
            match os with
            | Windows -> "win"
            | Linux -> "linux"
            | MacOs -> "osx"
            | Android -> "android"
            | Ios -> "ios"
            | UnknownOs -> "unknown"

        let a =
            match arch with
            | X64 -> "x64"
            | Arm64 -> "arm64"
            | OtherArch s -> s.ToLowerInvariant()

        o + "-" + a

    let detect () : RuntimeIdentity =
        let os = detectOs ()
        let arch = detectArch ()

        { Rid = ridOf os arch
          Os = os
          Arch = arch
          IsAot = not (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
          IsContainer = not (isNull (Environment.GetEnvironmentVariable "DOTNET_RUNNING_IN_CONTAINER"))
          IsWsl = not (isNull (Environment.GetEnvironmentVariable "WSL_DISTRO_NAME")) }

// ---- File watching: debounce, overflow detection, polling fallback ----------------
type ChangeKind =
    | Created
    | Changed
    | Deleted
    | Renamed

type FileChange = { Path: string; Kind: ChangeKind }

type WatchEvent =
    | Batch of FileChange list
    /// The OS dropped events (ReadDirectoryChangesW overflow, inotify limit). Never silent.
    | Overflow of reason: string
    | FellBackToPolling

type IFileWatcher =
    inherit IDisposable
    abstract Start: root: string -> unit
    abstract Events: IObservable<WatchEvent>

// ---- IPC transport ----------------------------------------------------------------
type TransportKind =
    | UnixDomainSocket
    | NamedPipe

type ITransport =
    abstract Kind: TransportKind
    abstract EndpointFor: name: string -> string

// ---- Path identity: case sensitivity and long paths differ per OS -----------------
type IPathNormalizer =
    abstract Normalize: path: string -> string
    abstract AreSame: a: string * b: string -> bool

// ---- Overlay store: in-memory, content-addressed; filesystem CoW is only an optimisation
type IOverlayStore =
    abstract Get: key: string -> byte[] option
    abstract Put: key: string * value: byte[] -> unit
    /// Freezes the current layer and returns a new writable child. O(layer) copy, not O(world).
    abstract Fork: unit -> IOverlayStore

type InMemoryOverlay(parent: IOverlayStore option, seed: seq<KeyValuePair<string, byte[]>>) =
    let layer = Dictionary<string, byte[]>()

    do
        for kv in seed do
            layer.[kv.Key] <- kv.Value

    new() = InMemoryOverlay(None, Seq.empty)

    interface IOverlayStore with
        member _.Get key =
            match layer.TryGetValue key with
            | true, v -> Some v
            | _ ->
                match parent with
                | Some p -> p.Get key
                | None -> None

        member _.Put(key, value) = layer.[key] <- value

        member _.Fork() =
            let frozen = InMemoryOverlay(parent, layer) :> IOverlayStore
            InMemoryOverlay(Some frozen, Seq.empty) :> IOverlayStore

// ---- Sandbox and terminal ---------------------------------------------------------
type SandboxResult = { ExitCode: int; TimedOut: bool }

type ISandbox =
    /// Windows Job Objects, Linux cgroups/process groups, macOS process groups behind one contract.
    abstract Run: command: string * args: string list * timeoutMs: int -> SandboxResult

type ITerminal =
    abstract SupportsAnsi: bool
    abstract Write: text: string -> unit

module Pillar =
    [<Literal>]
    let Id = "Platform"

    [<Literal>]
    let Stability = "Stable"

    [<Literal>]
    let Status = "Skeleton"
