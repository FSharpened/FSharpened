module FSharpened.Ide.Tests.IdeTests

open Xunit
open FSharpened.Diagnostics
open FSharpened.Ide

[<Fact>]
let ``language ids round-trip and aliases resolve`` () =
    for l in Language.all do
        Assert.Equal(Some l, Language.tryOfId (Language.id l))

    Assert.Equal(Some Cpp, Language.tryOfId "C++")
    Assert.Equal(Some CSharp, Language.tryOfId "c#")
    Assert.Equal(None, Language.tryOfId "cobol")

[<Fact>]
let ``every language has a default template and compiler`` () =
    for l in Language.all do
        Assert.NotEmpty((Templates.defaultFor l).Code)
        Assert.NotEmpty(Compilers.forLanguage l)
        Assert.Equal(l, (Compilers.defaultFor l).Language)

[<Fact>]
let ``workspace survives a trip through a statepoint overlay`` () =
    let w =
        Workspace.initial
        |> Workspace.withText C "int main(void){return 7;}"
        |> Workspace.withStdin "42"
        |> Workspace.activate C

    let store =
        FSharpened.Platform.InMemoryOverlay() :> FSharpened.Platform.IOverlayStore

    Workspace.write store w
    let back = Workspace.read store
    Assert.Equal(C, back.Active)
    Assert.Equal("42", back.Stdin)
    Assert.Equal("int main(void){return 7;}", Workspace.text C back)
    Assert.Equal(Workspace.text FSharp w, Workspace.text FSharp back)

[<Fact>]
let ``timeline snapshot, restore and fork keep history isolated`` () =
    let w0 = Workspace.initial
    let t = Timeline(w0)
    let start = t.HeadId
    let w1 = w0 |> Workspace.withText FSharp "printfn \"one\""
    let s1 = t.Snapshot("run 1", w1)
    let w2 = w1 |> Workspace.withText FSharp "printfn \"two\""
    let s2 = t.Snapshot("run 2", w2)
    Assert.Equal(3, t.Count)
    Assert.Equal(s2, t.HeadId)

    // rollback moves head back and returns the old buffer
    let restored = (t.Restore s1).Value
    Assert.Equal("printfn \"one\"", Workspace.text FSharp restored)
    Assert.Equal(s1, t.HeadId)

    // later states are still readable (graph, not a stack)
    Assert.Equal("printfn \"two\"", Workspace.text FSharp (t.Read s2).Value)

    // fork is isolated: snapshotting on the fork never rewrites the source
    let f = (t.Fork(start, "experiment")).Value
    Assert.Equal(Workspace.text FSharp w0, Workspace.text FSharp (t.Read f).Value)
    Assert.Equal(None, t.Restore "nope")
    Assert.Contains(t.Entries, fun e -> e.Id = f && e.Branch = "experiment")
    Assert.Single(t.Entries |> List.filter (fun e -> e.IsHead)) |> ignore

[<Fact>]
let ``diff reports only changed lines`` () =
    let d = Diff.lines "a\nb\nc" "a\nB\nc\nd"
    Assert.Equal(3, Diff.changed d)
    Assert.Equal(0, Diff.changed (Diff.lines "x" "x"))

[<Fact>]
let ``gcc and clang lines parse with position`` () =
    let g = Problems.parseLine C "<source>:3:5: error: expected ';' before 'return'"
    Assert.True g.IsSome
    Assert.Equal(Severity.Error, g.Value.Severity)
    Assert.Equal(3, g.Value.Range.Value.StartLine)
    Assert.Equal(5, g.Value.Range.Value.StartColumn)

    let w =
        Problems.parseLine
            Cpp
            "\u001b[01m\u001b[K<source>:2:10: \u001b[01;35m\u001b[Kwarning: \u001b[m\u001b[Kunused variable 'x' [-Wunused-variable]"

    Assert.Equal(Severity.Warning, w.Value.Severity)
    Assert.StartsWith("unused variable", w.Value.Message)

[<Fact>]
let ``dotnet compiler lines parse for F# and C#`` () =
    let f =
        Problems.parseLine
            FSharp
            "/app/Program.fs(4,13,4,17): error FS0039: The value or constructor 'prnt' is not defined."

    Assert.Equal("FS0039", f.Value.Code)
    Assert.Equal(17, f.Value.Range.Value.EndColumn)

    let c =
        Problems.parseLine
            CSharp
            "/app/Program.cs(2,9): error CS0103: The name 'x' does not exist in the current context [/app/p.csproj]"

    Assert.Equal("CS0103", c.Value.Code)
    Assert.DoesNotContain("csproj", c.Value.Message)
    Assert.Equal(None, Problems.parseLine C "just some program output")

[<Fact>]
let ``readable text drops colour codes and breaks lines at F# group separators`` () =
    let esc = string (char 27)
    let sep = string (char 29)
    let nl = string (char 10)

    let raw =
        esc
        + "[01m"
        + esc
        + "[Kerror"
        + esc
        + "[m: bad"
        + sep
        + "    'int'"
        + sep
        + "but here"

    Assert.Equal("error: bad" + nl + "    'int'" + nl + "but here", Problems.readable raw)
    Assert.Equal("error: bad" + sep + "    'int'" + sep + "but here", Problems.stripAnsi raw)

