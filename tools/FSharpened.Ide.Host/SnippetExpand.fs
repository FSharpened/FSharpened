/// Turning a snippet into text with places to fill in, and reading the person's own snippet files. Pure.
namespace FSharpened.Ide.Host

open System
open System.Text
open System.Text.Json
open FSharpened.Ide

/// The places of a snippet, in the order Tab visits them: `Index` is the field's number (0 is where the caret ends),
/// `Ranges` are the (offset, length) of the field and of its linked copies in the expanded text.
type SnippetStop =
    { Index: int; Ranges: (int * int) list }

/// A snippet ready to insert.
type ExpandedSnippet =
    {
        Text: string
        /// Fields 1, 2, ... in order, then the final stop (index 0, empty) last. Always at least the final stop.
        Stops: SnippetStop list
    }

module SnippetExpand =
    /// Reads the editor's template syntax that `Snippets.template` produces: `${1:default}`, `${1}` (a copy of field
    /// 1, or an empty field) and `${}` for the final caret position.
    let parse (template: string) : ExpandedSnippet =
        let sb = StringBuilder()
        let fields = Collections.Generic.SortedDictionary<int, ResizeArray<int * int>>()
        let mutable final: int option = None
        let mutable i = 0

        while i < template.Length do
            if template[i] = '$' && i + 1 < template.Length && template[i + 1] = '{' then
                match template.IndexOf('}', i + 2) with
                | -1 ->
                    sb.Append(template, i, template.Length - i) |> ignore
                    i <- template.Length
                | close ->
                    let inner = template.Substring(i + 2, close - i - 2)

                    let number, def =
                        match inner.IndexOf ':' with
                        | -1 -> inner, ""
                        | c -> inner.Substring(0, c), inner.Substring(c + 1)

                    match Int32.TryParse number with
                    | true, n when n > 0 ->
                        let at = sb.Length
                        sb.Append def |> ignore

                        if not (fields.ContainsKey n) then
                            fields[n] <- ResizeArray()

                        fields[n].Add((at, def.Length))
                    | _ when number = "" -> final <- Some sb.Length
                    | _ -> sb.Append(template, i, close - i + 1) |> ignore

                    i <- close + 1
            else
                sb.Append template[i] |> ignore
                i <- i + 1

        let stops =
            [ for kv in fields ->
                  { Index = kv.Key
                    Ranges = Seq.toList kv.Value } ]
            @ [ { Index = 0
                  Ranges = [ defaultArg final sb.Length, 0 ] } ]

        { Text = sb.ToString(); Stops = stops }

    /// Fits the text to where it goes: each line after the first starts with the indentation of the line it is put
    /// on, and a tab in the snippet is `unit` (a tab, or the spaces of one indent).
    let layout (indent: string) (unit: string) (lineBreak: string) (s: ExpandedSnippet) : ExpandedSnippet =
        let map = Array.zeroCreate<int> (s.Text.Length + 1)
        let sb = StringBuilder()

        for i in 0 .. s.Text.Length - 1 do
            map[i] <- sb.Length

            match s.Text[i] with
            | '\n' -> sb.Append(lineBreak).Append(indent) |> ignore
            | '\t' -> sb.Append unit |> ignore
            | '\r' -> ()
            | c -> sb.Append c |> ignore

        map[s.Text.Length] <- sb.Length

        let fit (o: int, l: int) =
            let a = map[min o s.Text.Length]
            let b = map[min (o + l) s.Text.Length]
            a, b - a

        { Text = sb.ToString()
          Stops =
            s.Stops
            |> List.map (fun st ->
                { st with
                    Ranges = st.Ranges |> List.map fit }) }

    /// The word right before `offset` in the text, when it can be the prefix of a snippet (letters, digits, `_`, `-`
    /// and `.`), with where it starts.
    let prefixBefore (text: string) (offset: int) : (string * int) option =
        let o = max 0 (min offset text.Length)
        let mutable s = o

        while s > 0
              && (Char.IsLetterOrDigit text[s - 1]
                  || text[s - 1] = '_'
                  || text[s - 1] = '-'
                  || text[s - 1] = '.') do
            s <- s - 1

        if s < o then Some(text.Substring(s, o - s), s) else None

    /// The snippet whose label is exactly `word`, if there is one (the first, so a built-in wins over an import).
    let find (snippets: Snippet list) (word: string) : Snippet option =
        snippets |> List.tryFind (fun s -> s.Label = word)

