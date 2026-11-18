namespace FSharpened.Ide.Host

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Text.Json
open FSharpened.Ide
open FSharpened.Interop.Rust
open FSharpened.Platform

/// The extern block is the whole binding surface of `native/rust/fsh-watch`: plain DllImport, no reflection, no
/// generated code (ADR-0019). The library file is found by `Native.initialize`, never by default probing.
module internal NativeImports =
    [<Literal>]
    let Library = "fsh_watch"

    [<DllImport(Library)>]
    extern uint32 fsh_watch_abi_version()

    [<DllImport(Library)>]
    extern int fsh_watch_start(nativeint root, nativeint ignoreCsv, uint32 debounceMs, uint64& handle)

    [<DllImport(Library)>]
    extern int fsh_watch_poll(uint64 handle, uint32 timeoutMs, nativeint& json)

    [<DllImport(Library)>]
    extern int fsh_watch_stop(uint64 handle)

    [<DllImport(Library)>]
    extern int fsh_search(
        nativeint root,
        nativeint query,
        uint32 flags,
        nativeint includes,
        nativeint excludes,
        nativeint ignoreCsv,
        uint32 maxHits,
        uint32 maxPerFile,
        uint64 maxFileBytes,
        nativeint& json
    )

    [<DllImport(Library)>]
    extern nativeint fsh_watch_last_error()

    [<DllImport(Library)>]
    extern void fsh_watch_free_string(nativeint text)

/// Whether the Rust library is there to use.
type NativeStatus =
    | Available of path: string
    /// Why not: no file for this RID, an unexpected ABI version, a file that would not load. The IDE then uses the
    /// managed engines and says so.
    | Unavailable of reason: string

/// What happened to a path (the F# side of the watcher's JSON).
type FolderChange =
    {
        Kind: ChangeKind
        /// Relative to the watched folder, forward slashes. For a rename, the new path.
        Path: string
        /// For a rename, the old path.
        From: string option
        IsDir: bool
    }

type ChangeBatch =
    {
        Changes: FolderChange list
        /// Events were lost or too many to list: look at the whole folder again.
        Overflow: bool
    }

