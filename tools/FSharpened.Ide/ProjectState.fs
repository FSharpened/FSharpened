namespace FSharpened.Ide

open System.Text
open FSharpened.Platform

/// What the page holds for one open project: the project itself, the file being edited, and the program's input.
/// Immutable like `Workspace`: every edit or restore produces a new value.
type ProjectState =
    {
        Project: Project
        /// The path of the file being edited, or "" when the project has no file open.
        Active: string
        Stdin: string
    }

/// Storage of a whole project in a statepoint overlay, so a snapshot captures every file, the entry, the pipeline and
/// the open file together (ADR-0021). Keys are stable, and a value that did not change is not written again, so the
/// layers of two snapshots share every unchanged file.
module ProjectState =
    let private enc (s: string) = Encoding.UTF8.GetBytes s
    let private dec (b: byte[]) = Encoding.UTF8.GetString b

    // Paths cannot contain a line break (ProjectPath refuses control characters), so a newline is a safe separator.
    let private lines (s: string) =
        s.Split('\n') |> Array.filter (fun x -> x <> "") |> Array.toList

    let private put (store: IOverlayStore) (key: string) (text: string) =
        let bytes = enc text

        match store.Get key with
        | Some existing when existing = bytes -> ()
        | _ -> store.Put(key, bytes)

    let write (store: IOverlayStore) (s: ProjectState) =
        for f in s.Project.Files do
            put store ("file/" + f.Path) f.Text

        // A snapshot forks the previous overlay, and an overlay cannot delete a key. The index is what says which of
        // the stored files belong to this state, so a file removed since the last snapshot never comes back.
        put store "meta/files" (String.concat "\n" (Project.paths s.Project))
        put store "meta/name" s.Project.Name
        put store "meta/entry" (defaultArg s.Project.Entry "")
        put store "meta/pipeline" (String.concat "\n" s.Project.Pipeline)
        put store "meta/active" s.Active
        put store "meta/stdin" s.Stdin

    let read (store: IOverlayStore) : ProjectState =
        let get key =
            match store.Get key with
            | Some b -> dec b
            | None -> ""

        let files =
            lines (get "meta/files")
            |> List.map (fun path ->
                { Path = path
                  Text = get ("file/" + path) })

        let entry = get "meta/entry"

        { Project =
            { Name = get "meta/name"
              Files = files
              Entry = if entry = "" then None else Some entry
              Pipeline = lines (get "meta/pipeline") }
          Active = get "meta/active"
          Stdin = get "meta/stdin" }

/// Whole-project history: the same statepoint graph as `Timeline`, holding a `ProjectState`.
type ProjectTimeline(start: ProjectState) =
    inherit StateTimeline<ProjectState>(start, ProjectState.write, ProjectState.read)
