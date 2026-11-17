/// What the hover box says: the problems under the pointer, then whatever the language server reports. Kept free of
/// controls so it can be tested (ADR-0023).
module FSharpened.Ide.Desktop.Hover

open FSharpened.Diagnostics

/// The most text one box shows; a server can send a whole manual page.
let maxChars = 1200

/// Whether a 1-based line and column are inside a range. A zero-width range (a position) covers its own column.
let covers (r: SourceRange) (line: int) (col: int) : bool =
    let afterStart = line > r.StartLine || (line = r.StartLine && col >= r.StartColumn)

    let beforeEnd =
        line < r.EndLine
        || (line = r.EndLine && col < r.EndColumn)
        || (line = r.EndLine
            && r.StartLine = r.EndLine
            && r.StartColumn = r.EndColumn
            && col = r.EndColumn)

    afterStart && beforeEnd

let private severityName (s: Severity) =
    match s with
    | Severity.Error -> "error"
    | Severity.Warning -> "warning"
    | _ -> "note"

/// One line per problem that covers the position, worst first.
let problemsAt (list: StructuredDiagnostic list) (line: int) (col: int) : string list =
    list
    |> List.filter (fun d ->
        match d.Range with
        | Some r -> covers r line col
        | None -> false)
    |> List.sortBy (fun d ->
        match d.Severity with
        | Severity.Error -> 0
        | Severity.Warning -> 1
        | _ -> 2)
    |> List.map (fun d ->
        let code =
            if System.String.IsNullOrWhiteSpace d.Code then
                ""
            else
                " " + d.Code

        sprintf "%s%s: %s" (severityName d.Severity) code (d.Message.Trim()))

/// The box's text, or None when there is nothing to say. Markdown fences are dropped; the text is shown as it is.
let compose (problems: string list) (server: string option) : string option =
    let hover =
        match server with
        | Some s ->
            s.Replace("\r", "").Split('\n')
            |> Array.filter (fun l -> not (l.TrimStart().StartsWith "```"))
            |> String.concat "\n"
            |> fun t -> t.Trim()
        | None -> ""

    let parts = problems @ (if hover = "" then [] else [ hover ])

    if parts.IsEmpty then
        None
    else
        let text = String.concat "\n\n" parts

        Some(
            if text.Length > maxChars then
                text.Substring(0, maxChars) + "…"
            else
                text
        )
