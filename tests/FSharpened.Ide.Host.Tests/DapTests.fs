module DapTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading
open Xunit
open FSharpened.Ide.Host

// ---- a way to find Python for the scripted adapter -----------------------------------------------------

let private python =
    lazy
        ([ "python"; "python3" ]
         |> List.tryFind (fun p ->
             match Proc.run [ p; "-c"; "print(1)" ] (Path.GetTempPath()) None (TimeSpan.FromSeconds 10.0) with
             | Ok r -> r.ExitCode = Some 0
             | _ -> false))

let private fakeScript = Path.Combine(AppContext.BaseDirectory, "fake_dap.py")

let private spec (transport: string) (extra: string list) (install: string) : DebugSpec option =
    python.Value
    |> Option.map (fun py ->
        { Adapter = py
          Args = [ fakeScript ] @ extra
          Install = install
          Transport = transport
          AdapterId = "fake"
          Request = "launch"
          Build = None
          Configuration = """{"program":"main.fake"}""" })

let private tempSource () =
    let dir = Path.Combine(Path.GetTempPath(), "fsh-dap-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    let file = Path.Combine(dir, "main.fake")
    File.WriteAllText(file, "line1\nline2\nline3\nline4\n")
    file

/// Collects the adapter's events and waits for one that satisfies a test.
type private Events() =
    let seen = ConcurrentQueue<DapEvent>()
    member _.Add(e: DapEvent) = seen.Enqueue e
    member _.All = seen.ToArray() |> Array.toList

    member this.Wait(what: string, pick: DapEvent -> 'a option) : 'a =
        let deadline = DateTime.UtcNow.AddSeconds 20.0
        let mutable found = None

        while found.IsNone && DateTime.UtcNow < deadline do
            found <- this.All |> List.tryPick pick

            if found.IsNone then
                Thread.Sleep 20

        match found with
        | Some v -> v
        | None -> failwithf "no %s event; saw %A" what this.All

let private ok (r: Result<'a, string>) =
    match r with
    | Ok v -> v
    | Error e -> failwith e

let private session (s: DebugSpec) (events: Events) =
    match Dap.start s events.Add with
    | Ok x -> x
    | Error e -> failwith e

let private stopped =
    function
    | Stopped(t, reason, _) -> Some(t, reason)
    | _ -> None

let private runThrough (transport: string) =
    match spec transport (if transport = "tcp" then [ "--port"; "{port}" ] else []) "" with
    | None -> () // no Python here: the scripted adapter cannot run
    | Some s ->
        let file = tempSource ()
        let events = Events()
        let sess = session s events

        try
            ok (sess.Begin(s.Configuration, [ file, [ 2 ] ]).Result)
            let thread, reason = events.Wait("stopped", stopped)
            Assert.Equal(1, thread)
            Assert.Equal("breakpoint", reason)

            // the program's output arrives as events, and the client refused the adapter's runInTerminal
            let out =
                events.Wait(
                    "output",
                    function
                    | Output("stdout", t) -> Some t
                    | _ -> None
                )

            Assert.Equal("hello from the program\n", out)

            events.Wait(
                "refusal",
                function
                | Output(_, t) when t.StartsWith "runInTerminal refused" -> Some()
                | _ -> None
            )

            let c = sess.Client
            Assert.Equal<string list>([ "main" ], ok (c.Threads().Result) |> List.map (fun t -> t.ThreadName))
            let frames = ok (c.StackTrace(1).Result)
            Assert.Equal(3, frames.Length)
            Assert.Equal("compute", frames.Head.FrameName)
            Assert.Equal(file, frames.Head.FramePath)
            Assert.Equal(2, frames.Head.FrameLine)
            Assert.Equal("", frames[2].FramePath)

            let scopes = ok (c.Scopes(frames.Head.FrameId).Result)
            Assert.Equal<string list>([ "Locals"; "Globals" ], scopes |> List.map (fun s -> s.ScopeName))
            Assert.True scopes[1].Expensive
            let vars = ok (c.Variables(scopes.Head.ScopeRef).Result)
            Assert.Equal<string list>([ "x"; "obj" ], vars |> List.map (fun v -> v.VarName))
            Assert.Equal("1", vars.Head.Value)
            Assert.Equal("int", vars.Head.VarType)
            Assert.True(vars[1].VarRef > 0)
            let fields = ok (c.Variables(vars[1].VarRef).Result)
            Assert.Equal<string list>([ "X"; "Y" ], fields |> List.map (fun v -> v.VarName))

            let value, _ = ok (c.Evaluate("x + 1", frames.Head.FrameId, "repl").Result)
            Assert.Equal("<x + 1>", value)

            match c.Evaluate("boom", frames.Head.FrameId, "repl").Result with
            | Error m -> Assert.Equal("name 'boom' is not defined", m)
            | Ok _ -> failwith "an evaluation error should come back as an error"

            // a step moves one line and reports where
            ok (c.Next(1).Result)

            let deadline = DateTime.UtcNow.AddSeconds 20.0

            while (events.All |> List.filter (fun e -> (stopped e).IsSome)).Length < 2
                  && DateTime.UtcNow < deadline do
                Thread.Sleep 20

            let after = ok (c.StackTrace(1).Result)
            Assert.Equal(3, after.Head.FrameLine)

            ok (c.Continue(1).Result)

            events.Wait(
                "terminated",
                (function
                | Terminated -> Some()
                | _ -> None)
            )

            events.Wait(
                "exited",
                (function
                | Exited code -> Some code
                | _ -> None)
            )
            |> ignore
        finally
            sess.Stop().Wait(10000) |> ignore

[<Fact>]
let ``a scripted adapter over standard streams: launch, stop at a breakpoint, look around, step, continue`` () =
    runThrough "stdio"

[<Fact>]
let ``the same session over a local port, for adapters that listen`` () = runThrough "tcp"

[<Fact>]
let ``a launch the adapter refuses is an error with the adapter's words`` () =
    match spec "stdio" [ "--fail-launch" ] "" with
    | None -> ()
    | Some s ->
        let events = Events()
        let sess = session s events

        try
            match sess.Begin(s.Configuration, []).Result with
            | Error m -> Assert.Contains("cannot launch main.fake", m)
            | Ok() -> failwith "the launch should fail"
        finally
            sess.Stop().Wait(10000) |> ignore

[<Fact>]
let ``an adapter that is not installed says how to get it`` () =
    let s: DebugSpec =
        { Adapter = "fsh-no-such-adapter-" + Guid.NewGuid().ToString "N"
          Args = []
          Install = "https://example.invalid/adapter"
          Transport = "stdio"
          AdapterId = "x"
          Request = "launch"
          Build = None
          Configuration = "{}" }

    match Dap.start s ignore with
    | Ok _ -> failwith "should not start"
    | Error e -> Assert.Contains("https://example.invalid/adapter", e)

[<Fact>]
let ``an adapter that starts and then dies explains itself, with its own complaint and the install hint`` () =
    match python.Value with
    | None -> ()
    | Some py ->
        let s: DebugSpec =
            { Adapter = py
              Args =
                [ "-c"
                  "import sys; sys.stderr.write('No module named debugpy\\n'); sys.exit(1)" ]
              Install = "python -m pip install debugpy"
              Transport = "stdio"
              AdapterId = "debugpy"
              Request = "launch"
              Build = None
              Configuration = "{}" }

        let events = Events()
        let sess = session s events

        match sess.Begin("{}", []).Result with
        | Error m ->
            Assert.Contains("No module named debugpy", m)
            Assert.Contains("python -m pip install debugpy", m)
        | Ok() -> failwith "should fail"

        events.Wait(
            "closed",
            (function
            | Closed -> Some()
            | _ -> None)
        )

// ---- launch templates ---------------------------------------------------------------------------------

let private ctx (root: string) (entry: string) : PlanContext =
    { Root = root
      Entry = entry
      OutDir = Path.Combine(root, "out")
      Flags = [ "-O2" ]
      ExeSuffix = ".exe" }

let private tempRoot () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fsh-plan-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

[<Fact>]
let ``placeholders in a launch template are filled and the result is still valid JSON`` () =
    let root = tempRoot ()
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    let c = ctx root "src/main.go"

    let template =
        """{"program":"{file}","cwd":"{dir}","name":"{name}","list":["{root}","x"],"n":3,"quote":"a\"b"}"""

    match DebugPlan.expandConfiguration c template with
    | Ok json ->
        use d = JsonDocument.Parse json
        let r = d.RootElement
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "main.go")), r.GetProperty("program").GetString())
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src")), r.GetProperty("cwd").GetString())
        Assert.Equal("main", r.GetProperty("name").GetString())
        Assert.Equal(Path.GetFullPath(root), (r.GetProperty("list").EnumerateArray() |> Seq.head).GetString())
        Assert.Equal(3, r.GetProperty("n").GetInt32())
        Assert.Equal("a\"b", r.GetProperty("quote").GetString())
    | Error e -> failwith e

