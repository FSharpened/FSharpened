namespace FSharpened.Ide.Host

open System
open System.Collections.Generic
open System.IO
open System.Threading
open FSharpened.Platform

/// What is at one path, for the polling watcher.
type SnapshotEntry =
    {
        IsDir: bool
        Size: int64
        /// Last write time, as ticks.
        Stamp: int64
    }

/// The folder as the polling watcher sees it: every file and folder below the root (apart from the folders the explorer
/// leaves out), keyed by its relative path.
module Snapshot =
    /// The most entries one snapshot holds; a folder bigger than this is watched only in part.
    let maxEntries = 50000

    /// Temporary files the IDE writes beside a file while saving it; they are never reported.
    let isTemporary (rel: string) = rel.EndsWith ".fsh-tmp"

    let take (root: string) : Map<string, SnapshotEntry> =
        let found = Dictionary<string, SnapshotEntry>()
        let rootFull = Path.GetFullPath root

        let rec walk (dir: string) (depth: int) =
            if depth <= DiskWorkspace.maxDepth && found.Count < maxEntries then
                try
                    for info in DirectoryInfo(dir).EnumerateFileSystemInfos() do
                        let isLink = info.Attributes.HasFlag FileAttributes.ReparsePoint
                        let isDir = info.Attributes.HasFlag FileAttributes.Directory

                        if not isLink && not (isDir && DiskWorkspace.ignoredDirs.Contains info.Name) then
                            let rel = Path.GetRelativePath(rootFull, info.FullName).Replace('\\', '/')

                            if not (isTemporary rel) && found.Count < maxEntries then
                                found[rel] <-
                                    { IsDir = isDir
                                      Size =
                                        (match info with
                                         | :? FileInfo as f -> f.Length
                                         | _ -> 0L)
                                      Stamp = info.LastWriteTimeUtc.Ticks }

                                if isDir then
                                    walk info.FullName (depth + 1)
                with _ ->
                    () // an unreadable folder is skipped

        walk rootFull 0

        found |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    /// What changed between two snapshots, by path: new entries, entries gone, and files whose size or time moved. A
    /// folder is reported when it appears or disappears, not when something inside it changes. A rename cannot be told
    /// from a deletion and a creation by looking at the disk twice, so it is reported as those two.
    let diff (before: Map<string, SnapshotEntry>) (after: Map<string, SnapshotEntry>) : FileChange list =
        [ for KeyValue(path, now) in after do
              match Map.tryFind path before with
              | None -> { Path = path; Kind = Created }
              | Some was when not now.IsDir && (was.Size <> now.Size || was.Stamp <> now.Stamp) ->
                  { Path = path; Kind = Changed }
              | Some _ -> ()
          for KeyValue(path, _) in before do
              if not (after.ContainsKey path) then
                  { Path = path; Kind = Deleted } ]

type internal Hub() =
    let observers = List<IObserver<WatchEvent>>()

    member _.Publish(event: WatchEvent) =
        let snapshot = lock observers (fun () -> observers.ToArray())

        for o in snapshot do
            try
                o.OnNext event
            with _ ->
                () // one broken subscriber does not stop the others

    member _.Observable: IObservable<WatchEvent> =
        { new IObservable<WatchEvent> with
            member _.Subscribe(observer) =
                lock observers (fun () -> observers.Add observer)

                { new IDisposable with
                    member _.Dispose() =
                        lock observers (fun () -> observers.Remove observer |> ignore) } }

/// The managed fallback: looks at the folder every `interval` and reports what differs from the last look. It never
/// misses a change and never needs a native library, at the price of reading the whole folder each time.
type PollingWatcher(interval: TimeSpan) =
    let hub = Hub()
    let stop = new ManualResetEventSlim(false)
    let mutable thread: Thread option = None

    interface IFileWatcher with
        member _.Events = hub.Observable

        member _.Start(root: string) =
            if thread.IsNone then
                // The first look is taken before Start returns, so nothing that happens after it is missed.
                let first = Snapshot.take root

                let run () =
                    hub.Publish FellBackToPolling
                    let mutable last = first

                    while not (stop.Wait interval) do
                        let now = Snapshot.take root

                        match Snapshot.diff last now with
                        | [] -> ()
                        | changes -> hub.Publish(Batch changes)

                        last <- now

                let t = Thread(run, IsBackground = true, Name = "fsh-poll-watch")
                t.Start()
                thread <- Some t

        member _.Dispose() =
            stop.Set()

            match thread with
            | Some t ->
                t.Join(TimeSpan.FromSeconds 5.0) |> ignore
                thread <- None
            | None -> ()

