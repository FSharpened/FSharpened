namespace FSharpened.Ide

open System.Text
open System.Text.RegularExpressions

/// One entry of a file's outline: a function, a type, a module, a heading.
type Symbol =
    {
        Name: string
        /// "function", "method", "constructor", "class", "struct", "interface", "trait", "impl", "enum", "type",
        /// "record", "module", "namespace", "constant", "variable", "macro" or "heading".
        Kind: string
        /// 1-based line and column of the name.
        Line: int
        Column: int
        /// The last line before the next symbol that is not nested inside this one.
        EndLine: int
        /// 0 for a top-level symbol, 1 for one inside it, and so on.
        Depth: int
        /// The name of the enclosing symbol, or "".
        Container: string
    }

/// A symbol found in a file of a project.
type Definition = { Path: string; Symbol: Symbol }

/// The outline of a source file (Ctrl+Shift+O, `@` in quick open, breadcrumbs, go to definition). It reads declarations
/// line by line with a table of patterns per language: it is a fast index, not a parser, and says so in the UI.
/// Comments and string contents are masked first, so commented-out code does not appear.
module Symbols =
    type private Spec =
        { Lines: string list
          Block: (string * string) option
          Quotes: string
          Rules: Rule list }

    and private Rule =
        {
            Kind: string
            /// 0 anywhere, 1 only directly inside a type, 2 not inside a function or method.
            Where: int
            /// The regex group holding the name that is highlighted and navigated to.
            Leaf: int
            Re: Regex
            Label: (Match -> string) option
        }

    let private typeKinds =
        [ "class"; "struct"; "interface"; "trait"; "impl"; "enum"; "type"; "record" ]

    let private callableKinds = [ "function"; "method"; "constructor" ]

    let private mk (ci: bool) (kind: string) (where: int) (pattern: string) : Rule =
        { Kind = kind
          Where = where
          Leaf = 1
          Re = Regex(pattern, (if ci then RegexOptions.IgnoreCase else RegexOptions.None))
          Label = None }

    let private notKeyword =
        """(?!(?:if|else|for|foreach|while|switch|return|catch|using|lock|new|throw|await|yield|sizeof|typeof|do|case|goto|delete|fixed|when|try|function|typedef|define)\b)"""

    // ---- per-language tables ------------------------------------------------------------------------

    // The rest of a function signature: an open parameter list, a one-line body with no semicolon before its brace, or an
    // expression body. A line that ends in a semicolon straight after the parameters is a call or a prototype, not a
    // definition.
    let private sg = """\((?:[^;]*$|[^;]*\)[^;{]*\{.*$|.*\)\s*=>.*$)"""

    let private csharpLike (typeMods: string) (methodMods: string) (extra: Rule list) : Rule list =
        let m = mk false

        [ yield m "namespace" 0 """^\s*namespace\s+([\w.]+)"""
          yield m "namespace" 0 """^\s*package\s+([\w.]+)"""

          for kw in [ "class"; "struct"; "interface"; "enum" ] do
              yield m kw 0 ("""^\s*(?:(?:""" + typeMods + """)\s+)*""" + kw + """\s+(\w+)""")

          yield m "record" 0 ("""^\s*(?:(?:""" + typeMods + """)\s+)*record\s+(?:struct\s+|class\s+)?(\w+)""")
          yield! extra
          yield
              m
                  "method"
                  1
                  ("""^\s*(?:(?:"""
                   + methodMods
                   + """)\s+)*"""
                   + notKeyword
                   // the type is not itself a modifier: "public Board()" is a constructor, not a method called Board
                   + """(?!(?:"""
                   + methodMods
                   + """)\b)[\w<>\[\],.?]+\s+(\w+)\s*(?:<[^>]*>)?\s*"""
                   + sg)

          yield m "constructor" 1 ("""^\s*(?:(?:""" + methodMods + """)\s+)+(\w+)\s*""" + sg) ]

    let private csharp =
        csharpLike
            "public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new|file"
            "public|private|protected|internal|static|virtual|override|abstract|async|sealed|extern|unsafe|new|partial"
            []

    let private java =
        csharpLike
            "public|private|protected|static|final|abstract|sealed|non-sealed|strictfp"
            "public|private|protected|static|final|abstract|synchronized|native|default|strictfp"
            []

    let private dart =
        let m = mk false

        [ m "class" 0 """^\s*(?:(?:abstract|base|sealed|final|interface|mixin)\s+)*class\s+(\w+)"""
          m "class" 0 """^\s*mixin\s+(?!class\b)(\w+)"""
          m "enum" 0 """^\s*enum\s+(\w+)"""
          m "class" 0 """^\s*extension\s+(?:type\s+)?(\w+)"""
          m
              "function"
              0
              ("""^\s*(?:(?:static|external)\s+)*"""
               + notKeyword
               + """[\w<>?,.\[\] ]+?\s+(\w+)\s*(?:<[^>]*>)?\s*"""
               + sg) ]

    let private clike (isCpp: bool) =
        let m = mk false

        [ yield m "macro" 2 """^\s*#\s*define\s+(\w+)"""

          if isCpp then
              yield m "namespace" 2 """^\s*namespace\s+([\w:]+)"""
              yield m "class" 2 """^\s*(?:template\s*<[^>]*>\s*)?class\s+(\w+)\s*(?:\{|$|:|final)"""

          yield m "struct" 2 """^\s*(?:typedef\s+)?(?:struct|union)\s+(\w+)\s*(?:\{|$|:)"""
          yield m "enum" 2 """^\s*(?:typedef\s+)?enum\s+(?:class\s+|struct\s+)?(\w+)"""

          yield
              m
                  "function"
                  2
                  ("""^[A-Za-z_]"""
                   + """(?!(?:if|else|for|while|switch|return|sizeof|do|typedef|define|case|class|struct|union|enum|namespace|using)\b)"""
                   + """[\w\s\*&:<>,~]*?[\s\*&:](~?\w+)\s*"""
                   + sg)

          if isCpp then
              yield
                  m
                      "method"
                      1
                      ("""^\s+(?:(?:virtual|static|inline|explicit|constexpr|friend)\s+)*"""
                       + notKeyword
                       + """[\w:<>,\*&~\s]*?[\s\*&](~?\w+)\s*"""
                       + sg) ]

    let private go =
        let m = mk false

        [ m "namespace" 0 """^package\s+(\w+)"""
          { (m "method" 0 """^func\s+\(\s*\w*\s*\*?\s*([\w.]+)(?:\[[^\]]*\])?\s*\)\s*(\w+)""") with
              Leaf = 2
              Label = Some(fun g -> g.Groups.[1].Value + "." + g.Groups.[2].Value) }
          m "function" 0 """^func\s+(\w+)"""
          m "struct" 0 """^type\s+(\w+)\s+struct\b"""
          m "interface" 0 """^type\s+(\w+)\s+interface\b"""
          m "type" 0 """^type\s+(\w+)"""
          m "constant" 0 """^const\s+(\w+)"""
          m "variable" 0 """^var\s+(\w+)""" ]

    let private rust =
        let m = mk false
        let vis = """(?:pub(?:\([^)]*\))?\s+)?"""

        [ m
              "function"
              0
              ("""^\s*"""
               + vis
               + """(?:(?:default|const|async|unsafe)\s+)*(?:extern\s+(?:"[^"]*"\s+)?)?fn\s+(\w+)""")
          m "struct" 0 ("""^\s*""" + vis + """struct\s+(\w+)""")
          m "struct" 0 ("""^\s*""" + vis + """union\s+(\w+)""")
          m "enum" 0 ("""^\s*""" + vis + """enum\s+(\w+)""")
          m "trait" 0 ("""^\s*""" + vis + """(?:unsafe\s+)?trait\s+(\w+)""")
          m "module" 0 ("""^\s*""" + vis + """mod\s+(\w+)""")
          { (m "impl" 0 """^\s*(?:unsafe\s+)?impl(?:\s*<[^>]*>)?\s+(?:!\s*)?([\w:]+)(?:<[^>]*>)?\s+for\s+([\w:]+)""") with
              Leaf = 2
              Label = Some(fun g -> "impl " + g.Groups.[1].Value + " for " + g.Groups.[2].Value) }
          { (m "impl" 0 """^\s*(?:unsafe\s+)?impl(?:\s*<[^>]*>)?\s+([\w:]+)""") with
              Label = Some(fun g -> "impl " + g.Groups.[1].Value) }
          m "type" 2 ("""^\s*""" + vis + """type\s+(\w+)""")
          m "constant" 2 ("""^\s*""" + vis + """const\s+(\w+)\s*:""")
          m "variable" 2 ("""^\s*""" + vis + """static\s+(?:mut\s+)?(\w+)\s*:""")
          m "macro" 0 """^\s*(?:#\[macro_export\]\s*)?macro_rules!\s*(\w+)""" ]

    let private python =
        let m = mk false

        [ m "function" 0 """^\s*(?:async\s+)?def\s+(\w+)"""
          m "class" 0 """^\s*class\s+(\w+)"""
          m "constant" 0 """^([A-Z][A-Z0-9_]*)\s*(?::[^=]+)?=[^=]""" ]

    let private javascript (typed: bool) =
        let m = mk false
        let exp = """^\s*(?:export\s+)?(?:default\s+)?"""

        [ yield m "class" 0 (exp + """(?:abstract\s+)?class\s+(\w+)""")
          yield m "function" 0 (exp + """(?:async\s+)?function\s*\*?\s*(\w+)""")

          if typed then
              yield m "interface" 0 (exp + """(?:declare\s+)?interface\s+(\w+)""")
              yield m "type" 0 (exp + """(?:declare\s+)?type\s+(\w+)\s*(?:<[^>]*>)?\s*=""")
              yield m "enum" 0 (exp + """(?:declare\s+)?(?:const\s+)?enum\s+(\w+)""")
              yield m "namespace" 0 (exp + """(?:declare\s+)?(?:namespace|module)\s+(\w+)""")

          yield
              m
                  "function"
                  0
                  (exp
                   + """(?:const|let|var)\s+(\w+)\s*(?::[^=]+)?=\s*(?:async\s*)?(?:\([^)]*\)|\w+)\s*(?::\s*[^=]+)?=>""")

          yield m "function" 0 (exp + """(?:const|let|var)\s+(\w+)\s*(?::[^=]+)?=\s*(?:async\s*)?function\b""")
          yield m "constant" 0 """^(?:export\s+)?const\s+([A-Z][A-Z0-9_]*)\s*[=:]"""

          yield
              m
                  "method"
                  1
                  """^\s+(?:(?:static|async|get|set|public|private|protected|readonly|override)\s+)*(?!(?:if|for|while|switch|catch|function|return|else|do|try|with)\b)(#?\w+)\s*(?:<[^>]*>)?\s*\([^)]*\)\s*(?::\s*[^{]+)?\{""" ]

    let private ruby =
        let m = mk false

        [ m "function" 0 """^\s*def\s+(?:self\.)?([\w?!=]+)"""
          m "class" 0 """^\s*class\s+([\w:]+)"""
          m "module" 0 """^\s*module\s+([\w:]+)"""
          m "constant" 0 """^([A-Z]\w*)\s*=""" ]

    let private lua =
        let m = mk false

        [ m "function" 0 """^\s*(?:local\s+)?function\s+([\w.:]+)"""
          m "function" 0 """^\s*(?:local\s+)?([\w.]+)\s*=\s*function\s*\(""" ]

    let private php =
        let m = mk false

        [ m "namespace" 0 """^\s*namespace\s+([\w\\]+)"""
          m "class" 0 """^\s*(?:(?:abstract|final|readonly)\s+)*class\s+(\w+)"""
          m "interface" 0 """^\s*interface\s+(\w+)"""
          m "trait" 0 """^\s*trait\s+(\w+)"""
          m "enum" 0 """^\s*enum\s+(\w+)"""
          m "function" 0 """^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+&?(\w+)"""
          m "constant" 0 """^\s*(?:(?:public|private|protected|final)\s+)*const\s+(\w+)""" ]

    let private rlang =
        let m = mk false

        [ m "function" 0 """^\s*([A-Za-z.][\w.]*)\s*(?:<-|=)\s*function\s*\("""
          m "class" 0 """setClass\s*\(\s*["'](\w+)["']"""
          m "class" 0 """setRefClass\s*\(\s*["'](\w+)["']"""
          m "function" 0 """setGeneric\s*\(\s*["'](\w+)["']"""
          m "method" 0 """setMethod\s*\(\s*["'](\w+)["']""" ]

    let private haskell =
        let m = mk false
        let ctx = """(?:\([^)]*\)\s*=>\s*|[\w ]+=>\s*)?"""

        [ m "module" 0 """^module\s+([\w.]+)"""
          m "type" 0 """^(?:data|newtype)\s+(?:family\s+)?(\w+)"""
          m "type" 0 """^type\s+(?:family\s+)?(\w+)"""
          m "class" 0 ("""^class\s+""" + ctx + """(\w+)""")
          { (m "impl" 0 ("""^instance\s+""" + ctx + """([\w ()\[\],.']+?)\s+where""")) with
              Label = Some(fun g -> "instance " + g.Groups.[1].Value) }
          m "function" 0 """^([a-z_][\w']*)\s*::""" ]

    let private ocamlLike (fsharp: bool) =
        let m = mk false
        let mods = """(?:(?:inline|rec|private|internal|public|mutable)\s+)*"""
        let notMod = """(?!(?:inline|rec|private|internal|public|mutable|use)\b)"""

        [ if fsharp then
              yield m "namespace" 0 """^\s*namespace\s+(?:rec\s+|global\s*)?([\w.]*\w)"""
              yield m "module" 0 """^\s*module\s+(?:(?:rec|internal|public|private)\s+)*([\w.]+)"""
              yield m "type" 0 """^\s*(?:and|type)\s+(?:(?:private|internal|public|inline)\s+)*(\w+)"""

              yield
                  m
                      "method"
                      1
                      """^\s*(?:static\s+)?(?:member|override|abstract|default)\s+(?:val\s+)?(?:inline\s+)?(?:(?:private|internal|public)\s+)?(?:\w+\.)?(\w+)"""
          else
              yield m "type" 0 """^\s*type\s+(?:nonrec\s+)?(?:'\w+\s+|\([^)]*\)\s+)?(\w+)"""
              yield m "module" 0 """^\s*module\s+(?:type\s+)?(\w+)"""
              yield m "class" 0 """^\s*class\s+(?:virtual\s+)?(\w+)"""
              yield m "function" 0 """^\s*(?:val|external)\s+(\w+)"""

          yield m "type" 0 """^\s*exception\s+(\w+)"""

          yield
              m
                  "function"
                  2
                  ("""^\s*(?:let|and)\s+"""
                   + mods
                   + notMod
                   + (if fsharp then
                          """(\w+)(?![\w'])\s*(?:\(|[A-Za-z_'~?])"""
                      else
                          """(\w+)\s+[\w(~?'_\[{]"""))

          yield m "variable" 2 ("""^\s*let\s+""" + mods + notMod + """(\w+)\s*(?::[^=]*)?=""") ]

    let private vb =
        let m = mk true

        let mods =
            """(?:(?:Public|Private|Protected|Friend|Shared|Overrides|Overridable|MustOverride|MustInherit|NotInheritable|NotOverridable|Async|Partial|Shadows|ReadOnly|WriteOnly|Default)\s+)*"""

        [ m "namespace" 0 """^\s*Namespace\s+([\w.]+)"""
          m "class" 0 ("""^\s*""" + mods + """Class\s+(\w+)""")
          m "module" 0 ("""^\s*""" + mods + """Module\s+(\w+)""")
          m "struct" 0 ("""^\s*""" + mods + """Structure\s+(\w+)""")
          m "interface" 0 ("""^\s*""" + mods + """Interface\s+(\w+)""")
          m "enum" 0 ("""^\s*""" + mods + """Enum\s+(\w+)""")
          m "function" 0 ("""^\s*""" + mods + """(?:Sub|Function)\s+(\w+)""") ]

    let private specOf (lang: Language) : Spec =
        let slash =
            { Lines = [ "//" ]
              Block = Some("/*", "*/")
              Quotes = "\"'"
              Rules = [] }

        match lang with
        | CSharp -> { slash with Rules = csharp }
        | Java -> { slash with Rules = java }
        | Dart -> { slash with Rules = dart }
        | C -> { slash with Rules = clike false }
        | Cpp -> { slash with Rules = clike true }
        | Go -> { slash with Rules = go }
        | Rust ->
            { slash with
                Quotes = "\""
                Rules = rust }
        | JavaScript -> { slash with Rules = javascript false }
        | TypeScript -> { slash with Rules = javascript true }
        | Php ->
            { slash with
                Lines = [ "//"; "#" ]
                Rules = php }
        | Python ->
            { Lines = [ "#" ]
              Block = Some("\"\"\"", "\"\"\"")
              Quotes = "\"'"
              Rules = python }
        | Ruby ->
            { Lines = [ "#" ]
              Block = None
              Quotes = "\"'"
              Rules = ruby }
        | Lua ->
            { Lines = [ "--" ]
              Block = Some("--[[", "]]")
              Quotes = "\"'"
              Rules = lua }
        | R ->
            { Lines = [ "#" ]
              Block = None
              Quotes = "\"'"
              Rules = rlang }
        | Haskell ->
            { Lines = [ "--" ]
              Block = Some("{-", "-}")
              Quotes = "\""
              Rules = haskell }
        | FSharp ->
            { Lines = [ "//" ]
              Block = Some("(*", "*)")
              Quotes = "\""
              Rules = ocamlLike true }
        | OCaml ->
            { Lines = []
              Block = Some("(*", "*)")
              Quotes = "\""
              Rules = ocamlLike false }
        | VisualBasic ->
            { Lines = [ "'" ]
              Block = None
              Quotes = "\""
              Rules = vb }

    let private specs = System.Collections.Generic.Dictionary<string, Spec>()

    let private specFor (lang: Language) : Spec =
        let key = Language.id lang

        match specs.TryGetValue key with
        | true, s -> s
        | _ ->
            let s = specOf lang
            specs.[key] <- s
            s

    // ---- reading a file -----------------------------------------------------------------------------

    let private at (s: string) (i: int) (token: string) =
        token <> ""
        && i + token.Length <= s.Length
        && s.Substring(i, token.Length) = token

    /// The line with comments blanked out (same length, so columns stay right), and whether a block comment is still
    /// open at its end. Quoted strings are left alone, but a comment marker inside one does not start a comment.
    let private mask (spec: Spec) (line: string) (inBlock: bool) : string * bool =
        let sb = StringBuilder()
        let mutable i = 0
        let mutable block = inBlock
        let mutable quote = ' '
        let mutable cut = false

        while not cut && i < line.Length do
            if block then
                match spec.Block with
                | Some(_, close) when at line i close ->
                    sb.Append(String.replicate close.Length " ") |> ignore
                    i <- i + close.Length
                    block <- false
                | _ ->
                    sb.Append ' ' |> ignore
                    i <- i + 1
            elif quote <> ' ' then
                let c = line.[i]
                sb.Append c |> ignore
                i <- i + 1

                if c = '\\' && i < line.Length then
                    sb.Append line.[i] |> ignore
                    i <- i + 1
                elif c = quote then
                    quote <- ' '
            else
                match spec.Block with
                | Some(op, _) when at line i op ->
                    sb.Append(String.replicate op.Length " ") |> ignore
                    i <- i + op.Length
                    block <- true
                | _ ->
                    let here = i

                    if spec.Lines |> List.exists (fun l -> at line here l) then
                        cut <- true
                    else
                        let c = line.[i]

                        if spec.Quotes.IndexOf c >= 0 then
                            quote <- c

                        sb.Append c |> ignore
                        i <- i + 1

        sb.ToString(), block

    let private indentOf (line: string) =
        let mutable w = 0
        let mutable i = 0

        while i < line.Length && (line.[i] = ' ' || line.[i] = '\t') do
            w <- w + (if line.[i] = '\t' then 4 else 1)
            i <- i + 1

        w

    type private Open =
        { Indent: int
          Kind: string
          Name: string }

    /// A symbol ends on the line before the next symbol that is not nested inside it (indentation, or heading level for
    /// Markdown, no deeper than its own). The last ones run to the end of the file.
    let private closeRanges (items: (Symbol * int)[]) : Symbol list =
        let ends = Array.init items.Length (fun i -> (fst items.[i]).EndLine)
        let open' = ResizeArray<int>()

        for i in 0 .. items.Length - 1 do
            let s, level = items.[i]

            while open'.Count > 0 && snd items.[open'.[open'.Count - 1]] >= level do
                let k = open'.[open'.Count - 1]
                ends.[k] <- max (fst items.[k]).Line (s.Line - 1)
                open'.RemoveAt(open'.Count - 1)

            open'.Add i

        items
        |> Array.mapi (fun i (s, _) -> { s with EndLine = ends.[i] })
        |> Array.toList

    /// The outline of a source file in the given language, in file order.
    let outline (lang: Language) (text: string) : Symbol list =
        let spec = specFor lang
        let lines = text.Split('\n')

        let total =
            if text.EndsWith "\n" then
                lines.Length - 1
            else
                lines.Length

        let found = ResizeArray<Symbol * int>() // symbol, indent
        let stack = ResizeArray<Open>()
        let mutable block = false

        for li in 0 .. lines.Length - 1 do
            let raw = lines.[li]
            let masked, stillOpen = mask spec raw block
            block <- stillOpen

            let first = masked.TrimStart()
            // a line that only opens or closes a block (Allman braces, a closing parenthesis) starts no declaration and
            // must not end the one above it
            let structural =
                first <> ""
                && (first.[0] = '{'
                    || first.[0] = '}'
                    || first.[0] = ')'
                    || first.[0] = ']'
                    || first.TrimEnd() = "public:"
                    || first.TrimEnd() = "private:"
                    || first.TrimEnd() = "protected:")

            if first <> "" && not structural then
                let indent = indentOf masked

                // the symbols this line closes
                while stack.Count > 0 && stack.[stack.Count - 1].Indent >= indent do
                    stack.RemoveAt(stack.Count - 1)

                let parent =
                    if stack.Count > 0 then
                        Some stack.[stack.Count - 1]
                    else
                        None

                let parentKind =
                    match parent with
                    | Some p -> p.Kind
                    | None -> ""

                let allowed (r: Rule) =
                    match r.Where with
                    | 1 -> List.contains parentKind typeKinds
                    | 2 -> not (List.contains parentKind callableKinds)
                    | _ -> true

                let hit =
                    spec.Rules
                    |> List.tryPick (fun r ->
                        if not (allowed r) then
                            None
                        else
                            let m = r.Re.Match masked

                            if m.Success then Some(r, m) else None)

                match hit with
                | None -> ()
                | Some(r, m) ->
                    let leaf = m.Groups.[r.Leaf].Value

                    let name =
                        match r.Label with
                        | Some f -> f m
                        | None -> leaf

                    if name <> "" && name <> "_" then
                        let kind =
                            if r.Kind = "function" && List.contains parentKind typeKinds then
                                "method"
                            else
                                r.Kind

                        let col = masked.IndexOf(leaf, m.Index)

                        found.Add(
                            { Name = name
                              Kind = kind
                              Line = li + 1
                              Column = (if col < 0 then indent else col) + 1
                              EndLine = total
                              Depth = stack.Count
                              Container =
                                match parent with
                                | Some p -> p.Name
                                | None -> "" },
                            indent
                        )

                        stack.Add
                            { Indent = indent
                              Kind = kind
                              Name = name }

        closeRanges (found.ToArray())

    /// Markdown headings (not inside code fences) as an outline.
    let markdown (text: string) : Symbol list =
        let lines = text.Split('\n')

        let total =
            if text.EndsWith "\n" then
                lines.Length - 1
            else
                lines.Length

        let found = ResizeArray<Symbol * int>()
        let mutable fence = false

        for li in 0 .. lines.Length - 1 do
            let line = lines.[li].TrimEnd()
            let trimmed = line.TrimStart()

            if trimmed.StartsWith "```" || trimmed.StartsWith "~~~" then
                fence <- not fence
            elif not fence && line.StartsWith "#" then
                let level = line.Length - line.TrimStart('#').Length

                if level <= 6 && line.Length > level && line.[level] = ' ' then
                    let title = line.Substring(level).Trim().TrimEnd('#').Trim()

                    if title <> "" then
                        found.Add(
                            { Name = title
                              Kind = "heading"
                              Line = li + 1
                              Column = level + 2
                              EndLine = total
                              Depth = level - 1
                              Container = "" },
                            level
                        )

        closeRanges (found.ToArray())

    /// The outline of a file, chosen by its extension. Empty for files that are neither source nor Markdown.
    let outlineOfPath (path: string) (text: string) : Symbol list =
        match ProjectPath.languageOf path with
        | Some lang -> outline lang text
        | None ->
            match ProjectPath.extensionOf path with
            | "md"
            | "markdown" -> markdown text
            | _ -> []

    /// The symbols that contain a line, outermost first: the breadcrumb path to that line.
    let enclosing (symbols: Symbol list) (line: int) : Symbol list =
        symbols
        |> List.filter (fun s -> s.Line <= line && line <= s.EndLine)
        |> List.sortBy (fun s -> s.Depth, s.Line)

    // The part of a name that code refers to: "Type.method", "Mod::fn" and "impl X for Y" are looked up by their end.
    let private leafName (name: string) =
        let cut =
            max (name.LastIndexOf '.') (max (name.LastIndexOf ':') (name.LastIndexOf ' '))

        if cut >= 0 then name.Substring(cut + 1) else name

    /// Where a name is declared in the given files (go to definition). An exact, case-sensitive match on the name or on
    /// its last part, so `area` finds `Shape.area`.
    let definitions (name: string) (files: (string * string) list) : Definition list =
        if name = "" then
            []
        else
            [ for (path, text) in files do
                  for s in outlineOfPath path text do
                      if s.Kind <> "heading" && (s.Name = name || leafName s.Name = name) then
                          yield { Path = path; Symbol = s } ]

    /// The identifier that touches a position in a line (the word under the cursor), or "".
    let wordAt (line: string) (column: int) : string =
        let isWord (c: char) =
            System.Char.IsLetterOrDigit c || c = '_' || c = '$'

        let pos = max 0 (min (column - 1) line.Length)
        let mutable a = pos
        let mutable b = pos

        while a > 0 && isWord line.[a - 1] do
            a <- a - 1

        while b < line.Length && isWord line.[b] do
            b <- b + 1

        line.Substring(a, b - a)
