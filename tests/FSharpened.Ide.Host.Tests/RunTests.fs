module RunTests

open System
open System.IO
open Xunit
open FSharpened.Ide
open FSharpened.Ide.Host

/// `dotnet` is on the PATH of every machine that runs `dotnet test`.
let dotnet = "dotnet"

let withFolder (f: string -> unit) = DiskTests.withFolder f

[<Fact>]
let ``a program gets its input, and its output, errors and exit code come back separately`` () =
    withFolder (fun dir ->
        File.WriteAllText(
            Path.Combine(dir, "echo.fsx"),
            "let input = System.Console.In.ReadToEnd()\nprintf \"%s\" (input.ToUpperInvariant())\neprintf \"warned\"\nexit 3\n"
        )

        match
            Proc.run [ dotnet; "fsi"; "--quiet"; "--exec"; "echo.fsx" ] dir (Some "hello") (TimeSpan.FromSeconds 60.0)
        with
        | Error e -> failwith e
        | Ok r ->
            Assert.Equal(Some 3, r.ExitCode)
            Assert.False r.TimedOut
            Assert.Contains("HELLO", r.Stdout)
            Assert.Contains("warned", r.Stderr))

[<Fact>]
let ``a program that runs too long is killed and reported as timed out`` () =
    withFolder (fun dir ->
        File.WriteAllText(Path.Combine(dir, "sleep.fsx"), "System.Threading.Thread.Sleep 120000\n")
        let clock = Diagnostics.Stopwatch.StartNew()

        match Proc.run [ dotnet; "fsi"; "--quiet"; "--exec"; "sleep.fsx" ] dir None (TimeSpan.FromSeconds 8.0) with
        | Error e -> failwith e
        | Ok r ->
            Assert.True r.TimedOut
            Assert.Equal(None, r.ExitCode)
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds 40.0, "the whole tree must be killed promptly"))

[<Fact>]
let ``a program that cannot be started is an error, not an exception`` () =
    withFolder (fun dir ->
        Assert.True(
            (Proc.run [ "fsh-no-such-program-" + Guid.NewGuid().ToString "N" ] dir None (TimeSpan.FromSeconds 5.0))
                .IsError
        ))

[<Fact>]
let ``an installed tool is found with its version and an absent one is reported with how to get it`` () =
    let have: ToolSpec =
        { Id = "dotnet"
          Label = ".NET"
          Candidates = [ [ dotnet ] ]
          Version = [ "--version" ]
          Install = "" }

    let lack: ToolSpec =
        { Id = "nothing"
          Label = "Nothing"
          Candidates = [ [ "fsh-absent-a" ]; [ "fsh-absent-b" ] ]
          Version = [ "--version" ]
          Install = "https://example.invalid" }

    let box = ToolBox [ have; lack ]
    let all = box.Refresh()
    Assert.True((all |> List.find (fun t -> t.Id = "dotnet")).Found)
    Assert.Matches("^[0-9]+\\.", (box.Get "dotnet").Value.Version)
    Assert.False (box.Get "nothing").Value.Found

    let plan: Plan =
        { Files = []
          EntryExtensions = []
          Steps =
            [ { Phase = Execute
                Tool = "nothing"
                Args = [] } ] }

    Assert.Equal<string list>([ "nothing" ], Runner.missing box plan |> List.map (fun t -> t.Id))

let private pack json =
    match LanguagePack.parse "test" json with
    | Ok p -> p
    | Error e -> failwith e

let private fsiPack =
    pack (
        sprintf
            """{ "format": "fsharpened.language/1", "id": "demo", "name": "Demo", "extensions": [".fsx"],
                 "tools": [ { "id": "dn", "candidates": [[%s]], "version": ["--version"] } ],
                 "run": [ { "steps": [ { "phase": "run", "tool": "dn", "args": ["fsi", "--quiet", "--exec", "{entry}"] } ] } ] }"""
            (System.Text.Json.JsonSerializer.Serialize dotnet)
    )

[<Fact>]
let ``running a plan in a folder gives the website's result shape, so the same interpreter reads it`` () =
    let leftovers () =
        Directory.GetDirectories(Path.GetTempPath(), "fsharpened-run-*").Length

    let leftBefore = leftovers ()

    withFolder (fun dir ->
        File.WriteAllText(Path.Combine(dir, "hello.fsx"), "printfn \"hello from %s\" \"fsx\"\n")
        let box = ToolBox(Languages.tools (Languages.merge [ [ fsiPack ], [] ]))
        let plan = (Plans.choose dir "hello.fsx" fsiPack.Run).Value

        let req: RunRequest =
            { Root = dir
              Entry = "hello.fsx"
              Pack = fsiPack
              Plan = plan
              Stdin = ""
              Flags = []
              TimeoutSec = 60 }

        match Runner.run box req with
        | Error e -> failwith e
        | Ok raw ->
            Assert.False raw.BuildFailed
            Assert.Equal(0, raw.ExitCode)
            Assert.Contains("hello from fsx", raw.Stdout)
            Assert.StartsWith("local", raw.Engine)
            let report = Interpret.report "fsharp" raw
            Assert.True report.Ok
            Assert.Equal(leftBefore, leftovers ()))

