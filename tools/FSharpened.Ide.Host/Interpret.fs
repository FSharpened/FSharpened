namespace FSharpened.Ide.Host

open System.Text.RegularExpressions
open FSharpened.Diagnostics
open FSharpened.Ide

/// Turns what a local program printed into the same report the website shows. Languages the F# core knows (the
/// website's eighteen) use its own parsers; every other language gets the generic ones, which read the two shapes
/// almost every compiler uses (`file:line:col: error: text` and `file(line,col): error CODE: text`).
module Interpret =
    let private colonStyle =
        Regex(
            "^(.+?):(\\d+):(?:(\\d+):)?\\s*(fatal error|error|warning|note|info)\\b:?\\s*(.*)$",
            RegexOptions.Compiled ||| RegexOptions.IgnoreCase
        )

    let private parenStyle =
        Regex(
            "^(.+?)\\((\\d+)(?:,(\\d+))?\\)\\s*:\\s*(error|warning|info)\\s*([A-Za-z]*\\d*)\\s*:?\\s*(.*)$",
            RegexOptions.Compiled ||| RegexOptions.IgnoreCase
        )

    let private severity (s: string) =
        match s.ToLowerInvariant() with
        | "warning" -> Warning
        | "note"
        | "info" -> Severity.Info
        | _ -> Severity.Error

    let private at (file: string) (line: int) (col: int) =
        Some
            { File = file
              StartLine = line
              StartColumn = max 1 col
              EndLine = line
              EndColumn = max 1 col }

    let private group (g: Group) = if g.Success then int g.Value else 1

    /// Diagnostics found in compiler or runtime output, for any language.
    let genericDiagnostics (output: string) : StructuredDiagnostic list =
        output.Replace("\r\n", "\n").Split('\n')
        |> Array.choose (fun raw ->
            let line = raw.Trim()

            match colonStyle.Match line, parenStyle.Match line with
            | m, _ when m.Success ->
                let d =
                    StructuredDiagnostic.create "" (severity m.Groups[4].Value) (m.Groups[5].Value.Trim())

                Some
                    { d with
                        Range = at (m.Groups[1].Value) (int m.Groups[2].Value) (group m.Groups[3]) }
            | _, m when m.Success ->
                let d =
                    StructuredDiagnostic.create
                        m.Groups[5].Value
                        (severity m.Groups[4].Value)
                        (m.Groups[6].Value.Trim())

                Some
                    { d with
                        Range = at (m.Groups[1].Value) (int m.Groups[2].Value) (group m.Groups[3]) }
            | _ -> None)
        |> Array.truncate 200
        |> Array.toList

    let private generic (raw: RawResult) : Report =
        if raw.BuildFailed then
            let diags = genericDiagnostics raw.BuildOutput

            { Ok = false
              Phase = Phase.Build
              Summary = sprintf "Build failed · %s" raw.Engine
              Diagnostics = diags
              Stdout = ""
              Stderr = raw.BuildOutput
              ExitCode = raw.ExitCode }
        elif raw.TimedOut then
            { Ok = false
              Phase = Phase.Run
              Summary = sprintf "Timed out after %.1f s · %s" (float raw.DurationMs / 1000.0) raw.Engine
              Diagnostics = []
              Stdout = raw.Stdout
              Stderr = raw.Stderr
              ExitCode = raw.ExitCode }
        else
            let diags =
                if raw.ExitCode <> 0 then
                    genericDiagnostics raw.Stderr
                else
                    []

            { Ok = raw.ExitCode = 0
              Phase = Phase.Run
              Summary =
                sprintf
                    "%s · exit %d · %d ms · %s"
                    (if raw.ExitCode = 0 then "Finished" else "Failed")
                    raw.ExitCode
                    raw.DurationMs
                    raw.Engine
              Diagnostics = diags
              Stdout = raw.Stdout
              Stderr = raw.Stderr
              ExitCode = raw.ExitCode }

    /// The report for a finished run of the language with this pack id.
    let report (packId: string) (raw: RawResult) : Report =
        match Language.tryOfId packId with
        | Some lang -> Protocol.interpret lang raw
        | None -> generic raw
