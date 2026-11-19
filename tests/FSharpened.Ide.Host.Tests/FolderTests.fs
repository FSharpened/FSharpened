module FolderTests

// Folder search and folder watching (PLAN P7.8): the pure parts (JSON from the Rust library, snapshot diffs, merging
// editor text into a result, ordering), the managed fallbacks (managed search, the polling watcher), and the native
// engines called for real.
//
// The native tests need the library built from native/rust/fsh-watch, which the Host project's build does when cargo is
// on the PATH. Without it they pass vacuously, so a contributor who never touches Rust can run the suite; with
// FSH_REQUIRE_RUST=1 (the CI runner sets it for --require-rust) a missing library is a failure instead.

open System
open System.Collections.Generic
open System.IO
open System.Threading
open Xunit
open FSharpened.Ide
open FSharpened.Ide.Host
open FSharpened.Platform

let private requireRust =
    Environment.GetEnvironmentVariable "FSH_REQUIRE_RUST" = "1"

/// Runs the body when the Rust library loaded. Not loaded: pass, unless FSH_REQUIRE_RUST=1.
let private withNative (body: unit -> unit) =
    match Native.status () with
    | Available _ -> body ()
    | Unavailable reason ->
        if requireRust then
            failwith ("FSH_REQUIRE_RUST=1 but the Rust library is not available: " + reason)

let private opts (q: string) : SearchOptions =
    { Query = q
      CaseSensitive = false
      WholeWord = false
      Regex = false }

let private write (root: string) (rel: string) (text: string) =
    let full = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
    File.WriteAllText(full, text)

// ---- pure: JSON from the library ----------------------------------------------------------------

[<Fact>]
let ``a search result in JSON becomes the managed types`` () =
    let json =
        """{"files":[{"path":"src/a.fs","more":true,"hits":[{"line":3,"column":5,"length":4,"previewStart":2,"preview":"let hello"}]}],"total":1,"filesSearched":7,"truncated":false,"error":""}"""

    match NativeJson.parseSearch json with
    | Error e -> failwith e
    | Ok r ->
        Assert.Equal(1, r.Total)
        Assert.Equal(7, r.FilesSearched)
        Assert.False r.Truncated
        Assert.Equal("", r.Error)
        let f = Assert.Single r.Files
        Assert.Equal("src/a.fs", f.Path)
        Assert.True f.More
        let h = Assert.Single f.Hits

        Assert.Equal<SearchHit>(
            { Line = 3
              Column = 5
              Length = 4
              Preview = "let hello"
              PreviewStart = 2 },
            h
        )

[<Fact>]
let ``garbage from the library is an error, not an exception`` () =
    Assert.True((NativeJson.parseSearch "not json").IsError)
    Assert.True((NativeJson.parseBatch "{").IsError)

[<Fact>]
let ``a change batch in JSON is read, and a rename is a deletion plus a rename in the platform vocabulary`` () =
    let json =
        """{"overflow":false,"events":[{"kind":"renamed","path":"b.txt","from":"a.txt","dir":false},{"kind":"created","path":"d","dir":true},{"kind":"mystery","path":"x","dir":false}]}"""

    match NativeJson.parseBatch json with
    | Error e -> failwith e
    | Ok b ->
        Assert.False b.Overflow
        Assert.Equal(3, b.Changes.Length)
        Assert.Equal(Some "a.txt", b.Changes[0].From)
        Assert.True b.Changes[1].IsDir
        Assert.Equal(Changed, b.Changes[2].Kind) // an unknown kind is still a change

        match NativeJson.toPlatform b with
        | [ Batch changes ] ->
            Assert.Equal<FileChange list>(
                [ { Path = "a.txt"; Kind = Deleted }
                  { Path = "b.txt"; Kind = Renamed }
                  { Path = "d"; Kind = Created }
                  { Path = "x"; Kind = Changed } ],
                changes
            )
        | other -> failwithf "unexpected %A" other

[<Fact>]
let ``an overflow batch says so and an empty one says nothing`` () =
    match NativeJson.parseBatch """{"overflow":true,"events":[]}""" with
    | Ok b ->
        match NativeJson.toPlatform b with
        | [ Overflow reason ] -> Assert.NotEmpty reason
        | other -> failwithf "unexpected %A" other
    | Error e -> failwith e

    match NativeJson.parseBatch """{"overflow":false,"events":[]}""" with
    | Ok b -> Assert.Empty(NativeJson.toPlatform b)
    | Error e -> failwith e

