namespace FSharpened.Ide

/// One file of a project: a normalised relative path ("src/engine.rs") and its text.
type ProjectFile = { Path: string; Text: string }

/// A polyglot project (ADR-0021): a tree of files in any of the IDE's languages, the file a plain Run uses, and an
/// ordered pipeline of files whose standard output feeds the next one's standard input.
type Project =
    {
        Name: string
        /// Sorted by path, ordinal and case-insensitive.
        Files: ProjectFile list
        Entry: string option
        Pipeline: string list
    }

/// One row of the file tree, flattened in display order.
type TreeRow =
    { Path: string
      Name: string
      Depth: int
      IsFolder: bool
      Language: Language option }

/// What an import kept and what it left out, with the reason for each.
type ImportResult =
    { Project: Project
      Skipped: (string * string) list }

module ProjectPath =
    let maxLength = 200
    let maxDepth = 8
    let maxSegment = 100

    /// The manifest is project metadata, not a project file.
    let manifestName = "fsharpened.project.json"

    // Names Windows reserves. Refusing them everywhere keeps every project portable (Invariant 1).
    let private reserved =
        set
            [ "con"
              "prn"
              "aux"
              "nul"
              "com1"
              "com2"
              "com3"
              "com4"
              "com5"
              "com6"
              "com7"
              "com8"
              "com9"
              "lpt1"
              "lpt2"
              "lpt3"
              "lpt4"
              "lpt5"
              "lpt6"
              "lpt7"
              "lpt8"
              "lpt9" ]

    let private hostile = "<>:\"|?*"

    let private segmentError (seg: string) : string option =
        if seg = "." || seg = ".." then
            Some "'.' and '..' are not allowed in a path"
        elif seg.Length > maxSegment then
            Some "a name is longer than 100 characters"
        elif seg.[seg.Length - 1] = '.' || seg.[seg.Length - 1] = ' ' then
            Some "a name cannot end with a dot or a space"
        elif seg |> Seq.exists (fun c -> c < ' ' || hostile.IndexOf c >= 0) then
            Some "a name has a character that is not portable: control characters and < > : \" | ? *"
        else
            let stem =
                (match seg.IndexOf '.' with
                 | -1 -> seg
                 | i -> seg.Substring(0, i))
                    .ToLowerInvariant()

            if reserved.Contains stem then
                Some("'" + seg + "' is a reserved name on Windows")
            else
                None

    /// Turns what a person or an archive wrote into the one spelling the project uses: forward slashes, no leading
    /// "./", no empty parts. Refuses anything that could escape the project or fail on another operating system.
    let normalize (raw: string) : Result<string, string> =
        let swapped = raw.Trim().Replace('\\', '/')

        let p =
            if swapped.Length >= 2 && swapped.[0] = '.' && swapped.[1] = '/' then
                swapped.Substring 2
            else
                swapped

        if p = "" then
            Error "the path is empty"
        elif p.[0] = '/' then
            Error "absolute paths are not allowed"
        elif p.Length > maxLength then
            Error "the path is longer than 200 characters"
        else
            let segs = p.Split('/') |> Array.filter (fun s -> s <> "") |> Array.toList

            match segs with
            | [] -> Error "the path is empty"
            | _ when segs.Length > maxDepth -> Error "the path is nested deeper than 8 folders"
            | _ ->
                match segs |> List.tryPick segmentError with
                | Some e -> Error e
                | None -> Ok(String.concat "/" segs)

    let segments (path: string) = path.Split('/') |> Array.toList

    let name (path: string) =
        match path.LastIndexOf '/' with
        | -1 -> path
        | i -> path.Substring(i + 1)

    let parent (path: string) =
        match path.LastIndexOf '/' with
        | -1 -> ""
        | i -> path.Substring(0, i)

    let join (folder: string) (leaf: string) =
        if folder = "" then leaf else folder + "/" + leaf

    let key (path: string) = path.ToLowerInvariant()

    /// True when `path` is `folder` or lies inside it (case-insensitive, whole segments only).
    let isUnder (folder: string) (path: string) =
        let f = key folder
        let p = key path
        p = f || (p.Length > f.Length && p.Substring(0, f.Length + 1) = f + "/")

    let private byExtension =
        [ "fs", FSharp
          "fsx", FSharp
          "cs", CSharp
          "vb", VisualBasic
          "js", JavaScript
          "mjs", JavaScript
          "cjs", JavaScript
          "ts", TypeScript
          "py", Python
          "rs", Rust
          "dart", Dart
          "c", C
          "h", C
          "cpp", Cpp
          "cc", Cpp
          "cxx", Cpp
          "hpp", Cpp
          "hh", Cpp
          "go", Go
          "java", Java
          "rb", Ruby
          "lua", Lua
          "php", Php
          "r", R
          "hs", Haskell
          "ml", OCaml
          "mli", OCaml ]
        |> Map.ofList

    let extensionOf (path: string) =
        let n = name path

        match n.LastIndexOf '.' with
        | -1 -> ""
        | i -> n.Substring(i + 1).ToLowerInvariant()

    /// The language a file is written in, from its extension. None for text and data files (README.md, data.json).
    let languageOf (path: string) : Language option =
        Map.tryFind (extensionOf path) byExtension