/// The watcher backed by the Rust library: the operating system's own notifications, batched and de-duplicated by
/// the library, read on one background thread.
type NativeWatcher(debounceMs: int) =
    let hub = Hub()
    let stopped = new ManualResetEventSlim(false)
    let mutable handle: uint64 option = None
    let mutable thread: Thread option = None

    interface IFileWatcher with
        member _.Events = hub.Observable

        member _.Start(root: string) =
            if handle.IsNone then
                match Native.startWatch root debounceMs with
                | Error e -> failwith e
                | Ok h ->
                    handle <- Some h

                    let run () =
                        let mutable go = true

                        while go && not stopped.IsSet do
                            match Native.pollWatch h 250 with
                            | Ok None -> ()
                            | Ok(Some batch) ->
                                for event in NativeJson.toPlatform batch do
                                    match event with
                                    | Batch changes ->
                                        match changes |> List.filter (fun c -> not (Snapshot.isTemporary c.Path)) with
                                        | [] -> ()
                                        | kept -> hub.Publish(Batch kept)
                                    | other -> hub.Publish other
                            | Error _ ->
                                // the watch was stopped, or the library lost it; either way this loop is done
                                go <- false

                    let t = Thread(run, IsBackground = true, Name = "fsh-native-watch")
                    t.Start()
                    thread <- Some t

        member _.Dispose() =
            stopped.Set()

            match handle with
            | Some h ->
                Native.stopWatch h
                handle <- None
            | None -> ()

            match thread with
            | Some t ->
                t.Join(TimeSpan.FromSeconds 5.0) |> ignore
                thread <- None
            | None -> ()

/// Which engine is watching.
type WatchEngine =
    | NativeWatch
    /// The polling watcher, and why the native one was not used.
    | PollingWatch of reason: string

/// What changed on disk, as far as an editor cares.
type DiskChange =
    /// These paths (relative, forward slashes) were created, changed, removed or renamed.
    | SomePaths of Set<string>
    /// Events were lost or too many to list: look at everything again.
    | Everything

type RunningWatch =
    { Watcher: IFileWatcher
      Engine: WatchEngine }

module FolderWatch =
    let pollInterval = TimeSpan.FromSeconds 2.0

    /// Starts watching `root`: the native watcher when the library is there and starts, the polling watcher otherwise.
    /// The IDE never fails to open a folder because of Rust.
    let start (preferNative: bool) (root: string) : RunningWatch =
        let polling reason =
            let w = new PollingWatcher(pollInterval) :> IFileWatcher
            w.Start root

            { Watcher = w
              Engine = PollingWatch reason }

        if not preferNative then
            polling "native watching was not asked for"
        else
            match Native.status () with
            | Unavailable reason -> polling reason
            | Available _ ->
                let w = new NativeWatcher(200) :> IFileWatcher

                try
                    w.Start root
                    { Watcher = w; Engine = NativeWatch }
                with e ->
                    w.Dispose()
                    polling ("the native watcher did not start: " + e.Message)

    let engineLabel (e: WatchEngine) : string =
        match e with
        | NativeWatch -> "native"
        | PollingWatch "" -> "polling"
        | PollingWatch reason -> "polling: " + reason

    /// What a consumer that only wants to know "which files, or everything" does with an event.
    let interpret (event: WatchEvent) : DiskChange option =
        match event with
        | Batch changes -> Some(SomePaths(changes |> List.map (fun c -> c.Path) |> Set.ofList))
        | Overflow _ -> Some Everything
        | FellBackToPolling -> None

    /// Calls `onChange` (on the watcher's own thread) for every change on disk the running watch reports.
    let subscribe (running: RunningWatch) (onChange: DiskChange -> unit) : IDisposable =
        running.Watcher.Events.Subscribe
            { new IObserver<WatchEvent> with
                member _.OnNext event = interpret event |> Option.iter onChange
                member _.OnError _ = ()
                member _.OnCompleted() = () }
