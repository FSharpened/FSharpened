namespace FSharpened.Ide.Host

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// A position in a file as the Language Server Protocol counts it: 0-based line, 0-based UTF-16 character.
type LspPos = { Line: int; Character: int }

type LspRange = { Start: LspPos; End: LspPos }

type LspDiagnostic =
    {
        Range: LspRange
        /// 1 error, 2 warning, 3 information, 4 hint.
        Severity: int
        Message: string
        Code: string
        Source: string
    }

type LspCompletion =
    {
        Label: string
        Detail: string
        /// What to type in; the label when the server gives nothing else.
        Insert: string
    }

type LspLocation = { Path: string; Range: LspRange }

/// One replacement of a range of a file's text by new text.
type TextEdit = { Range: LspRange; NewText: string }

/// The edits a server returns for a rename (`WorkspaceEdit`): per file, in the order the server gave them. Creating,
/// renaming and deleting files is not supported; a server that asks for it is refused with `Unsupported` set.
type WorkspaceEdit =
    {
        Files: (string * TextEdit list) list
        /// Names the kinds of file operation the server asked for ("create", "rename", "delete"), empty when none.
        Unsupported: string list
    }

/// What the server says about the symbol under the caret before a rename.
type PrepareRenameResult =
    {
        /// The range of the name that would change, when the server says so.
        Range: LspRange option
        /// The current name, when the server gives it.
        Placeholder: string option
    }

/// Whether a server can rename, from its `renameProvider` capability.
type RenameSupport =
    | NoRename
    | RenameOnly
    | RenameWithPrepare

/// Messages are a header block and a JSON body: `Content-Length: n`, a blank line, n bytes of UTF-8.
module LspWire =
    let frame (json: string) : byte[] =
        let body = Encoding.UTF8.GetBytes json
        Array.append (Encoding.ASCII.GetBytes(sprintf "Content-Length: %d\r\n\r\n" body.Length)) body

    let private maxBody = 32 * 1024 * 1024

    /// Reads one message; None at the end of the stream or when the framing is broken (the connection is then unusable).
    let read (s: Stream) : string option =
        let header = Text.StringBuilder()
        let mutable length = -1
        let mutable eof = false
        let mutable finished = false

        while not finished && not eof do
            let b = s.ReadByte()

            if b < 0 then
                eof <- true
            else
                header.Append(char b) |> ignore

                if
                    header.Length >= 2
                    && header[header.Length - 2] = '\r'
                    && header[header.Length - 1] = '\n'
                then
                    let line = header.ToString().TrimEnd('\r', '\n')
                    header.Clear() |> ignore

                    if line = "" then
                        finished <- true
                    elif line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
                        match Int32.TryParse(line.Substring(15).Trim()) with
                        | true, n when n >= 0 && n <= maxBody -> length <- n
                        | _ -> eof <- true
                elif header.Length > 4096 then
                    eof <- true

        if eof || length < 0 then
            None
        else
            let buf = Array.zeroCreate<byte> length
            let mutable got = 0

            while got < length && not eof do
                let n = s.Read(buf, got, length - got)

                if n <= 0 then eof <- true else got <- got + n

            if eof then None else Some(Encoding.UTF8.GetString buf)

    /// `file:///C:/work/a.go` for a path.
    let toUri (path: string) : string = Uri(Path.GetFullPath path).AbsoluteUri

    /// The path of a `file:` URI, or "" for any other kind.
    let ofUri (uri: string) : string =
        match Uri.TryCreate(uri, UriKind.Absolute) with
        | true, u when u.IsFile -> u.LocalPath
        | _ -> ""

module internal LspText =
    let rec flatten (c: JsonElement) : string =
        match c.ValueKind with
        | JsonValueKind.String -> c.GetString()
        | JsonValueKind.Array -> String.Join("\n", [ for x in c.EnumerateArray() -> flatten x ])
        | JsonValueKind.Object ->
            (match c.TryGetProperty "value" with
             | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
             | _ -> "")
        | _ -> ""