[<Fact>]
let ``an F# multi-part message parses as one diagnostic with the whole message`` () =
    // Real fsc output (via Compiler Explorer): the parts of FS0001 are joined on ONE line with 0x1D separators.
    let sep = string (char 29)

    let line =
        "<source>(1,14): error FS0001: This expression was expected to have type"
        + sep
        + "    'int'"
        + sep
        + "but here has type"
        + sep
        + "    'string'"

    let parsed = Problems.parse FSharp line
    Assert.Equal(1, parsed.Length)
    let d = parsed.Head
    Assert.Equal("FS0001", d.Code)
    Assert.Equal(Severity.Error, d.Severity)
    Assert.Equal("This expression was expected to have type 'int' but here has type 'string'", d.Message)
    Assert.Equal(1, d.Range.Value.StartLine)
    Assert.Equal(14, d.Range.Value.StartColumn)

[<Fact>]
let ``build summary counts parsed errors and never says 1 errors`` () =
    let raw: RawResult =
        { Engine = "GCC 14.2"
          BuildFailed = true
          BuildOutput = "<source>:1:21: error: 'x' was not declared in this scope"
          Stdout = ""
          Stderr = ""
          ExitCode = -1
          TimedOut = false
          DurationMs = 500
          Cached = false }

    Assert.Equal("Build failed: 1 error", (Protocol.interpret Cpp raw).Summary)

    let unparsed =
        { raw with
            BuildOutput = "something went wrong" }

    Assert.Equal("Build failed: 1 error", (Protocol.interpret Cpp unparsed).Summary)

[<Fact>]
let ``bracket check finds unclosed and mismatched but ignores strings and comments`` () =
    Assert.Empty(Problems.bracketCheck C "int main(){ printf(\"}\"); /* ( */ return 0; } // {")
    Assert.Empty(Problems.bracketCheck FSharp "let f (x: int) = (* ( *) x + 1")
    let bad = Problems.bracketCheck Cpp "int main() {\n  if (x {\n}"
    Assert.NotEmpty bad
    Assert.All(bad, fun d -> Assert.Equal(Severity.Error, d.Severity))
    Assert.Contains(Problems.localChecks C "int f(){return 1;}", fun d -> d.Severity = Severity.Warning)

[<Fact>]
let ``interpret separates build failures from runtime results`` () =
    let raw =
        { Engine = "GCC 14.2"
          BuildFailed = true
          BuildOutput = "<source>:1:1: error: boom"
          Stdout = ""
          Stderr = ""
          ExitCode = -1
          TimedOut = false
          DurationMs = 0
          Cached = false }

    let r = Protocol.interpret C raw
    Assert.False r.Ok
    Assert.Equal(Build, r.Phase)
    Assert.Contains("1 error", r.Summary)

    let ok =
        { raw with
            BuildFailed = false
            BuildOutput = ""
            Stdout = "hi"
            ExitCode = 0
            DurationMs = 350 }

    let r2 = Protocol.interpret C ok
    Assert.True r2.Ok
    Assert.Contains("0.35s", r2.Summary)

    let t =
        Protocol.interpret
            C
            { ok with
                TimedOut = true
                ExitCode = 124 }

    Assert.False t.Ok
    Assert.Contains("Timed out", t.Summary)

[<Fact>]
let ``fallback chain always ends in local checks then escalation`` () =
    for l in Language.all do
        let steps = Fallback.chain l "does-not-exist"

        match steps with
        | TryCompiler c :: _ -> Assert.Equal(l, c.Language)
        | _ -> failwith "chain must start with a compiler"

        let tail = steps |> List.rev |> List.truncate 3 |> List.rev
        Assert.Equal<Step list>([ WarmResult; LocalChecks; tail.[2] ], tail)

        Assert.True(
            match tail.[2] with
            | Escalate _ -> true
            | _ -> false
        )

    let withSelected = Fallback.chain Cpp "clang1910"

    Assert.Equal(
        "clang1910",
        (match withSelected.Head with
         | TryCompiler c -> c.Id
         | _ -> "")
    )

[<Fact>]
let ``request json escapes control characters and fingerprint is pinned`` () =
    let bs = string (char 92)

    let src =
        "int main(){"
        + string (char 10)
        + "puts("
        + string (char 34)
        + "a"
        + bs
        + "b"
        + string (char 34)
        + ");}"

    let json = Protocol.requestJson (Compilers.defaultFor C) "-O2" src ""
    Assert.Contains("a" + bs + bs + "b", json)
    Assert.Contains(bs + "n", json)
    Assert.DoesNotContain(string (char 10), json)
    Assert.Equal("09f9120a90ee8d9e", Protocol.fingerprint [ "fsharp"; "let x = 1" ])
