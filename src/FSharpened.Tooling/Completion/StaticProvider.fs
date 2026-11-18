namespace FSharpened.Tooling.Completion

open System.Threading

/// The provider that cannot fail: keywords and snippets that continue the word at the cursor. No model, no network,
/// no state; the same request always gives the same answer (ADR-0012: the symbolic layer is the guaranteed path).
module StaticProvider =
    let private words =
        Map.ofList
            [ "fsharp",
              [ "let"
                "let rec"
                "match"
                "with"
                "function"
                "fun"
                "type"
                "module"
                "namespace"
                "open"
                "if"
                "then"
                "elif"
                "else"
                "for"
                "while"
                "do"
                "yield"
                "use"
                "member"
                "interface"
                "inherit"
                "async"
                "task"
                "try"
                "finally"
                "printfn"
                "failwith"
                "Some"
                "None"
                "List.map"
                "Seq.map"
                "Option.map" ]
              "csharp",
              [ "using"
                "namespace"
                "class"
                "struct"
                "record"
                "interface"
                "public"
                "private"
                "static"
                "async"
                "await"
                "var"
                "foreach"
                "return"
                "switch"
                "string"
                "int"
                "bool"
                "void"
                "Console.WriteLine"
                "throw new" ]
              "typescript",
              [ "const"
                "let"
                "function"
                "async"
                "await"
                "return"
                "interface"
                "type"
                "export"
                "import"
                "from"
                "class"
                "extends"
                "implements"
                "if"
                "else"
                "for"
                "while"
                "switch"
                "console.log"
                "throw new Error" ]
              "javascript",
              [ "const"
                "let"
                "function"
                "async"
                "await"
                "return"
                "export"
                "import"
                "from"
                "class"
                "extends"
                "if"
                "else"
                "for"
                "while"
                "switch"
                "console.log"
                "throw new Error" ]
              "python",
              [ "def"
                "class"
                "import"
                "from"
                "return"
                "if"
                "elif"
                "else"
                "for"
                "while"
                "with"
                "as"
                "try"
                "except"
                "finally"
                "lambda"
                "yield"
                "async"
                "await"
                "print"
                "raise"
                "None"
                "True"
                "False" ]
              "rust",
              [ "fn"
                "let"
                "let mut"
                "struct"
                "enum"
                "impl"
                "trait"
                "pub"
                "use"
                "mod"
                "match"
                "if let"
                "while let"
                "for"
                "loop"
                "return"
                "Some"
                "None"
                "Ok"
                "Err"
                "println!"
                "unwrap_or"
                "Vec::new" ]
              "go",
              [ "func"
                "package"
                "import"
                "var"
                "const"
                "type"
                "struct"
                "interface"
                "return"
                "if"
                "else"
                "for"
                "range"
                "switch"
                "defer"
                "go"
                "chan"
                "select"
                "fmt.Println"
                "make"
                "nil" ]
              "c",
              [ "#include"
                "#define"
                "int"
                "char"
                "void"
                "struct"
                "typedef"
                "return"
                "if"
                "else"
                "for"
                "while"
                "switch"
                "static"
                "const"
                "sizeof"
                "printf"
                "NULL" ]
              "cpp",
              [ "#include"
                "#define"
                "namespace"
                "class"
                "struct"
                "template"
                "typename"
                "int"
                "auto"
                "void"
                "return"
                "if"
                "else"
                "for"
                "while"
                "switch"
                "std::string"
                "std::vector"
                "std::cout"
                "nullptr" ]
              "java",
              [ "import"
                "package"
                "class"
                "interface"
                "public"
                "private"
                "static"
                "void"
                "int"
                "String"
                "return"
                "if"
                "else"
                "for"
                "while"
                "switch"
                "try"
                "catch"
                "throw new"
                "System.out.println" ] ]

    let private isWordChar (c: char) =
        System.Char.IsLetterOrDigit c
        || c = '_'
        || c = '#'
        || c = '.'
        || c = ':'
        || c = '!'

    /// The identifier characters directly before the cursor: the word being typed.
    let currentWord (prefix: string) : string =
        let mutable i = prefix.Length

        while i > 0 && isWordChar prefix.[i - 1] do
            i <- i - 1
        // a word starts with a letter, underscore or '#', not with ':' '.' or a digit
        while i < prefix.Length
              && not (System.Char.IsLetter prefix.[i] || prefix.[i] = '_' || prefix.[i] = '#') do
            i <- i + 1

        prefix.Substring i

    /// A snippet to offer by name: type `Prefix` and `Body` is suggested. The IDE passes the ones it already has.
    type SnippetEntry =
        { Label: string
          Body: string
          Languages: string list }

    let candidates (snippets: SnippetEntry list) (language: string) (prefix: string) (max: int) : Candidate list =
        let word = currentWord prefix

        if word.Length = 0 then
            []
        else
            let starts (w: string) =
                w.Length > word.Length
                && w.StartsWith(word, System.StringComparison.OrdinalIgnoreCase)

            let kw =
                (Map.tryFind language words |> Option.defaultValue [])
                |> List.filter starts
                |> List.sortBy (fun w -> w.Length, w)
                |> List.map (fun w ->
                    { Text = w.Substring word.Length
                      Kind = Keyword
                      Confidence = 0.3 })

            let sn =
                snippets
                |> List.filter (fun s -> (s.Languages.IsEmpty || List.contains language s.Languages) && starts s.Label)
                |> List.sortBy (fun s -> s.Label.Length, s.Label)
                |> List.map (fun s ->
                    { Text = s.Body
                      Kind = Snippet
                      Confidence = 0.4 })

            (kw @ sn) |> List.truncate max

    /// The provider. `snippets` is whatever the host already has (the IDE's snippet list).
    let create (snippets: SnippetEntry list) : ICompletionProvider =
        { new ICompletionProvider with
            member _.Name = "static"
            member _.IsRemote = false

            member _.Complete(request, ct) =
                async {
                    if ct.IsCancellationRequested then
                        return Unavailable Cancelled
                    else
                        match candidates snippets request.Language request.Prefix 5 with
                        | [] -> return Unavailable Empty
                        | cs -> return Answer cs
                } }
