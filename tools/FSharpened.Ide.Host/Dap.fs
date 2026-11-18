namespace FSharpened.Ide.Host

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open FSharpened.Platform

/// A stack frame. Lines and columns are 1-based, as the client asks the adapter to count them.
type DapFrame =
    {
        FrameId: int
        FrameName: string
        /// The source file, or "" when the frame has none (native code, a generated frame).
        FramePath: string
        FrameLine: int
        FrameColumn: int
    }

type DapThread = { ThreadId: int; ThreadName: string }

type DapScope =
    { ScopeName: string
      ScopeRef: int
      Expensive: bool }

type DapVariable =
    {
        VarName: string
        Value: string
        VarType: string
        /// Non-zero when the value has children that `Variables` can fetch.
        VarRef: int
    }

type DapBreakpoint =
    { Verified: bool
      BpLine: int
      BpMessage: string }

/// What an adapter reports on its own.
type DapEvent =
    | Initialized
    | Stopped of threadId: int * reason: string * text: string
    | Continued of threadId: int
    | Output of category: string * text: string
    | Exited of code: int
    | Terminated
    | ThreadChanged of reason: string * threadId: int
    | BreakpointChanged of DapBreakpoint
    /// The connection to the adapter ended (it exited or crashed).
    | Closed

module internal DapJson =
    let str (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetRawText()
        | _ -> ""

    let int' (e: JsonElement) (name: string) (fallback: int) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            (match v.TryGetInt32() with
             | true, n -> n
             | _ -> fallback)
        | _ -> fallback

    let bool' (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v -> v.ValueKind = JsonValueKind.True
        | _ -> false

    let list (e: JsonElement) (name: string) : JsonElement list =
        match e.TryGetProperty name with
        | true, a when a.ValueKind = JsonValueKind.Array -> [ for x in a.EnumerateArray() -> x ]
        | _ -> []

    let breakpoint (b: JsonElement) : DapBreakpoint =
        { Verified = bool' b "verified"
          BpLine = int' b "line" 0
          BpMessage = str b "message" }

    let frame (f: JsonElement) : DapFrame =
        { FrameId = int' f "id" 0
          FrameName = str f "name"
          FramePath =
            (match f.TryGetProperty "source" with
             | true, s when s.ValueKind = JsonValueKind.Object -> str s "path"
             | _ -> "")
          FrameLine = int' f "line" 0
          FrameColumn = int' f "column" 0 }

    let variable (v: JsonElement) : DapVariable =
        { VarName = str v "name"
          Value = str v "value"
          VarType = str v "type"
          VarRef = int' v "variablesReference" 0 }

    let event (name: string) (body: JsonElement option) : DapEvent option =
        let b =
            match body with
            | Some b -> b
            | None -> JsonDocument.Parse("{}").RootElement

        match name with
        | "initialized" -> Some Initialized
        | "stopped" -> Some(Stopped(int' b "threadId" 0, str b "reason", (str b "text" + str b "description").Trim()))
        | "continued" -> Some(Continued(int' b "threadId" 0))
        | "output" ->
            Some(
                Output(
                    (match str b "category" with
                     | "" -> "console"
                     | c -> c),
                    str b "output"
                )
            )
        | "exited" -> Some(Exited(int' b "exitCode" 0))
        | "terminated" -> Some Terminated
        | "thread" -> Some(ThreadChanged(str b "reason", int' b "threadId" 0))
        | "breakpoint" ->
            match b.TryGetProperty "breakpoint" with
            | true, bp -> Some(BreakpointChanged(breakpoint bp))
            | _ -> None
        | _ -> None

/// A connection to one debug adapter, over two streams (a process's pipes, a socket, or a test's memory). Messages are
/// framed as in the language server protocol (`Content-Length`), carry a `seq`, and are requests, responses or events.
/// It never blocks the caller; everything the adapter sends is treated as data, and the few requests an adapter may
/// make of a client (`runInTerminal`, `startDebugging`) are answered as unsupported.
type DapClient(fromAdapter: Stream, toAdapter: Stream, onEvent: DapEvent -> unit) =
    let write = obj ()

    let pending =
        ConcurrentDictionary<int, TaskCompletionSource<Result<JsonElement option, string>>>()

    let mutable nextSeq = 0
    let mutable closed = false
    let mutable capabilities: JsonElement option = None

    let initialized =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let timeout = TimeSpan.FromSeconds 30.0

    let send (json: string) =
        lock write (fun () ->
            try
                let bytes = LspWire.frame json
                toAdapter.Write(bytes, 0, bytes.Length)
                toAdapter.Flush()
            with _ ->
                closed <- true)

    let refuse (requestSeq: int) (command: string) =
        let s = Interlocked.Increment(&nextSeq)

        send (
            JsonSerializer.Serialize(
                {| seq = s
                   ``type`` = "response"
                   request_seq = requestSeq
                   success = false
                   command = command
                   message = "the IDE does not support this request" |}
            )
        )

    let handle (json: string) =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            match DapJson.str root "type" with
            | "response" ->
                match pending.TryRemove(DapJson.int' root "request_seq" -1) with
                | true, tcs ->
                    if DapJson.bool' root "success" then
                        match root.TryGetProperty "body" with
                        | true, b when b.ValueKind <> JsonValueKind.Null ->
                            tcs.TrySetResult(Ok(Some(b.Clone()))) |> ignore
                        | _ -> tcs.TrySetResult(Ok None) |> ignore
                    else
                        let reason =
                            match DapJson.str root "message" with
                            | "" ->
                                match root.TryGetProperty "body" with
                                | true, b when b.ValueKind = JsonValueKind.Object ->
                                    match b.TryGetProperty "error" with
                                    | true, er -> DapJson.str er "format"
                                    | _ -> ""
                                | _ -> ""
                            | m -> m

                        tcs.TrySetResult(Error(if reason = "" then "the adapter refused" else reason))
                        |> ignore
                | _ -> ()
            | "event" ->
                let name = DapJson.str root "event"

                let body =
                    match root.TryGetProperty "body" with
                    | true, b when b.ValueKind = JsonValueKind.Object -> Some(b.Clone())
                    | _ -> None

                if name = "initialized" then
                    initialized.TrySetResult() |> ignore

                match DapJson.event name body with
                | Some e -> onEvent e
                | None -> ()
            | "request" -> refuse (DapJson.int' root "seq" 0) (DapJson.str root "command")
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
                            LspWire.read fromAdapter
                         with _ ->
                             None)
                    with
                    | Some m -> handle m
                    | None -> go <- false

                closed <- true

                for kv in pending do
                    kv.Value.TrySetResult(Error "the debug adapter closed the connection") |> ignore

                initialized.TrySetResult() |> ignore

                try
                    onEvent Closed
                with _ ->
                    ()),
            IsBackground = true,
            Name = "dap-reader"
        )

    do loop.Start()

    member _.IsOpen = not closed

    /// Completes when the adapter has sent `initialized` (or the connection ended).
    member _.InitializedEvent: Task = initialized.Task

    member _.Capabilities = capabilities

    member _.SupportsConfigurationDone =
        match capabilities with
        | Some c -> DapJson.bool' c "supportsConfigurationDoneRequest"
        | None -> false

    /// Sends a request and waits for the response: Ok with its body (None when it has none), or Error with the adapter's
    /// reason. Never throws and never waits longer than `wait` (30 s by default).
    member _.RequestWithin(command: string, args: obj, wait: TimeSpan) : Task<Result<JsonElement option, string>> =
        if closed then
            Task.FromResult(Error "the debug adapter is not running")
        else
            let s = Interlocked.Increment(&nextSeq)

            let tcs =
                TaskCompletionSource<Result<JsonElement option, string>>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            pending[s] <- tcs

            send (
                JsonSerializer.Serialize(
                    {| seq = s
                       ``type`` = "request"
                       command = command
                       arguments = args |}
                )
            )

            task {
                let! finished = Task.WhenAny(tcs.Task, Task.Delay wait)

                if Object.ReferenceEquals(finished, tcs.Task) then
                    return! tcs.Task
                else
                    pending.TryRemove s |> ignore
                    return Error(sprintf "the adapter did not answer “%s”" command)
            }

    member this.Request(command: string, args: obj) =
        this.RequestWithin(command, args, timeout)

    member private this.Unit(command: string, args: obj) : Task<Result<unit, string>> =
        task {
            let! r = this.Request(command, args)

            match r with
            | Ok _ -> return Ok()
            | Error m -> return Error m
        }

    /// The `initialize` handshake. The client counts lines and columns from 1 and cannot run programs in its own terminal.
    member this.Initialize(adapterId: string) : Task<Result<unit, string>> =
        task {
            let! r =
                this.Request(
                    "initialize",
                    {| clientID = "fsharpened"
                       clientName = "FSharpened"
                       adapterID = adapterId
                       locale = "en-US"
                       linesStartAt1 = true
                       columnsStartAt1 = true
                       pathFormat = "path"
                       supportsVariableType = true
                       supportsRunInTerminalRequest = false
                       supportsStartDebuggingRequest = false |}
                )

            match r with
            | Ok body ->
                capabilities <- body
                return Ok()
            | Error m -> return Error m
        }

    /// Sends `launch` or `attach` with the given JSON object as its arguments, without waiting: many adapters answer it
    /// only after `configurationDone`.
    member this.Start(request: string, argumentsJson: string) : Task<Result<JsonElement option, string>> =
        let args =
            try
                JsonDocument.Parse(argumentsJson).RootElement.Clone() |> box
            with _ ->
                box {| |}

        this.Request(request, args)

    member this.SetBreakpoints(path: string, lines: int list) : Task<Result<DapBreakpoint list, string>> =
        task {
            let! r =
                this.Request(
                    "setBreakpoints",
                    {| source = {| path = path |}
                       breakpoints = [| for l in lines -> {| line = l |} |]
                       lines = List.toArray lines |}
                )

            match r with
            | Ok(Some b) -> return Ok(DapJson.list b "breakpoints" |> List.map DapJson.breakpoint)
            | Ok None -> return Ok []
            | Error m -> return Error m
        }

    member this.SetExceptionBreakpoints(filters: string list) =
        this.Unit("setExceptionBreakpoints", {| filters = List.toArray filters |})

    member this.ConfigurationDone() = this.Unit("configurationDone", {| |})

    member this.Continue(threadId: int) =
        this.Unit("continue", {| threadId = threadId |})

    member this.Next(threadId: int) =
        this.Unit("next", {| threadId = threadId |})

    member this.StepIn(threadId: int) =
        this.Unit("stepIn", {| threadId = threadId |})

    member this.StepOut(threadId: int) =
        this.Unit("stepOut", {| threadId = threadId |})

    member this.Pause(threadId: int) =
        this.Unit("pause", {| threadId = threadId |})

    member this.Threads() : Task<Result<DapThread list, string>> =
        task {
            let! r = this.Request("threads", null)

            match r with
            | Ok(Some b) ->
                return
                    Ok(
                        DapJson.list b "threads"
                        |> List.map (fun t ->
                            { ThreadId = DapJson.int' t "id" 0
                              ThreadName = DapJson.str t "name" })
                    )
            | Ok None -> return Ok []
            | Error m -> return Error m
        }

    member this.StackTrace(threadId: int) : Task<Result<DapFrame list, string>> =
        task {
            let! r =
                this.Request(
                    "stackTrace",
                    {| threadId = threadId
                       startFrame = 0
                       levels = 40 |}
                )

            match r with
            | Ok(Some b) -> return Ok(DapJson.list b "stackFrames" |> List.map DapJson.frame)
            | Ok None -> return Ok []
            | Error m -> return Error m
        }

    member this.Scopes(frameId: int) : Task<Result<DapScope list, string>> =
        task {
            let! r = this.Request("scopes", {| frameId = frameId |})

            match r with
            | Ok(Some b) ->
                return
                    Ok(
                        DapJson.list b "scopes"
                        |> List.map (fun s ->
                            { ScopeName = DapJson.str s "name"
                              ScopeRef = DapJson.int' s "variablesReference" 0
                              Expensive = DapJson.bool' s "expensive" })
                    )
            | Ok None -> return Ok []
            | Error m -> return Error m
        }

    /// The children of a scope or of a structured value; at most 200 are kept (a large array is not fetched whole).
    member this.Variables(reference: int) : Task<Result<DapVariable list, string>> =
        task {
            let! r =
                this.Request(
                    "variables",
                    {| variablesReference = reference
                       count = 200 |}
                )

            match r with
            | Ok(Some b) -> return Ok(DapJson.list b "variables" |> List.truncate 200 |> List.map DapJson.variable)
            | Ok None -> return Ok []
            | Error m -> return Error m
        }

    /// Evaluates an expression in a frame (frame 0 means the adapter's choice); the result text and whether it has children.
    member this.Evaluate(expression: string, frameId: int, context: string) : Task<Result<string * int, string>> =
        task {
            let args: obj =
                if frameId > 0 then
                    box
                        {| expression = expression
                           frameId = frameId
                           context = context |}
                else
                    box
                        {| expression = expression
                           context = context |}

            let! r = this.Request("evaluate", args)

            match r with
            | Ok(Some b) -> return Ok(DapJson.str b "result", DapJson.int' b "variablesReference" 0)
            | Ok None -> return Ok("", 0)
            | Error m -> return Error m
        }

    /// Ends the session. The program is stopped too unless `keepRunning` (for an attach).
    member this.Disconnect(keepRunning: bool) : Task<Result<unit, string>> =
        task {
            let! r =
                this.RequestWithin(
                    "disconnect",
                    {| terminateDebuggee = not keepRunning
                       restart = false |},
                    TimeSpan.FromSeconds 3.0
                )

            match r with
            | Ok _ -> return Ok()
            | Error m -> return Error m
        }

    member private this.SendBreakpoints(rest: (string * int list) list) : Task<unit> =
        match rest with
        | [] -> Task.FromResult()
        | (file, lines) :: more ->
            task {
                let! _ = this.SetBreakpoints(file, lines)
                return! this.SendBreakpoints more
            }

    member private this.Configure(breakpoints: (string * int list) list) : Task<unit> =
        task {
            do! this.SendBreakpoints breakpoints
            let! _ = this.SetExceptionBreakpoints []

            if this.SupportsConfigurationDone then
                let! _ = this.ConfigurationDone()
                ()
        }

    /// The whole start-up: `initialize`, then `launch` or `attach`, then (when the adapter says it is ready) the
    /// breakpoints, `configurationDone`, and the answer to `launch`. Breakpoints are (file, lines) pairs.
    member this.Begin
        (adapterId: string, request: string, configuration: string, breakpoints: (string * int list) list)
        : Task<Result<unit, string>> =
        task {
            let! init = this.Initialize adapterId

            match init with
            | Error m -> return Error(sprintf "the adapter did not start: %s" m)
            | Ok() ->
                let started = this.Start(request, configuration)
                let! _ = Task.WhenAny(this.InitializedEvent, started, Task.Delay(TimeSpan.FromSeconds 15.0))

                let refused =
                    if started.IsCompleted then
                        match started.Result with
                        | Error m -> Some m
                        | _ -> None
                    else
                        None

                match refused with
                | Some m -> return Error(sprintf "%s failed: %s" request m)
                | None ->
                    do! this.Configure breakpoints
                    let! answer = started

                    match answer with
                    | Error m -> return Error(sprintf "%s failed: %s" request m)
                    | Ok _ -> return Ok()
        }

/// Turning a pack's debug block and the editor's state into what the adapter is asked: the placeholders of the launch
/// template, the project file, the newest build output. Pure over the file system it is given. The context is the one
/// plans use: the root, the file (relative), a fresh output folder, the chosen flags and the executable suffix.
module DebugPlan =
    let private projectExtensions = [ ".fsproj"; ".csproj"; ".vbproj" ]

    /// The project file nearest the entry file, looking in its folder and then each folder up to the root.
    let findProject (root: string) (entry: string) : string option =
        let rootFull = Path.GetFullPath root
        let start = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(rootFull, entry)))

        let rec up (dir: string) =
            if
                String.IsNullOrEmpty dir
                || not (dir.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            then
                None
            else
                let found =
                    try
                        Directory.EnumerateFiles dir
                        |> Seq.filter (fun f ->
                            List.contains (Path.GetExtension(f).ToLowerInvariant()) projectExtensions)
                        |> Seq.sort
                        |> Seq.tryHead
                    with _ ->
                        None

                match found with
                | Some f -> Some f
                | None ->
                    let parent = Path.GetDirectoryName dir

                    if parent = dir then None else up parent

        up start

    let private hasWildcard (s: string) = s.Contains '*' || s.Contains '?'

    /// The most recently written file matching a glob under a folder. The glob has `/` separators and may use `*` and
    /// `?`, also in folder names (`bin/Debug/*/App.dll`); it can never leave the folder.
    let newest (baseDir: string) (pattern: string) : string option =
        let parts =
            pattern.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray

        if parts.IsEmpty || parts |> List.exists (fun p -> p = ".." || p.Contains ':') then
            None
        else
            let rec walk (dirs: string list) (parts: string list) : string list =
                match parts with
                | [] -> []
                | [ last ] ->
                    dirs
                    |> List.collect (fun d ->
                        try
                            Directory.EnumerateFiles(d, last) |> List.ofSeq
                        with _ ->
                            [])
                | p :: rest ->
                    let next =
                        dirs
                        |> List.collect (fun d ->
                            if hasWildcard p then
                                try
                                    Directory.EnumerateDirectories(d, p) |> List.ofSeq
                                with _ ->
                                    []
                            else
                                [ Path.Combine(d, p) ])
                        |> List.filter Directory.Exists

                    walk next rest

            walk [ baseDir ] parts
            |> List.sortByDescending (fun f -> File.GetLastWriteTimeUtc f)
            |> List.tryHead

    let private holder = Regex(@"\{([^{}]*)\}", RegexOptions.Compiled)
    let private newestHolder = Regex(@"\{newest:([^{}]*)\}", RegexOptions.Compiled)

    exception private Missing of string

    /// The value of one placeholder, or an error saying what is missing.
    let private valueOf (ctx: PlanContext) (name: string) : string =
        let project () =
            match findProject ctx.Root ctx.Entry with
            | Some p -> p
            | None ->
                raise (
                    Missing(
                        sprintf
                            "debugging %s needs a project file (*.fsproj, *.csproj) in its folder or above it"
                            ctx.Entry
                    )
                )

        match name with
        | "entry" -> ctx.Entry
        | "file" -> Path.GetFullPath(Path.Combine(ctx.Root, ctx.Entry))
        | "root" -> Path.GetFullPath ctx.Root
        | "dir" -> Path.GetDirectoryName(Path.GetFullPath(Path.Combine(ctx.Root, ctx.Entry)))
        | "name" -> Path.GetFileNameWithoutExtension ctx.Entry
        | "out" -> Path.Combine(ctx.OutDir, "fsh-out" + ctx.ExeSuffix)
        | "project" -> Path.GetFileNameWithoutExtension(project ())
        | "projectfile" -> project ()
        | "projectdir" -> Path.GetDirectoryName(project ())
        | other -> raise (Missing(sprintf "{%s} is not known here" other))

    /// Replaces the placeholders in a string; `{newest:glob}` is resolved under the project folder (or the root when
    /// there is no project) after the others, so `{newest:bin/Debug/*/{project}.dll}` works.
    let expandString (ctx: PlanContext) (s: string) : Result<string, string> =
        try
            let first =
                holder.Replace(
                    s,
                    MatchEvaluator(fun m ->
                        let n = m.Groups[1].Value

                        if n.StartsWith "newest:" then m.Value else valueOf ctx n)
                )

            let second =
                newestHolder.Replace(
                    first,
                    MatchEvaluator(fun m ->
                        let glob = m.Groups[1].Value

                        let baseDir =
                            match findProject ctx.Root ctx.Entry with
                            | Some p -> Path.GetDirectoryName p
                            | None -> Path.GetFullPath ctx.Root

                        match newest baseDir glob with
                        | Some f -> f
                        | None ->
                            raise (
                                Missing(sprintf "nothing matches %s under %s (was the build successful?)" glob baseDir)
                            ))
                )

            Ok second
        with Missing m ->
            Error m

    /// Arguments of the build step: an argument that is exactly `{flags}` becomes the chosen flags.
    let expandArgs (ctx: PlanContext) (args: string list) : Result<string list, string> =
        args
        |> List.fold
            (fun acc a ->
                match acc with
                | Error e -> Error e
                | Ok xs ->
                    if a = "{flags}" then
                        Ok(xs @ ctx.Flags)
                    else
                        expandString ctx a |> Result.map (fun v -> xs @ [ v ]))
            (Ok [])

    /// The launch template with its placeholders replaced, as JSON text for the adapter.
    let expandConfiguration (ctx: PlanContext) (template: string) : Result<string, string> =
        let mutable problem: string option = None

        let rec walk (n: JsonNode) : JsonNode =
            match n with
            | :? JsonObject as o ->
                for k in o |> Seq.map (fun kv -> kv.Key) |> Seq.toList do
                    o[k] <- walk o[k]

                n
            | :? JsonArray as a ->
                for i in 0 .. a.Count - 1 do
                    a[i] <- walk a[i]

                n
            | :? JsonValue as v ->
                match v.TryGetValue<string>() with
                | true, s ->
                    match expandString ctx s with
                    | Ok x -> JsonValue.Create x :> JsonNode
                    | Error e ->
                        if problem.IsNone then
                            problem <- Some e

                        n
                | _ -> n
            | _ -> n

        try
            let root = JsonNode.Parse template
            let result = walk root

            match problem with
            | Some e -> Error e
            | None -> Ok(result.ToJsonString())
        with e ->
            Error("the launch template is not valid: " + e.Message)

/// Breakpoint bookkeeping that needs no editor.
module Breakpoints =
    /// Lines (1-based) after a toggle: added when absent, removed when present; sorted, without duplicates.
    let toggle (lines: int list) (line: int) : int list =
        if line < 1 then
            lines |> List.distinct |> List.sort
        elif List.contains line lines then
            lines |> List.filter ((<>) line) |> List.distinct |> List.sort
        else
            line :: lines |> List.distinct |> List.sort

/// A debug adapter started as a child process from a pack's `debug` entry.
type DapSession(proc: Process, client: DapClient, spec: DebugSpec, stderr: unit -> string) =
    member _.Client = client
    member _.Spec = spec
    member _.Exited = proc.HasExited

    /// What the adapter wrote to its standard error (the start of it), for explaining a failure.
    member _.ErrorText = stderr ()

    /// Starts the debuggee. The text of an error includes the adapter's complaint and how to install it.
    member _.Begin(configuration: string, breakpoints: (string * int list) list) : Task<Result<unit, string>> =
        task {
            let! r = client.Begin(spec.AdapterId, spec.Request, configuration, breakpoints)

            match r with
            | Ok() -> return Ok()
            | Error m ->
                let err = (stderr ()).Trim()

                let more = if err <> "" then "\n" + (err.Split('\n')[0]).Trim() else ""

                let hint =
                    if spec.Install <> "" && not client.IsOpen then
                        sprintf "\n%s may not be installed: %s" spec.Adapter spec.Install
                    else
                        ""

                return Error(m + more + hint)
        }

    /// Asks the adapter to end the program and exit, then makes sure the process is gone.
    member _.Stop() =
        task {
            try
                if client.IsOpen then
                    let! _ = client.Disconnect false
                    ()
            with _ ->
                ()

            try
                if not proc.HasExited then
                    proc.Kill true
            with _ ->
                ()
        }

module Dap =
    /// Whether a program name can be found: on the PATH (with the executable suffixes of this system), so that a missing
    /// adapter is known before a build is spent on it.
    let onPath (name: string) : bool =
        let dirs =
            (Environment.GetEnvironmentVariable "PATH"
             |> Option.ofObj
             |> Option.defaultValue "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)

        let suffixes =
            match (RuntimeIdentity.detect ()).Os with
            | Windows -> [ ""; ".exe"; ".cmd"; ".bat"; ".com" ]
            | _ -> [ "" ]

        dirs
        |> Array.exists (fun d ->
            suffixes
            |> List.exists (fun x ->
                try
                    File.Exists(Path.Combine(d.Trim('"'), name + x))
                with _ ->
                    false))

    let private freePort () =
        let l = new TcpListener(IPAddress.Loopback, 0)
        l.Start()
        let port = (l.LocalEndpoint :?> IPEndPoint).Port
        l.Stop()
        port

    /// Starts the adapter a pack names and connects to it (over its standard streams, or a local port for "tcp"
    /// adapters, whose `{port}` argument is filled in). Its standard error is kept for messages. Error holds a plain
    /// sentence, with the install hint, when the program cannot be started.
    let start (spec: DebugSpec) (onEvent: DapEvent -> unit) : Result<DapSession, string> =
        let notInstalled (why: string) =
            Error(
                sprintf
                    "%s could not be started (%s)%s"
                    spec.Adapter
                    why
                    (if spec.Install <> "" then
                         " — get it from: " + spec.Install
                     else
                         "")
            )

        try
            let tcp = spec.Transport = "tcp"
            let port = if tcp then freePort () else 0
            let psi = ProcessStartInfo(spec.Adapter)

            for a in spec.Args do
                psi.ArgumentList.Add(a.Replace("{port}", string port))

            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            let p = Process.Start psi

            if isNull p then
                notInstalled "it did not start"
            else
                let err = StringBuilder()

                p.ErrorDataReceived.Add(fun e ->
                    if not (isNull e.Data) then
                        lock err (fun () ->
                            if err.Length < 4000 then
                                err.AppendLine e.Data |> ignore))

                p.BeginErrorReadLine()

                let errText () = lock err (fun () -> err.ToString())

                if not tcp then
                    Ok(
                        DapSession(
                            p,
                            DapClient(p.StandardOutput.BaseStream, p.StandardInput.BaseStream, onEvent),
                            spec,
                            errText
                        )
                    )
                else
                    p.OutputDataReceived.Add ignore
                    p.BeginOutputReadLine()
                    let deadline = DateTime.UtcNow.AddSeconds 15.0
                    let mutable stream: NetworkStream option = None

                    while stream.IsNone && DateTime.UtcNow < deadline && not p.HasExited do
                        try
                            let c = new TcpClient()
                            c.Connect(IPAddress.Loopback, port)
                            c.NoDelay <- true
                            stream <- Some(c.GetStream())
                        with _ ->
                            Thread.Sleep 100

                    match stream with
                    | Some s -> Ok(DapSession(p, DapClient(s, s, onEvent), spec, errText))
                    | None ->
                        (try
                            if not p.HasExited then
                                p.Kill true
                         with _ ->
                             ())

                        let e = (errText ()).Trim()

                        notInstalled (
                            if e <> "" then
                                e.Split('\n')[0]
                            else
                                "it did not open its port"
                        )
        with e ->
            notInstalled e.Message
