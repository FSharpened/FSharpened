namespace FSharpened.Ide

open System.Text

/// A snippet as VS Code files store it (`contributes.snippets` of an extension, or a `*.code-snippets` file).
type SnippetDef =
    {
        Name: string
        Prefixes: string list
        /// The body with its lines already joined by "\n".
        Body: string
        Description: string
        /// VS Code language ids this snippet is for. Empty means every language.
        Scope: string list
    }

/// One entry of the completion list: type `Label` and the snippet is offered.
type Snippet =
    {
        Label: string
        Detail: string
        /// The body in VS Code snippet syntax. It becomes an editor template when it is inserted.
        Body: string
        /// The IDE's language ids this snippet is for. Empty means every language.
        Languages: string list
    }

/// What a snippet body can refer to when it is inserted.
type SnippetContext =
    {
        FileName: string
        FilePath: string
        Folder: string
        Selected: string
        ProjectName: string
        Year: int
        Month: int
        Day: int
        /// The language's line comment marker, for `$LINE_COMMENT`.
        LineComment: string
    }

type SnippetTemplate =
    {
        /// The editor's template syntax: `${1:default}` fields and `${}` for the final cursor position.
        Template: string
        /// What the editor's templates cannot hold and was simplified.
        Warnings: string list
    }

/// VS Code snippet syntax converted to the editor's templates, plus built-in snippets for every language. A snippet body is
/// text with `$1`, `${2:default}`, `${3|a,b|}`, `$0` and `$VARIABLES`. The editor's templates have fields with defaults and
/// linked copies, so choices become their first option, transforms are dropped, and a literal `${` cannot be written; each
/// of those is reported.
module Snippets =
    type private Node =
        | Text of string
        | Tab of int * Node list
        | Choice of int * string list
        | Var of string * Node list

    let private isDigit (c: char) = c >= '0' && c <= '9'
    let private isIdStart (c: char) = System.Char.IsLetter c || c = '_'

    let private isIdChar (c: char) =
        System.Char.IsLetterOrDigit c || c = '_'

    let private at (s: string) (i: int) (token: string) =
        i + token.Length <= s.Length && s.Substring(i, token.Length) = token

    let private find (s: string) (token: string) (from: int) : int =
        let mutable i = from
        let mutable hit = -1

        while hit < 0 && i + token.Length <= s.Length do
            if at s i token then hit <- i else i <- i + 1

        hit

    let rec private sequence (s: string) (start: int) (nested: bool) : Node list * int =
        let nodes = ResizeArray<Node>()
        let text = StringBuilder()

        let flush () =
            if text.Length > 0 then
                nodes.Add(Text(text.ToString()))
                text.Clear() |> ignore

        let mutable i = start
        let mutable fin = false

        while not fin && i < s.Length do
            let c = s.[i]

            if
                c = '\\'
                && i + 1 < s.Length
                && (s.[i + 1] = '$' || s.[i + 1] = '}' || s.[i + 1] = '\\')
            then
                text.Append s.[i + 1] |> ignore
                i <- i + 2
            elif c = '}' && nested then
                fin <- true
            elif c = '$' then
                match dollar s i with
                | Some n, next ->
                    flush ()
                    nodes.Add n
                    i <- next
                | None, _ ->
                    text.Append '$' |> ignore
                    i <- i + 1
            else
                text.Append c |> ignore
                i <- i + 1

        flush ()
        Seq.toList nodes, i

    // s.[i] is a '$'. Returns the node and the index after it, or None when the '$' is just a character.
    and private dollar (s: string) (i: int) : Node option * int =
        if i + 1 >= s.Length then
            None, i
        else
            let c = s.[i + 1]

            if isDigit c then
                let mutable j = i + 1

                while j < s.Length && isDigit s.[j] do
                    j <- j + 1

                if j - (i + 1) > 4 then
                    None, i
                else
                    Some(Tab(int (s.Substring(i + 1, j - i - 1)), [])), j
            elif isIdStart c then
                let mutable j = i + 1

                while j < s.Length && isIdChar s.[j] do
                    j <- j + 1

                Some(Var(s.Substring(i + 1, j - i - 1), [])), j
            elif c = '{' then
                braced s (i + 2) i
            else
                None, i

    // k is just after "${". `origin` is where the '$' was, returned when the construct is malformed.
    and private braced (s: string) (k: int) (origin: int) : Node option * int =
        let isNum = k < s.Length && isDigit s.[k]
        let mutable j = k

        if isNum then
            while j < s.Length && isDigit s.[j] do
                j <- j + 1
        elif k < s.Length && isIdStart s.[k] then
            while j < s.Length && isIdChar s.[j] do
                j <- j + 1

        if j = k || j >= s.Length || (isNum && j - k > 4) then
            None, origin
        else
            let id = s.Substring(k, j - k)

            let make (body: Node list) =
                if isNum then Tab(int id, body) else Var(id, body)

            match s.[j] with
            | '}' -> Some(make []), j + 1
            | ':' ->
                let inner, next = sequence s (j + 1) true

                if next < s.Length && s.[next] = '}' then
                    Some(make inner), next + 1
                else
                    None, origin
            | '|' when isNum ->
                let close = find s "|}" (j + 1)

                if close < 0 then
                    None, origin
                else
                    let opts = s.Substring(j + 1, close - j - 1).Split(',') |> Array.toList

                    Some(Choice(int id, opts)), close + 2
            | '/' ->
                // a transform: ${1/regex/format/options}. It can hold braces of its own, so skip to the matching one.
                let mutable depth = 1
                let mutable p = j + 1

                while depth > 0 && p < s.Length do
                    if s.[p] = '\\' then
                        p <- p + 1
                    elif s.[p] = '{' then
                        depth <- depth + 1
                    elif s.[p] = '}' then
                        depth <- depth - 1

                    p <- p + 1

                if depth = 0 then (Some(make []), p) else (None, origin)
            | _ -> None, origin

    let private pad2 (n: int) =
        if n < 10 then "0" + string n else string n

    let private baseName (file: string) =
        match file.LastIndexOf '.' with
        | -1
        | 0 -> file
        | i -> file.Substring(0, i)

    /// Converts a VS Code snippet body to an editor template, filling in variables from the context.
    let template (body: string) (ctx: SnippetContext) : SnippetTemplate =
        let nodes, _ = sequence body 0 false
        let warnings = ResizeArray<string>()

        let warn (m: string) =
            if not (warnings.Contains m) then
                warnings.Add m

        let varValue (name: string) : string option =
            match name with
            | "TM_FILENAME" -> Some ctx.FileName
            | "TM_FILENAME_BASE" -> Some(baseName ctx.FileName)
            | "TM_DIRECTORY" -> Some ctx.Folder
            | "TM_FILEPATH"
            | "RELATIVE_FILEPATH" -> Some ctx.FilePath
            | "WORKSPACE_NAME" -> Some ctx.ProjectName
            | "TM_SELECTED_TEXT" -> Some ctx.Selected
            | "CLIPBOARD" -> Some ""
            | "CURRENT_YEAR" -> Some(string ctx.Year)
            | "CURRENT_YEAR_SHORT" -> Some(pad2 (ctx.Year % 100))
            | "CURRENT_MONTH" -> Some(pad2 ctx.Month)
            | "CURRENT_DATE" -> Some(pad2 ctx.Day)
            | "LINE_COMMENT" -> Some ctx.LineComment
            | _ -> None

        // the first default given to a numbered field is also what its later copies show
        let defaults = System.Collections.Generic.Dictionary<int, Node list>()

        let rec collect (ns: Node list) =
            for n in ns do
                match n with
                | Tab(i, d) when not d.IsEmpty ->
                    if not (defaults.ContainsKey i) then
                        defaults.[i] <- d

                    collect d
                | Var(_, d) -> collect d
                | _ -> ()

        collect nodes

        let rec plain (ns: Node list) : string =
            ns
            |> List.map (fun n ->
                match n with
                | Text t -> t
                | Tab(i, []) ->
                    (match defaults.TryGetValue i with
                     | true, d -> plain d
                     | _ -> "")
                | Tab(_, d) -> plain d
                | Choice(_, opts) -> List.head opts
                | Var(name, d) ->
                    match varValue name with
                    | Some v -> v
                    | None -> if d.IsEmpty then name else plain d)
            |> String.concat ""

        // the editor reads "${" and "#{" as the start of a field, so a literal one cannot be written
        let lit (t: string) =
            if t.Contains "${" || t.Contains "#{" then
                warn
                    "The text contains a literal ${ or #{, which the editor's templates cannot hold; the $ or # was left out."

                t.Replace("${", "{").Replace("#{", "{")
            else
                t

        let fieldDefault (t: string) =
            let cleaned = t.Replace("}", "").Replace("\r", "").Replace("\n", " ")

            if cleaned <> t then
                warn
                    "A field default had a closing brace or a line break, which the editor's templates cannot hold; it was removed."

            cleaned

        let field (i: int) (d: string) =
            if d = "" then
                "${" + string i + "}"
            else
                "${" + string i + ":" + fieldDefault d + "}"

        let rec render (ns: Node list) : string =
            ns
            |> List.map (fun n ->
                match n with
                | Text t -> lit t
                | Tab(0, d) -> lit (plain d) + "${}"
                | Tab(i, d) -> field i (plain (Tab(i, d) |> List.singleton))
                | Choice(i, opts) ->
                    warn "A choice ${n|a,b|} became a field with its first option as the default."
                    field i (List.head opts)
                | Var(name, d) ->
                    match varValue name with
                    | Some v -> lit v
                    | None ->
                        warn (
                            "The variable $"
                            + name
                            + " is not supported; its default or its name was inserted instead."
                        )

                        lit (if d.IsEmpty then name else plain d))
            |> String.concat ""

        { Template = render nodes
          Warnings = Seq.toList warnings }

    // ---- reading snippet files ----------------------------------------------------------------------

    let maxSnippets = 500
    let maxBody = 4000

    // VS Code language ids that are not the IDE's own
    let private scopeLanguage (id: string) : string option =
        match id.Trim().ToLowerInvariant() with
        | "javascriptreact" -> Some "javascript"
        | "typescriptreact" -> Some "typescript"
        | "c++" -> Some "cpp"
        | "csharp" -> Some "csharp"
        | other -> Language.tryOfId other |> Option.map Language.id

    /// One snippet per prefix. A snippet with no prefix, an empty body or an oversized one is dropped, and at most
    /// `maxSnippets` are kept. A scope that names only languages the IDE does not have means "no language here".
    let ofDefs (defs: SnippetDef list) : Snippet list =
        [ for d in defs do
              let langs = d.Scope |> List.choose scopeLanguage |> List.distinct

              let languages =
                  if d.Scope.IsEmpty then []
                  elif langs.IsEmpty then [ "unsupported" ]
                  else langs

              for p in d.Prefixes do
                  if p.Trim() <> "" && d.Body.Trim() <> "" && d.Body.Length <= maxBody then
                      yield
                          { Label = p.Trim()
                            Detail = (if d.Description <> "" then d.Description else d.Name)
                            Body = d.Body
                            Languages = languages } ]
        |> List.truncate maxSnippets

    // ---- built-in snippets --------------------------------------------------------------------------

    let private b (langs: string list) (label: string) (detail: string) (body: string) : Snippet =
        { Label = label
          Detail = detail
          Body = body
          Languages = langs }

    /// Snippets the IDE offers without anything imported, in each language's own idiom.
    let builtin: Snippet list =
        let fs = [ "fsharp" ]
        let cs = [ "csharp" ]
        let vb = [ "vb" ]
        let js = [ "javascript" ]
        let ts = [ "typescript" ]
        let py = [ "python" ]
        let rs = [ "rust" ]
        let dt = [ "dart" ]
        let c = [ "c" ]
        let cpp = [ "cpp" ]
        let go = [ "go" ]
        let jv = [ "java" ]
        let rb = [ "ruby" ]
        let lu = [ "lua" ]
        let ph = [ "php" ]
        let r = [ "r" ]
        let hs = [ "haskell" ]
        let ml = [ "ocaml" ]
        let both = [ "javascript"; "typescript" ]

        [ b fs "let" "a function" "let ${1:name} ${2:arg} =\n\t${0}"
          b fs "match" "match expression" "match ${1:value} with\n| ${2:pattern} -> ${0}\n| _ -> ()"
          b fs "type" "a record type" "type ${1:Name} =\n\t{ ${2:Field}: ${3:string} }"
          b fs "union" "a union type" "type ${1:Name} =\n\t| ${2:Case}${0}"
          b fs "for" "for loop" "for ${1:i} in ${2:0} .. ${3:10} do\n\t${0}"
          b fs "printfn" "print a line" "printfn \"${1:%A}\" ${0}"
          b cs "class" "a class" "public class ${1:Name}\n{\n\t${0}\n}"
          b cs "prop" "an auto property" "public ${1:int} ${2:Name} { get; set; }"
          b cs "for" "for loop" "for (int ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++)\n{\n\t${0}\n}"
          b cs "foreach" "foreach loop" "foreach (var ${1:item} in ${2:items})\n{\n\t${0}\n}"
          b cs "cw" "Console.WriteLine" "Console.WriteLine(${1});"
          b vb "sub" "a Sub" "Sub ${1:Name}(${2})\n\t${0}\nEnd Sub"
          b vb "func" "a Function" "Function ${1:Name}(${2}) As ${3:Integer}\n\t${0}\nEnd Function"
          b vb "for" "For loop" "For ${1:i} As Integer = 0 To ${2:n}\n\t${0}\nNext"
          b js "fn" "a function" "function ${1:name}(${2:args}) {\n\t${0}\n}"
          b js "class" "a class" "class ${1:Name} {\n\tconstructor(${2}) {\n\t\t${0}\n\t}\n}"
          b ts "class" "a class" "class ${1:Name} {\n\tconstructor(${2}) {\n\t\t${0}\n\t}\n}"
          b ts "interface" "an interface" "interface ${1:Name} {\n\t${0}\n}"
          b both "arrow" "an arrow function" "const ${1:name} = (${2:args}) => {\n\t${0}\n};"
          b both "log" "console.log" "console.log(${1});"
          b both "for" "for loop" "for (let ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++) {\n\t${0}\n}"
          b both "forof" "for...of loop" "for (const ${1:item} of ${2:items}) {\n\t${0}\n}"
          b py "def" "a function" "def ${1:name}(${2:args}):\n\t${0:pass}"
          b py "class" "a class" "class ${1:Name}:\n\tdef __init__(self${2}):\n\t\t${0:pass}"
          b py "for" "for loop" "for ${1:item} in ${2:items}:\n\t${0}"
          b py "main" "main guard" "if __name__ == \"__main__\":\n\t${0:main()}"
          b py "with" "with statement" "with ${1:open(path)} as ${2:f}:\n\t${0}"
          b rs "fn" "a function" "fn ${1:name}(${2}) -> ${3:()} {\n\t${0}\n}"
          b rs "struct" "a struct" "struct ${1:Name} {\n\t${0}\n}"
          b rs "impl" "an impl block" "impl ${1:Type} {\n\t${0}\n}"
          b rs "match" "match expression" "match ${1:value} {\n\t${2:pattern} => ${3:expr},\n\t_ => ${0:()},\n}"
          b rs "for" "for loop" "for ${1:item} in ${2:items} {\n\t${0}\n}"
          b rs "println" "println!" "println!(\"{}\", ${1});"
          b dt "main" "main function" "void main() {\n\t${0}\n}"
          b dt "class" "a class" "class ${1:Name} {\n\t${0}\n}"
          b dt "for" "for loop" "for (var ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++) {\n\t${0}\n}"
          b c "main" "main with stdio" "#include <stdio.h>\n\nint main(void) {\n\t${0}\n\treturn 0;\n}"
          b c "for" "for loop" "for (int ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++) {\n\t${0}\n}"
          b c "struct" "a struct" "typedef struct {\n\t${0}\n} ${1:Name};"
          b c "printf" "printf" "printf(\"${1}\\n\"${2});"
          b cpp "main" "main with iostream" "#include <iostream>\n\nint main() {\n\t${0}\n\treturn 0;\n}"
          b cpp "class" "a class" "class ${1:Name} {\npublic:\n\t${0}\n};"
          b cpp "for" "for loop" "for (int ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++) {\n\t${0}\n}"
          b cpp "cout" "std::cout" "std::cout << ${1} << std::endl;"
          b go "main" "main package" "package main\n\nimport \"fmt\"\n\nfunc main() {\n\t${0}\n}"
          b go "func" "a function" "func ${1:name}(${2}) ${3:error} {\n\t${0}\n}"
          b go "for" "for loop" "for ${1:i} := 0; ${1:i} < ${2:n}; ${1:i}++ {\n\t${0}\n}"
          b go "iferr" "if err != nil" "if err != nil {\n\treturn ${1:err}\n}"
          b go "println" "fmt.Println" "fmt.Println(${1})"
          b jv "main" "main method" "public static void main(String[] args) {\n\t${0}\n}"
          b jv "class" "a class" "public class ${1:Name} {\n\t${0}\n}"
          b jv "for" "for loop" "for (int ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++) {\n\t${0}\n}"
          b jv "sout" "System.out.println" "System.out.println(${1});"
          b rb "def" "a method" "def ${1:name}(${2})\n\t${0}\nend"
          b rb "class" "a class" "class ${1:Name}\n\t${0}\nend"
          b rb "each" "each block" "${1:items}.each do |${2:item}|\n\t${0}\nend"
          b rb "puts" "puts" "puts ${1}"
          b lu "fn" "a function" "function ${1:name}(${2})\n\t${0}\nend"
          b lu "for" "numeric for" "for ${1:i} = ${2:1}, ${3:n} do\n\t${0}\nend"
          b lu "forp" "for pairs" "for ${1:k}, ${2:v} in pairs(${3:t}) do\n\t${0}\nend"
          b ph "fn" "a function" "function ${1:name}(${2}) {\n\t${0}\n}"
          b ph "class" "a class" "class ${1:Name}\n{\n\t${0}\n}"
          b ph "foreach" "foreach loop" "foreach (${1:\\$items} as ${2:\\$item}) {\n\t${0}\n}"
          b ph "echo" "echo" "echo ${1} . PHP_EOL;"
          b r "fn" "a function" "${1:name} <- function(${2}) {\n\t${0}\n}"
          b r "for" "for loop" "for (${1:i} in ${2:seq_len(n)}) {\n\t${0}\n}"
          b r "if" "if else" "if (${1:cond}) {\n\t${2}\n} else {\n\t${0}\n}"
          b hs "fn" "a function with its type" "${1:name} :: ${2:Int} -> ${3:Int}\n${1:name} ${4:x} = ${0}"
          b hs "data" "a data type" "data ${1:Name} = ${2:Constructor}"
          b hs "main" "main" "main :: IO ()\nmain = do\n\t${0}"
          b hs "case" "case expression" "case ${1:value} of\n\t${2:pattern} -> ${0}"
          b ml "let" "a function" "let ${1:name} ${2:arg} =\n\t${0}"
          b ml "match" "match expression" "match ${1:value} with\n| ${2:pattern} -> ${0}"
          b ml "type" "a variant type" "type ${1:name} =\n\t| ${2:Case}${0}" ]

    /// The snippets offered in a language: the built-in ones, then the imported ones that apply to it.
    let forLanguage (languageId: string) (imported: Snippet list) : Snippet list =
        let applies (s: Snippet) =
            s.Languages.IsEmpty || List.contains languageId s.Languages

        (builtin @ imported) |> List.filter applies
