namespace FSharpened.Ide.Host

open System.Collections.Generic
open FSharpened.Ide

/// What a replace-in-files did.
type ReplaceOutcome =
    {
        /// Files that changed, and the matches replaced in them.
        Files: int
        Matches: int
        /// Files that are open in the editor are not written: their new text is returned here for the editor to apply as
        /// one edit (so Undo works), and they stay unsaved.
        Editor: (string * string) list
        /// One line per file that could not be read, rewritten or written.
        Failed: string list
    }

/// Find and replace across a folder, over the same rules as the website (`Search`), reading and writing through
/// `DiskWorkspace` so a path can never leave the folder.
module Replace =
    /// The files a search looks at: the folder's files as they are on disk, except that a file open in the editor is
    /// searched as it is in the editor, so unsaved changes are found and replaced too.
    let gather (root: string) (opened: (string * string) list) : (string * string) list =
        let entries, _ = DiskWorkspace.allFiles root 4000
        let inEditor = dict opened

        entries
        |> List.choose (fun e ->
            match inEditor.TryGetValue e.Rel with
            | true, t -> Some(e.Rel, t)
            | _ ->
                match DiskWorkspace.readText root e.Rel with
                | Ok t -> Some(e.Rel, t)
                | Error _ -> None)

    /// Applies `replacement` to every file in `hits`. A file in `opened` is not written; see `ReplaceOutcome.Editor`.
    let apply
        (root: string)
        (opts: SearchOptions)
        (replacement: string)
        (hits: FileHits list)
        (opened: (string * string) list)
        : ReplaceOutcome =
        let inEditor = dict opened
        let editor = List<string * string>()
        let failed = List<string>()
        let mutable files = 0
        let mutable matches = 0

        for f in hits do
            let current =
                match inEditor.TryGetValue f.Path with
                | true, t -> Ok t
                | _ -> DiskWorkspace.readText root f.Path

            match current with
            | Error e -> failed.Add(sprintf "%s: %s" f.Path e)
            | Ok text ->
                match Search.replaceInText opts replacement text with
                | Error e -> failed.Add(sprintf "%s: %s" f.Path e)
                | Ok(_, 0) -> ()
                | Ok(rewritten, n) ->
                    if inEditor.ContainsKey f.Path then
                        editor.Add((f.Path, rewritten))
                        files <- files + 1
                        matches <- matches + n
                    else
                        match DiskWorkspace.writeText root f.Path rewritten with
                        | Ok() ->
                            files <- files + 1
                            matches <- matches + n
                        | Error e -> failed.Add(sprintf "%s: %s" f.Path e)

        { Files = files
          Matches = matches
          Editor = List.ofSeq editor
          Failed = List.ofSeq failed }
