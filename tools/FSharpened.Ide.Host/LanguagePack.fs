namespace FSharpened.Ide.Host

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// What a step of a plan is for. A failed `Compile` step stops the plan and is reported as a build failure.
type StepPhase =
    | Compile
    | Execute
    | Verify

/// One program to start. `Tool` is the id of a tool the pack declares, or "built" for the output of an earlier compile
/// step. Arguments may hold `{entry}`, `{out}`, `{dir}`, `{root}` and `{name}`; no shell is ever involved.
type Step =
    { Phase: StepPhase
      Tool: string
      Args: string list }

/// Steps that apply when every pattern in `Files` matches a file in the folder and the entry file's extension is one of
/// `EntryExtensions` (an empty list means no condition). The first plan that applies is used, so a project file can
/// take precedence over a single-file run.
type Plan =
    { Files: string list
      EntryExtensions: string list
      Steps: Step list }

/// A program a pack may start. Candidates are tried in order; the first whose version command succeeds is used.
type ToolSpec =
    { Id: string
      Label: string
      Candidates: string list list
      Version: string list
      Install: string }

/// A language server or a debug adapter: a program to start and how to get it.
type ServerSpec =
    { Command: string
      Args: string list
      Install: string }

/// A language, as data (`fsharpened.language/1`). Adding a language is adding a file; a pack declares and runs nothing
/// by itself (ADR-0023).
type LanguagePack =
    {
        Id: string
        Name: string
        Extensions: string list
        FileNames: string list
        /// The TextMate scope of the syntax grammar ("source.go"), or "".
        Scope: string
        /// A grammar file of the pack's own, relative to the pack file; used when the app does not ship the scope.
        Grammar: string option
        LineComment: string option
        BlockComment: (string * string) option
        UseTabs: bool
        IndentSize: int
        Server: ServerSpec option
        Run: Plan list
        Test: Plan list
        Format: Step option
        Debug: ServerSpec option
        Tools: ToolSpec list
        /// Where the pack came from (a file path), for messages.
        Origin: string
    }

type PackProblem = { Origin: string; Message: string }

type Registry =
    { Packs: LanguagePack list
      Problems: PackProblem list }