module Project =
    let maxFiles = 200
    let maxFileChars = 65536
    let maxTotalChars = 2000000
    let maxPipeline = 24

    let cleanName (raw: string) =
        let n =
            raw
            |> Seq.filter (fun c -> c >= ' ' && c <> '/' && c <> '\\')
            |> Seq.toArray
            |> System.String
            |> fun s -> s.Trim()

        if n = "" then "Untitled"
        elif n.Length > 80 then n.Substring(0, 80)
        else n

    let create (name: string) : Project =
        { Name = cleanName name
          Files = []
          Entry = None
          Pipeline = [] }

    let paths (p: Project) = p.Files |> List.map (fun f -> f.Path)

    let tryFind (path: string) (p: Project) =
        let k = ProjectPath.key path
        p.Files |> List.tryFind (fun f -> ProjectPath.key f.Path = k)

    let exists (path: string) (p: Project) = (tryFind path p).IsSome

    let languages (p: Project) : Language list =
        let used = p.Files |> List.choose (fun f -> ProjectPath.languageOf f.Path)
        Language.all |> List.filter (fun l -> List.contains l used)

    let totalChars (p: Project) =
        p.Files |> List.sumBy (fun f -> f.Text.Length)

    /// A set of paths is consistent when no two collide ignoring case and none is both a file and a folder.
    let private treeError (all: string list) : string option =
        let keys = all |> List.map ProjectPath.key

        match keys |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
        | Some(k, _) ->
            Some(
                "two files would have the same name ignoring case: "
                + (all |> List.find (fun a -> ProjectPath.key a = k))
            )
        | None ->
            keys
            |> List.tryPick (fun a ->
                keys
                |> List.tryFind (fun b -> b.Length > a.Length && b.Substring(0, a.Length + 1) = a + "/")
                |> Option.map (fun _ -> "'" + a + "' cannot be both a file and a folder"))

    let private sorted (files: ProjectFile list) =
        files |> List.sortBy (fun f -> ProjectPath.key f.Path, f.Path)

    /// Every change goes through here, so the limits and the tree rules hold for every operation.
    let private rebuild (p: Project) (files: ProjectFile list) : Result<Project, string> =
        if files.Length > maxFiles then
            Error("a project holds at most " + string maxFiles + " files")
        elif files |> List.exists (fun f -> f.Text.Length > maxFileChars) then
            Error(
                "a file is larger than "
                + string maxFileChars
                + " characters, the most the execution services accept"
            )
        elif (files |> List.sumBy (fun f -> f.Text.Length)) > maxTotalChars then
            Error "the project is larger than 2 million characters"
        else
            match treeError (files |> List.map (fun f -> f.Path)) with
            | Some e -> Error e
            | None ->
                let known = files |> List.map (fun f -> ProjectPath.key f.Path) |> Set.ofList
                let keep (path: string) = known.Contains(ProjectPath.key path)

                Ok
                    { p with
                        Files = sorted files
                        Entry = p.Entry |> Option.filter keep
                        Pipeline = p.Pipeline |> List.filter keep }

    let addFile (path: string) (text: string) (p: Project) : Result<Project, string> =
        match ProjectPath.normalize path with
        | Error e -> Error e
        | Ok np ->
            if exists np p then
                Error("'" + np + "' already exists")
            else
                rebuild p ({ Path = np; Text = text } :: p.Files)

    let setText (path: string) (text: string) (p: Project) : Result<Project, string> =
        match tryFind path p with
        | None -> Error("'" + path + "' is not in the project")
        | Some f ->
            rebuild
                p
                (p.Files
                 |> List.map (fun x -> if x.Path = f.Path then { x with Text = text } else x))

    let removeFile (path: string) (p: Project) : Result<Project, string> =
        match tryFind path p with
        | None -> Error("'" + path + "' is not in the project")
        | Some f -> rebuild p (p.Files |> List.filter (fun x -> x.Path <> f.Path))

    /// Removes a folder and everything inside it.
    let removeFolder (folder: string) (p: Project) : Result<Project, string> =
        match p.Files |> List.filter (fun f -> ProjectPath.isUnder folder f.Path) with
        | [] -> Error("'" + folder + "' is not in the project")
        | gone -> rebuild p (p.Files |> List.filter (fun f -> not (List.contains f gone)))

    /// Renames or moves a file or a whole folder. Entry and pipeline follow the files.
    let move (fromPath: string) (toPath: string) (p: Project) : Result<Project, string> =
        match ProjectPath.normalize toPath with
        | Error e -> Error e
        | Ok target ->
            let affected = p.Files |> List.filter (fun f -> ProjectPath.isUnder fromPath f.Path)

            match affected with
            | [] -> Error("'" + fromPath + "' is not in the project")
            | _ when
                ProjectPath.isUnder fromPath target
                && ProjectPath.key fromPath <> ProjectPath.key target
                ->
                Error "a folder cannot be moved into itself"
            | _ ->
                let from = (affected |> List.head).Path

                let isFolder =
                    affected.Length > 1 || ProjectPath.key from <> ProjectPath.key fromPath

                let renamed (path: string) =
                    if isFolder then
                        // keep the spelling of the source's own tail, replace its folder prefix
                        let tail = path.Substring(fromPath.Length)
                        target + tail
                    else
                        target

                let mapping = affected |> List.map (fun f -> f.Path, renamed f.Path)

                let moved =
                    p.Files
                    |> List.map (fun f ->
                        match mapping |> List.tryFind (fun (o, _) -> o = f.Path) with
                        | Some(_, n) -> { f with Path = n }
                        | None -> f)

                let follow (path: string) =
                    match mapping |> List.tryFind (fun (o, _) -> o = path) with
                    | Some(_, n) -> n
                    | None -> path

                match rebuild p moved with
                | Error e -> Error e
                | Ok q ->
                    // `rebuild` filtered the references by the old names; put them back under the new names
                    Ok
                        { q with
                            Entry = p.Entry |> Option.map follow |> Option.filter (fun e -> exists e q)
                            Pipeline = p.Pipeline |> List.map follow |> List.filter (fun e -> exists e q) }

    let rename (path: string) (newName: string) (p: Project) =
        move path (ProjectPath.join (ProjectPath.parent path) newName) p

    let setEntry (path: string) (p: Project) : Result<Project, string> =
        match tryFind path p with
        | None -> Error("'" + path + "' is not in the project")
        | Some f when (ProjectPath.languageOf f.Path).IsNone ->
            Error("'" + f.Path + "' is not a source file the IDE can run")
        | Some f -> Ok { p with Entry = Some f.Path }

    let setPipeline (stages: string list) (p: Project) : Result<Project, string> =
        if stages.Length > maxPipeline then
            Error("a pipeline has at most " + string maxPipeline + " stages")
        else
            let resolved =
                stages
                |> List.map (fun s ->
                    match tryFind s p with
                    | None -> Error("pipeline stage '" + s + "' is not in the project")
                    | Some f when (ProjectPath.languageOf f.Path).IsNone ->
                        Error("pipeline stage '" + f.Path + "' is not a source file the IDE can run")
                    | Some f -> Ok f.Path)

            match
                resolved
                |> List.tryPick (function
                    | Error e -> Some e
                    | Ok _ -> None)
            with
            | Some e -> Error e
            | None ->
                Ok
                    { p with
                        Pipeline =
                            resolved
                            |> List.choose (function
                                | Ok v -> Some v
                                | Error _ -> None) }

    /// The file a plain Run uses: the chosen entry, else a conventional name at the root, else the first source file.
    let entryFile (p: Project) : ProjectFile option =
        match p.Entry |> Option.bind (fun e -> tryFind e p) with
        | Some f -> Some f
        | None ->
            let conventional =
                Language.infos |> List.map (fun i -> ProjectPath.key i.File) |> Set.ofList

            let runnable =
                p.Files |> List.filter (fun f -> (ProjectPath.languageOf f.Path).IsSome)

            runnable
            |> List.tryFind (fun f -> conventional.Contains(ProjectPath.key f.Path))
            |> Option.orElse (List.tryHead runnable)

    /// The file tree as flat rows in display order: folders before files, each group sorted ignoring case.
    let listing (p: Project) : TreeRow list =
        let rec level (folder: string) (depth: int) : TreeRow list =
            let inside =
                p.Files
                |> List.filter (fun f -> folder = "" || ProjectPath.isUnder folder f.Path && f.Path <> folder)

            let rest (f: ProjectFile) =
                if folder = "" then
                    f.Path
                else
                    f.Path.Substring(folder.Length + 1)

            let folders =
                inside
                |> List.filter (fun f -> (rest f).Contains "/")
                |> List.map (fun f -> (rest f).Split('/').[0])
                |> List.distinct
                |> List.sortBy ProjectPath.key

            let files =
                inside
                |> List.filter (fun f -> not ((rest f).Contains "/"))
                |> List.sortBy (fun f -> ProjectPath.key f.Path)

            [ for name in folders do
                  let path = ProjectPath.join folder name

                  yield
                      { Path = path
                        Name = name
                        Depth = depth
                        IsFolder = true
                        Language = None }

                  yield! level path (depth + 1)

              for f in files do
                  yield
                      { Path = f.Path
                        Name = ProjectPath.name f.Path
                        Depth = depth
                        IsFolder = false
                        Language = ProjectPath.languageOf f.Path } ]

        level "" 0

    /// Problems with the project as a whole, for the linter and for imports. Empty means well-formed.
    let issues (p: Project) : string list =
        [ match p.Entry with
          | Some e when not (exists e p) -> yield "the entry file '" + e + "' is not in the project"
          | _ -> ()

          for s in p.Pipeline do
              if not (exists s p) then
                  yield "pipeline stage '" + s + "' is not in the project"
              elif (ProjectPath.languageOf s).IsNone then
                  yield "pipeline stage '" + s + "' is not a source file the IDE can run"

          if p.Pipeline.Length > maxPipeline then
              yield "the pipeline has more than " + string maxPipeline + " stages" ]

    /// A project made from the single-language buffers the IDE had before projects existed. Nothing is lost.
    let ofBuffers (name: string) (active: Language) (buffers: (Language * string) list) : Project =
        let files =
            buffers
            |> List.map (fun (l, text) ->
                { Path = Language.fileName l
                  Text = text })

        { Name = cleanName name
          Files = sorted files
          Entry = Some(Language.fileName active)
          Pipeline = [] }

    /// Archives made by "download folder" wrap everything in one top-level folder; drop it.
    let private stripCommonRoot (entries: (string * string) list) =
        match entries with
        | [] -> entries
        | (first, _) :: _ ->
            let root = (ProjectPath.segments first).Head

            let allShare =
                entries
                |> List.forall (fun (path, _) ->
                    let s = ProjectPath.segments path
                    s.Length > 1 && ProjectPath.key s.Head = ProjectPath.key root)

            if allShare then
                entries |> List.map (fun (path, text) -> path.Substring(root.Length + 1), text)
            else
                entries

    /// Builds a project from files that came from an archive, a folder or a repository. Each file is judged on its own:
    /// an unusable one is skipped with its reason, the rest are kept. `entry` and `pipeline` come from the manifest when
    /// there was one; references that do not resolve are dropped and reported.
    let ofEntries
        (name: string)
        (entries: (string * string) list)
        (entry: string option)
        (pipeline: string list)
        : ImportResult =
        let accepted, skipped =
            entries
            |> List.fold
                (fun (ok, bad) (raw, text) ->
                    match ProjectPath.normalize raw with
                    | Error e -> ok, (raw, e) :: bad
                    | Ok p when p = ProjectPath.manifestName -> ok, bad
                    | Ok p -> (p, text) :: ok, bad)
                ([], [])

        // a manifest that sat inside the archive's wrapper folder is metadata too, whatever the wrapper was called
        let cleaned =
            stripCommonRoot (List.rev accepted)
            |> List.filter (fun (path, _) -> path <> ProjectPath.manifestName)

        let built, rejected =
            cleaned
            |> List.fold
                (fun (proj, bad) (path, text) ->
                    match addFile path text proj with
                    | Ok next -> next, bad
                    | Error e -> proj, (path, e) :: bad)
                (create name, [])

        let withEntry =
            match entry with
            | Some e when exists e built ->
                { built with
                    Entry = (tryFind e built) |> Option.map (fun f -> f.Path) }
            | _ -> built

        let withPipeline, pipelineProblems =
            match setPipeline pipeline withEntry with
            | Ok q -> q, []
            | Error e -> withEntry, [ ProjectPath.manifestName, e ]

        { Project = withPipeline
          Skipped = List.rev skipped @ List.rev rejected @ pipelineProblems }