// ---- pure: ordering, merging, snapshots ----------------------------------------------------------

[<Fact>]
let ``files come before folders, each alphabetical without regard to case`` () =
    let sorted =
        [ "b/z.fs"; "B.fs"; "a/y.fs"; "a.fs"; "b/a/q.fs" ]
        |> List.sortWith FolderSearch.comparePath

    Assert.Equal<string list>([ "a.fs"; "B.fs"; "a/y.fs"; "b/z.fs"; "b/a/q.fs" ], sorted)

let private hit line column : SearchHit =
    { Line = line
      Column = column
      Length = 3
      Preview = "abc"
      PreviewStart = 0 }

[<Fact>]
let ``text open in the editor replaces what the disk gave for that file`` () =
    let disk: SearchResult =
        { Files =
            [ { Path = "a.txt"
                Hits = [ hit 1 1 ]
                More = false }
              { Path = "z.txt"
                Hits = [ hit 1 1 ]
                More = false } ]
          Total = 2
          FilesSearched = 5
          Truncated = false
          Error = "" }
    // a.txt: the editor no longer has the word; m.txt: only the editor has it; z.txt is not open.
    let merged =
        FolderSearch.mergeOpen (opts "abc") "" "" [ "a.txt", "nothing here\n"; "m.txt", "xx abc\n" ] disk

    Assert.Equal<string list>([ "m.txt"; "z.txt" ], merged.Files |> List.map (fun f -> f.Path))
    Assert.Equal(2, merged.Total)
    Assert.Equal(5, merged.FilesSearched)

[<Fact>]
let ``open files in ignored folders or outside the globs are not merged in`` () =
    let disk: SearchResult =
        { Files = []
          Total = 0
          FilesSearched = 0
          Truncated = false
          Error = "" }

    let merged =
        FolderSearch.mergeOpen (opts "abc") "*.fs" "" [ "bin/x.fs", "abc"; "src/y.txt", "abc"; "src/z.fs", "abc" ] disk

    Assert.Equal<string list>([ "src/z.fs" ], merged.Files |> List.map (fun f -> f.Path))

let private entry size stamp : SnapshotEntry =
    { IsDir = false
      Size = size
      Stamp = stamp }

[<Fact>]
let ``a snapshot diff reports created, changed and deleted paths, and a folder only when it appears or goes`` () =
    let dir: SnapshotEntry = { IsDir = true; Size = 0L; Stamp = 1L }

    let before =
        Map
            [ "same.txt", entry 1L 1L
              "edited.txt", entry 1L 1L
              "gone.txt", entry 1L 1L
              "folder", dir ]

    let after =
        Map
            [ "same.txt", entry 1L 1L
              "edited.txt", entry 2L 1L
              "new.txt", entry 1L 1L
              "folder", { dir with Stamp = 99L } // something inside it changed: not reported
              "newfolder", dir ]

    let changes = Snapshot.diff before after |> List.sortBy (fun c -> c.Path)

    Assert.Equal<FileChange list>(
        [ { Path = "edited.txt"; Kind = Changed }
          { Path = "gone.txt"; Kind = Deleted }
          { Path = "new.txt"; Kind = Created }
          { Path = "newfolder"; Kind = Created } ],
        changes
    )

[<Fact>]
let ``a snapshot leaves out ignored folders and temporary save files`` () =
    DiskTests.withFolder (fun root ->
        write root "src/a.fs" "x"
        write root "node_modules/p/x.js" "x"
        write root "bin/Debug/x.dll" "x"
        write root "src/a.fs.fsh-tmp" "x"
        let keys = Snapshot.take root |> Map.keys |> List.ofSeq
        Assert.Equal<string list>([ "src"; "src/a.fs" ], keys))

// ---- managed fallback: search ---------------------------------------------------------------------

let private stubSearch (r: Result<SearchResult, string>) : Result<NativeSearch, string> = Ok(fun _ _ _ _ -> r)

