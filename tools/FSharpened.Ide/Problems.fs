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

    /// The tool whose output a language's diagnostics come from (shown as the "tool" context of a diagnostic).
    let private toolOf (lang: Language) =
        match lang with
        | FSharp -> "fsc"
        | CSharp -> "csc"
        | VisualBasic -> "vbc"
        | TypeScript -> "tsc"
        | C
        | Cpp -> "cc"
        | other -> Language.id other

    /// The four languages whose compiler output the generic "error:" and linker patterns were written for.
    let private legacyStyle (lang: Language) =
        match lang with
        | FSharp
        | CSharp
        | C
        | Cpp -> true
        | _ -> false

    // ---- one-line formats of the other compilers and interpreters (captured from the real services) ----
    // vbc:   <source>(3) : error BC30451: 'missing' is not declared.
    let private vbLike =
        Regex("^(.*?)\\((\\d+)(?:,(\\d+))?\\)\\s*:\\s*(error|warning)\\s+(BC\\d+):\\s*(.*)$")

    // dart:  example.dart:3:9: Error: Undefined name 'y'.
    let private dartLike =
        Regex("^[\\w./\\\\-]*\\.dart:(\\d+):(\\d+):\\s*(Error|Warning|Info):\\s*(.*)$")

    // go:    ./example.go:4:7: undefined: undefinedThing
    let private goLike = Regex("^(?:\\./)?[\\w./\\\\-]+\\.go:(\\d+):(\\d+):\\s*(.*)$")

    // ruby:  /app/output.s:2:in '<main>': boom (RuntimeError)      /app/output.s:1: syntax errors found (SyntaxError)
    let private rubyRuntime = Regex("^\\S+?:(\\d+):in [`']([^']*)': (.*)$")

    let private rubySyntax = Regex("^\\S+?:(\\d+): (.*\\(SyntaxError\\))$")

    // lua:   lua: /app/output.s:2: boom                              /app/output.s:1: unexpected symbol near '='
    let private luaLike = Regex("^(?:\\S*lua\\d*: )?\\S+?:(\\d+): (.*)$")

    // php:   Fatal error: Uncaught Exception: boom in /home/wandbox/prog.php:3
    //        Parse error: syntax error, unexpected token ";" in /home/wandbox/prog.php on line 2
    let private phpLike =
        Regex("^(?:PHP )?(Parse error|Fatal error|Warning|Notice|Deprecated): (.*?) in \\S+?(?: on line |:)(\\d+)")

    // R:     Error in undefined_fn(3) : could not find function "undefined_fn"      Error: boom
    let private rLike = Regex("^Error(?: in (.*?))?\\s?:\\s+(.*)$")

    // ghc:   <source>:2:22: error: [GHC-83865]   (the explanation is on the following lines; `prepass` joins them)
    let private ghcLike =
        Regex("^\\S+?:(\\d+):(\\d+)(?:-\\d+)?: (error|warning): \\[(GHC-\\d+)\\]\\s*(.*)$")

    let private specific (lang: Language) (line: string) : StructuredDiagnostic option =
        let file = Language.fileName lang

        let atCol (l: Group) (c: Group) =
            let line' = toInt l
            let col = if c.Success && c.Value <> "" then toInt c else 1
            range file line' col line' (col + 1)

        let atLine (l: Group) = range file (toInt l) 1 (toInt l) 2

        let make code sev msg where' =
            Some(
                StructuredDiagnostic.withContext
                    "tool"
                    (toolOf lang)
                    { StructuredDiagnostic.create code sev msg with
                        Range = where' }
            )

        match lang with
        | VisualBasic ->
            let m = vbLike.Match line

            if m.Success then
                make
                    m.Groups.[5].Value
                    (severityOf m.Groups.[4].Value)
                    m.Groups.[6].Value
                    (atCol m.Groups.[2] m.Groups.[3])
            else
                None
        | Dart ->
            let m = dartLike.Match line

            if m.Success then
                make "dart" (severityOf m.Groups.[3].Value) m.Groups.[4].Value (atCol m.Groups.[1] m.Groups.[2])
            else
                None
        | Go ->
            let m = goLike.Match line

            if m.Success then
                make "go" Severity.Error m.Groups.[3].Value (atCol m.Groups.[1] m.Groups.[2])
            else
                None
        | Ruby ->
            let r = rubyRuntime.Match line

            if r.Success then
                make "ruby" Severity.Error (r.Groups.[3].Value) (atLine r.Groups.[1])
            else
                let s = rubySyntax.Match line

                if s.Success then
                    make "ruby" Severity.Error s.Groups.[2].Value (atLine s.Groups.[1])
                else
                    None
        | Lua ->
            let m = luaLike.Match line

            if m.Success then
                make "lua" Severity.Error m.Groups.[2].Value (atLine m.Groups.[1])
            else
                None
        | Haskell ->
            let m = ghcLike.Match line

            if m.Success then
                make
                    m.Groups.[4].Value
                    (severityOf m.Groups.[3].Value)
                    m.Groups.[5].Value
                    (atCol m.Groups.[1] m.Groups.[2])
            else
                None
        | Php ->
            let m = phpLike.Match line

            if m.Success then
                let sev =
                    match m.Groups.[1].Value with
                    | "Warning" -> Severity.Warning
                    | "Notice"
                    | "Deprecated" -> Severity.Info
                    | _ -> Severity.Error

                let msg = Regex.Replace(m.Groups.[2].Value, ",? called$", "")
                make "php" sev msg (atLine m.Groups.[3])
            else
                None
        | R ->
            let m = rLike.Match line

            if m.Success then
                make "R" Severity.Error m.Groups.[2].Value None
            else
                None
        | _ -> None

    /// One output line to at most one diagnostic.
    let parseLine (lang: Language) (raw: string) : StructuredDiagnostic option =
        let line = forParsing raw
        let file = Language.fileName lang

        match specific lang line with
        | Some d -> Some d
        | None ->
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

                Some(StructuredDiagnostic.withContext "tool" (toolOf lang) d)
            else
                let gm = gccLike.Match line

                if gm.Success then
                    let l = toInt gm.Groups.[1]
                    let c = if gm.Groups.[2].Success then toInt gm.Groups.[2] else 1

                    let d =
                        { StructuredDiagnostic.create "cc" (severityOf gm.Groups.[3].Value) gm.Groups.[4].Value with
                            Range = range file l c l (c + 1) }

                    Some(StructuredDiagnostic.withContext "tool" (if legacyStyle lang then "cc" else toolOf lang) d)
                elif legacyStyle lang && undefinedRef.IsMatch line then
                    Some(StructuredDiagnostic.create "link" Severity.Error line)
                elif legacyStyle lang then
                    let bm = bare.Match line

                    if bm.Success then
                        Some(StructuredDiagnostic.create "error" Severity.Error bm.Groups.[2].Value)
                    else
                        None
                else
                    None

    // ---- multi-line formats: a location on one line, the message on another ---------------------------
    // Python, Node.js and OCaml print the location first; Go, the JVM, Dart, .NET and Haskell print the message first.
    // Each language lists the two patterns; one small fold pairs them. A message with no location is still reported.
    type private Trace =
        {
            /// Group 1 is the line, group 2 an optional column.
            Where: Regex
            /// Group 1 is the error kind (becomes the diagnostic code), group 2 the message text.
            What: Regex
            LocationFirst: bool
            /// False for TypeScript at run time: the line numbers are those of the compiled JavaScript, not of the source.
            UseLocation: bool
            /// 0 when the tool counts columns from zero (OCaml), 1 otherwise.
            ColumnBase: int
        }

    let private trace (where': string) (what: string) (first: bool) (useLocation: bool) (columnBase: int) =
        Some
            { Where = Regex where'
              What = Regex what
              LocationFirst = first
              UseLocation = useLocation
              ColumnBase = columnBase }

    let private nodeWhere = "^(?:\\S+/)?[\\w.-]+\\.(?:m?js|cjs):(\\d+)(?::(\\d+))?$"
    let private nodeWhat = "^([A-Za-z]*(?:Error|Exception)):\\s*(.*)$"

    let private dotnetWhere =
        "^\\s+at .* in \\S+\\.(?:cs|fs|vb):line (\\d+)(?::(\\d+))?$"

    let private dotnetWhat =
        "^Unhandled exception\\.\\s*([\\w.]+(?:Exception|Error)):\\s*(.*)$"

    let private traceOf (lang: Language) =
        match lang with
        | Python ->
            trace
                "^\\s*File \"(?![^\"]*(?:/lib/python|/usr/lib|site-packages|<frozen))[^\"]*\", line (\\d+)(?::(\\d+))?"
                "^([A-Za-z_][\\w.]*(?:Error|Exception|Exit|Interrupt|Warning)):\\s*(.*)$"
                true
                true
                1
        | JavaScript -> trace nodeWhere nodeWhat true true 1
        | TypeScript -> trace nodeWhere nodeWhat true false 1
        | FSharp
        | CSharp
        | VisualBasic -> trace dotnetWhere dotnetWhat false true 1
        | Java ->
            trace
                "^\\s+at [\\w$.<>]+\\((?:example|Main)\\.java:(\\d+)(?::(\\d+))?\\)$"
                "^Exception in thread \"[^\"]*\" ((?:[\\w$]+\\.)*[\\w$]+)(?::\\s*(.*))?$"
                false
                true
                1
        | Go ->
            trace
                "^\\s+(?!\\S*(?:/go/src/|/usr/))(?:\\S+/)?[\\w.-]+\\.go:(\\d+)(?::(\\d+))?"
                "^(panic|fatal error):\\s*(.*)$"
                false
                true
                1
        | Dart ->
            trace
                "^#\\d+\\s+\\S+ \\(file:///\\S*?\\.dart:(\\d+)(?::(\\d+))?\\)$"
                "^(Unhandled exception):\\s*(.*)$"
                false
                true
                1
        | Haskell -> trace "called at \\S+\\.hs:(\\d+):(\\d+)" "^(?:\\S+: )?(Uncaught exception):\\s*(.*)$" false true 1
        | OCaml ->
            trace
                "^File \"[^\"]*\", line (\\d+)(?:, characters (\\d+)-\\d+)?:"
                "^(Error|Warning \\d+ \\[[\\w-]+\\]|Fatal error):\\s*(?:exception )?(.*)$"
                true
                true
                0
        | _ -> None

    /// Rewrites output so a message that spans lines becomes one line before parsing.
    let private prepass (lang: Language) (text: string) =
        match lang with
        | Dart -> Regex.Replace(text, "Unhandled exception:\\n", "Unhandled exception: ")
        | Haskell ->
            let joined =
                Regex.Replace(text, "\\b(error|warning): (\\[GHC-\\d+\\])[ \\t]*\\n[ \\t]+(?:\\* )?", "$1: $2 ")

            Regex.Replace(joined, "Uncaught exception [^\\n]*:\\n\\n([^\\n]+)", "Uncaught exception: $1")
        | _ -> text

    let private runTrace (lang: Language) (t: Trace) (lines: string list) : StructuredDiagnostic list =
        let file = Language.fileName lang
        let tool = toolOf lang

        let locOf (m: Match) =
            let l = toInt m.Groups.[1]

            let c =
                if m.Groups.[2].Success && m.Groups.[2].Value <> "" then
                    toInt m.Groups.[2] + (1 - t.ColumnBase)
                else
                    1

            range file l c l (c + 1)

        let diag (m: Match) (where': SourceRange option) =
            let code = m.Groups.[1].Value
            let text = m.Groups.[2].Value.Trim()

            let sev =
                if code.StartsWith "Warning" then
                    Severity.Warning
                else
                    Severity.Error

            StructuredDiagnostic.withContext
                "tool"
                tool
                { StructuredDiagnostic.create code sev (if text = "" then code else text) with
                    Range = if t.UseLocation then where' else None }

        let step (acc: StructuredDiagnostic list, loc: SourceRange option, pending: Match option) (raw: string) =
            let line = forParsing raw
            let wm = t.Where.Match line
            let mm = t.What.Match line

            if t.LocationFirst then
                if wm.Success then (acc, locOf wm, pending)
                elif mm.Success then (diag mm loc :: acc, None, pending)
                else (acc, loc, pending)
            elif mm.Success then
                let flushed =
                    match pending with
                    | Some p -> diag p None :: acc
                    | None -> acc

                (flushed, loc, Some mm)
            else
                match pending with
                | Some p when wm.Success -> (diag p (locOf wm) :: acc, loc, None)
                | _ -> (acc, loc, pending)

        let (found, _, left) = lines |> List.fold step ([], None, None)

        let tail =
            match left with
            | Some p -> [ diag p None ]
            | None -> []

        List.rev (tail @ found)

    // ---- rustc: multi-line diagnostics ---------------------------------------------------------------
    // error[E0425]: cannot find value `y` in this scope
    //  --> <source>:3:20
    // and run-time panics:  thread 'main' (2) panicked at /app/example.rs:5:21:  then the message on the next line.
    let private rustHeader =
        Regex("^(error|warning)(?:\\[([A-Za-z0-9_:]+)\\])?:\\s*(.*)$")

    let private rustLocation = Regex("^\\s*-->\\s*(.+?):(\\d+):(\\d+)\\s*$")

    let private rustPanic =
        Regex("^thread '.*' (?:\\(\\d+\\) )?panicked at (.+?):(\\d+):(\\d+):\\s*$")

    let private rustSummary =
        Regex("^(?:aborting due to|could not compile|\\d+ warnings? emitted)")

    type private RustState =
        | Idle
        /// severity, code, message; the `-->` location line is still to come
        | Header of Severity * string * string
        /// the location is known; the message is the next non-empty line
        | Panic of SourceRange

    let private rustDocs (code: string) =
        if Regex.IsMatch(code, "^E\\d{4}$") then
            Some("https://doc.rust-lang.org/error_codes/" + code + ".html")
        else
            None

    let private rustDiag (sev: Severity) (code: string) (msg: string) (where: SourceRange option) =
        let d = StructuredDiagnostic.create (if code = "" then "rustc" else code) sev msg

        StructuredDiagnostic.withContext
            "tool"
            "rustc"
            { d with
                Range = where
                DocsUrl = rustDocs code }

    let private parseRust (output: string) : StructuredDiagnostic list =
        let file = Language.fileName Rust

        let step (acc: StructuredDiagnostic list, state: RustState) (raw: string) =
            let line = (stripAnsi raw).TrimEnd()

            // A header that never got a location (a linker failure, for example) is still worth reporting.
            let pendingHeader =
                match state with
                | Header(s, c, m) -> rustDiag s c m None :: acc
                | _ -> acc

            match state with
            | Panic r ->
                if line.Trim() = "" then
                    (acc, state)
                else
                    (rustDiag Severity.Error "panic" (line.Trim()) (Some r) :: acc, Idle)
            | _ ->
                let pm = rustPanic.Match line

                if pm.Success then
                    let l = toInt pm.Groups.[2]
                    let c = toInt pm.Groups.[3]

                    (pendingHeader,
                     Panic
                         { File = file
                           StartLine = l
                           StartColumn = c
                           EndLine = l
                           EndColumn = c + 1 })
                else
                    let hm = rustHeader.Match line

                    if hm.Success then
                        let msg = hm.Groups.[3].Value

                        if rustSummary.IsMatch msg then
                            (pendingHeader, Idle)
                        else
                            (pendingHeader, Header(severityOf hm.Groups.[1].Value, hm.Groups.[2].Value, msg))
                    else
                        let lm = rustLocation.Match line

                        match state with
                        | Header(s, c, m) when lm.Success ->
                            let l = toInt lm.Groups.[2]
                            let col = toInt lm.Groups.[3]
                            (rustDiag s c m (range file l col l (col + 1)) :: acc, Idle)
                        | _ -> (acc, state)

        let (acc, last) =
            output.Replace("\r\n", "\n").Split('\n') |> Array.fold step ([], Idle)

        let all =
            match last with
            | Header(s, c, m) -> rustDiag s c m None :: acc
            | _ -> acc

        all |> List.rev |> List.distinct

    let parse (lang: Language) (output: string) : StructuredDiagnostic list =
        if lang = Rust then
            parseRust output
        else
            let text = prepass lang (output.Replace("\r\n", "\n"))
            let lines = text.Split('\n') |> Array.toList
            let single = lines |> List.choose (parseLine lang)

            let traced =
                match traceOf lang with
                | Some t -> runTrace lang t lines
                | None -> []

            single @ traced |> List.distinct

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
    let private bracketCheckCore (lang: Language) (text: string) : StructuredDiagnostic list =
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

        let isIdentChar (c: char) =
            (c >= 'a' && c <= 'z')
            || (c >= 'A' && c <= 'Z')
            || (c >= '0' && c <= '9')
            || c = '_'

        // Rust raw strings: r"...", r#"..."#, br##"..."##. Returns the number of # marks, or -1 when this is not one.
        let rawHashes (k: int) =
            let mutable j = k

            while at j = '#' do
                j <- j + 1

            if at j = '"' then j - k else -1

        let closesRaw (k: int) (hashes: int) =
            let mutable ok = true

            for m in 0 .. hashes - 1 do
                if at (k + m) <> '#' then
                    ok <- false

            ok

        let rustRawAt () =
            if lang <> Rust || (i > 0 && isIdentChar text.[i - 1]) then
                -1
            elif at i = 'r' then
                rawHashes (i + 1)
            elif at i = 'b' && at (i + 1) = 'r' then
                rawHashes (i + 2)
            else
                -1

        while i < n do
            let ch = at i
            let raw = if ch = 'r' || ch = 'b' then rustRawAt () else -1

            if ch = '/' && at (i + 1) = '/' then
                while i < n && at i <> '\n' do
                    advance ()
            elif ch = '/' && at (i + 1) = '*' then
                advance ()
                advance ()
                // Rust block comments nest; the other languages' do not.
                let mutable depth = 1

                while i < n && depth > 0 do
                    if lang = Rust && at i = '/' && at (i + 1) = '*' then
                        advance ()
                        advance ()
                        depth <- depth + 1
                    elif at i = '*' && at (i + 1) = '/' then
                        advance ()
                        advance ()
                        depth <- depth - 1
                    else
                        advance ()
            elif lang = FSharp && ch = '(' && at (i + 1) = '*' && at (i + 2) <> ')' then
                advance ()
                advance ()

                while i < n && not (at i = '*' && at (i + 1) = ')') do
                    advance ()

                advance ()
                advance ()
            elif raw >= 0 then
                // skip the prefix (r or br), the hashes and the opening quote, then everything up to "###
                let prefix = if ch = 'b' then 2 else 1

                for _ in 1 .. prefix + raw + 1 do
                    advance ()

                let mutable finished = false

                while i < n && not finished do
                    if at i = '"' && closesRaw (i + 1) raw then
                        for _ in 1 .. raw + 1 do
                            advance ()

                        finished <- true
                    else
                        advance ()
            elif ch = '"' then
                advance ()

                while i < n && at i <> '"' && (at i <> '\n' || lang = Rust) do
                    if at i = '\\' then
                        advance ()

                    advance ()

                advance ()
            elif ch = '\'' && lang = Rust then
                // 'a' and '\n' are chars; 'a and 'outer: are lifetimes and labels and must not swallow the line.
                if at (i + 1) = '\\' then
                    advance ()

                    while i < n && at i <> '\'' && at i <> '\n' do
                        if at i = '\\' then
                            advance ()

                        advance ()

                    advance ()
                elif at (i + 2) = '\'' then
                    advance ()
                    advance ()
                    advance ()
                elif int (at (i + 1)) >= 0xD800 && int (at (i + 1)) <= 0xDBFF && at (i + 3) = '\'' then
                    advance ()
                    advance ()
                    advance ()
                    advance ()
                else
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

    /// The languages whose comment and string rules `bracketCheckCore` really implements. For the others a bracket
    /// check would invent errors (Python triple quotes, Ruby heredocs, Lua long strings, PHP heredocs, Haskell and
    /// OCaml nested comments), and a false error is worse than none: the compiler is the authority whenever it is
    /// reachable (ADR-0020).
    let private bracketLanguages = [ FSharp; CSharp; C; Cpp; Rust; Java ]

    let bracketCheck (lang: Language) (text: string) : StructuredDiagnostic list =
        if List.contains lang bracketLanguages then
            bracketCheckCore lang text
        else
            []

    /// The pattern that proves a program has an entry point, and the fix to suggest when it does not.
    let private entryPoint (lang: Language) : (Regex * string) option =
        match lang with
        | C
        | Cpp -> Some(Regex("\\bmain\\s*\\("), "Add: int main(void) { return 0; }")
        | Rust -> Some(Regex("\\bfn\\s+main\\s*\\("), "Add: fn main() { }")
        | Go -> Some(Regex("\\bfunc\\s+main\\s*\\("), "Add: func main() { }")
        | Java -> Some(Regex("\\bvoid\\s+main\\s*\\("), "Add: public static void main(String[] args) { }")
        | Dart -> Some(Regex("\\bmain\\s*\\("), "Add: void main() { }")
        | VisualBasic ->
            Some(Regex("\\bSub\\s+Main\\b", RegexOptions.IgnoreCase), "Add: Sub Main() ... End Sub inside a Module")
        | Haskell -> Some(Regex("^main\\s*(::|=)", RegexOptions.Multiline), "Add: main :: IO ()")
        | _ -> None

    let localChecks (lang: Language) (text: string) : StructuredDiagnostic list =
        let structural = bracketCheck lang text

        let entry =
            match entryPoint lang with
            | Some(pattern, fix) when not (pattern.IsMatch text) ->
                [ { StructuredDiagnostic.create "local-entry" Severity.Warning "No main function found" with
                      SuggestedFix = Some fix } ]
            | _ -> []

        structural @ entry
