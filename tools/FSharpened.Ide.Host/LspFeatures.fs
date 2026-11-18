namespace FSharpened.Ide.Host

open System
open System.Text.Json
open FSharpened.Ide

/// One entry of a file's outline as a language server gives it (`DocumentSymbol`, or `SymbolInformation` made flat).
type LspSymbol =
    {
        Name: string
        Detail: string
        /// The protocol's `SymbolKind` number (5 class, 6 method, 12 function, ...).
        Kind: int
        /// The whole declaration.
        Range: LspRange
        /// The name alone: where a click goes.
        Selection: LspRange
        Container: string
        Children: LspSymbol list
    }

type SignatureParameter = { Label: string; Doc: string }

type Signature =
    {
        Label: string
        Doc: string
        Parameters: SignatureParameter list
        /// This signature's own active parameter, when the server gives one.
        ActiveParameter: int option
    }

type SignatureHelp =
    { Signatures: Signature list
      ActiveSignature: int
      ActiveParameter: int }

/// An entry of the quick-fix menu: a `CodeAction`, or a bare `Command` (which this client cannot run).
type LspCodeAction =
    {
        Title: string
        /// "quickfix", "refactor.extract", "source.organizeImports" ... or "".
        Kind: string
        IsPreferred: bool
        /// The edit the action carries; None when it has none (it may still be resolved, or be a command).
        Edit: WorkspaceEdit option
        /// The action runs a server command (`workspace/executeCommand`), which this client does not do.
        HasCommand: bool
        /// The server says why the action cannot be used now.
        Disabled: string option
        /// The action as the server sent it, to send back to `codeAction/resolve`.
        Raw: string
    }

/// Formatting options sent with a document formatting request.
type FormatOptions = { TabSize: int; InsertSpaces: bool }