module LanguagePack =
    [<Literal>]
    let FormatId = "fsharpened.language/1"

    let placeholders = [ "entry"; "out"; "dir"; "root"; "name"; "flags" ]

    exception private Bad of string

    let private bad fmt =
        Printf.ksprintf (fun s -> raise (Bad s)) fmt

    let private idRe = Regex("^[a-z0-9][a-z0-9+#._-]{0,31}$", RegexOptions.Compiled)

    let private extRe =
        Regex("^\\.[A-Za-z0-9][A-Za-z0-9._+-]{0,15}$", RegexOptions.Compiled)

    let private nameRe =
        Regex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", RegexOptions.Compiled)

    let private fileNameRe =
        Regex("^\.?[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", RegexOptions.Compiled)

    let private scopeRe = Regex("^[a-z][a-z0-9.+_-]{0,63}$", RegexOptions.Compiled)
    let private globRe = Regex("^[A-Za-z0-9._*?-]{1,64}$", RegexOptions.Compiled)
    let private holderRe = Regex("\\{([^{}]*)\\}", RegexOptions.Compiled)
    let private toolIdRe = Regex("^[a-z0-9][a-z0-9+._-]{0,31}$", RegexOptions.Compiled)

    let private prop (e: JsonElement) (n: string) =
        if e.ValueKind <> JsonValueKind.Object then
            None
        else
            match e.TryGetProperty n with
            | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
            | _ -> None

    let private text (e: JsonElement) (n: string) (limit: int) =
        match prop e n with
        | None -> None
        | Some v when v.ValueKind = JsonValueKind.String ->
            let s = v.GetString()

            if s.Length > limit then
                bad "“%s” is longer than %d characters" n limit
            elif s |> Seq.exists (fun c -> c = '\000' || c = '\n' || c = '\r') then
                bad "“%s” holds a control character" n
            else
                Some s
        | Some _ -> bad "“%s” must be text" n

    let private items (e: JsonElement) (n: string) (limit: int) =
        match prop e n with
        | None -> []
        | Some v when v.ValueKind = JsonValueKind.Array ->
            if v.GetArrayLength() > limit then
                bad "“%s” has more than %d entries" n limit
            else
                v.EnumerateArray() |> Seq.toList
        | Some _ -> bad "“%s” must be a list" n

    let private strings (e: JsonElement) (n: string) (limit: int) (each: int) =
        items e n limit
        |> List.map (fun v ->
            if v.ValueKind <> JsonValueKind.String then
                bad "“%s” must hold text only" n

            let s = v.GetString()

            if s.Length > each then
                bad "an entry of “%s” is longer than %d characters" n each
            elif s |> Seq.exists (fun c -> c = '\000') then
                bad "an entry of “%s” holds a control character" n
            else
                s)

    /// A program name looked up on the PATH: never a path, so a pack cannot point at a file it ships.
    let private program (what: string) (s: string) =
        if nameRe.IsMatch s then
            s
        else
            bad "%s “%s” must be a bare program name, not a path" what s

    let private checkPlaceholders (what: string) (s: string) =
        for m in holderRe.Matches s do
            if not (List.contains m.Groups[1].Value placeholders) then
                bad
                    "%s uses {%s}, which is not one of %s"
                    what
                    m.Groups[1].Value
                    (String.Join(", ", placeholders |> List.map (fun p -> "{" + p + "}")))

    let private parseStep
        (declared: Set<string>)
        (builtBefore: bool)
        (fixedPhase: StepPhase option)
        (e: JsonElement)
        : Step =
        let phase =
            match fixedPhase, text e "phase" 16 with
            | Some p, _ -> p
            | None, Some "build" -> Compile
            | None, Some "run" -> Execute
            | None, Some "test" -> Verify
            | None, Some other -> bad "a step has phase “%s” (use build, run or test)" other
            | None, None -> bad "a step needs a phase"

        let tool =
            match text e "tool" 32 with
            | Some "built" when builtBefore -> "built"
            | Some "built" -> bad "a step uses “built” but no earlier step compiles anything"
            | Some t when declared.Contains t -> t
            | Some t -> bad "a step uses the tool “%s”, which the pack does not declare in “tools”" t
            | None -> bad "a step needs a tool"

        let args = strings e "args" 40 1000
        args |> List.iter (checkPlaceholders "an argument")

        { Phase = phase
          Tool = tool
          Args = args }

    let private parsePlans (declared: Set<string>) (e: JsonElement) (n: string) : Plan list =
        items e n 8
        |> List.map (fun p ->
            let files =
                match prop p "when" with
                | None -> []
                | Some w ->
                    strings w "files" 8 64
                    |> List.map (fun g ->
                        if globRe.IsMatch g then
                            g
                        else
                            bad "the pattern “%s” in “when.files” is not a plain file pattern" g)

            let exts =
                match prop p "when" with
                | None -> []
                | Some w ->
                    strings w "entryExtensions" 8 16
                    |> List.map (fun x ->
                        if extRe.IsMatch x then
                            x.ToLowerInvariant()
                        else
                            bad "the extension “%s” in “when.entryExtensions” must look like .fsx" x)

            let raw = items p "steps" 8

            if raw.IsEmpty then
                bad "a plan in “%s” has no steps" n

            let steps, _ =
                raw
                |> List.fold
                    (fun (acc, built) s ->
                        let st = parseStep declared built None s
                        acc @ [ st ], built || st.Phase = Compile)
                    ([], false)

            { Files = files
              EntryExtensions = exts
              Steps = steps })

    let private parseTool (e: JsonElement) : ToolSpec =
        let id =
            match text e "id" 32 with
            | Some i when toolIdRe.IsMatch i -> i
            | Some i -> bad "the tool id “%s” is not valid" i
            | None -> bad "a tool needs an id"

        let cands =
            items e "candidates" 6
            |> List.map (fun c ->
                if c.ValueKind <> JsonValueKind.Array || c.GetArrayLength() = 0 then
                    bad "a candidate of tool “%s” must be a non-empty list" id

                let parts =
                    c.EnumerateArray()
                    |> Seq.map (fun v ->
                        if v.ValueKind = JsonValueKind.String then
                            v.GetString()
                        else
                            bad "a candidate of tool “%s” must hold text" id)
                    |> Seq.toList

                program "the program" parts.Head |> ignore

                if parts |> List.exists (fun p -> p.Length > 200 || p.Contains '\000') then
                    bad "a candidate of tool “%s” holds an unusable argument" id

                parts)

        if cands.IsEmpty then
            bad "the tool “%s” needs at least one candidate" id

        { Id = id
          Label = text e "label" 60 |> Option.defaultValue id
          Candidates = cands
          Version = strings e "version" 4 100
          Install = text e "install" 300 |> Option.defaultValue "" }

    let private parseServer (e: JsonElement) (cmdName: string) : ServerSpec =
        let cmd =
            match text e cmdName 64 with
            | Some c -> program "the program" c
            | None -> bad "“%s” is needed" cmdName

        { Command = cmd
          Args = strings e "args" 20 300
          Install = text e "install" 300 |> Option.defaultValue "" }

    let private parseRoot (origin: string) (root: JsonElement) : LanguagePack =
        if root.ValueKind <> JsonValueKind.Object then
            bad "a pack must be a JSON object"

        match text root "format" 40 with
        | Some f when f = FormatId -> ()
        | Some f -> bad "the format is “%s”; this app reads “%s”" f FormatId
        | None -> bad "the pack has no “format”"

        let id =
            match text root "id" 32 with
            | Some i when idRe.IsMatch i -> i
            | Some i -> bad "the id “%s” is not valid (lower case letters, digits and + # . _ -, at most 32)" i
            | None -> bad "the pack has no “id”"

        let name =
            match text root "name" 60 with
            | Some n when n.Trim().Length > 0 -> n.Trim()
            | _ -> bad "the pack has no “name”"

        let exts =
            strings root "extensions" 32 16
            |> List.map (fun x ->
                if extRe.IsMatch x then
                    x.ToLowerInvariant()
                else
                    bad "the extension “%s” must look like .go" x)

        let names =
            strings root "filenames" 32 64
            |> List.map (fun x ->
                if fileNameRe.IsMatch x then
                    x
                else
                    bad "the file name “%s” is not valid" x)

        if exts.IsEmpty && names.IsEmpty then
            bad "a pack needs at least one extension or file name"

        let scope =
            match text root "scope" 64 with
            | None
            | Some "" -> ""
            | Some s when scopeRe.IsMatch s -> s
            | Some s -> bad "the scope “%s” is not a TextMate scope name" s

        let grammar =
            text root "grammar" 200
            |> Option.map (fun g ->
                if g.Contains ".." || Path.IsPathRooted g || g.Contains ':' then
                    bad "the grammar path “%s” must stay inside the pack's folder" g
                else
                    g)

        let line, block =
            match prop root "comments" with
            | None -> None, None
            | Some c ->
                let lc = text c "line" 8
                let bc = strings c "block" 2 8

                match bc with
                | [] -> lc, None
                | [ a; b ] -> lc, Some(a, b)
                | _ -> bad "“comments.block” needs exactly two entries"

        let tabs, size =
            match prop root "indent" with
            | None -> false, 4
            | Some i ->
                let t =
                    match prop i "tabs" with
                    | Some v when v.ValueKind = JsonValueKind.True -> true
                    | _ -> false

                let s =
                    match prop i "size" with
                    | Some v when v.ValueKind = JsonValueKind.Number && v.GetInt32() >= 1 && v.GetInt32() <= 8 ->
                        v.GetInt32()
                    | Some _ -> bad "“indent.size” must be a number from 1 to 8"
                    | None -> 4

                t, s

        let tools =
            match prop root "tools" with
            | None -> []
            | Some t when t.ValueKind = JsonValueKind.Array -> items root "tools" 24 |> List.map parseTool
            | Some _ -> bad "“tools” must be a list"

        let declared = tools |> List.map (fun t -> t.Id) |> Set.ofList

        if declared.Count <> tools.Length then
            bad "two tools share an id"

        let formatter =
            prop root "formatter" |> Option.map (parseStep declared false (Some Execute))

        { Id = id
          Name = name
          Extensions = exts
          FileNames = names
          Scope = scope
          Grammar = grammar
          LineComment = line
          BlockComment = block
          UseTabs = tabs
          IndentSize = size
          Server = prop root "server" |> Option.map (fun s -> parseServer s "command")
          Run = parsePlans declared root "run"
          Test = parsePlans declared root "test"
          Format = formatter
          Debug = prop root "debug" |> Option.map (fun s -> parseServer s "adapter")
          Tools = tools
          Origin = origin }

    /// Read one pack from its JSON, or say why it is refused. Never throws.
    let parse (origin: string) (json: string) : Result<LanguagePack, string> =
        try
            let opts =
                JsonDocumentOptions(
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    MaxDepth = 16
                )

            use doc = JsonDocument.Parse(json, opts)
            Ok(parseRoot origin doc.RootElement)
        with
        | Bad m -> Error m
        | :? JsonException as e -> Error("not valid JSON: " + e.Message)
        | :? InvalidOperationException as e -> Error("not a valid pack: " + e.Message)
        | :? FormatException as e -> Error("not a valid pack: " + e.Message)