[<Fact>]
let ``with no native library the managed engine answers, and says why`` () =
    DiskTests.withFolder (fun root ->
        write root "a.txt" "say hello\n"
        write root "b.txt" "nothing\n"

        let r =
            FolderSearch.run (Error "no library for this RID") root (opts "hello") "" "" []

        Assert.Equal(1, r.Result.Total)
        Assert.Equal(ManagedEngine "no library for this RID", r.Engine)
        Assert.Contains("no library", FolderSearch.engineLabel r.Engine))

[<Fact>]
let ``a native failure falls back to the managed engine with the reason`` () =
    DiskTests.withFolder (fun root ->
        write root "a.txt" "say hello\n"
        let r = FolderSearch.run (stubSearch (Error "boom")) root (opts "hello") "" "" []
        Assert.Equal(1, r.Result.Total)

        match r.Engine with
        | ManagedEngine reason -> Assert.Contains("boom", reason)
        | NativeEngine -> failwith "should have fallen back")

[<Fact>]
let ``a regular expression the native engine cannot read goes to the managed engine`` () =
    DiskTests.withFolder (fun root ->
        write root "a.txt" "foobar\nfoobaz\n"

        let nativeRefused: SearchResult =
            { Files = []
              Total = 0
              FilesSearched = 0
              Truncated = false
              Error = "not a valid regular expression: look-around is not supported" }

        let r =
            FolderSearch.run (stubSearch (Ok nativeRefused)) root { opts "foo(?=bar)" with Regex = true } "" "" []

        Assert.Equal("", r.Result.Error)
        Assert.Equal(1, r.Result.Total) // .NET reads look-ahead
        Assert.True(r.Engine <> NativeEngine))

[<Fact>]
let ``a bad pattern that is not a regular expression keeps the native message`` () =
    DiskTests.withFolder (fun root ->
        let nativeSaid: SearchResult =
            { Files = []
              Total = 0
              FilesSearched = 0
              Truncated = false
              Error = "the search text is longer than 500 characters" }

        let r =
            FolderSearch.run (stubSearch (Ok nativeSaid)) root (opts (String('x', 600))) "" "" []

        Assert.Equal(NativeEngine, r.Engine)
        Assert.Contains("500", r.Result.Error))

// ---- managed fallback: the polling watcher -----------------------------------------------------------

let private collect (w: IFileWatcher) =
    let seen = List<WatchEvent>()

    let sub =
        w.Events.Subscribe
            { new IObserver<WatchEvent> with
                member _.OnNext e = lock seen (fun () -> seen.Add e)
                member _.OnError _ = ()
                member _.OnCompleted() = () }

    seen, sub

