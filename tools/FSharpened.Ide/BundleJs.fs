namespace FSharpened.Ide

open System.Collections.Generic
open System.Text.RegularExpressions
open FSharpened.Ide.BundleKit

/// Bundling JavaScript and TypeScript projects (ADR-0024).
///
/// JavaScript keeps every file in its own scope: the files become functions in a small module table, `import` and
/// `export` become table lookups, and `require("./x")` is rewritten to the same lookup. That keeps what a module
/// system means (separate scopes, one run per module, circular imports) and handles CommonJS and ES modules alike.
///
/// TypeScript cannot do that, because types declared inside a function are invisible to the other files. Its files
/// are joined into one shared scope instead, imported files first, with `import` and `export` removed. That keeps
/// every type, so the compiler checks the whole program, at the price that two files must not declare the same name
/// and that `import { a as b }` and `import * as ns` cannot be expressed.
module BundleJs =
    type Resolved =
        | Local of string
        | Bare
        | Failed of string

    type Es =
        | Side of spec: string
        | Imp of typeOnly: bool * clause: string * spec: string
        | ExpFrom of typeOnly: bool * clause: string * spec: string
        | ExpClause of typeOnly: bool * names: string
        | ExpDefault of name: string * strip: int
        | ExpDefaultExpr of strip: int
        | ExpDecl of keyword: string * strip: int * declStart: int
        | Bad of string

    type Stmt = { Start: int; End: int; Es: Es }

    let private reSide = Regex("^import\\s*([\"'])( *)\\1[ \\t]*;?")

    let private reImp =
        Regex("^import\\s+([^\"';]*?)\\s*\\bfrom\\s*([\"'])( *)\\2[ \\t]*;?")

    let private reImpEq = Regex("^import\\s+[\\w$]+\\s*=")

    let private reExpFrom =
        Regex("^export\\s+(type\\s+)?(\\*(?:\\s+as\\s+[\\w$]+)?|\\{[^}]*\\})\\s*from\\s*([\"'])( *)\\3[ \\t]*;?")

    let private reExpClause = Regex("^export\\s+(type\\s+)?\\{([^}]*)\\}[ \\t]*;?")
    let private reExpAssign = Regex("^export\\s*=")
    let private reExport = Regex("^export\\s+")
    let private reDefault = Regex("^export\\s+default\\s+")
    let private reDefClass = Regex("^(?:abstract\\s+)?class\\s+([A-Za-z_$][\\w$]*)")

    let private reDefFunc =
        Regex("^(?:async\\s+)?function\\s*\\*?\\s*([A-Za-z_$][\\w$]*)\\s*[(<]")

    let private reDefOther =
        Regex("^(?:interface|enum|const\\s+enum)\\s+([A-Za-z_$][\\w$]*)")

    let private reDecl =
        Regex(
            "^(?:declare\\s+)?(?:abstract\\s+)?(?:async\\s+)?(const\\s+enum|enum|const|let|var|function\\s*\\*?|class|interface|type|namespace|module)\\b"
        )

    let private reWord = Regex("^\\s*\\*?\\s*([A-Za-z_$][\\w$]*)")
    let private reKeyword = Regex("\\b(import|export)\\b")
    let private reRequire = Regex("\\brequire\\s*\\(\\s*([\"'])( *)\\1\\s*\\)")
    let private reDynamic = Regex("\\bimport\\s*\\(\\s*([\"'])( *)\\1\\s*\\)")

    let private isRelative (spec: string) =
        spec = "." || spec = ".." || spec.StartsWith "./" || spec.StartsWith "../"

    /// How deep in ( [ { each position of the masked text is.
    let private depths (masked: string) : int[] =
        let d = Array.zeroCreate (masked.Length + 1)
        let mutable depth = 0

        for i in 0 .. masked.Length - 1 do
            d.[i] <- depth
            let c = masked.[i]

            if c = '{' || c = '(' || c = '[' then
                depth <- depth + 1
            elif c = '}' || c = ')' || c = ']' then
                depth <- max 0 (depth - 1)

        d.[masked.Length] <- depth
        d

    /// The import and export statements at the top level of a module.
    let statements (text: string) (masked: string) : Stmt list =
        let depth = depths masked

        let atStatementStart (idx: int) =
            if depth.[idx] <> 0 then
                false
            else
                let mutable k = idx - 1

                while k >= 0 && (masked.[k] = ' ' || masked.[k] = '\t') do
                    k <- k - 1

                k < 0 || masked.[k] = '\n' || masked.[k] = ';' || masked.[k] = '}'

        let one (idx: int) : Stmt option =
            let rest = masked.Substring idx
            let isImport = rest.StartsWith "import"

            let spec (m: Match) (g: int) =
                // the quotes were left in place by the mask: the specifier starts right after the first one
                let q = m.Value.IndexOfAny [| '"'; '\'' |]
                text.Substring(idx + q + 1, m.Groups.[g].Value.Length)

            let mk (len: int) es =
                Some
                    { Start = idx
                      End = idx + len
                      Es = es }

            if isImport then
                let m1 = reSide.Match rest

                if m1.Success then
                    mk m1.Length (Side(spec m1 2))
                else
                    let m2 = reImp.Match rest

                    if m2.Success then
                        let clause = m2.Groups.[1].Value.Trim()
                        let typeOnly = Regex.IsMatch(clause, "^type\\s+(?!from\\b)")

                        mk
                            m2.Length
                            (Imp(
                                typeOnly,
                                (if typeOnly then
                                     Regex.Replace(clause, "^type\\s+", "")
                                 else
                                     clause),
                                spec m2 3
                            ))
                    elif reImpEq.IsMatch rest then
                        mk 6 (Bad "`import x = require(...)` is not supported; use `import x from \"...\"`")
                    else
                        None
            elif reExpAssign.IsMatch rest then
                mk 6 (Bad "`export =` is not supported; use named exports or `export default`")
            else
                let mf = reExpFrom.Match rest

                if mf.Success then
                    mk mf.Length (ExpFrom(mf.Groups.[1].Success, mf.Groups.[2].Value, spec mf 4))
                else
                    let mc = reExpClause.Match rest

                    if mc.Success then
                        mk
                            mc.Length
                            (ExpClause(
                                mc.Groups.[1].Success,
                                text.Substring(idx + mc.Value.IndexOf '{' + 1, mc.Groups.[2].Value.Length)
                            ))
                    else
                        let md = reDefault.Match rest

                        if md.Success then
                            let after = rest.Substring md.Length
                            let strip = md.Length

                            let named (rx: Regex) =
                                let m = rx.Match after

                                if
                                    m.Success
                                    && m.Groups.[1].Value <> "extends"
                                    && m.Groups.[1].Value <> "implements"
                                then
                                    Some m.Groups.[1].Value
                                else
                                    None

                            let found =
                                named reDefClass
                                |> Option.orElse (named reDefFunc)
                                |> Option.orElse (named reDefOther)

                            match found with
                            | Some name -> mk strip (ExpDefault(name, strip))
                            | None -> mk strip (ExpDefaultExpr strip)
                        else
                            let me = reExport.Match rest

                            if not me.Success then
                                None
                            else
                                let after = rest.Substring me.Length
                                let dm = reDecl.Match after

                                if dm.Success then
                                    mk
                                        me.Length
                                        (ExpDecl(
                                            Regex.Replace(dm.Groups.[1].Value, "\\s+", " "),
                                            me.Length,
                                            idx + me.Length + dm.Index + dm.Length
                                        ))
                                else
                                    mk 6 (Bad "this kind of export is not supported")

        [ for m in reKeyword.Matches masked |> Seq.cast<Match> do
              if atStatementStart m.Index then
                  match one m.Index with
                  | Some s -> yield s
                  | None -> () ]

    /// The names a clause binds: the default one, the namespace one, and `{ imported as local }` pairs (`type`
    /// specifiers are dropped, they have no run-time meaning).
    let parseClause (clause: string) : string option * string option * (string * string) list =
        let c = clause.Trim()
        let o = c.IndexOf '{'
        let before = if o >= 0 then c.Substring(0, o) else c

        let inside =
            if o >= 0 then
                let e = c.IndexOf('}', o)
                c.Substring(o + 1, (if e < 0 then c.Length else e) - o - 1)
            else
                ""

        let heads =
            before.Split(',')
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> s <> "")

        let ns =
            heads
            |> Array.tryPick (fun h ->
                let m = Regex.Match(h, "^\\*\\s*as\\s+([\\w$]+)$")
                if m.Success then Some m.Groups.[1].Value else None)

        let def =
            heads
            |> Array.tryFind (fun h -> not (h.StartsWith "*") && Regex.IsMatch(h, "^[\\w$]+$"))

        let named =
            inside.Split(',')
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> s <> "" && not (Regex.IsMatch(s, "^type\\s+(?!as\\b)")))
            |> Array.map (fun s ->
                let m = Regex.Match(s, "^([\\w$]+)\\s+as\\s+([\\w$]+)$")

                if m.Success then
                    (m.Groups.[1].Value, m.Groups.[2].Value)
                else
                    (s, s))
            |> Array.toList

        (def, ns, named)

    /// Where an import specifier points.
    let resolve (files: Map<string, ProjectFile>) (lang: Language) (fromDir: string) (spec: string) : Resolved =
        if not (isRelative spec) then
            Bare
        else
            match resolveRel fromDir spec with
            | None -> Failed("\"" + spec + "\" climbs out of the project")
            | Some basePath ->
                let ts = (lang = TypeScript)

                let exts =
                    if ts then
                        [ ""; ".ts" ]
                    else
                        [ ""; ".js"; ".mjs"; ".cjs"; ".json" ]

                let swapped =
                    // TypeScript writes imports of x.ts as "./x.js"
                    if ts && Regex.IsMatch(basePath, "\\.m?js$") then
                        [ Regex.Replace(basePath, "\\.m?js$", ".ts") ]
                    else
                        []

                let direct = exts |> List.map (fun e -> basePath + e)

                let index =
                    exts
                    |> List.filter (fun e -> e <> "")
                    |> List.map (fun e -> basePath + "/index" + e)

                let candidates = direct @ swapped @ index

                match candidates |> List.tryPick (fun c -> find files c) with
                | None when
                    (if ts then [ ".js" ] else [ ".ts" ])
                    |> List.exists (fun e -> (find files (basePath + e)).IsSome)
                    ->
                    let other = if ts then JavaScript else TypeScript

                    Failed(
                        basePath
                        + (if ts then ".js" else ".ts")
                        + " is "
                        + Language.displayName other
                        + ", not "
                        + Language.displayName lang
                    )
                | None ->
                    let shown =
                        candidates
                        |> List.filter (fun c ->
                            not (c.EndsWith "/index.mjs" || c.EndsWith "/index.cjs" || c.EndsWith "/index.json"))

                    Failed("cannot find \"" + spec + "\" (looked for " + String.concat ", " shown + ")")
                | Some f ->
                    let ext = ProjectPath.extensionOf f.Path

                    match ProjectPath.languageOf f.Path with
                    | Some l when l = lang -> Local f.Path
                    | None when ext = "json" && not ts -> Local f.Path
                    | Some l -> Failed(f.Path + " is " + Language.displayName l + ", not " + Language.displayName lang)
                    | None -> Failed(f.Path + " is not a " + Language.displayName lang + " file")

    // ---- the module graph ----------------------------------------------------------------------

    type Loaded =
        {
            File: ProjectFile
            /// The text with line endings as LF.
            Text: string
            Masked: string
            Stmts: Stmt list
            Resolve: string -> Resolved
        }

    let private normal (t: string) =
        t.Replace("\r\n", "\n").Replace('\r', '\n')

    let private load (files: Map<string, ProjectFile>) (lang: Language) (file: ProjectFile) : Loaded =
        let text = normal file.Text
        let masked = Scan.mask JsLike text

        { File = file
          Text = text
          Masked = masked
          Stmts =
            (if ProjectPath.extensionOf file.Path = "json" then
                 []
             else
                 statements text masked)
          Resolve = resolve files lang (ProjectPath.parent file.Path) }

    /// A relative `require("./x")` or `import("./x")` in the code: where it is, the specifier and the call's extent.
    let private calls (l: Loaded) : (int * int * string * bool) list =
        let make (rx: Regex) (isRequire: bool) =
            [ for m in rx.Matches l.Masked |> Seq.cast<Match> do
                  let q = m.Value.IndexOfAny [| '"'; '\'' |]

                  yield
                      (m.Index,
                       m.Index + m.Length,
                       l.Text.Substring(m.Index + q + 1, m.Groups.[2].Value.Length),
                       isRequire) ]

        make reRequire true @ make reDynamic false

    /// The files a module needs, in the order it names them, and the problems with the ones it cannot have.
    let private depsOf (l: Loaded) : string list * BundleProblem list =
        let found = ResizeArray<string>()
        let problems = ResizeArray<BundleProblem>()

        let take (pos: int) (spec: string) =
            match l.Resolve spec with
            | Local p ->
                if not (found.Contains p) then
                    found.Add p
            | Bare -> ()
            | Failed why -> problems.Add(problem l.File.Path (lineOf l.Text pos) why)

        for s in l.Stmts do
            match s.Es with
            | Side spec -> take s.Start spec
            | Imp(false, _, spec) -> take s.Start spec
            | ExpFrom(false, _, spec) -> take s.Start spec
            | _ -> ()

        for (pos, _, spec, isRequire) in calls l do
            if isRelative spec then
                if isRequire then
                    take pos spec
                else
                    problems.Add(
                        problem
                            l.File.Path
                            (lineOf l.Text pos)
                            "dynamic import() of a project file cannot be bundled; use a static import"
                    )

        (List.ofSeq found, List.ofSeq problems)

    /// Every module reachable from the entry, imported ones before the files that import them (the entry is last),
    /// and the problems found on the way.
    let private graph (files: Map<string, ProjectFile>) (lang: Language) (entry: ProjectFile) =
        let loaded = Dictionary<string, Loaded>()
        let order = ResizeArray<string>()
        let problems = ResizeArray<BundleProblem>()
        let visiting = HashSet<string>()

        let rec visit (file: ProjectFile) =
            let k = ProjectPath.key file.Path

            if not (loaded.ContainsKey k) && visiting.Add k then
                let l = load files lang file
                let deps, ps = depsOf l
                problems.AddRange ps

                for d in deps do
                    match find files d with
                    | Some f -> visit f
                    | None -> ()

                loaded.[k] <- l
                order.Add file.Path

        visit entry
        (order |> Seq.map (fun p -> loaded.[ProjectPath.key p]) |> List.ofSeq, List.ofSeq problems)

    // ---- JavaScript: a module table ------------------------------------------------------------

    /// The end of a statement that starts at `from`: at a `;` outside brackets, or at a line break outside brackets that
    /// does not look like the middle of an expression.
    let private statementEnd (masked: string) (from: int) : int =
        let n = masked.Length
        let mutable depth = 0
        let mutable i = from
        let mutable fin = false

        while not fin && i < n do
            let c = masked.[i]

            if c = '{' || c = '(' || c = '[' then
                depth <- depth + 1
            elif c = '}' || c = ')' || c = ']' then
                if depth = 0 then fin <- true else depth <- depth - 1
            elif depth = 0 && c = ';' then
                fin <- true
            elif depth = 0 && c = '\n' then
                let mutable b = i - 1

                while b >= from && System.Char.IsWhiteSpace masked.[b] do
                    b <- b - 1

                let mutable a = i + 1

                while a < n && System.Char.IsWhiteSpace masked.[a] do
                    a <- a + 1

                let last = if b >= from then masked.[b] else ';'
                let next = if a < n then masked.[a] else ';'

                if ",=+-*/%&|^?:<>!~".IndexOf last < 0 && ".,?:+-*/%&|^<>=".IndexOf next < 0 then
                    fin <- true

            if not fin then
                i <- i + 1

        i

    /// The names `const a = 1, b = 2` declares, or why they cannot be listed (destructuring).
    let private declarators (masked: string) (from: int) : Result<string list, string> =
        let stop = statementEnd masked from
        let names = ResizeArray<string>()
        let mutable i = from
        let mutable depth = 0
        let mutable expectName = true
        let mutable bad = false

        while i < stop && not bad do
            let c = masked.[i]

            if expectName && not (System.Char.IsWhiteSpace c) then
                if c = '{' || c = '[' then
                    bad <- true
                else
                    let m = reWord.Match(masked.Substring(i, stop - i))

                    if m.Success then
                        names.Add m.Groups.[1].Value
                        i <- i + m.Length - 1

                    expectName <- false
            elif c = '{' || c = '(' || c = '[' then
                depth <- depth + 1
            elif c = '}' || c = ')' || c = ']' then
                depth <- max 0 (depth - 1)
            elif c = ',' && depth = 0 then
                expectName <- true

            i <- i + 1

        if bad then
            Error "exporting a destructuring pattern is not supported; export the names one by one"
        else
            Ok(List.ofSeq names)

    let private getter (name: string) (expr: string) =
        "Object.defineProperty(exports,"
        + quote name
        + ",{enumerable:true,get:function(){return "
        + expr
        + ";}});"

    /// One module as a function in the table.
    let private moduleJs (l: Loaded) : Piece list * BundleProblem list =
        let path = l.File.Path
        let id = quote path
        let edits = ResizeArray<int * int * string>()
        let header = ResizeArray<string>()
        let problems = ResizeArray<BundleProblem>()
        let mutable temp = 0
        let mutable es = false
        let at (pos: int) = lineOf l.Text pos

        let source (spec: string) (pos: int) : string =
            match l.Resolve spec with
            | Local p -> "__r(" + quote p + ")"
            | Bare -> "require(" + quote spec + ")"
            | Failed _ -> "undefined"

        for s in l.Stmts do
            es <- true

            match s.Es with
            | Side spec -> edits.Add((s.Start, s.End, source spec s.Start + ";"))
            | Imp(typeOnly, clause, spec) ->
                if typeOnly then
                    edits.Add((s.Start, s.End, ""))
                else
                    let def, ns, named = parseClause clause
                    let t = "__i" + string temp
                    temp <- temp + 1

                    let parts =
                        [ yield "const " + t + "=" + source spec s.Start + ";"
                          match def with
                          | Some d -> yield "const " + d + "=__def(" + t + ");"
                          | None -> ()
                          match ns with
                          | Some n -> yield "const " + n + "=" + t + ";"
                          | None -> ()
                          if not named.IsEmpty then
                              yield
                                  "const {"
                                  + (named
                                     |> List.map (fun (a, b) -> if a = b then a else a + ":" + b)
                                     |> String.concat ",")
                                  + "}="
                                  + t
                                  + ";" ]

                    edits.Add((s.Start, s.End, String.concat " " parts))
            | ExpFrom(typeOnly, clause, spec) ->
                if typeOnly then
                    edits.Add((s.Start, s.End, ""))
                else
                    let src = source spec s.Start
                    edits.Add((s.Start, s.End, src + ";"))

                    if clause.StartsWith "*" then
                        let m = Regex.Match(clause, "^\\*\\s+as\\s+([\\w$]+)$")

                        if m.Success then
                            header.Add(getter m.Groups.[1].Value src)
                        else
                            header.Add("__star(exports," + src + ");")
                    else
                        let _, _, named = parseClause clause

                        for (a, b) in named do
                            header.Add(getter b (src + "[" + quote a + "]"))
            | ExpClause(typeOnly, names) ->
                edits.Add((s.Start, s.End, ""))

                if not typeOnly then
                    let _, _, named = parseClause ("{" + names + "}")

                    for (a, b) in named do
                        header.Add(getter b a)
            | ExpDefault(name, strip) ->
                edits.Add((s.Start, s.Start + strip, ""))
                header.Add(getter "default" name)
            | ExpDefaultExpr strip -> edits.Add((s.Start, s.Start + strip, "exports.default = "))
            | ExpDecl(keyword, strip, declStart) ->
                edits.Add((s.Start, s.Start + strip, ""))

                match keyword with
                | "const"
                | "let"
                | "var" ->
                    match declarators l.Masked declStart with
                    | Ok names ->
                        for n in names do
                            header.Add(getter n n)
                    | Error why -> problems.Add(problem path (at s.Start) why)
                | "interface"
                | "type" -> ()
                | _ ->
                    let m = reWord.Match(l.Masked.Substring declStart)

                    if m.Success then
                        header.Add(getter m.Groups.[1].Value m.Groups.[1].Value)
            | Bad why -> problems.Add(problem path (at s.Start) why)

        for (pos, stop, spec, isRequire) in calls l do
            if isRequire && isRelative spec then
                match l.Resolve spec with
                | Local p -> edits.Add((pos, stop, "__r(" + quote p + ")"))
                | _ -> ()

        let body = bodyLines (applyEdits l.Text (List.ofSeq edits))

        let open' =
            "__m["
            + id
            + "]=function(module,exports){"
            + (if es then
                   "\"use strict\";Object.defineProperty(exports,\"__esModule\",{value:true});"
               else
                   "")
            + String.concat "" header

        let body, open' =
            if ProjectPath.extensionOf path = "json" then
                (body, "__m[" + id + "]=function(module,exports){module.exports=")
            else
                (body, open')

        ([ generated [ open' ]; fromFile path body; generated [ "};" ] ], List.ofSeq problems)

    let private preludeJs =
        [ "(function(){"
          "var __m=Object.create(null),__c=Object.create(null);"
          "function __r(id){var c=__c[id];if(c)return c.exports;c=__c[id]={exports:{}};__m[id](c,c.exports);return c.exports;}"
          "function __def(m){return m&&m.__esModule?m.default:m;}"
          "function __star(t,m){for(var k in m)if(k!==\"default\"&&!(k in t))Object.defineProperty(t,k,{enumerable:true,get:function(){return m[k];}});}" ]

    // ---- TypeScript: one shared scope ----------------------------------------------------------

    let private normaliseStatement (t: string) = Regex.Replace(t, "\\s+", " ").Trim()

    let private moduleTs
        (defaults: Dictionary<string, string>)
        (hoisted: ResizeArray<string>)
        (l: Loaded)
        : Piece list * BundleProblem list =
        let path = l.File.Path
        let edits = ResizeArray<int * int * string>()
        let problems = ResizeArray<BundleProblem>()
        let at (pos: int) = lineOf l.Text pos

        for s in l.Stmts do
            let remove () = edits.Add((s.Start, s.End, ""))

            let bare (spec: string) =
                let t = normaliseStatement (l.Text.Substring(s.Start, s.End - s.Start))

                if not (hoisted.Contains t) then
                    hoisted.Add t

                remove ()

            match s.Es with
            | Side spec -> if l.Resolve spec = Bare then bare spec else remove ()
            | Imp(typeOnly, clause, spec) ->
                match l.Resolve spec with
                | Bare -> if typeOnly then remove () else bare spec
                | Failed _ -> remove ()
                | Local target ->
                    let def, ns, named = parseClause clause
                    let mutable replacement = ""

                    if ns.IsSome then
                        problems.Add(
                            problem
                                path
                                (at s.Start)
                                "`import * as` is not supported in a TypeScript bundle; import the names you use"
                        )

                    for (a, b) in named do
                        if a <> b then
                            problems.Add(
                                problem
                                    path
                                    (at s.Start)
                                    ("`import { "
                                     + a
                                     + " as "
                                     + b
                                     + " }` is not supported in a TypeScript bundle; import `"
                                     + a
                                     + "` under its own name")
                            )

                    match def with
                    | None -> ()
                    | Some d ->
                        match defaults.TryGetValue(ProjectPath.key target) with
                        | true, n when n = d -> ()
                        | true, n when n.StartsWith "__default_" -> replacement <- "const " + d + " = " + n + ";"
                        | true, n ->
                            problems.Add(
                                problem
                                    path
                                    (at s.Start)
                                    ("import the default export of "
                                     + target
                                     + " as `"
                                     + n
                                     + "`, the name it is declared with")
                            )
                        | _ -> problems.Add(problem path (at s.Start) (target + " has no default export"))

                    edits.Add((s.Start, s.End, replacement))
            | ExpFrom(_, _, spec) -> if l.Resolve spec = Bare then bare spec else remove ()
            | ExpClause(typeOnly, names) ->
                remove ()

                if not typeOnly then
                    let _, _, named = parseClause ("{" + names + "}")

                    for (a, b) in named do
                        if a <> b && b <> "default" then
                            problems.Add(
                                problem
                                    path
                                    (at s.Start)
                                    ("`export { " + a + " as " + b + " }` is not supported in a TypeScript bundle")
                            )
            | ExpDefault(_, strip) -> edits.Add((s.Start, s.Start + strip, ""))
            | ExpDefaultExpr strip ->
                edits.Add((s.Start, s.Start + strip, "const " + defaults.[ProjectPath.key path] + " = "))
            | ExpDecl(_, strip, _) -> edits.Add((s.Start, s.Start + strip, ""))
            | Bad why -> problems.Add(problem path (at s.Start) why)

        let body = bodyLines (applyEdits l.Text (List.ofSeq edits))
        ([ fromFile path body ], List.ofSeq problems)

    /// What a module's default import means: the name of its default declaration, or a generated one for an expression.
    let private defaultsOf (loaded: Loaded list) : Dictionary<string, string> =
        let d = Dictionary<string, string>()

        loaded
        |> List.iteri (fun i l ->
            for s in l.Stmts do
                match s.Es with
                | ExpDefault(name, _) -> d.[ProjectPath.key l.File.Path] <- name
                | ExpDefaultExpr _ -> d.[ProjectPath.key l.File.Path] <- "__default_" + string i
                | ExpClause(false, names) ->
                    let _, _, named = parseClause ("{" + names + "}")

                    for (a, b) in named do
                        if b = "default" then
                            d.[ProjectPath.key l.File.Path] <- a
                | _ -> ())

        d

    // ---- entry points --------------------------------------------------------------------------

    let bundle (project: Project) (entry: ProjectFile) (lang: Language) : Result<Bundle, BundleProblem list> =
        let files = index project
        let loaded, problems = graph files lang entry

        let others =
            project.Files
            |> List.filter (fun f ->
                ProjectPath.languageOf f.Path = Some lang
                && ProjectPath.key f.Path <> ProjectPath.key entry.Path)

        let notes included =
            let left =
                others
                |> List.filter (fun f -> not (List.contains f.Path included))
                |> List.map (fun f -> f.Path)

            if left.IsEmpty then
                []
            else
                [ "not reached from " + entry.Path + ": " + String.concat ", " left ]

        if not problems.IsEmpty then
            Error problems
        elif
            loaded.Length = 1
            && (calls loaded.Head |> List.forall (fun (_, _, s, _) -> not (isRelative s)))
        then
            Ok(alone lang entry (notes [ entry.Path ]))
        elif lang = JavaScript then
            let results = loaded |> List.map moduleJs
            let bad = results |> List.collect snd

            if not bad.IsEmpty then
                Error bad
            else
                let included = loaded |> List.map (fun l -> l.File.Path)

                let pieces =
                    [ generated preludeJs ]
                    @ (results |> List.collect fst)
                    @ [ generated [ "__r(" + quote entry.Path + ");"; "})();" ] ]

                finish lang entry.Path pieces included (notes included)
        else
            let defaults = defaultsOf loaded
            let hoisted = ResizeArray<string>()
            let results = loaded |> List.map (moduleTs defaults hoisted)
            let bad = results |> List.collect snd

            if not bad.IsEmpty then
                Error bad
            else
                let included = loaded |> List.map (fun l -> l.File.Path)
                let pieces = [ generated (List.ofSeq hoisted) ] @ (results |> List.collect fst)
                finish lang entry.Path pieces included (notes included)
