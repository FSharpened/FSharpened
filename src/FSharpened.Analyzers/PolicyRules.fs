namespace FSharpened.Analyzers

open System
open FSharpened.Diagnostics

/// Repository-policy checks that work on text alone (no compiler service), so `fsh analyze` has real diagnostics to
/// report while the FSharp.Analyzers.SDK rule set is built. They are intentionally few and conservative: a line
/// comment is skipped, block comments and multi-line strings are not understood. The curated analyzers replace this.
module PolicyRules =
    [<Literal>]
    let DocsBase =
        "https://github.com/FSharpened/FSharpened/blob/main/docs/packages/Diagnostics.md#"

    let private platformSymbols =
        set [ "WINDOWS"; "LINUX"; "OSX"; "MACOS"; "MACOSX"; "FREEBSD" ]

    let private isIdent (c: char) =
        Char.IsLetterOrDigit c || c = '_' || c = '\''

    let private withDocs code (d: StructuredDiagnostic) =
        { d with
            DocsUrl = Some(DocsBase + code.ToString().ToLowerInvariant()) }

    let private range file line startCol endCol =
        { File = file
          StartLine = line
          StartColumn = startCol
          EndLine = line
          EndColumn = endCol }

    /// FSH0001: `#if WINDOWS` and friends. Platform behaviour lives in FSharpened.Platform (AGENTS.md, hard rules).
    let private platformConditional (file: string) (lineNo: int) (line: string) : Finding list =
        let trimmed = line.TrimStart()

        if trimmed.StartsWith "#if " || trimmed.StartsWith "#elif " then
            let indent = line.Length - trimmed.Length
            let condition = trimmed.Substring(trimmed.IndexOf ' ' + 1)

            let tokens =
                condition.Split([| ' '; '!'; '('; ')'; '|'; '&'; '\t' |], StringSplitOptions.RemoveEmptyEntries)

            match tokens |> Array.tryFind (fun t -> platformSymbols.Contains t) with
            | Some symbol ->
                [ { Diagnostic =
                      { StructuredDiagnostic.create
                            "FSH0001"
                            Warning
                            ("Conditional compilation on a platform ("
                             + symbol
                             + ") is not allowed in product code; platform behaviour belongs in FSharpened.Platform.") with
                          Range = Some(range file lineNo indent (line.TrimEnd().Length))
                          OffendingConstruct = Some(trimmed.TrimEnd())
                          SuggestedFix =
                              Some
                                  "Move the platform-specific code behind a FSharpened.Platform seam and call that instead." }
                      |> withDocs "FSH0001"
                    Edits = [] } ]
            | None -> []
        else
            []

    let private printfFamily = [ "printfn"; "printf"; "eprintfn"; "eprintf" ]

    /// FSH0002: `printfn "literal"` with no format specifiers. printf-family functions use reflection that NativeAOT
    /// cannot compile (ADR-0014, gap K); a plain console write does the same job. The fix is a mechanical edit.
    let private printfOfLiteral (file: string) (lineNo: int) (line: string) : Finding list =
        if line.TrimStart().StartsWith "//" then
            []
        else
            [ for name in printfFamily do
                  let mutable from = 0
                  let mutable go = true

                  while go do
                      let i = line.IndexOf(name + " \"", from, StringComparison.Ordinal)

                      if i < 0 then
                          go <- false
                      else
                          from <- i + name.Length
                          let before = if i = 0 then ' ' else line.[i - 1]
                          let open_ = i + name.Length + 1 // the opening quote
                          let close = line.IndexOf('"', open_ + 1)

                          let literal =
                              if close < 0 then
                                  None
                              else
                                  Some(line.Substring(open_ + 1, close - open_ - 1))

                          match literal with
                          | Some lit when
                              not (isIdent before)
                              && before <> '.'
                              && not (lit.Contains "%")
                              && not (lit.Contains "\\")
                              ->
                              let target =
                                  match name with
                                  | "printfn" -> "System.Console.Out.WriteLine"
                                  | "printf" -> "System.Console.Out.Write"
                                  | "eprintfn" -> "System.Console.Error.WriteLine"
                                  | _ -> "System.Console.Error.Write"

                              yield
                                  { Diagnostic =
                                      { StructuredDiagnostic.create
                                            "FSH0002"
                                            Info
                                            (name
                                             + " uses reflection that NativeAOT cannot compile; write the literal with System.Console instead.") with
                                          Range = Some(range file lineNo i (i + name.Length))
                                          OffendingConstruct = Some name
                                          SuggestedFix = Some("Replace " + name + " with " + target) }
                                      |> withDocs "FSH0002"
                                    Edits =
                                      [ { Range = range file lineNo i (i + name.Length)
                                          NewText = target } ] }
                          | _ -> () ]

    /// Every finding for one file's text, in line order. `file` is reported as given.
    let check (file: string) (text: string) : Finding list =
        text.Replace("\r\n", "\n").Split '\n'
        |> Array.mapi (fun i line -> platformConditional file (i + 1) line @ printfOfLiteral file (i + 1) line)
        |> List.concat