[<Fact>]
let ``a failing compile step is a build failure with its output and stops the plan`` () =
    withFolder (fun dir ->
        let p =
            pack (
                sprintf
                    """{ "format": "fsharpened.language/1", "id": "demo2", "name": "Demo2", "extensions": [".d2"],
                         "tools": [ { "id": "dn", "candidates": [[%s]], "version": ["--version"] } ],
                         "run": [ { "steps": [ { "phase": "build", "tool": "dn", "args": ["build", "no-such-project.proj"] }, { "phase": "run", "tool": "built" } ] } ] }"""
                    (System.Text.Json.JsonSerializer.Serialize dotnet)
            )

        let box = ToolBox(Languages.tools (Languages.merge [ [ p ], [] ]))

        let req: RunRequest =
            { Root = dir
              Entry = "a.d2"
              Pack = p
              Plan = p.Run.Head
              Stdin = ""
              Flags = []
              TimeoutSec = 30 }

        match Runner.run box req with
        | Error e -> failwith e
        | Ok raw ->
            Assert.True raw.BuildFailed
            Assert.NotEqual(0, raw.ExitCode)
            Assert.NotEqual<string>("", raw.BuildOutput)
            Assert.Equal<string>("", raw.Stdout))

[<Fact>]
let ``a missing tool is an error that says how to get it, before anything starts`` () =
    withFolder (fun dir ->
        let p =
            pack
                """{ "format": "fsharpened.language/1", "id": "demo3", "name": "Demo3", "extensions": [".d3"],
                     "tools": [ { "id": "gone", "label": "Gone Lang", "candidates": [["fsh-absent-tool"]], "version": ["--version"], "install": "https://example.invalid/gone" } ],
                     "run": [ { "steps": [ { "phase": "run", "tool": "gone", "args": ["{entry}"] } ] } ] }"""

        let box = ToolBox(Languages.tools (Languages.merge [ [ p ], [] ]))

        let req: RunRequest =
            { Root = dir
              Entry = "a.d3"
              Pack = p
              Plan = p.Run.Head
              Stdin = ""
              Flags = []
              TimeoutSec = 5 }

        match Runner.run box req with
        | Ok _ -> failwith "should not run"
        | Error e ->
            Assert.Contains("Gone Lang", e)
            Assert.Contains("https://example.invalid/gone", e))

let private raw out err code buildFailed : RawResult =
    { Engine = "local · test"
      BuildFailed = buildFailed
      BuildOutput = (if buildFailed then err else "")
      Stdout = out
      Stderr = (if buildFailed then "" else err)
      ExitCode = code
      TimedOut = false
      DurationMs = 12
      Cached = false }

[<Fact>]
let ``problems are read from any language's output with the file, line and column`` () =
    let gcc =
        Interpret.genericDiagnostics
            "main.zig:3:9: error: expected ';' after statement\nmain.zig:1:1: note: declared here\nignored line"

    Assert.Equal(2, gcc.Length)
    Assert.Equal(FSharpened.Diagnostics.Severity.Error, gcc.Head.Severity)
    Assert.Equal(Some "main.zig", gcc.Head.Range |> Option.map (fun r -> r.File))
    Assert.Equal(Some 3, gcc.Head.Range |> Option.map (fun r -> r.StartLine))
    Assert.Equal(Some 9, gcc.Head.Range |> Option.map (fun r -> r.StartColumn))
    Assert.Equal(FSharpened.Diagnostics.Severity.Info, gcc[1].Severity)

    let msvc =
        Interpret.genericDiagnostics "src\\Program.vb(12,5): error BC30451: 'x' is not declared."

    Assert.Equal("BC30451", msvc.Head.Code)
    Assert.Equal(Some 12, msvc.Head.Range |> Option.map (fun r -> r.StartLine))

[<Fact>]
let ``a language the core does not know still gets a full report, and one it knows uses the core's parsers`` () =
    let r =
        Interpret.report "kotlin" (raw "" "Main.kt:4:5: error: unresolved reference: foo" 1 true)

    Assert.False r.Ok
    Assert.Equal(Phase.Build, r.Phase)
    Assert.Single r.Diagnostics |> ignore
    let ok = Interpret.report "kotlin" (raw "hi\n" "" 0 false)
    Assert.True ok.Ok
    Assert.Equal("hi\n", ok.Stdout)

    let py =
        Interpret.report
            "python"
            (raw
                ""
                "Traceback (most recent call last):\n  File \"main.py\", line 2, in <module>\nNameError: name 'x' is not defined"
                1
                false)

    Assert.False py.Ok
