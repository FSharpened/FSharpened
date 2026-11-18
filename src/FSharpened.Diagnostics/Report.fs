namespace FSharpened.Diagnostics

open System.Text

/// A replacement the tooling can apply for a diagnostic. Ranges use the `SourceRange` convention (see `DiagnosticReport`).
type TextEdit = { Range: SourceRange; NewText: string }

/// A diagnostic plus the edits that fix it (empty when it has no mechanical fix; `SuggestedFix` is then advice only).
type Finding =
    { Diagnostic: StructuredDiagnostic
      Edits: TextEdit list }

/// The stable, machine-readable report format that `fsh analyze --format json` prints and editors parse
/// (schema id `fsharpened.diagnostics/1`; documented in docs/operations/vscode.md).
///
/// Ranges: lines are 1-based, columns are 0-based UTF-16 code units, the end position is exclusive (the F# compiler
/// service convention). Written by hand, with no reflection, so it runs under NativeAOT.
module DiagnosticReport =
    [<Literal>]
    let Schema = "fsharpened.diagnostics/1"

    let severityName s =
        match s with
        | Hidden -> "hidden"
        | Info -> "info"
        | Warning -> "warning"
        | Error -> "error"

    let private escape (sb: StringBuilder) (s: string) =
        sb.Append '"' |> ignore

        for c in s do
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.Append("\\u").Append((int c).ToString("x4")) |> ignore
            | c -> sb.Append c |> ignore

        sb.Append '"' |> ignore

    let private str sb s = escape sb s

    let private range (sb: StringBuilder) (r: SourceRange) =
        sb
            .Append("{\"startLine\":")
            .Append(r.StartLine)
            .Append(",\"startColumn\":")
            .Append(r.StartColumn)
            .Append(",\"endLine\":")
            .Append(r.EndLine)
            .Append(",\"endColumn\":")
            .Append(r.EndColumn)
            .Append('}')
        |> ignore

    let private optional (sb: StringBuilder) (name: string) (v: string option) =
        sb.Append(",\"").Append(name).Append("\":") |> ignore

        match v with
        | Some s -> str sb s
        | None -> sb.Append "null" |> ignore

    let private finding (sb: StringBuilder) (f: Finding) =
        let d = f.Diagnostic
        sb.Append "{\"code\":" |> ignore
        str sb d.Code

        sb.Append(",\"severity\":\"").Append(severityName d.Severity).Append('"')
        |> ignore

        sb.Append(",\"file\":") |> ignore

        match d.Range with
        | Some r -> str sb r.File
        | None -> sb.Append "null" |> ignore

        sb.Append(",\"range\":") |> ignore

        match d.Range with
        | Some r -> range sb r
        | None -> sb.Append "null" |> ignore

        sb.Append(",\"message\":") |> ignore
        str sb d.Message
        optional sb "construct" d.OffendingConstruct
        optional sb "docs" d.DocsUrl
        sb.Append(",\"fix\":") |> ignore

        match d.SuggestedFix with
        | None -> sb.Append "null" |> ignore
        | Some title ->
            sb.Append "{\"title\":" |> ignore
            str sb title
            sb.Append ",\"edits\":[" |> ignore

            f.Edits
            |> List.iteri (fun i e ->
                if i > 0 then
                    sb.Append ',' |> ignore

                sb.Append "{\"range\":" |> ignore
                range sb e.Range
                sb.Append(",\"newText\":") |> ignore
                str sb e.NewText
                sb.Append '}' |> ignore)

            sb.Append "]}" |> ignore

        sb.Append ",\"context\":{" |> ignore

        d.Context
        |> Map.toList
        |> List.iteri (fun i (k, v) ->
            if i > 0 then
                sb.Append ',' |> ignore

            str sb k
            sb.Append ':' |> ignore
            str sb v)

        sb.Append "}}" |> ignore

    /// The whole report as one JSON document ending in a newline. `tool` names the producer (for example "fsh 0.0.1").
    let toJson (tool: string) (findings: Finding list) : string =
        let sb = StringBuilder()
        sb.Append("{\"schema\":\"").Append(Schema).Append("\",\"tool\":") |> ignore
        str sb tool
        sb.Append ",\"diagnostics\":[" |> ignore

        findings
        |> List.iteri (fun i f ->
            if i > 0 then
                sb.Append ',' |> ignore

            finding sb f)

        sb.Append "]}\n" |> ignore
        sb.ToString()

    /// One line per diagnostic in the usual `file(line,col): severity CODE: message` shape, for terminals and CI logs.
    let toText (findings: Finding list) : string =
        let sb = StringBuilder()

        for f in findings do
            let d = f.Diagnostic

            match d.Range with
            | Some r ->
                sb.Append(r.File).Append('(').Append(r.StartLine).Append(',').Append(r.StartColumn + 1).Append("): ")
                |> ignore
            | None -> ()

            sb.Append(severityName d.Severity).Append(' ').Append(d.Code).Append(": ").Append(d.Message).Append('\n')
            |> ignore

        sb.ToString()
