namespace FSharpened.Ide.Host

open System
open System.IO
open System.Text

/// What a rename would change in one file, for the preview and the confirmation.
type FileChange =
    {
        /// Absolute path, as the server named it.
        Path: string
        /// How many places change.
        Count: int
        /// The file's text before and after.
        Before: string
        After: string
        /// The edits as (start offset, end offset, new text) in the text before, in order: what an editor applies, last
        /// one first, to change the file in place and keep Undo, the caret and the breakpoints working.
        Spans: (int * int * string) list
    }

/// Applying the edits of a language server to text. Pure: nothing here reads or writes a file, so every rule is tested
/// without a server (ADR-0023). Positions follow the protocol: 0-based lines, 0-based UTF-16 characters (which is what
/// a .NET string counts).
module Edits =
    /// The character offset of the start of each line. Lines end at \n, \r\n or a lone \r.
    let lineStarts (text: string) : int[] =
        let starts = Collections.Generic.List<int>()
        starts.Add 0
        let mutable i = 0

        while i < text.Length do
            match text[i] with
            | '\n' -> starts.Add(i + 1)
            | '\r' ->
                if i + 1 < text.Length && text[i + 1] = '\n' then
                    i <- i + 1

                starts.Add(i + 1)
            | _ -> ()

            i <- i + 1

        starts.ToArray()

    /// The offset of a position. A character past the end of its line means the end of the line (the protocol says so);
    /// a line past the end of the text is an error, except the line just after a final newline, which is the end.
    let offsetOf (text: string) (starts: int[]) (p: LspPos) : Result<int, string> =
        if p.Line < 0 || p.Character < 0 then
            Error(sprintf "the position %d:%d is negative" p.Line p.Character)
        elif p.Line >= starts.Length then
            Error(sprintf "line %d is beyond the end of the file (%d lines)" (p.Line + 1) starts.Length)
        else
            let lineStart = starts[p.Line]

            // the end of the line's text, before its line break
            let mutable lineEnd =
                if p.Line + 1 < starts.Length then
                    starts[p.Line + 1]
                else
                    text.Length

            while lineEnd > lineStart && (text[lineEnd - 1] = '\n' || text[lineEnd - 1] = '\r') do
                lineEnd <- lineEnd - 1

            Ok(min lineEnd (lineStart + p.Character))

    /// Checks edits against a text and puts them in order: (start, end, new text) by offset, ascending.
    /// Refused when a range runs backwards, lies outside the text, or two ranges overlap. Several insertions at the same
    /// position keep the order they were given in, as the protocol requires.
    let spans (text: string) (edits: TextEdit list) : Result<(int * int * string) list, string> =
        let starts = lineStarts text

        let resolved =
            edits
            |> List.mapi (fun i e ->
                match offsetOf text starts e.Range.Start, offsetOf text starts e.Range.End with
                | Ok a, Ok b when a <= b -> Ok(a, b, i, e.NewText)
                | Ok _, Ok _ ->
                    Error(
                        sprintf
                            "an edit at %d:%d ends before it starts"
                            (e.Range.Start.Line + 1)
                            (e.Range.Start.Character + 1)
                    )
                | Error m, _
                | _, Error m -> Error m)

        match
            resolved
            |> List.tryPick (function
                | Error m -> Some m
                | _ -> None)
        with
        | Some m -> Error m
        | None ->
            let items =
                resolved
                |> List.choose (function
                    | Ok x -> Some x
                    | _ -> None)
                |> List.sortBy (fun (a, b, i, _) -> a, b, i)

            let overlap =
                items
                |> List.pairwise
                |> List.tryFind (fun ((_, b1, _, _), (a2, _, _, _)) -> a2 < b1)

            match overlap with
            | Some((a1, _, _, _), _) ->
                let line = (starts |> Array.filter (fun s -> s <= a1) |> Array.length)
                Error(sprintf "two edits overlap near line %d, so none was applied" line)
            | None -> Ok(items |> List.map (fun (a, b, _, n) -> a, b, n))

    /// Applies edits to a text: they are checked first (see `spans`), then applied from the last to the first so no
    /// offset moves. Nothing is applied when any edit is refused.
    let apply (text: string) (edits: TextEdit list) : Result<string, string> =
        spans text edits
        |> Result.map (fun items ->
            let sb = StringBuilder(text)

            for (a, b, n) in List.rev items do
                sb.Remove(a, b - a).Insert(a, n) |> ignore

            sb.ToString())

    /// The lines that differ between two texts of the same number of lines, as (1-based line, before, after); at most
    /// 200. A rename keeps the line count; when a file's does not, the preview shows only the counts.
    let changedLines (before: string) (after: string) : (int * string * string) list =
        let split (t: string) =
            t.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')

        let a = split before
        let b = split after

        if a.Length <> b.Length then
            []
        else
            [ for i in 0 .. a.Length - 1 do
                  if a[i] <> b[i] then
                      yield i + 1, a[i].Trim(), b[i].Trim() ]
            |> List.truncate 200

    /// Computes the new text of every file of a workspace edit, or refuses the whole edit. Nothing is written: the caller
    /// shows the preview, then writes. `read` gives a file's current text (the editor's, when it is open).
    let plan (read: string -> Result<string, string>) (edit: WorkspaceEdit) : Result<FileChange list, string> =
        if not edit.Unsupported.IsEmpty then
            Error(
                sprintf
                    "the server asked to %s, which the rename does not support yet; nothing was changed"
                    (String.Join(", ", edit.Unsupported))
            )
        else
            let results =
                edit.Files
                |> List.filter (fun (_, es) -> not es.IsEmpty)
                |> List.map (fun (path, es) ->
                    match read path with
                    | Error m -> Error(sprintf "%s: %s" (Path.GetFileName path) m)
                    | Ok before ->
                        match spans before es with
                        | Error m -> Error(sprintf "%s: %s" (Path.GetFileName path) m)
                        | Ok sp ->
                            let sb = StringBuilder(before)

                            for (a, b, n) in List.rev sp do
                                sb.Remove(a, b - a).Insert(a, n) |> ignore

                            Ok
                                { Path = path
                                  Count = es.Length
                                  Before = before
                                  After = sb.ToString()
                                  Spans = sp })

            match
                results
                |> List.tryPick (function
                    | Error m -> Some m
                    | _ -> None)
            with
            | Some m -> Error m
            | None ->
                Ok(
                    results
                    |> List.choose (function
                        | Ok c -> Some c
                        | _ -> None)
                )

    /// "3 places in 2 files" for the confirmation.
    let summary (changes: FileChange list) : string =
        let n = changes |> List.sumBy (fun c -> c.Count)

        sprintf
            "%d place%s in %d file%s"
            n
            (if n = 1 then "" else "s")
            changes.Length
            (if changes.Length = 1 then "" else "s")

    /// A name a rename may be given: not empty, no line break. Whether it is valid in the language is the server's call.
    let checkName (name: string) : string =
        if String.IsNullOrWhiteSpace name then
            "the new name is empty"
        elif name.Contains '\n' || name.Contains '\r' then
            "the new name has a line break"
        elif name.Trim() <> name then
            "the new name starts or ends with a space"
        else
            ""