module SnippetFiles =
    /// More than this many bytes of a snippet file are not read.
    let maxBytes = 1024 * 1024

    let private strings (el: JsonElement) : string list =
        match el.ValueKind with
        | JsonValueKind.String -> [ el.GetString() ]
        | JsonValueKind.Array ->
            [ for x in el.EnumerateArray() do
                  if x.ValueKind = JsonValueKind.String then
                      yield x.GetString() ]
        | _ -> []

    /// Reads a snippet file in VS Code's format: `{ "name": { "prefix": "x" or ["x", "y"], "body": "text" or ["line",
    /// "line"], "description": "...", "scope": "fsharp,go" } }`. A `fsharp.json` (not `.code-snippets`) belongs to
    /// the language its name says. Comments and trailing commas are allowed. Returns the snippets and what was wrong.
    let parse (fileName: string) (json: string) : SnippetDef list * string list =
        let options =
            JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)

        try
            use doc = JsonDocument.Parse(json, options)

            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                [], [ sprintf "%s: not an object of snippets" fileName ]
            else
                let stem = IO.Path.GetFileNameWithoutExtension fileName

                let fileScope =
                    if fileName.EndsWith(".code-snippets", StringComparison.OrdinalIgnoreCase) then
                        []
                    else
                        [ stem ]

                let defs = ResizeArray<SnippetDef>()
                let problems = ResizeArray<string>()

                for p in doc.RootElement.EnumerateObject() do
                    if p.Value.ValueKind <> JsonValueKind.Object then
                        problems.Add(sprintf "%s: “%s” is not an object" fileName p.Name)
                    else
                        let get (name: string) =
                            match p.Value.TryGetProperty name with
                            | true, v -> Some v
                            | _ -> None

                        let prefixes = get "prefix" |> Option.map strings |> Option.defaultValue []
                        let body = get "body" |> Option.map strings |> Option.defaultValue []

                        let description =
                            get "description"
                            |> Option.map strings
                            |> Option.bind List.tryHead
                            |> Option.defaultValue ""

                        let scope =
                            match get "scope" |> Option.map strings |> Option.bind List.tryHead with
                            | Some s ->
                                s.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                                |> Array.toList
                            | None -> fileScope

                        if prefixes.IsEmpty then
                            problems.Add(sprintf "%s: “%s” has no prefix" fileName p.Name)
                        elif body.IsEmpty then
                            problems.Add(sprintf "%s: “%s” has no body" fileName p.Name)
                        else
                            defs.Add
                                { Name = p.Name
                                  Prefixes = prefixes
                                  Body = String.Join("\n", body)
                                  Description = description
                                  Scope = scope }

                Seq.toList defs, Seq.toList problems
        with :? JsonException as e ->
            [], [ sprintf "%s: not valid JSON (%s)" fileName e.Message ]

    /// Every `*.json` and `*.code-snippets` file of a folder, as the snippets the editor offers, and the problems.
    let loadFolder (folder: string) : Snippet list * string list =
        if not (IO.Directory.Exists folder) then
            [], []
        else
            let files =
                try
                    IO.Directory.EnumerateFiles folder
                    |> Seq.filter (fun f ->
                        f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".code-snippets", StringComparison.OrdinalIgnoreCase))
                    |> Seq.sort
                    |> Seq.truncate 50
                    |> Seq.toList
                with _ ->
                    []

            let results =
                [ for f in files ->
                      try
                          if IO.FileInfo(f).Length > int64 maxBytes then
                              [], [ sprintf "%s: larger than %d bytes, not read" (IO.Path.GetFileName f) maxBytes ]
                          else
                              parse (IO.Path.GetFileName f) (IO.File.ReadAllText f)
                      with e ->
                          [], [ sprintf "%s: %s" (IO.Path.GetFileName f) e.Message ] ]

            let defs = results |> List.collect fst
            Snippets.ofDefs defs, results |> List.collect snd