/// Reading the answers to references, outline, signature, code action and formatting requests. Pure, so tests feed it JSON.
module LspFeatureParse =
    let private str (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""

    let private num (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
        | _ -> None

    /// A string, or a `MarkupContent` (`{kind, value}`), as plain text.
    let private markup (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v -> (LspText.flatten v).Trim()
        | _ -> ""

    /// `Location`, `Location[]` and `LocationLink[]` as places on disk; locations that are not files are dropped.
    let locations (e: JsonElement) : LspLocation list =
        let items =
            match e.ValueKind with
            | JsonValueKind.Array -> [ for x in e.EnumerateArray() -> x ]
            | JsonValueKind.Object -> [ e ]
            | _ -> []

        [ for i in items do
              let uri =
                  match str i "uri" with
                  | "" -> str i "targetUri"
                  | u -> u

              let rng =
                  match i.TryGetProperty "range" with
                  | true, r -> Some r
                  | _ ->
                      match i.TryGetProperty "targetSelectionRange" with
                      | true, r -> Some r
                      | _ -> None

              match rng with
              | Some r when LspWire.ofUri uri <> "" ->
                  yield
                      { Path = LspWire.ofUri uri
                        Range = LspParse.range r }
              | _ -> () ]

    let rec private docSymbol (container: string) (e: JsonElement) : LspSymbol =
        let range = LspParse.range (e.GetProperty "range")

        let selection =
            match e.TryGetProperty "selectionRange" with
            | true, s -> LspParse.range s
            | _ -> range

        let name = str e "name"

        { Name = name
          Detail = str e "detail"
          Kind = defaultArg (num e "kind") 0
          Range = range
          Selection = selection
          Container = container
          Children =
            match e.TryGetProperty "children" with
            | true, c when c.ValueKind = JsonValueKind.Array -> [ for x in c.EnumerateArray() -> docSymbol name x ]
            | _ -> [] }

    /// `DocumentSymbol[]` (a tree) or `SymbolInformation[]` (flat, with a container name); symbols that are not in this
    /// file (a `SymbolInformation` of another URI) are kept, the caller filters by what it opened.
    let documentSymbols (e: JsonElement) : LspSymbol list =
        if e.ValueKind <> JsonValueKind.Array then
            []
        else
            [ for x in e.EnumerateArray() do
                  if x.ValueKind = JsonValueKind.Object then
                      match x.TryGetProperty "location" with
                      | true, loc ->
                          yield
                              { Name = str x "name"
                                Detail = ""
                                Kind = defaultArg (num x "kind") 0
                                Range = LspParse.range (loc.GetProperty "range")
                                Selection = LspParse.range (loc.GetProperty "range")
                                Container = str x "containerName"
                                Children = [] }
                      | _ ->
                          if (x.TryGetProperty "range" |> fst) then
                              yield docSymbol "" x ]

    let private parameter (label: string) (p: JsonElement) : SignatureParameter =
        let text =
            match p.TryGetProperty "label" with
            | true, l when l.ValueKind = JsonValueKind.String -> l.GetString()
            | true, l when l.ValueKind = JsonValueKind.Array && l.GetArrayLength() = 2 ->
                // a pair of offsets into the signature's label
                let a, b = l[0].GetInt32(), l[1].GetInt32()

                if a >= 0 && b >= a && b <= label.Length then
                    label.Substring(a, b - a)
                else
                    ""
            | _ -> ""

        { Label = text
          Doc = markup p "documentation" }

    let signatureHelp (e: JsonElement) : SignatureHelp option =
        match e.TryGetProperty "signatures" with
        | true, sigs when sigs.ValueKind = JsonValueKind.Array && sigs.GetArrayLength() > 0 ->
            let signatures =
                [ for s in sigs.EnumerateArray() do
                      let label = str s "label"

                      yield
                          { Label = label
                            Doc = markup s "documentation"
                            Parameters =
                              (match s.TryGetProperty "parameters" with
                               | true, ps when ps.ValueKind = JsonValueKind.Array ->
                                   [ for p in ps.EnumerateArray() -> parameter label p ]
                               | _ -> [])
                            ActiveParameter = num s "activeParameter" } ]

            let active =
                defaultArg (num e "activeSignature") 0 |> max 0 |> min (signatures.Length - 1)

            Some
                { Signatures = signatures
                  ActiveSignature = active
                  ActiveParameter = defaultArg (num e "activeParameter") 0 |> max 0 }
        | _ -> None

    /// The label of the parameter to emphasise: the active signature's own index first, then the help's.
    let activeParameterLabel (h: SignatureHelp) : string option =
        let s = h.Signatures[h.ActiveSignature]
        let i = defaultArg s.ActiveParameter h.ActiveParameter

        if i >= 0 && i < s.Parameters.Length && s.Parameters[i].Label <> "" then
            Some s.Parameters[i].Label
        else
            None

    let private action (e: JsonElement) : LspCodeAction option =
        let title = str e "title"

        if title = "" then
            None
        else
            let edit =
                match e.TryGetProperty "edit" with
                | true, w when w.ValueKind = JsonValueKind.Object -> Some(LspParse.workspaceEdit w)
                | _ -> None

            // a `Command` has `command` as a string; a `CodeAction` has it as an object
            let hasCommand =
                match e.TryGetProperty "command" with
                | true, c -> c.ValueKind = JsonValueKind.String || c.ValueKind = JsonValueKind.Object
                | _ -> false

            Some
                { Title = title
                  Kind = str e "kind"
                  IsPreferred =
                    (match e.TryGetProperty "isPreferred" with
                     | true, v -> v.ValueKind = JsonValueKind.True
                     | _ -> false)
                  Edit = edit
                  HasCommand = hasCommand
                  Disabled =
                    (match e.TryGetProperty "disabled" with
                     | true, d when d.ValueKind = JsonValueKind.Object ->
                         Some(
                             match str d "reason" with
                             | "" -> "not available here"
                             | r -> r
                         )
                     | _ -> None)
                  Raw = e.GetRawText() }

    let codeActions (e: JsonElement) : LspCodeAction list =
        if e.ValueKind <> JsonValueKind.Array then
            []
        else
            [ for x in e.EnumerateArray() do
                  if x.ValueKind = JsonValueKind.Object then
                      match
                          (try
                              action x
                           with _ ->
                               None)
                      with
                      | Some a -> yield a
                      | None -> () ]

    /// The edits of a formatting answer (`TextEdit[]`); an edit that cannot be read makes the whole answer unusable.
    let textEdits (e: JsonElement) : TextEdit list =
        if e.ValueKind <> JsonValueKind.Array then
            []
        else
            [ for x in e.EnumerateArray() ->
                  { Range = LspParse.range (x.GetProperty "range")
                    NewText = x.GetProperty("newText").GetString() } ]

/// The outline a language server gives, as the IDE's own `Symbol`s (sidebar outline, breadcrumbs, Ctrl+Shift+O).
module LspOutline =
    let private kindName (k: int) =
        match k with
        | 2
        | 3 -> "module"
        | 5 -> "class"
        | 6
        | 9 -> "method"
        | 7
        | 8 -> "variable"
        | 10 -> "enum"
        | 11 -> "interface"
        | 12 -> "function"
        | 13 -> "variable"
        | 14
        | 22 -> "constant"
        | 23 -> "struct"
        | 26 -> "type"
        | _ -> "variable"

    /// The tree flattened in reading order: `Depth` counts the nesting, `Container` is the parent's name. Lines and
    /// columns are 1-based.
    let toOutline (symbols: LspSymbol list) : Symbol list =
        let rec go depth (container: string) (s: LspSymbol) =
            let me: Symbol =
                { Name = s.Name
                  Kind = (if s.Kind = 9 then "constructor" else kindName s.Kind)
                  Line = s.Selection.Start.Line + 1
                  Column = s.Selection.Start.Character + 1
                  EndLine = max (s.Selection.Start.Line + 1) (s.Range.End.Line + 1)
                  Depth = depth
                  Container =
                    (if s.Container <> "" && container = "" then
                         s.Container
                     else
                         container) }

            me :: List.collect (go (depth + 1) s.Name) s.Children

        symbols |> List.collect (go 0 "")

/// The requests this client can make beyond the original handful: references, the outline, signature help, code
/// actions and formatting. Each is sent only to a server that said at `initialize` that it supports it.
[<AutoOpen>]
module LspClientFeatures =
    let private textDoc (path: string) = {| uri = LspWire.toUri path |}

    let private at (pos: LspPos) =
        {| line = pos.Line
           character = pos.Character |}

    let private rangeJson (r: LspRange) =
        {| start = at r.Start
           ``end`` = at r.End |}

    /// A capability that is present and not `false`/`null`.
    let private provides (c: JsonElement option) (name: string) =
        match c with
        | Some caps ->
            match caps.TryGetProperty name with
            | true, v -> v.ValueKind <> JsonValueKind.False && v.ValueKind <> JsonValueKind.Null
            | _ -> false
        | None -> false

    type LspClient with
        member this.SupportsReferences = provides this.ServerCapabilities "referencesProvider"

        member this.SupportsDocumentSymbols =
            provides this.ServerCapabilities "documentSymbolProvider"

        member this.SupportsSignatureHelp =
            provides this.ServerCapabilities "signatureHelpProvider"

        member this.SupportsCodeActions = provides this.ServerCapabilities "codeActionProvider"

        member this.SupportsFormatting =
            provides this.ServerCapabilities "documentFormattingProvider"

        /// Characters that open signature help on their own ("(" and ","), as the server listed them.
        member this.SignatureTriggers: string list =
            match this.ServerCapabilities with
            | Some c ->
                match c.TryGetProperty "signatureHelpProvider" with
                | true, p when p.ValueKind = JsonValueKind.Object ->
                    [ for key in [ "triggerCharacters"; "retriggerCharacters" ] do
                          match p.TryGetProperty key with
                          | true, a when a.ValueKind = JsonValueKind.Array ->
                              for t in a.EnumerateArray() do
                                  if t.ValueKind = JsonValueKind.String then
                                      yield t.GetString()
                          | _ -> () ]
                    |> List.distinct
                | _ -> []
            | None -> []

        /// Whether the server can fill in a code action's edit later (`codeAction/resolve`).
        member this.CodeActionsResolve =
            match this.ServerCapabilities with
            | Some c ->
                match c.TryGetProperty "codeActionProvider" with
                | true, p when p.ValueKind = JsonValueKind.Object ->
                    (match p.TryGetProperty "resolveProvider" with
                     | true, v -> v.ValueKind = JsonValueKind.True
                     | _ -> false)
                | _ -> false
            | None -> false

        /// Every place that uses the symbol at the position (the declaration too). Error holds the server's words.
        member this.References
            (path: string, pos: LspPos)
            : System.Threading.Tasks.Task<Result<LspLocation list, string>> =
            task {
                if not this.SupportsReferences then
                    return Error "this language server does not support find references"
                else
                    let! r =
                        this.RequestEx(
                            "textDocument/references",
                            {| textDocument = textDoc path
                               position = at pos
                               context = {| includeDeclaration = true |} |}
                        )

                    match r with
                    | Error m -> return Error m
                    | Ok None -> return Ok []
                    | Ok(Some e) ->
                        try
                            return Ok(LspFeatureParse.locations e)
                        with ex ->
                            return Error("the references could not be read: " + ex.Message)
            }

        /// The outline of one file; Ok [] when the server has none for it.
        member this.DocumentSymbols(path: string) : System.Threading.Tasks.Task<Result<LspSymbol list, string>> =
            task {
                if not this.SupportsDocumentSymbols then
                    return Error "this language server does not give an outline"
                else
                    let! r = this.RequestEx("textDocument/documentSymbol", {| textDocument = textDoc path |})

                    match r with
                    | Error m -> return Error m
                    | Ok None -> return Ok []
                    | Ok(Some e) ->
                        try
                            return Ok(LspFeatureParse.documentSymbols e)
                        with ex ->
                            return Error("the outline could not be read: " + ex.Message)
            }

        /// The signature of the call the position is in, with the parameter being typed; None when there is none.
        member this.SignatureHelp(path: string, pos: LspPos) : System.Threading.Tasks.Task<SignatureHelp option> =
            task {
                if not this.SupportsSignatureHelp then
                    return None
                else
                    let! r =
                        this.Request(
                            "textDocument/signatureHelp",
                            {| textDocument = textDoc path
                               position = at pos |}
                        )

                    match r with
                    | None -> return None
                    | Some e ->
                        try
                            return LspFeatureParse.signatureHelp e
                        with _ ->
                            return None
            }

        /// The quick fixes and refactorings for a range. `diagnostics` are the problems the server reported there.
        member this.CodeActions
            (path: string, range: LspRange, diagnostics: LspDiagnostic list)
            : System.Threading.Tasks.Task<Result<LspCodeAction list, string>> =
            task {
                if not this.SupportsCodeActions then
                    return Ok []
                else
                    let! r =
                        this.RequestEx(
                            "textDocument/codeAction",
                            {| textDocument = textDoc path
                               range = rangeJson range
                               context =
                                {| diagnostics =
                                    [| for d in diagnostics ->
                                           {| range = rangeJson d.Range
                                              severity = d.Severity
                                              code = d.Code
                                              source = d.Source
                                              message = d.Message |} |] |} |}
                        )

                    match r with
                    | Error m -> return Error m
                    | Ok None -> return Ok []
                    | Ok(Some e) -> return Ok(LspFeatureParse.codeActions e)
            }

        /// Asks the server to fill in the edit of an action that came without one. The action is returned as it was
        /// when the server cannot or does not.
        member this.ResolveCodeAction(a: LspCodeAction) : System.Threading.Tasks.Task<LspCodeAction> =
            task {
                if a.Edit.IsSome || not this.CodeActionsResolve then
                    return a
                else
                    use doc = JsonDocument.Parse a.Raw
                    let! r = this.Request("codeAction/resolve", doc.RootElement.Clone())

                    match r with
                    | Some e ->
                        match
                            (try
                                LspFeatureParse.codeActions (JsonDocument.Parse("[" + e.GetRawText() + "]").RootElement)
                             with _ ->
                                 [])
                        with
                        | resolved :: _ -> return resolved
                        | [] -> return a
                    | None -> return a
            }

        /// The edits that format the whole document; Ok [] when it already is.
        member this.FormatDocument
            (path: string, options: FormatOptions)
            : System.Threading.Tasks.Task<Result<TextEdit list, string>> =
            task {
                if not this.SupportsFormatting then
                    return Error "this language server does not format documents"
                else
                    let! r =
                        this.RequestEx(
                            "textDocument/formatting",
                            {| textDocument = textDoc path
                               options =
                                {| tabSize = options.TabSize
                                   insertSpaces = options.InsertSpaces |} |}
                        )

                    match r with
                    | Error m -> return Error m
                    | Ok None -> return Ok []
                    | Ok(Some e) ->
                        try
                            return Ok(LspFeatureParse.textEdits e)
                        with ex ->
                            return Error("the edits could not be read: " + ex.Message)
            }
