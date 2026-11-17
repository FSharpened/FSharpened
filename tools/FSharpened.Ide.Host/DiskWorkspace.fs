namespace FSharpened.Ide.Host

open System
open System.Collections.Generic
open System.IO
open System.Text

/// One file or folder of the opened folder.
type Entry =
    {
        /// Relative to the root, forward slashes.
        Rel: string
        Name: string
        IsDir: bool
        Size: int64
    }

/// The opened folder on disk. Every operation takes the root and a relative path, and refuses a path that could leave
/// the root (`..`, absolute or drive paths, reserved names, links), like the website's project model (ADR-0023).
module DiskWorkspace =
    let private reserved =
        set
            [ "CON"
              "PRN"
              "AUX"
              "NUL"
              "COM1"
              "COM2"
              "COM3"
              "COM4"
              "COM5"
              "COM6"
              "COM7"
              "COM8"
              "COM9"
              "LPT1"
              "LPT2"
              "LPT3"
              "LPT4"
              "LPT5"
              "LPT6"
              "LPT7"
              "LPT8"
              "LPT9" ]

    /// Folders that are dependencies or build output; they are not shown or searched.
    let ignoredDirs =
        set
            [ ".git"
              "node_modules"
              "target"
              "bin"
              "obj"
              "__pycache__"
              ".venv"
              ".vs"
              ".idea"
              ".gradle"
              ".fsharpened" ]

    let maxDepth = 12
    let maxFileBytes = 5L * 1024L * 1024L

    let private segmentError (s: string) =
        if s = "" || s = "." || s = ".." then
            Some "not a valid path segment"
        elif s |> Seq.exists (fun c -> Char.IsControl c || "<>:\"|?*".IndexOf c >= 0) then
            Some "holds a character that is not allowed in a file name"
        elif s.EndsWith "." || s.EndsWith " " then
            Some "ends with a dot or a space"
        elif reserved.Contains((s.Split('.')[0]).ToUpperInvariant()) then
            Some "is a reserved name on Windows"
        else
            None

    /// A relative path (forward slashes) as its segments, or why it is refused.
    let segments (rel: string) : Result<string list, string> =
        if rel = "" then
            Ok []
        elif rel.Length > 400 || rel.Contains '\\' || rel.StartsWith "/" then
            Error "the path must be relative, use forward slashes and stay short"
        else
            let parts = rel.Split('/') |> Array.toList

            if parts.Length > maxDepth then
                Error(sprintf "the path is deeper than %d folders" maxDepth)
            else
                match
                    parts
                    |> List.tryPick (fun s -> segmentError s |> Option.map (fun e -> sprintf "“%s” %s" s e))
                with
                | Some e -> Error e
                | None -> Ok parts

    let private isLink (path: string) =
        try
            let attr = File.GetAttributes path
            attr.HasFlag FileAttributes.ReparsePoint
        with _ ->
            false

    /// The full path of `rel` inside `root`, refusing a link anywhere on the way (the target need not exist yet).
    let resolve (root: string) (rel: string) : Result<string, string> =
        match segments rel with
        | Error e -> Error e
        | Ok parts ->
            let rootFull = Path.GetFullPath root
            let mutable at = rootFull
            let mutable problem = None

            for p in parts do
                if problem.IsNone then
                    at <- Path.Combine(at, p)

                    if (File.Exists at || Directory.Exists at) && isLink at then
                        problem <- Some(sprintf "“%s” is a link; links are not followed" p)

            match problem with
            | Some e -> Error e
            | None -> Ok at

    let private relOf (root: string) (full: string) =
        Path.GetRelativePath(root, full).Replace('\\', '/')

    /// One level of a folder: folders first, then files, each alphabetical. Ignored folders are left out.
    let list (root: string) (rel: string) : Result<Entry list, string> =
        match resolve root rel with
        | Error e -> Error e
        | Ok dir ->
            try
                let rootFull = Path.GetFullPath root

                let dirs =
                    Directory.EnumerateDirectories dir
                    |> Seq.filter (fun d -> not (ignoredDirs.Contains(Path.GetFileName d)) && not (isLink d))
                    |> Seq.map (fun d ->
                        { Rel = relOf rootFull d
                          Name = Path.GetFileName d
                          IsDir = true
                          Size = 0L })
                    |> Seq.sortBy (fun e -> e.Name.ToLowerInvariant())

                let files =
                    Directory.EnumerateFiles dir
                    |> Seq.filter (fun f -> not (isLink f))
                    |> Seq.map (fun f ->
                        { Rel = relOf rootFull f
                          Name = Path.GetFileName f
                          IsDir = false
                          Size = (FileInfo f).Length })
                    |> Seq.sortBy (fun e -> e.Name.ToLowerInvariant())

                Ok(Seq.append dirs files |> Seq.toList)
            with e ->
                Error e.Message

    /// Every file under the root (for quick open and search), up to `limit`. The flag says the list was cut short.
    let allFiles (root: string) (limit: int) : Entry list * bool =
        let acc = List<Entry>()
        let rootFull = Path.GetFullPath root
        let mutable cut = false

        let rec walk (dir: string) (depth: int) =
            if not cut && depth <= maxDepth then
                try
                    for f in Directory.EnumerateFiles dir |> Seq.sortBy (fun x -> x.ToLowerInvariant()) do
                        if not cut then
                            if acc.Count >= limit then
                                cut <- true
                            elif not (isLink f) then
                                acc.Add
                                    { Rel = relOf rootFull f
                                      Name = Path.GetFileName f
                                      IsDir = false
                                      Size = (FileInfo f).Length }

                    for d in Directory.EnumerateDirectories dir |> Seq.sortBy (fun x -> x.ToLowerInvariant()) do
                        if not (ignoredDirs.Contains(Path.GetFileName d)) && not (isLink d) then
                            walk d (depth + 1)
                with _ ->
                    () // an unreadable folder is skipped

        walk rootFull 0
        List.ofSeq acc, cut

    /// The text of a file, or why it cannot be shown (binary, too large, unreadable). A byte-order mark is dropped.
    let readText (root: string) (rel: string) : Result<string, string> =
        match resolve root rel with
        | Error e -> Error e
        | Ok path ->
            try
                let info = FileInfo path

                if not info.Exists then
                    Error "the file does not exist"
                elif info.Length > maxFileBytes then
                    Error(sprintf "larger than %d MB" (maxFileBytes / 1048576L))
                else
                    let bytes = File.ReadAllBytes path
                    let head = bytes |> Array.truncate 8192

                    if head |> Array.contains 0uy then
                        Error "binary file"
                    else
                        let enc = UTF8Encoding(false, true)

                        try
                            let start =
                                if bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then
                                    3
                                else
                                    0

                            Ok(enc.GetString(bytes, start, bytes.Length - start))
                        with :? DecoderFallbackException ->
                            Error "not UTF-8 text"
            with e ->
                Error e.Message

    /// Write a file whole or not at all: to a temporary name beside it, then renamed over it.
    let writeText (root: string) (rel: string) (text: string) : Result<unit, string> =
        match resolve root rel with
        | Error e -> Error e
        | Ok path ->
            try
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                let temp = path + ".fsh-tmp"
                File.WriteAllText(temp, text, UTF8Encoding(false))
                File.Move(temp, path, true)
                Ok()
            with e ->
                Error e.Message

    let createFile (root: string) (rel: string) : Result<unit, string> =
        match resolve root rel with
        | Error e -> Error e
        | Ok path when File.Exists path || Directory.Exists path -> Error "something with that name already exists"
        | Ok _ -> writeText root rel ""

    let createFolder (root: string) (rel: string) : Result<unit, string> =
        match resolve root rel with
        | Error e -> Error e
        | Ok path when File.Exists path -> Error "a file with that name already exists"
        | Ok path ->
            try
                Directory.CreateDirectory path |> ignore
                Ok()
            with e ->
                Error e.Message

    /// Rename or move a file or folder inside the root.
    let move (root: string) (fromRel: string) (toRel: string) : Result<unit, string> =
        match resolve root fromRel, resolve root toRel with
        | Error e, _
        | _, Error e -> Error e
        | Ok a, Ok b ->
            try
                if not (File.Exists a || Directory.Exists a) then
                    Error "it does not exist"
                elif File.Exists b || Directory.Exists b then
                    Error "something with the new name already exists"
                else
                    Directory.CreateDirectory(Path.GetDirectoryName b) |> ignore

                    if Directory.Exists a then
                        Directory.Move(a, b)
                    else
                        File.Move(a, b)

                    Ok()
            with e ->
                Error e.Message

    /// Delete a file or a folder with everything in it. This is permanent; the caller asks first.
    let delete (root: string) (rel: string) : Result<unit, string> =
        if rel = "" then
            Error "the opened folder itself is not deleted from here"
        else
            match resolve root rel with
            | Error e -> Error e
            | Ok path ->
                try
                    if Directory.Exists path then
                        Directory.Delete(path, true)
                    elif File.Exists path then
                        File.Delete path

                    Ok()
                with e ->
                    Error e.Message