[<Fact>]
let ``the project is the nearest project file, and the newest build output is found under it`` () =
    let root = tempRoot ()
    let proj = Path.Combine(root, "App")

    Directory.CreateDirectory(Path.Combine(proj, "bin", "Debug", "net9.0"))
    |> ignore

    Directory.CreateDirectory(Path.Combine(proj, "bin", "Debug", "net8.0"))
    |> ignore

    File.WriteAllText(Path.Combine(proj, "App.fsproj"), "<Project/>")
    File.WriteAllText(Path.Combine(proj, "Program.fs"), "")
    let old = Path.Combine(proj, "bin", "Debug", "net8.0", "App.dll")
    let fresh = Path.Combine(proj, "bin", "Debug", "net9.0", "App.dll")
    File.WriteAllText(old, "")
    File.WriteAllText(fresh, "")
    File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1.0))

    let c = ctx root "App/Program.fs"
    Assert.Equal(Some(Path.Combine(proj, "App.fsproj")), DebugPlan.findProject root "App/Program.fs")

    match DebugPlan.expandString c "{newest:bin/Debug/*/{project}.dll}", DebugPlan.expandString c "{projectdir}" with
    | Ok dll, Ok dir ->
        Assert.Equal(fresh, dll)
        Assert.Equal(proj, dir)
    | a, b -> failwithf "unexpected %A %A" a b

    // nothing built yet: a message that says what to check, not a crash
    match DebugPlan.expandString c "{newest:bin/Release/*/{project}.dll}" with
    | Error e -> Assert.Contains("successful", e)
    | Ok v -> failwithf "unexpected %s" v