module Languages =
    /// Every `*.json` in a folder (not recursive; names that start with "_" are notes). A pack that is refused is
    /// reported and skipped; one bad file never hides the others.
    let loadDirectory (dir: string) : LanguagePack list * PackProblem list =
        if not (Directory.Exists dir) then
            [], []
        else
            Directory.EnumerateFiles(dir, "*.json")
            |> Seq.filter (fun f -> not ((Path.GetFileName f).StartsWith "_"))
            |> Seq.sort
            |> Seq.map (fun f ->
                try
                    let info = FileInfo f

                    if info.Length > 64L * 1024L then
                        Error
                            { Origin = f
                              Message = "larger than 64 KB" }
                    else
                        match LanguagePack.parse f (File.ReadAllText f) with
                        | Ok p -> Ok p
                        | Error m -> Error { Origin = f; Message = m }
                with e ->
                    Error { Origin = f; Message = e.Message })
            |> Seq.toList
            |> fun rs ->
                (rs
                 |> List.choose (function
                     | Ok p -> Some p
                     | _ -> None)),
                (rs
                 |> List.choose (function
                     | Error e -> Some e
                     | _ -> None))

    /// Later packs replace earlier ones with the same id: the user's folder wins over the shipped packs.
    let merge (layers: (LanguagePack list * PackProblem list) list) : Registry =
        let packs =
            layers
            |> List.collect fst
            |> List.fold (fun (m: Map<string, LanguagePack>) p -> Map.add p.Id p m) Map.empty
            |> Map.toList
            |> List.map snd

        { Packs = packs
          Problems = layers |> List.collect snd }

    let byId (reg: Registry) (id: string) =
        reg.Packs |> List.tryFind (fun p -> p.Id = id)

    /// The pack for a file: an exact file name first ("Dockerfile"), then the longest matching extension (".d.ts").
    let forFile (reg: Registry) (path: string) : LanguagePack option =
        let name = Path.GetFileName(path.Replace('\\', '/'))

        match
            reg.Packs
            |> List.tryFind (fun p ->
                p.FileNames
                |> List.exists (fun n -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        with
        | Some p -> Some p
        | None ->
            let lower = name.ToLowerInvariant()

            reg.Packs
            |> List.collect (fun p ->
                p.Extensions
                |> List.filter (fun e -> lower.EndsWith e && lower.Length > e.Length)
                |> List.map (fun e -> e.Length, p))
            |> List.sortByDescending fst
            |> List.tryHead
            |> Option.map snd

    /// Every tool the packs declare, once per id (the first declaration wins).
    let tools (reg: Registry) : ToolSpec list =
        reg.Packs |> List.collect (fun p -> p.Tools) |> List.distinctBy (fun t -> t.Id)