/// Reading the shapes a server sends for rename, kept apart from the client so tests can feed it JSON.
module LspParse =
    let private pos (e: JsonElement) : LspPos =
        { Line = e.GetProperty("line").GetInt32()
          Character = e.GetProperty("character").GetInt32() }

    let range (e: JsonElement) : LspRange =
        { Start = pos (e.GetProperty "start")
          End = pos (e.GetProperty "end") }

    let private edit (e: JsonElement) : TextEdit =
        { Range = range (e.GetProperty "range")
          NewText = e.GetProperty("newText").GetString() }

    let private edits (a: JsonElement) : TextEdit list =
        [ for x in a.EnumerateArray() -> edit x ]

    /// `prepareRename` answers with a Range, `{range, placeholder}` or `{defaultBehavior: true}`.
    let prepareRename (e: JsonElement) : PrepareRenameResult =
        if e.ValueKind <> JsonValueKind.Object then
            { Range = None; Placeholder = None }
        elif e.TryGetProperty("start") |> fst then
            { Range = Some(range e)
              Placeholder = None }
        else
            { Range =
                (match e.TryGetProperty "range" with
                 | true, r -> Some(range r)
                 | _ -> None)
              Placeholder =
                (match e.TryGetProperty "placeholder" with
                 | true, p when p.ValueKind = JsonValueKind.String -> Some(p.GetString())
                 | _ -> None) }

    /// A `WorkspaceEdit`: the `changes` map, or `documentChanges` (text edits of a document, and create/rename/delete
    /// operations, which are reported, not applied). Edits of one file that come in several pieces are joined in order.
    let workspaceEdit (e: JsonElement) : WorkspaceEdit =
        let files = Collections.Generic.List<string * TextEdit list>()
        let unsupported = Collections.Generic.List<string>()

        let add (uri: string) (list: TextEdit list) =
            let path = LspWire.ofUri uri

            if path = "" then
                unsupported.Add "a file outside the disk"
            else
                match files |> Seq.tryFindIndex (fun (p, _) -> p = path) with
                | Some i ->
                    let p, old = files[i]
                    files[i] <- (p, old @ list)
                | None -> files.Add((path, list))

        match e.TryGetProperty "documentChanges" with
        | true, dc when dc.ValueKind = JsonValueKind.Array ->
            for c in dc.EnumerateArray() do
                match c.TryGetProperty "kind" with
                | true, k when k.ValueKind = JsonValueKind.String -> unsupported.Add(k.GetString())
                | _ ->
                    let uri = c.GetProperty("textDocument").GetProperty("uri").GetString()
                    add uri (edits (c.GetProperty "edits"))
        | _ ->
            match e.TryGetProperty "changes" with
            | true, ch when ch.ValueKind = JsonValueKind.Object ->
                for p in ch.EnumerateObject() do
                    add p.Name (edits p.Value)
            | _ -> ()

        { Files = List.ofSeq files
          Unsupported = unsupported |> Seq.distinct |> List.ofSeq }