[<Fact>]
let ``a template that needs a project says so when there is none, and unknown placeholders are refused`` () =
    let root = tempRoot ()
    File.WriteAllText(Path.Combine(root, "a.fsx"), "")
    let c = ctx root "a.fsx"

    match DebugPlan.expandString c "{projectfile}" with
    | Error e -> Assert.Contains("needs a project file", e)
    | Ok v -> failwithf "unexpected %s" v

    Assert.True(Result.isError (DebugPlan.expandString c "{nonsense}"))
    // a glob can never leave the folder
    Assert.Equal(None, DebugPlan.newest root "../*")
    Assert.Equal(None, DebugPlan.newest root "C:/*")

[<Fact>]
let ``build arguments expand and the flags slot takes the chosen flags`` () =
    let root = tempRoot ()
    let c = ctx root "a.c"

    Assert.Equal(
        Ok [ "-g"; "a.c"; "-o"; Path.Combine(root, "out", "fsh-out.exe"); "-O2" ],
        DebugPlan.expandArgs c [ "-g"; "{entry}"; "-o"; "{out}"; "{flags}" ]
    )

[<Fact>]
let ``breakpoints toggle on and off and stay sorted`` () =
    Assert.Equal<int list>([ 3 ], Breakpoints.toggle [] 3)
    Assert.Equal<int list>([ 3; 9 ], Breakpoints.toggle [ 9 ] 3)
    Assert.Equal<int list>([ 9 ], Breakpoints.toggle [ 3; 9 ] 3)
    Assert.Equal<int list>([ 9 ], Breakpoints.toggle [ 9 ] 0)

// ---- packs ---------------------------------------------------------------------------------------------

let private pack (debug: string) =
    LanguagePack.parse
        "test.json"
        (sprintf
            """{"format":"fsharpened.language/1","id":"x","name":"X","extensions":[".x"],"tools":[{"id":"cc","candidates":[["cc"]],"version":["--version"]}],"debug":%s}"""
            debug)

[<Fact>]
let ``a debug block is read, with its defaults`` () =
    match
        pack
            """{"adapter":"lldb-dap","install":"get it","launch":{"program":"{out}"},"build":{"phase":"build","tool":"cc","args":["{entry}","-o","{out}"]}}"""
    with
    | Ok p ->
        let d = p.Debug.Value
        Assert.Equal("lldb-dap", d.Adapter)
        Assert.Equal("stdio", d.Transport)
        Assert.Equal("launch", d.Request)
        Assert.Equal("cc", d.Build.Value.Tool)
        Assert.Contains("{out}", d.Configuration)
    | Error e -> failwith e

[<Fact>]
let ``a debug block that names a path, an unknown placeholder or an unknown transport is refused`` () =
    let refused (debug: string) =
        match pack debug with
        | Error _ -> ()
        | Ok _ -> failwithf "should be refused: %s" debug

    refused """{"adapter":"/usr/bin/lldb-dap"}"""
    refused """{"adapter":"x","launch":{"program":"{surprise}"}}"""
    refused """{"adapter":"x","transport":"pipe"}"""
    refused """{"adapter":"x","request":"restart"}"""
    refused """{"adapter":"x","args":["{root}"]}"""
    refused """{"adapter":"x","build":{"phase":"build","tool":"missing","args":[]}}"""
    refused """{"adapter":"x","launch":[1]}"""

