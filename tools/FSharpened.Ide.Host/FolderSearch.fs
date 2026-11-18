namespace FSharpened.Ide.Host

open System
open FSharpened.Ide

/// Which engine answered a search.
type SearchEngine =
    | NativeEngine
    /// The managed engine, and why the native one was not used (empty when it was not asked for).
    | ManagedEngine of reason: string

/// What a folder search found, and who found it.
type FolderSearchResult =
    { Result: SearchResult
      Engine: SearchEngine }

/// A search over the folder on disk, for the native engine. The default is `Native.search`; tests and callers without
/// the library pass something else (or nothing).
type NativeSearch = string -> SearchOptions -> string -> string -> Result<SearchResult, string>

/// Search-in-files: the Rust engine when the library is there, the managed one otherwise (PLAN P7.8). Both follow the
/// same rules (`Search`), so the answer does not depend on which one ran, with two exceptions that are deliberate: the
/// native engine honours `.gitignore`, and it reads a regular expression in Rust's syntax, so a pattern it cannot
/// compile (look-around, back-references) is handed to the managed engine, which reads .NET's.
module FolderSearch =
    /// The order the explorer and the managed search use: within a folder the files first, then the sub-folders, each
    /// alphabetical without regard to case.
    let comparePath (a: string) (b: string) : int =
        let pa = a.Split '/'
        let pb = b.Split '/'
        let n = min pa.Length pb.Length
        let mutable result = 0
        let mutable i = 0

        while result = 0 && i < n do
            let aFile = i = pa.Length - 1
            let bFile = i = pb.Length - 1

            if aFile <> bFile then
                result <- if aFile then -1 else 1
            else
                result <- String.CompareOrdinal(pa[i].ToLowerInvariant(), pb[i].ToLowerInvariant())

            i <- i + 1

        if result <> 0 then result else compare pa.Length pb.Length

    let private inIgnoredFolder (rel: string) =
        rel.Split '/' |> Array.exists DiskWorkspace.ignoredDirs.Contains

    /// A native result that knows the disk only, corrected for the files open in the editor: for each of them the hits
    /// come from the text in the editor (so unsaved changes are found), in place of whatever the disk gave.
    let mergeOpen
        (opts: SearchOptions)
        (includes: string)
        (excludes: string)
        (opened: (string * string) list)
        (disk: SearchResult)
        : SearchResult =
        let live =
            opened
            |> List.filter (fun (rel, _) -> not (inIgnoredFolder rel) && Glob.allowed includes excludes rel)

        if live.IsEmpty then
            disk
        else
            let paths = live |> List.map fst |> Set.ofList

            let fromEditor =
                live
                |> List.choose (fun (rel, text) ->
                    match Search.findInText opts rel text with
                    | Ok fh when not fh.Hits.IsEmpty -> Some fh
                    | _ -> None)

            let files =
                (disk.Files |> List.filter (fun f -> not (paths.Contains f.Path))) @ fromEditor
                |> List.sortWith (fun a b -> comparePath a.Path b.Path)

            let total = files |> List.sumBy (fun f -> f.Hits.Length)

            { disk with
                Files = files
                Total = total
                Truncated = disk.Truncated || total >= Search.maxHits }

    /// Searches the files of the folder as they are on disk, except that a file open in the editor is searched as it is
    /// there. `native` is the fast engine (None: not available, with the reason).
    let run
        (native: Result<NativeSearch, string>)
        (root: string)
        (opts: SearchOptions)
        (includes: string)
        (excludes: string)
        (opened: (string * string) list)
        : FolderSearchResult =
        let managed reason =
            { Result = Search.run opts includes excludes (Replace.gather root opened)
              Engine = ManagedEngine reason }

        match native with
        | Error reason -> managed reason
        | Ok search ->
            match search root opts includes excludes with
            | Error e -> managed ("the native search failed: " + e)
            | Ok r when r.Error <> "" && opts.Regex -> managed "the pattern needs the managed regular-expression engine"
            | Ok r when r.Error <> "" -> { Result = r; Engine = NativeEngine }
            | Ok r ->
                { Result = mergeOpen opts includes excludes opened r
                  Engine = NativeEngine }

    /// The native engine of this machine: the library next to the program, or why there is none.
    let nativeEngine () : Result<NativeSearch, string> =
        match Native.status () with
        | Available _ -> Ok Native.search
        | Unavailable reason -> Error reason

    /// `run` with the default engines.
    let runDefault
        (root: string)
        (opts: SearchOptions)
        (includes: string)
        (excludes: string)
        (opened: (string * string) list)
        : FolderSearchResult =
        run (nativeEngine ()) root opts includes excludes opened

    let engineLabel (e: SearchEngine) : string =
        match e with
        | NativeEngine -> "native"
        | ManagedEngine "" -> "managed"
        | ManagedEngine reason -> "managed: " + reason