/// A connection to one language server. It speaks over two streams (a process's pipes, or a test's memory), never
/// blocks the caller, and treats everything the server sends as data: it answers the few requests a server may make of
/// a client with an empty result and ignores the rest.
type LspClient(fromServer: Stream, toServer: Stream, onDiagnostics: string -> LspDiagnostic list -> unit) =
    let write = obj ()
    let pending = ConcurrentDictionary<int, TaskCompletionSource<JsonElement option>>()
    let failures = ConcurrentDictionary<int, string>()
    let mutable nextId = 0
    let mutable closed = false
    let mutable capabilities: JsonElement option = None
    let versions = ConcurrentDictionary<string, int>()
    let timeout = TimeSpan.FromSeconds 15.0

    let send (json: string) =
        lock write (fun () ->
            try
                let bytes = LspWire.frame json
                toServer.Write(bytes, 0, bytes.Length)
                toServer.Flush()
            with _ ->
                closed <- true)

    let respondEmpty (id: JsonElement) =
        send (sprintf """{"jsonrpc":"2.0","id":%s,"result":null}""" (id.GetRawText()))

    let posOf (e: JsonElement) : LspPos =
        { Line = e.GetProperty("line").GetInt32()
          Character = e.GetProperty("character").GetInt32() }

    let rangeOf (e: JsonElement) : LspRange =
        { Start = posOf (e.GetProperty "start")
          End = posOf (e.GetProperty "end") }

    let text (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetRawText()
        | _ -> ""

    let parseDiagnostics (p: JsonElement) : string * LspDiagnostic list =
        let path = LspWire.ofUri (text p "uri")

        let list =
            match p.TryGetProperty "diagnostics" with
            | true, a when a.ValueKind = JsonValueKind.Array ->
                [ for d in a.EnumerateArray() do
                      match
                          (try
                              Some(rangeOf (d.GetProperty "range"))
                           with _ ->
                               None)
                      with
                      | Some r ->
                          yield
                              { Range = r
                                Severity =
                                  (match d.TryGetProperty "severity" with
                                   | true, s when s.ValueKind = JsonValueKind.Number -> s.GetInt32()
                                   | _ -> 1)
                                Message = text d "message"
                                Code = text d "code"
                                Source = text d "source" }
                      | None -> () ]
            | _ -> []

        path, list

    let handle (json: string) =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let hasMethod, methodName =
                (match root.TryGetProperty "method" with
                 | true, m -> true, m.GetString()
                 | _ -> false, "")

            let hasId, id = root.TryGetProperty "id"

            if hasMethod && hasId then
                respondEmpty id // workspace/configuration, client/registerCapability, window/workDoneProgress/create ...
            elif hasMethod then
                if methodName = "textDocument/publishDiagnostics" then
                    match root.TryGetProperty "params" with
                    | true, p ->
                        let path, list = parseDiagnostics p

                        if path <> "" then
                            onDiagnostics path list
                    | _ -> ()
            elif hasId && id.ValueKind = JsonValueKind.Number then
                match pending.TryRemove(id.GetInt32()) with
                | true, tcs ->
                    match root.TryGetProperty "error" with
                    | true, err when err.ValueKind = JsonValueKind.Object ->
                        failures[id.GetInt32()] <- text err "message"
                    | _ -> ()

                    match root.TryGetProperty "result" with
                    | true, r when r.ValueKind <> JsonValueKind.Null -> tcs.TrySetResult(Some(r.Clone())) |> ignore
                    | _ -> tcs.TrySetResult None |> ignore
                | _ -> ()
        with _ ->
            () // a message that is not understood is not acted on

    let loop =
        Thread(
            (fun () ->
                let mutable go = true

                while go do
                    match
                        (try
                            LspWire.read fromServer
                         with _ ->
                             None)
                    with
                    | Some m -> handle m
                    | None -> go <- false

                closed <- true

                for kv in pending do
                    kv.Value.TrySetResult None |> ignore),
            IsBackground = true,
            Name = "lsp-reader"
        )

    do loop.Start()

    member _.IsOpen = not closed

    /// Sends a request and waits for its result; Ok None when the server returns null or does not answer, Error with
    /// the server's own message when it answers with an error.
    member _.RequestEx(methodName: string, args: obj) : Task<Result<JsonElement option, string>> =
        if closed then
            Task.FromResult(Ok None)
        else
            let id = Interlocked.Increment(&nextId)

            let tcs =
                TaskCompletionSource<JsonElement option>(TaskCreationOptions.RunContinuationsAsynchronously)

            pending[id] <- tcs

            send (
                JsonSerializer.Serialize(
                    {| jsonrpc = "2.0"
                       id = id
                       ``method`` = methodName
                       ``params`` = args |}
                )
            )

            task {
                let! finished = Task.WhenAny(tcs.Task, Task.Delay timeout)

                if Object.ReferenceEquals(finished, tcs.Task) then
                    let! r = tcs.Task

                    match failures.TryRemove id with
                    | true, m -> return Error(if m = "" then "the server reported an error" else m)
                    | _ -> return Ok r
                else
                    pending.TryRemove id |> ignore
                    return Ok None
            }

    /// Sends a request and waits for its result; None when the server returns nothing, an error, or does not answer.
    member this.Request(methodName: string, args: obj) : Task<JsonElement option> =
        task {
            let! r = this.RequestEx(methodName, args)

            match r with
            | Ok v -> return v
            | Error _ -> return None
        }

    member _.Notify(methodName: string, args: obj) =
        if not closed then
            send (
                JsonSerializer.Serialize(
                    {| jsonrpc = "2.0"
                       ``method`` = methodName
                       ``params`` = args |}
                )
            )

    /// The `initialize` handshake; true when the server answered.
    member this.Initialize(rootPath: string) : Task<bool> =
        task {
            let! result =
                this.Request(
                    "initialize",
                    {| processId = Environment.ProcessId
                       rootUri = LspWire.toUri rootPath
                       capabilities =
                        {| textDocument =
                            {| synchronization = {| didSave = true |}
                               publishDiagnostics = {| relatedInformation = false |}
                               completion = {| completionItem = {| snippetSupport = false |} |}
                               hover = {| contentFormat = [| "plaintext"; "markdown" |] |}
                               definition = {| linkSupport = false |}
                               rename = {| prepareSupport = true |} |}
                           workspace = {| workspaceEdit = {| documentChanges = true |} |} |}
                       workspaceFolders =
                        [| {| uri = LspWire.toUri rootPath
                              name = Path.GetFileName(rootPath.TrimEnd('/', '\\')) |} |] |}
                )

            match result with
            | Some r ->
                capabilities <-
                    (match r.TryGetProperty "capabilities" with
                     | true, c -> Some c
                     | _ -> None)

                this.Notify("initialized", {| |})
                return true
            | None -> return false
        }

    member this.DidOpen(path: string, languageId: string, text: string) =
        let uri = LspWire.toUri path
        versions[uri] <- 1

        this.Notify(
            "textDocument/didOpen",
            {| textDocument =
                {| uri = uri
                   languageId = languageId
                   version = 1
                   text = text |} |}
        )

    member this.DidChange(path: string, text: string) =
        let uri = LspWire.toUri path
        let v = versions.AddOrUpdate(uri, 2, (fun _ old -> old + 1))

        this.Notify(
            "textDocument/didChange",
            {| textDocument = {| uri = uri; version = v |}
               contentChanges = [| {| text = text |} |] |}
        )

    member this.DidSave(path: string) =
        this.Notify("textDocument/didSave", {| textDocument = {| uri = LspWire.toUri path |} |})

    member this.DidClose(path: string) =
        let uri = LspWire.toUri path
        versions.TryRemove uri |> ignore
        this.Notify("textDocument/didClose", {| textDocument = {| uri = uri |} |})

    member this.Completion(path: string, pos: LspPos) : Task<LspCompletion list> =
        task {
            let! r =
                this.Request(
                    "textDocument/completion",
                    {| textDocument = {| uri = LspWire.toUri path |}
                       position =
                        {| line = pos.Line
                           character = pos.Character |} |}
                )

            match r with
            | None -> return []
            | Some e ->
                let items =
                    if e.ValueKind = JsonValueKind.Array then
                        e
                    else
                        (match e.TryGetProperty "items" with
                         | true, i -> i
                         | _ -> e)

                if items.ValueKind <> JsonValueKind.Array then
                    return []
                else
                    return
                        [ for i in items.EnumerateArray() |> Seq.truncate 300 do
                              let label = text i "label"

                              let insert =
                                  match i.TryGetProperty "textEdit" with
                                  | true, te when te.ValueKind = JsonValueKind.Object -> text te "newText"
                                  | _ ->
                                      (match text i "insertText" with
                                       | "" -> label
                                       | t -> t)

                              if label <> "" then
                                  yield
                                      { Label = label
                                        Detail = text i "detail"
                                        Insert = insert } ]
        }

    member this.Definition(path: string, pos: LspPos) : Task<LspLocation list> =
        task {
            let! r =
                this.Request(
                    "textDocument/definition",
                    {| textDocument = {| uri = LspWire.toUri path |}
                       position =
                        {| line = pos.Line
                           character = pos.Character |} |}
                )

            match r with
            | None -> return []
            | Some e ->
                let items =
                    if e.ValueKind = JsonValueKind.Array then
                        [ for x in e.EnumerateArray() -> x ]
                    else
                        [ e ]

                return
                    [ for i in items do
                          let uri =
                              (match text i "uri" with
                               | "" -> text i "targetUri"
                               | u -> u)

                          let rng =
                              (match i.TryGetProperty "range" with
                               | true, r -> Some r
                               | _ ->
                                   (match i.TryGetProperty "targetSelectionRange" with
                                    | true, r -> Some r
                                    | _ -> None))

                          match rng with
                          | Some r when LspWire.ofUri uri <> "" ->
                              yield
                                  { Path = LspWire.ofUri uri
                                    Range = rangeOf r }
                          | _ -> () ]
        }

    member this.Hover(path: string, pos: LspPos) : Task<string option> =
        task {
            let! r =
                this.Request(
                    "textDocument/hover",
                    {| textDocument = {| uri = LspWire.toUri path |}
                       position =
                        {| line = pos.Line
                           character = pos.Character |} |}
                )

            match r with
            | None -> return None
            | Some e ->
                match e.TryGetProperty "contents" with
                | true, c ->
                    let t = (LspText.flatten c).Trim()
                    return (if t = "" then None else Some t)
                | _ -> return None
        }

    /// Whether the server said it can rename symbols, from the capabilities it sent at `initialize`.
    member _.RenameSupport: RenameSupport =
        match capabilities with
        | None -> NoRename
        | Some c ->
            match c.TryGetProperty "renameProvider" with
            | true, p when p.ValueKind = JsonValueKind.True -> RenameOnly
            | true, p when p.ValueKind = JsonValueKind.Object ->
                match p.TryGetProperty "prepareProvider" with
                | true, v when v.ValueKind = JsonValueKind.True -> RenameWithPrepare
                | _ -> RenameOnly
            | _ -> NoRename

    /// Asks which name is under the position; Error holds the server's reason (or a plain one) when there is nothing
    /// renameable there. A server without `prepareRename` is not asked: the result then has neither range nor name.
    member this.PrepareRename(path: string, pos: LspPos) : Task<Result<PrepareRenameResult, string>> =
        task {
            if this.RenameSupport <> RenameWithPrepare then
                return Ok { Range = None; Placeholder = None }
            else
                let! r =
                    this.RequestEx(
                        "textDocument/prepareRename",
                        {| textDocument = {| uri = LspWire.toUri path |}
                           position =
                            {| line = pos.Line
                               character = pos.Character |} |}
                    )

                match r with
                | Error m -> return Error m
                | Ok None -> return Error "there is no symbol here that can be renamed"
                | Ok(Some e) -> return Ok(LspParse.prepareRename e)
        }

    /// Asks the server for the edits that rename the symbol at the position.
    member this.Rename(path: string, pos: LspPos, newName: string) : Task<Result<WorkspaceEdit, string>> =
        task {
            if this.RenameSupport = NoRename then
                return Error "this language server does not support rename"
            else
                let! r =
                    this.RequestEx(
                        "textDocument/rename",
                        {| textDocument = {| uri = LspWire.toUri path |}
                           position =
                            {| line = pos.Line
                               character = pos.Character |}
                           newName = newName |}
                    )

                match r with
                | Error m -> return Error m
                | Ok None -> return Error "the server returned no edits"
                | Ok(Some e) ->
                    try
                        return Ok(LspParse.workspaceEdit e)
                    with e ->
                        return Error("the server's edits could not be read: " + e.Message)
        }

    member this.Shutdown() =
        task {
            let! _ = this.Request("shutdown", null)
            this.Notify("exit", null)
        }

/// A language server started as a child process from a pack's `server` entry.
type LspSession(proc: Process, client: LspClient) =
    member _.Client = client
    member _.Exited = proc.HasExited

    member _.Stop() =
        try
            if not proc.HasExited then
                proc.Kill true
        with _ ->
            ()

module Lsp =
    /// Starts the server a pack names. The command is a bare program name looked up on the PATH, with the pack's
    /// arguments, and its standard error is discarded; None when it cannot be started (not installed).
    let start (server: ServerSpec) (onDiagnostics: string -> LspDiagnostic list -> unit) : Result<LspSession, string> =
        let hint =
            if server.Install <> "" then
                " — get it from: " + server.Install
            else
                ""

        try
            // found along the PATH (PATHEXT on Windows); a `.cmd` shim (npm's) runs through cmd.exe with quoted arguments
            match Launch.startInfo (server.Command :: server.Args) with
            | Error e -> Error(sprintf "%s could not be started (%s)%s" server.Command e hint)
            | Ok psi ->
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.RedirectStandardInput <- true
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                let p = Process.Start psi

                if isNull p then
                    Error(sprintf "%s did not start" server.Command)
                else
                    p.ErrorDataReceived.Add ignore
                    p.BeginErrorReadLine()

                    let client =
                        LspClient(p.StandardOutput.BaseStream, p.StandardInput.BaseStream, onDiagnostics)

                    Ok(LspSession(p, client))
        with e ->
            Error(sprintf "%s could not be started (%s)%s" server.Command e.Message hint)