/// The JSON the library returns, read without reflection (safe under NativeAOT, like the rest of the pillar).
module NativeJson =
    let private str (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""

    let private num (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
        | _ -> 0

    let private flag (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v -> v.ValueKind = JsonValueKind.True
        | _ -> false

    let private items (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array -> [ for x in v.EnumerateArray() -> x ]
        | _ -> []

    /// The result of a search, in the managed types, so the IDE does not care which engine answered.
    let parseSearch (json: string) : Result<SearchResult, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let hit (h: JsonElement) : SearchHit =
                { Line = num h "line"
                  Column = num h "column"
                  Length = num h "length"
                  Preview = str h "preview"
                  PreviewStart = num h "previewStart" }

            let file (f: JsonElement) : FileHits =
                { Path = str f "path"
                  Hits = items f "hits" |> List.map hit
                  More = flag f "more" }

            Ok
                { Files = items root "files" |> List.map file
                  Total = num root "total"
                  FilesSearched = num root "filesSearched"
                  Truncated = flag root "truncated"
                  Error = str root "error" }
        with :? JsonException as e ->
            Error("the search result is not valid JSON: " + e.Message)

    let private kindOf (name: string) =
        match name with
        | "created" -> Some Created
        | "removed" -> Some Deleted
        | "renamed" -> Some Renamed
        | "changed" -> Some Changed
        | _ -> None

    /// A batch of changes from the watcher. An event of a kind this reader does not know is read as a plain change, so
    /// a newer library cannot make a change invisible.
    let parseBatch (json: string) : Result<ChangeBatch, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let change (e: JsonElement) : FolderChange =
                { Kind = kindOf (str e "kind") |> Option.defaultValue Changed
                  Path = str e "path"
                  From =
                    (match str e "from" with
                     | "" -> None
                     | f -> Some f)
                  IsDir = flag e "dir" }

            Ok
                { Overflow = flag root "overflow"
                  Changes = items root "events" |> List.map change }
        with :? JsonException as e ->
            Error("the change batch is not valid JSON: " + e.Message)

    /// A batch in the platform's vocabulary. A rename is a deletion of the old path followed by a rename to the new
    /// one, so a consumer that only looks at paths sees both ends.
    let toPlatform (batch: ChangeBatch) : WatchEvent list =
        let changes =
            [ for c in batch.Changes do
                  match c.From with
                  | Some old ->
                      { Path = old; Kind = Deleted }
                      { Path = c.Path; Kind = Renamed }
                  | None -> { Path = c.Path; Kind = c.Kind } ]

        [ if not changes.IsEmpty then
              Batch changes
          if batch.Overflow then
              Overflow "events were lost or too many to list; the folder is looked at again" ]

/// The Rust library `native/rust/fsh-watch` (PLAN P7.8), bound through the Interop.Rust resolver.
module Native =
    let private stem = Targets.libraryStem "fsh-watch"
    let private gate = obj ()
    let mutable private state: NativeStatus option = None

    let private lastError () : string option =
        Utf8.takeOwned NativeImports.fsh_watch_free_string (NativeImports.fsh_watch_last_error ())

    let private check (code: int) : Result<unit, string> =
        Abi.check lastError code |> Result.mapError (fun e -> e.Message)

    let private load (roots: string list) : NativeStatus =
        let identity = RuntimeIdentity.detect ()

        match NativeResolver.planFor identity roots stem with
        | None -> Unavailable("no native library is known for the RID " + identity.Rid)
        | Some plan ->
            match NativeResolver.resolve File.Exists plan with
            | NotFound tried -> Unavailable(NativeResolver.describeMiss stem tried)
            | Found path ->
                try
                    NativeResolver.install
                        (Assembly.GetExecutingAssembly())
                        (Map [ NativeImports.Library, [ path ] ])
                        true

                    let version = NativeImports.fsh_watch_abi_version ()

                    if version = 1u then
                        Available path
                    else
                        Unavailable(sprintf "%s speaks ABI version %d; this IDE expects 1" path version)
                with
                | :? DllNotFoundException as e -> Unavailable e.Message
                | :? BadImageFormatException as e ->
                    Unavailable(path + " is not a library for this machine: " + e.Message)
                | :? EntryPointNotFoundException as e -> Unavailable(path + " lacks an expected function: " + e.Message)

    /// Looks for the library below the given folders (`runtimes/<rid>/native/` first, then the folder itself) and loads
    /// it. Only the first call in a process decides; later calls return the same answer.
    let initialize (roots: string list) : NativeStatus =
        lock gate (fun () ->
            match state with
            | Some s -> s
            | None ->
                let s = load roots
                state <- Some s
                s)

    /// The library next to the program (where the build puts it), or why there is none.
    let status () : NativeStatus = initialize [ AppContext.BaseDirectory ]

    let available () : bool =
        match status () with
        | Available _ -> true
        | Unavailable _ -> false

    /// The comma-separated list of folder names the native side leaves out.
    let ignoreList () : string =
        String.Join(",", DiskWorkspace.ignoredDirs)

    let private optional (text: string) (f: nativeint -> 'a) : 'a =
        if String.IsNullOrEmpty text then
            f 0n
        else
            Utf8.withBorrowed text f

    let private owned (pointer: nativeint) : string option =
        Utf8.takeOwned NativeImports.fsh_watch_free_string pointer

    /// Searches the files below `root`. The options carry what the native side can do; the glob lists are
    /// comma-separated. A pattern the native engine cannot compile comes back as `Ok` with `Error` set.
    let search (root: string) (o: SearchOptions) (includes: string) (excludes: string) : Result<SearchResult, string> =
        let flags =
            (if o.CaseSensitive then 1u else 0u)
            ||| (if o.WholeWord then 2u else 0u)
            ||| (if o.Regex then 4u else 0u)
            ||| 8u

        let mutable json = 0n

        let code =
            Utf8.withBorrowed root (fun r ->
                Utf8.withBorrowed o.Query (fun q ->
                    optional includes (fun i ->
                        optional excludes (fun x ->
                            Utf8.withBorrowed (ignoreList ()) (fun g ->
                                NativeImports.fsh_search (
                                    r,
                                    q,
                                    flags,
                                    i,
                                    x,
                                    g,
                                    uint32 Search.maxHits,
                                    uint32 Search.maxPerFile,
                                    uint64 DiskWorkspace.maxFileBytes,
                                    &json
                                ))))))

        match check code with
        | Error e -> Error e
        | Ok() ->
            match owned json with
            | None -> Error "the library returned no result"
            | Some text -> NativeJson.parseSearch text

    /// Starts a watch and returns its handle.
    let startWatch (root: string) (debounceMs: int) : Result<uint64, string> =
        let mutable handle = 0UL

        let code =
            Utf8.withBorrowed root (fun r ->
                Utf8.withBorrowed (ignoreList ()) (fun g ->
                    NativeImports.fsh_watch_start (r, g, uint32 debounceMs, &handle)))

        check code |> Result.map (fun () -> handle)

    /// Waits up to `timeoutMs` for a batch. `Ok None` means nothing happened in that time; `Error` after the watch was
    /// stopped.
    let pollWatch (handle: uint64) (timeoutMs: int) : Result<ChangeBatch option, string> =
        let mutable json = 0n
        let code = NativeImports.fsh_watch_poll (handle, uint32 timeoutMs, &json)

        match check code with
        | Error e -> Error e
        | Ok() ->
            match owned json with
            | None -> Ok None
            | Some text -> NativeJson.parseBatch text |> Result.map Some

    let stopWatch (handle: uint64) : unit =
        NativeImports.fsh_watch_stop handle |> ignore