/// Waits (up to 20 seconds, CI machines are slow) until the events satisfy the predicate.
let private eventually (seen: List<WatchEvent>) (pred: FileChange list -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 20.0
    let mutable ok = false

    while not ok && DateTime.UtcNow < deadline do
        let changes =
            lock seen (fun () ->
                [ for e in seen do
                      match e with
                      | Batch cs -> yield! cs
                      | _ -> () ])

        ok <- pred changes

        if not ok then
            Thread.Sleep 50

    ok

let private has path kind (cs: FileChange list) =
    cs |> List.exists (fun c -> c.Path = path && c.Kind = kind)

[<Fact>]
let ``the polling watcher announces itself and reports a created, changed and deleted file`` () =
    DiskTests.withFolder (fun root ->
        write root "keep/x.txt" "x"
        use w = new PollingWatcher(TimeSpan.FromMilliseconds 100.0)
        let watcher = w :> IFileWatcher
        let seen, sub = collect watcher
        watcher.Start root

        write root "keep/new.txt" "one"
        Assert.True(eventually seen (has "keep/new.txt" Created), "created")

        // make sure the size differs, since file times can be coarse
        write root "keep/new.txt" "two and more"
        Assert.True(eventually seen (has "keep/new.txt" Changed), "changed")

        File.Delete(Path.Combine(root, "keep", "new.txt"))
        Assert.True(eventually seen (has "keep/new.txt" Deleted), "deleted")

        write root "node_modules/p/ignored.js" "x"
        write root "last.txt" "x"
        Assert.True(eventually seen (has "last.txt" Created))

        lock seen (fun () ->
            Assert.Contains(FellBackToPolling, seen)

            let all =
                [ for e in seen do
                      match e with
                      | Batch cs -> yield! cs
                      | _ -> () ]

            Assert.DoesNotContain(all, fun c -> c.Path.StartsWith "node_modules"))

        sub.Dispose())

[<Fact>]
let ``starting a watch without asking for native gives the polling watcher`` () =
    DiskTests.withFolder (fun root ->
        let running = FolderWatch.start false root
        use _w = running.Watcher

        match running.Engine with
        | PollingWatch _ -> ()
        | NativeWatch -> failwith "native was not asked for")

// ---- native: the library called for real ---------------------------------------------------------------

let private same (a: SearchResult) (b: SearchResult) =
    Assert.Equal(b.Total, a.Total)
    Assert.Equal(b.Truncated, a.Truncated)
    Assert.Equal(b.Error, a.Error)
    Assert.Equal<string list>(b.Files |> List.map (fun f -> f.Path), a.Files |> List.map (fun f -> f.Path))

    for fa, fb in List.zip a.Files b.Files do
        Assert.Equal(fb.More, fa.More)
        Assert.Equal<SearchHit list>(fb.Hits, fa.Hits)

let private fixture (root: string) =
    write root "src/a.fs" "let hello = 1\nlet Hello = 2\n    let helloWorld = hello + 3\n"
    write root "src/deep/b.fs" "// Hello again, héllo wörld 😀 hello\n"
    write root "notes.md" "nothing here\r\nhello CRLF\r\nend\r\n"
    write root "readme.txt" (String.replicate 120 "x" + " hello at the end of a long line\n")
    write root "docs/guide.md" "# Hello\n\nsay hello twice: hello hello\n"
    write root "node_modules/p/x.js" "hello hidden\n"
    write root "bin/out.txt" "hello hidden\n"
    File.WriteAllBytes(Path.Combine(root, "blob.bin"), [| 104uy; 101uy; 108uy; 108uy; 111uy; 0uy; 1uy |])

[<Theory>]
[<InlineData("hello", false, false, false, "", "")>]
[<InlineData("hello", true, false, false, "", "")>]
[<InlineData("hello", false, true, false, "", "")>]
[<InlineData("hel+o", false, false, true, "", "")>]
[<InlineData("hel+o", false, true, true, "", "")>]
[<InlineData("^let", false, false, true, "", "")>]
[<InlineData("hello", false, false, false, "*.fs", "")>]
[<InlineData("hello", false, false, false, "", "deep, *.md")>]
[<InlineData("wörld", false, false, false, "", "")>]
[<InlineData("😀", false, false, false, "", "")>]
[<InlineData("nomatchanywhere", false, false, false, "", "")>]
let ``the native engine and the managed engine give the same answer`` (q, cs, ww, rx, inc: string, exc: string) =
    withNative (fun () ->
        DiskTests.withFolder (fun root ->
            fixture root

            let o =
                { Query = q
                  CaseSensitive = cs
                  WholeWord = ww
                  Regex = rx }

            let managed = Search.run o inc exc (Replace.gather root [])

            match Native.search root o inc exc with
            | Error e -> failwith e
            | Ok native -> same native managed))

[<Fact>]
let ``the native engine skips binary and ignored folders and honours gitignore`` () =
    withNative (fun () ->
        DiskTests.withFolder (fun root ->
            fixture root
            write root ".gitignore" "docs/\n"

            match Native.search root (opts "hello") "" "" with
            | Error e -> failwith e
            | Ok r ->
                let paths = r.Files |> List.map (fun f -> f.Path)
                Assert.DoesNotContain("blob.bin", paths)
                Assert.DoesNotContain("node_modules/p/x.js", paths)
                Assert.DoesNotContain("bin/out.txt", paths)
                Assert.DoesNotContain("docs/guide.md", paths)
                Assert.Contains("src/a.fs", paths)))

[<Fact>]
let ``the native engine caps the results and says so`` () =
    withNative (fun () ->
        DiskTests.withFolder (fun root ->
            for i in 0..29 do
                write root (sprintf "f%02d.txt" i) (String.replicate 100 "x\n")

            match Native.search root (opts "x") "" "" with
            | Error e -> failwith e
            | Ok r ->
                Assert.Equal(Search.maxHits, r.Total)
                Assert.True r.Truncated
                Assert.True(r.Files |> List.forall (fun f -> f.Hits.Length <= Search.maxPerFile))))

[<Fact>]
let ``a pattern native cannot compile is answered by the managed engine through the default path`` () =
    withNative (fun () ->
        DiskTests.withFolder (fun root ->
            write root "a.txt" "foobar\nfoobaz\n"

            let r =
                FolderSearch.runDefault root { opts "foo(?=bar)" with Regex = true } "" "" []

            Assert.Equal(1, r.Result.Total)
            Assert.True(r.Engine <> NativeEngine)))

[<Fact>]
let ``the default path uses the native engine and merges the text of open files`` () =
    withNative (fun () ->
        DiskTests.withFolder (fun root ->
            write root "a.txt" "saved text\n"
            write root "b.txt" "needle on disk\n"
            let opened = [ "a.txt", "typed needle text\n"; "b.txt", "edited away\n" ]
            let r = FolderSearch.runDefault root (opts "needle") "" "" opened
            Assert.Equal(NativeEngine, r.Engine)
            Assert.Equal<string list>([ "a.txt" ], r.Result.Files |> List.map (fun f -> f.Path))
            Assert.Equal(1, r.Result.Total)))

[<Fact>]
let ``an invalid regular expression is reported, not thrown`` () =
    withNative (fun () ->
        DiskTests.withFolder (fun root ->
            let r = FolderSearch.runDefault root { opts "(" with Regex = true } "" "" []
            Assert.False(String.IsNullOrEmpty r.Result.Error)))

[<Fact>]
let ``the native watcher reports changes made outside, and ignores the ignored folders`` () =
    withNative (fun () ->
        DiskTests.withFolder (fun root ->
            write root "src/a.txt" "one"
            let running = FolderWatch.start true root
            use w = running.Watcher
            Assert.Equal(NativeWatch, running.Engine)
            let seen, sub = collect w

            write root "src/b.txt" "x"

            let anyOverflow () =
                lock seen (fun () ->
                    seen
                    |> Seq.exists (function
                        | Overflow _ -> true
                        | _ -> false))

            Assert.True(eventually seen (has "src/b.txt" Created) || anyOverflow (), "created")

            write root "src/a.txt" "two and more"
            Assert.True(eventually seen (has "src/a.txt" Changed) || anyOverflow (), "changed")

            File.Move(Path.Combine(root, "src", "b.txt"), Path.Combine(root, "src", "c.txt"))
            Assert.True(eventually seen (has "src/c.txt" Renamed) || anyOverflow (), "renamed")
            Assert.True(eventually seen (has "src/b.txt" Deleted) || anyOverflow (), "the old name is gone")

            File.Delete(Path.Combine(root, "src", "c.txt"))
            Assert.True(eventually seen (has "src/c.txt" Deleted) || anyOverflow (), "deleted")

            write root "node_modules/q/ignored.js" "x"
            write root ".git/ignored" "x"
            write root "marker.txt" "x"
            Assert.True(eventually seen (has "marker.txt" Created) || anyOverflow ())

            let all =
                lock seen (fun () ->
                    [ for e in seen do
                          match e with
                          | Batch cs -> yield! cs
                          | _ -> () ])

            Assert.DoesNotContain(all, fun c -> c.Path.StartsWith "node_modules" || c.Path.StartsWith ".git")
            sub.Dispose()))

[<Fact>]
let ``stopping the native watcher ends its thread quickly`` () =
    withNative (fun () ->
        DiskTests.withFolder (fun root ->
            let running = FolderWatch.start true root
            let started = DateTime.UtcNow
            running.Watcher.Dispose()
            Assert.True((DateTime.UtcNow - started) < TimeSpan.FromSeconds 5.0)))

[<Fact>]
let ``the native watcher refuses a folder that does not exist and the factory falls back to polling`` () =
    withNative (fun () ->
        DiskTests.withFolder (fun root ->
            let missing = Path.Combine(root, "nope")
            let running = FolderWatch.start true missing
            use _w = running.Watcher

            match running.Engine with
            | PollingWatch reason -> Assert.False(String.IsNullOrEmpty reason)
            | NativeWatch -> failwith "a missing folder cannot be watched natively"))
