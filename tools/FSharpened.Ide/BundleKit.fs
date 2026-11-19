namespace FSharpened.Ide

open System.Text
open System.Text.RegularExpressions

/// Why a project cannot be turned into one source: the file, the line in it (0 when the whole file is at fault) and
/// what to do about it.
type BundleProblem =
    { Path: string
      Line: int
      Message: string }

/// Lines `Start` .. `Start + Count - 1` of the bundle are lines `From` .. of the file `Path` (all 1-based).
type Segment =
    { Path: string
      Start: int
      Count: int
      From: int }

/// One source made from a project's files (ADR-0024): the text to send, which files went into it, and the map from its
/// lines back to the files so a compiler's line numbers can be shown where the code really is.
type Bundle =
    { Language: Language
      Entry: string
      Source: string
      Included: string list
      Segments: Segment list
      Notes: string list }

/// What the IDE can do with several files of one language.
type MultiFile =
    /// The files are merged into one source; the text says how.
    | Merged of string
    /// Only the file that runs is sent; the text says why.
    | Alone of string

/// The shared parts of the per-language bundlers: how a bundle is assembled from pieces of files, and how paths inside a
/// project are resolved. Everything here is pure text work, so it runs the same on .NET and in the browser.
module BundleKit =
    /// A run of lines from one file (`Path` = "" for lines the bundler wrote itself).
    type Piece =
        { Path: string
          From: int
          Lines: string list }

    let generated (lines: string list) : Piece = { Path = ""; From = 0; Lines = lines }

    /// The lines of a text, the empty one after a final line break left out.
    let bodyLines (text: string) : string list =
        let ls = Scan.lines text

        match List.rev ls with
        | "" :: rest -> List.rev rest
        | _ -> ls

    let fromFile (path: string) (lines: string list) : Piece =
        { Path = path; From = 1; Lines = lines }

    let problem (path: string) (line: int) (message: string) : BundleProblem =
        { Path = path
          Line = line
          Message = message }

    /// The pieces joined into one text, and which lines came from where.
    let assemble (pieces: Piece list) : string * Segment list =
        let all = pieces |> List.collect (fun p -> p.Lines)

        let _, segments =
            pieces
            |> List.fold
                (fun (next, acc) p ->
                    let count = p.Lines.Length

                    if p.Path = "" || count = 0 then
                        (next + count, acc)
                    else
                        let seg: Segment =
                            { Path = p.Path
                              Start = next
                              Count = count
                              From = p.From }

                        (next + count, seg :: acc))
                (1, [])

        (String.concat "\n" all + "\n", List.rev segments)

    let private limitMessage (chars: int) =
        "the bundle is "
        + string chars
        + " characters; the gateway takes at most "
        + string Project.maxFileChars
        + " per run. Make the files smaller or run them one at a time."

    /// Finishes a bundle, or refuses it when the one source would be over what the gateway accepts.
    let finish
        (lang: Language)
        (entry: string)
        (pieces: Piece list)
        (included: string list)
        (notes: string list)
        : Result<Bundle, BundleProblem list> =
        let source, segments = assemble pieces

        if source.Length > Project.maxFileChars then
            Error [ problem entry 0 (limitMessage source.Length) ]
        else
            Ok
                { Language = lang
                  Entry = entry
                  Source = source
                  Included = included
                  Segments = segments
                  Notes = notes }

    /// The file on its own, byte for byte: what a project of one file, or a file that needs no others, sends.
    let alone (lang: Language) (file: ProjectFile) (notes: string list) : Bundle =
        { Language = lang
          Entry = file.Path
          Source = file.Text
          Included = [ file.Path ]
          Segments =
            [ { Path = file.Path
                Start = 1
                Count = (Scan.lines file.Text).Length
                From = 1 } ]
          Notes = notes }

    /// Files by lower-case path.
    let index (project: Project) : Map<string, ProjectFile> =
        project.Files |> List.map (fun f -> ProjectPath.key f.Path, f) |> Map.ofList

    let find (files: Map<string, ProjectFile>) (path: string) : ProjectFile option =
        Map.tryFind (ProjectPath.key path) files

    /// `rel` read from inside folder `dir`, with "." and ".." resolved; None when it climbs out of the project.
    let resolveRel (dir: string) (rel: string) : string option =
        let start =
            if rel.StartsWith "/" then
                []
            else
                dir.Split('/') |> Array.filter (fun s -> s <> "") |> Array.toList |> List.rev

        let folded =
            rel.Split('/')
            |> Array.fold
                (fun acc seg ->
                    match acc with
                    | None -> None
                    | Some stack ->
                        if seg = "" || seg = "." then
                            Some stack
                        elif seg = ".." then
                            (match stack with
                             | [] -> None
                             | _ :: rest -> Some rest)
                        else
                            Some(seg :: stack))
                (Some start)

        folded |> Option.map (fun s -> s |> List.rev |> String.concat "/")

    /// A 1-based line number for a character position.
    let lineOf (text: string) (position: int) : int =
        let mutable n = 1

        for k in 0 .. (min position text.Length) - 1 do
            if text.[k] = '\n' then
                n <- n + 1

        n

    /// Count of line breaks in a text.
    let breaks (text: string) : int =
        let mutable n = 0

        for ch in text do
            if ch = '\n' then
                n <- n + 1

        n

    /// Replaces ranges of a text (start, end, replacement); the ranges must not overlap. A replacement keeps the line
    /// count of what it replaces, by adding line breaks after it, so line numbers further down do not move.
    let applyEdits (text: string) (edits: (int * int * string) list) : string =
        let sb = StringBuilder()
        let mutable at = 0

        for (s, e, r) in List.sortBy (fun (s, _, _) -> s) edits do
            if s >= at then
                sb.Append(text.Substring(at, s - at)) |> ignore
                let removed = text.Substring(s, e - s)
                let had = breaks removed
                let have = breaks r
                sb.Append r |> ignore

                if had > have then
                    sb.Append(String.replicate (had - have) "\n") |> ignore

                at <- e

        sb.Append(text.Substring at) |> ignore
        sb.ToString()

    /// Matches `pattern` at the very start of `text` from `start`.
    let matchAt (pattern: Regex) (text: string) (start: int) : Match = pattern.Match(text.Substring start)

    /// A double-quoted string literal for the bundle's own code. JSON escapes read the same in JavaScript, TypeScript and
    /// Python, and keep the line breaks of an embedded file as backslash-n so the literal stays on one line.
    let quote (s: string) : string =
        let sb = StringBuilder()
        sb.Append '"' |> ignore

        for c in s do
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when int c < 0x20 -> sb.Append("\\u" + (int c).ToString("x4")) |> ignore
            | c -> sb.Append c |> ignore

        sb.Append '"' |> ignore
        sb.ToString()
