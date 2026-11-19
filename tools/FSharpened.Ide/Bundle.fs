namespace FSharpened.Ide

open System.Text.RegularExpressions
open FSharpened.Ide.BundleKit

/// Several files of one language, made into the one source the gateway takes (ADR-0024).
module Bundler =
    /// What the IDE does with a project's files of this language, and why for the ones it cannot merge.
    let support (lang: Language) : MultiFile =
        match lang with
        | JavaScript -> Merged "import, export and require(\"./file\") are resolved; each file keeps its own scope"
        | TypeScript ->
            Merged "imported files are joined into one scope (types are kept); import and export are removed"
        | Python -> Merged "the files are embedded and import, from ... import and relative imports find them"
        | Rust -> Merged "`mod name;` is replaced by the text of name.rs or name/mod.rs"
        | Go -> Merged "the files of the entry's folder in the same package are merged into one file"
        | Java -> Merged "the classes the entry names are merged into one file; only the entry keeps `public`"
        | C -> Merged "#include \"file.h\" is replaced by the file, and file.c follows"
        | Cpp -> Merged "#include \"file.h\" is replaced by the file, and file.cpp follows"
        | FSharp ->
            Alone
                "F# files depend on their order in a project file, and modules would have to be nested and re-indented to share one source"
        | CSharp
        | VisualBasic ->
            Alone
                "usings, namespaces and partial types would have to be merged by a real parser, which the IDE does not have"
        | Dart
        | Ruby
        | Lua
        | Php
        | R
        | Haskell
        | OCaml -> Alone "merging these files is not implemented yet"

    let isMerged (lang: Language) =
        match support lang with
        | Merged _ -> true
        | Alone _ -> false

    /// The one source for running `path` with the files it depends on.
    let make (project: Project) (path: string) : Result<Bundle, BundleProblem list> =
        match Project.tryFind path project with
        | None -> Error [ problem path 0 "this file is not in the project" ]
        | Some entry ->
            match ProjectPath.languageOf entry.Path with
            | None -> Error [ problem path 0 "this is not a source file the IDE can run" ]
            | Some lang ->
                match lang with
                | JavaScript
                | TypeScript -> BundleJs.bundle project entry lang
                | Python -> BundlePython.bundle project entry
                | Rust -> BundleRust.bundle project entry
                | Go -> BundleGo.bundle project entry
                | Java -> BundleJava.bundle project entry
                | C
                | Cpp -> BundleC.bundle project entry lang
                | other ->
                    let more =
                        project.Files
                        |> List.filter (fun f ->
                            ProjectPath.languageOf f.Path = Some other
                            && ProjectPath.key f.Path <> ProjectPath.key entry.Path)

                    let notes =
                        if more.IsEmpty then
                            []
                        else
                            match support other with
                            | Alone why ->
                                [ "only "
                                  + entry.Path
                                  + " is sent; the other "
                                  + Language.displayName other
                                  + " files stay behind ("
                                  + why
                                  + ")" ]
                            | Merged _ -> []

                    Ok(alone other entry notes)

    /// The file and line a line of the bundle came from.
    let locate (b: Bundle) (line: int) : (string * int) option =
        b.Segments
        |> List.tryFind (fun s -> line >= s.Start && line < s.Start + s.Count)
        |> Option.map (fun s -> (s.Path, s.From + (line - s.Start)))

    /// Like `locate`, for a line that a run reported. A Python run reports lines of the files themselves and names the
    /// file in its traceback, so there the output is read to find which file the line is in.
    let locateIn (b: Bundle) (output: string) (line: int) : (string * int) option =
        if b.Language = Python && b.Segments.IsEmpty then
            let frames =
                [ for m in Regex.Matches(output, "File \"([^\"]+)\", line (\\d+)") |> Seq.cast<Match> ->
                      (m.Groups.[1].Value, int m.Groups.[2].Value) ]

            frames
            |> List.filter (fun (p, l) -> l = line && List.contains p b.Included)
            |> List.tryLast
            |> Option.orElse (Some(b.Entry, line))
        elif b.Segments.IsEmpty then
            None
        else
            locate b line

    /// "util.js:3: message" for each problem, as a person reads them.
    let describe (problems: BundleProblem list) : string list =
        problems
        |> List.map (fun p ->
            if p.Line > 0 then
                p.Path + ":" + string p.Line + ": " + p.Message
            else
                p.Path + ": " + p.Message)
