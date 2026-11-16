namespace FSharpened.Ide

open System.Text.RegularExpressions
open FSharpened.Diagnostics

/// Turns raw compiler text (gcc, clang, csc, fsc) into structured diagnostics, and runs offline symbolic checks.
module Problems =
    /// Removes terminal colour codes only.
    let stripAnsi (s: string) =
        Regex.Replace(s, "\u001b\\[[0-9;?]*[A-Za-z]", "")

    /// Text for people: colour codes removed, and the F# compiler's 0x1D group separators (it joins the parts of a
    /// multi-part message such as FS0001 on one line with them) turned into line breaks.
    let readable (s: string) = (stripAnsi s).Replace("\u001d", "\n")

    /// Text for the parser: one physical line stays one line, so the separators collapse to single spaces and
    /// "expected type 'int' but here has type 'string'" is one message instead of being cut at the first part.
    let private forParsing (raw: string) =
        let s = stripAnsi raw

        if s.Contains "\u001d" then
            Regex.Replace(s.Replace("\u001d", " "), "\\s+", " ").Trim()
        else
            s.TrimEnd()

    let private severityOf (s: string) =
        match s.Trim().ToLowerInvariant() with
        | "error"
        | "fatal error" -> Severity.Error
        | "warning" -> Severity.Warning
        | "note"
        | "info"
        | "message" -> Severity.Info
        | _ -> Severity.Hidden

    let private range (file: string) l c l2 c2 =
        Some
            { File = file
              StartLine = l
              StartColumn = c
              EndLine = l2
              EndColumn = c2 }

    let private gccLike =
        Regex("^(?:[^:\\s][^:]*):(\\d+):(?:(\\d+):)?\\s*(fatal error|error|warning|note):\\s*(.*)$")

    let private dotnetLike =
        Regex("^(.*?)\\((\\d+),(\\d+)(?:,(\\d+),(\\d+))?\\):\\s*(error|warning|info)\\s+([A-Za-z]+\\d+):\\s*(.*)$")

    let private bare = Regex("^(?:.*?:\\s*)?(fatal error|error):\\s*(.*)$")
    let private projectSuffix = Regex("\\s*\\[[^\\]]*\\.(?:csproj|fsproj)\\]\\s*$")
    let private undefinedRef = Regex("undefined reference to")

    let private toInt (g: Group) = int g.Value

    /// One output line to at most one diagnostic.
    let parseLine (lang: Language) (raw: string) : StructuredDiagnostic option =
        let line = forParsing raw
        let file = Language.fileName lang
        let dm = dotnetLike.Match line

        if dm.Success then
            let l = toInt dm.Groups.[2]
            let c = toInt dm.Groups.[3]
            let l2 = if dm.Groups.[4].Success then toInt dm.Groups.[4] else l
            let c2 = if dm.Groups.[5].Success then toInt dm.Groups.[5] else c

            let d =
                { StructuredDiagnostic.create
                      dm.Groups.[7].Value
                      (severityOf dm.Groups.[6].Value)
                      (projectSuffix.Replace(dm.Groups.[8].Value, "")) with
                    Range = range file l c l2 c2 }

            Some(StructuredDiagnostic.withContext "tool" (if lang = FSharp then "fsc" else "csc") d)
        else
            let gm = gccLike.Match line

            if gm.Success then
                let l = toInt gm.Groups.[1]
                let c = if gm.Groups.[2].Success then toInt gm.Groups.[2] else 1

                let d =
                    { StructuredDiagnostic.create "cc" (severityOf gm.Groups.[3].Value) gm.Groups.[4].Value with
                        Range = range file l c l (c + 1) }

                Some(StructuredDiagnostic.withContext "tool" "cc" d)
            elif undefinedRef.IsMatch line then
                Some(StructuredDiagnostic.create "link" Severity.Error line)
            else
                let bm = bare.Match line

                if bm.Success then
                    Some(StructuredDiagnostic.create "error" Severity.Error bm.Groups.[2].Value)
                else
                    None

    let parse (lang: Language) (output: string) : StructuredDiagnostic list =
        output.Replace("\r\n", "\n").Split('\n')
        |> Array.choose (parseLine lang)
        |> Array.toList
        |> List.distinct

    let countOf (sev: Severity) (ds: StructuredDiagnostic list) =
        ds |> List.filter (fun d -> d.Severity = sev) |> List.length

    // ---- Layer 3 of the fallback chain: symbolic checks that need no compiler ----------------------
    let private pairOf =
        function
        | ')' -> Some '('
        | ']' -> Some '['
        | '}' -> Some '{'
        | _ -> None

    /// Bracket balance, ignoring strings, chars and comments (approximation per language).
    let bracketCheck (lang: Language) (text: string) : StructuredDiagnostic list =
        let file = Language.fileName lang
        let stack = ResizeArray<char * int * int>()
        let problems = ResizeArray<StructuredDiagnostic>()
        let mutable line = 1
        let mutable col = 0
        let mutable i = 0
        let n = text.Length
        let at k = if k < n then text.[k] else '\000'

        let add msg l c =
            problems.Add
                { StructuredDiagnostic.create "local-brackets" Severity.Error msg with
                    Range = range file l c l (c + 1) }

        let advance () =
            if at i = '\n' then
                line <- line + 1
                col <- 0
            else
                col <- col + 1

            i <- i + 1

        while i < n do
            let ch = at i

            if ch = '/' && at (i + 1) = '/' then
                while i < n && at i <> '\n' do
                    advance ()
            elif ch = '/' && at (i + 1) = '*' then
                advance ()
                advance ()

                while i < n && not (at i = '*' && at (i + 1) = '/') do
                    advance ()

                advance ()
                advance ()
            elif lang = FSharp && ch = '(' && at (i + 1) = '*' && at (i + 2) <> ')' then
                advance ()
                advance ()

                while i < n && not (at i = '*' && at (i + 1) = ')') do
                    advance ()

                advance ()
                advance ()
            elif ch = '"' then
                advance ()

                while i < n && at i <> '"' && at i <> '\n' do
                    if at i = '\\' then
                        advance ()

                    advance ()

                advance ()
            elif ch = '\'' && lang <> FSharp then
                advance ()

                while i < n && at i <> '\'' && at i <> '\n' do
                    if at i = '\\' then
                        advance ()

                    advance ()

                advance ()
            else
                match ch with
                | '('
                | '['
                | '{' -> stack.Add((ch, line, col + 1))
                | ')'
                | ']'
                | '}' ->
                    if stack.Count = 0 then
                        add ("Unmatched '" + string ch + "'") line (col + 1)
                    else
                        let (open', _, _) = stack.[stack.Count - 1]

                        if Some open' = pairOf ch then
                            stack.RemoveAt(stack.Count - 1)
                        else
                            add ("Mismatched '" + string ch + "'") line (col + 1)
                | _ -> ()

                advance ()

        for (c, l, k) in Seq.rev stack do
            add ("Unclosed '" + string c + "'") l k

        List.ofSeq problems

    let private hasMain (text: string) = Regex.IsMatch(text, "\\bmain\\s*\\(")

    let localChecks (lang: Language) (text: string) : StructuredDiagnostic list =
        let structural = bracketCheck lang text

        let entry =
            match lang with
            | C
            | Cpp when not (hasMain text) ->
                [ { StructuredDiagnostic.create "local-entry" Severity.Warning "No main function found" with
                      SuggestedFix = Some "Add: int main(void) { return 0; }" } ]
            | _ -> []

        structural @ entry