[<Fact>]
let ``the packs that ship a debug block are all valid and name their adapter`` () =
    let mutable dir = AppContext.BaseDirectory
    let mutable found = null

    while isNull found && not (isNull dir) do
        let candidate = Path.Combine(dir, "languages")

        if
            Directory.Exists candidate
            && File.Exists(Path.Combine(candidate, "fsharp.json"))
        then
            found <- candidate
        else
            dir <- Path.GetDirectoryName dir

    if not (isNull found) then
        let packs, problems = Languages.loadDirectory found
        Assert.Empty problems

        let withDebug =
            packs |> List.filter (fun p -> p.Debug.IsSome) |> List.map (fun p -> p.Id)

        for id in [ "fsharp"; "csharp"; "python"; "go"; "c"; "cpp"; "rust" ] do
            Assert.Contains(id, withDebug)

        for p in packs do
            match p.Debug with
            | Some d ->
                Assert.True(d.Install <> "")
                // every build step uses a tool the pack declares
                d.Build
                |> Option.iter (fun b -> Assert.True(p.Tools |> List.exists (fun t -> t.Id = b.Tool)))
            | None -> ()

// ---- the real thing, when it is here -------------------------------------------------------------------

/// debugpy is Python's own adapter. It is used when the tests can find it: installed, or its folder named in
/// FSH_DEBUGPY_LIBS (the VS Code Python extension ships one in `bundled/libs`).
[<Fact>]
let ``debugpy, when available, stops at a breakpoint in a real Python program and reports its variables`` () =
    let libs = Environment.GetEnvironmentVariable "FSH_DEBUGPY_LIBS"

    let env = if String.IsNullOrEmpty libs then None else Some libs

    match python.Value with
    | None -> ()
    | Some py ->
        let previous = Environment.GetEnvironmentVariable "PYTHONPATH"

        env
        |> Option.iter (fun l -> Environment.SetEnvironmentVariable("PYTHONPATH", l))

        try
            let have =
                match Proc.run [ py; "-c"; "import debugpy" ] (Path.GetTempPath()) None (TimeSpan.FromSeconds 20.0) with
                | Ok r -> r.ExitCode = Some 0
                | _ -> false

            if have then
                let dir =
                    Path.Combine(Path.GetTempPath(), "fsh-debugpy-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let file = Path.Combine(dir, "prog.py")
                File.WriteAllText(file, "def add(a, b):\n    total = a + b\n    return total\n\nprint(add(1, 2))\n")

                let s: DebugSpec =
                    { Adapter = py
                      Args = [ "-m"; "debugpy.adapter" ]
                      Install = "python -m pip install debugpy"
                      Transport = "stdio"
                      AdapterId = "debugpy"
                      Request = "launch"
                      Build = None
                      Configuration =
                        JsonSerializer.Serialize(
                            {| ``type`` = "python"
                               program = file
                               cwd = dir
                               console = "internalConsole" |}
                        ) }

                let events = Events()
                let sess = session s events

                try
                    ok (sess.Begin(s.Configuration, [ file, [ 2 ] ]).Result)
                    let thread, reason = events.Wait("stopped", stopped)
                    Assert.Equal("breakpoint", reason)
                    let c = sess.Client
                    let frames = ok (c.StackTrace(thread).Result)
                    Assert.Equal("add", frames.Head.FrameName)
                    Assert.Equal(2, frames.Head.FrameLine)
                    Assert.True(String.Equals(file, frames.Head.FramePath, StringComparison.OrdinalIgnoreCase))
                    let scopes = ok (c.Scopes(frames.Head.FrameId).Result)

                    let locals =
                        scopes |> List.find (fun s -> s.ScopeName.ToLowerInvariant().StartsWith "local")

                    let vars = ok (c.Variables(locals.ScopeRef).Result)
                    Assert.Equal("1", (vars |> List.find (fun v -> v.VarName = "a")).Value)
                    Assert.Equal("2", (vars |> List.find (fun v -> v.VarName = "b")).Value)
                    let v, _ = ok (c.Evaluate("a + b", frames.Head.FrameId, "repl").Result)
                    Assert.Equal("3", v)
                    ok (c.Next(thread).Result)

                    events.Wait(
                        "second stop",
                        fun e ->
                            match e with
                            | Stopped(_, "step", _) -> Some()
                            | _ -> None
                    )

                    let next = ok (c.StackTrace(thread).Result)
                    Assert.Equal(3, next.Head.FrameLine)
                    ok (c.Continue(thread).Result)

                    events.Wait(
                        "terminated",
                        (function
                        | Terminated -> Some()
                        | _ -> None)
                    )

                    Assert.Contains(
                        events.All,
                        fun e ->
                            match e with
                            | Output(_, t) -> t.Trim() = "3"
                            | _ -> false
                    )
                finally
                    sess.Stop().Wait(10000) |> ignore
        finally
            Environment.SetEnvironmentVariable("PYTHONPATH", previous)
