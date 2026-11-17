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

// ---- Rust (ADR-0019) ------------------------------------------------------------------------------

[<Fact>]
let ``rust has aliases, a file name and three compilers`` () =
    Assert.Equal(Some Rust, Language.tryOfId "rust")
    Assert.Equal(Some Rust, Language.tryOfId "RS")
    Assert.Equal("main.rs", Language.fileName Rust)
    Assert.Equal("Rust", Language.displayName Rust)
    Assert.Equal(3, (Compilers.forLanguage Rust).Length)
    Assert.All(Compilers.forLanguage Rust, fun c -> Assert.StartsWith("r", c.Id))

[<Fact>]
let ``rustc errors parse across lines with code, position and docs link, and the summary is not an error`` () =
    let esc = string (char 27)

    let output =
        [ esc
          + "[1m"
          + esc
          + "[91merror[E0425]"
          + esc
          + "[0m: cannot find value `y` in this scope"
          " " + esc + "[94m--> " + esc + "[0m<source>:3:20"
          "  |"
          "3 |     println!(\"{}\", y);"
          "  |                    ^"
          ""
          "error[E0308]: mismatched types"
          " --> <source>:2:18"
          "  |"
          "error: aborting due to 2 previous errors"
          ""
          "For more information about this error, try `rustc --explain E0425`." ]
        |> String.concat "\n"

    let ds = Problems.parse Rust output
    Assert.Equal(2, ds.Length)
    Assert.Equal("E0425", ds.[0].Code)
    Assert.Equal(Severity.Error, ds.[0].Severity)
    Assert.Equal("cannot find value `y` in this scope", ds.[0].Message)
    Assert.Equal(3, ds.[0].Range.Value.StartLine)
    Assert.Equal(20, ds.[0].Range.Value.StartColumn)
    Assert.Equal("main.rs", ds.[0].Range.Value.File)
    Assert.Equal(Some "https://doc.rust-lang.org/error_codes/E0425.html", ds.[0].DocsUrl)
    Assert.Equal("E0308", ds.[1].Code)
    Assert.Equal(2, ds.[1].Range.Value.StartLine)
    Assert.Equal(2, Problems.countOf Severity.Error ds)

[<Fact>]
let ``rustc lint warnings parse, and their summary line is dropped`` () =
    let output =
        "warning: unused variable: `unused`\n --> <source>:3:9\n  |\n3 |     let unused = 5;\n  |         ^^^^^^\n  |\n  = note: `#[warn(unused_variables)]` on by default\n\nwarning: 1 warning emitted\n"

    let ds = Problems.parse Rust output
    Assert.Single ds |> ignore
    Assert.Equal(Severity.Warning, ds.Head.Severity)
    Assert.Equal("rustc", ds.Head.Code)
    Assert.Equal(None, ds.Head.DocsUrl)
    Assert.Equal(9, ds.Head.Range.Value.StartColumn)

[<Fact>]
let ``a rust panic becomes an error pinned to the panicking line`` () =
    let stderr =
        "\nthread 'main' (2) panicked at /app/example.rs:5:21:\nindex out of bounds: the len is 1 but the index is 3\nnote: run with `RUST_BACKTRACE=1` environment variable to display a backtrace\n"

    let ds = Problems.parse Rust stderr
    Assert.Single ds |> ignore
    Assert.Equal("panic", ds.Head.Code)
    Assert.Equal("index out of bounds: the len is 1 but the index is 3", ds.Head.Message)
    Assert.Equal(5, ds.Head.Range.Value.StartLine)
    Assert.Equal(21, ds.Head.Range.Value.StartColumn)
    // the older panic format (no thread id) parses too
    Assert.Single(Problems.parse Rust "thread 'main' panicked at src/main.rs:2:5:\nboom")
    |> ignore

[<Fact>]
let ``a failed rust build is summarised like every other language`` () =
    let raw: RawResult =
        { Engine = "Rust 1.99"
          BuildFailed = true
          BuildOutput =
            "error[E0425]: cannot find value `y` in this scope\n --> <source>:3:20\n\nerror[E0308]: mismatched types\n --> <source>:2:18\n"
          Stdout = ""
          Stderr = ""
          ExitCode = -1
          TimedOut = false
          DurationMs = 900
          Cached = false }

    let r = Protocol.interpret Rust raw
    Assert.Equal("Build failed: 2 errors", r.Summary)
    Assert.Equal(2, r.Diagnostics.Length)

    let panicked =
        { raw with
            BuildFailed = false
            BuildOutput = ""
            Stderr = "thread 'main' (2) panicked at /app/example.rs:4:5:\nboom"
            ExitCode = 101 }

    let r2 = Protocol.interpret Rust panicked
    Assert.False r2.Ok
    Assert.StartsWith("Exited with code 101", r2.Summary)
    Assert.Equal(4, r2.Diagnostics.Head.Range.Value.StartLine)

[<Fact>]
let ``rust bracket check understands lifetimes, raw strings, nested comments and chars`` () =
    // Each of these would produce false errors if ' started a char literal or " ended a raw string.
    Assert.Empty(Problems.bracketCheck Rust "fn first<'a>(x: &'a str) -> &'a str {\n    x\n}\n")
    Assert.Empty(Problems.bracketCheck Rust "fn f() {\n    'outer: loop {\n        break 'outer;\n    }\n}\n")
    Assert.Empty(Problems.bracketCheck Rust "fn f() { let s = r#\"a \"quoted\" } brace\"#; let t = br\"{\"; }")
    Assert.Empty(Problems.bracketCheck Rust "/* outer /* inner { */ still a comment { */ fn f() {}")

    Assert.Empty(
        Problems.bracketCheck Rust "fn f() { let a = '{'; let b = '\\''; let c = '\\u{1F600}'; let d = '😀'; }"
    )

    Assert.Empty(Problems.bracketCheck Rust "fn f() {\n    let s = \"multi\nline } string\";\n}\n")
    // real mistakes are still found
    let bad = Problems.bracketCheck Rust "fn main() {\n    let v = vec![1, 2;\n}\n"
    Assert.NotEmpty bad
    Assert.All(bad, fun d -> Assert.Equal(Severity.Error, d.Severity))
    // an identifier ending in r is not a raw string prefix
    Assert.Empty(Problems.bracketCheck Rust "fn f() { let mr = 1; let ar = \"}\"; }")

[<Fact>]
let ``rust local checks warn when there is no main`` () =
    Assert.Contains(
        Problems.localChecks Rust "fn helper() {}",
        fun d -> d.Severity = Severity.Warning && d.Code = "local-entry"
    )

    Assert.Empty(Problems.localChecks Rust "fn main() {\n    println!(\"hi\");\n}\n")

[<Fact>]
let ``every starter template is balanced and has an entry point`` () =
    for t in Templates.all do
        let found = Problems.localChecks t.Language t.Code

        Assert.True(
            List.isEmpty found,
            t.Key
            + " has local problems: "
            + (found |> List.map (fun d -> d.Message) |> String.concat "; ")
        )

[<Fact>]
let ``rust templates teach the F# interop boundary`` () =
    let keys =
        Templates.all
        |> List.filter (fun t -> t.Language = Rust)
        |> List.map (fun t -> t.Key)

    Assert.Contains("rust-hello", keys)
    Assert.Contains("rust-ffi", keys)
    Assert.Contains("extern \"C\"", (Templates.tryFind "rust-ffi").Value.Code)

// ---- The polyglot IDE (ADR-0020) ------------------------------------------------------------------
// Every sample below is output captured from the real services (Compiler Explorer and Wandbox), except where a
// comment says it is the tool's documented format.

let private lines (xs: string list) = String.concat "\n" xs

let private only (ds: StructuredDiagnostic list) =
    Assert.Single ds |> ignore
    List.head ds

let private lineOf (d: StructuredDiagnostic) = d.Range.Value.StartLine
let private colOf (d: StructuredDiagnostic) = d.Range.Value.StartColumn

[<Fact>]
let ``the language table has eighteen languages in tab order with unique ids and files`` () =
    let ids = Language.all |> List.map Language.id

    Assert.Equal<string list>(
        [ "fsharp"
          "csharp"
          "vb"
          "javascript"
          "typescript"
          "python"
          "rust"
          "dart"
          "c"
          "cpp"
          "go"
          "java"
          "ruby"
          "lua"
          "php"
          "r"
          "haskell"
          "ocaml" ],
        ids
    )

    Assert.Equal(ids.Length, (List.distinct ids).Length)
    Assert.Equal(ids.Length, (Language.all |> List.map Language.fileName |> List.distinct).Length)
    Assert.Equal("Main.java", Language.fileName Java)
    Assert.Equal("Program.vb", Language.fileName VisualBasic)

[<Fact>]
let ``aliases resolve for the new languages and never collide with an id`` () =
    Assert.Equal(Some VisualBasic, Language.tryOfId "VB.NET")
    Assert.Equal(Some JavaScript, Language.tryOfId "js")
    Assert.Equal(Some TypeScript, Language.tryOfId "TS")
    Assert.Equal(Some Python, Language.tryOfId "py")
    Assert.Equal(Some Go, Language.tryOfId "golang")
    Assert.Equal(Some Haskell, Language.tryOfId "hs")
    Assert.Equal(Some OCaml, Language.tryOfId "ml")
    Assert.Equal(Some Ruby, Language.tryOfId "rb")
    Assert.Equal(None, Language.tryOfId "kotlin")

    for info in Language.infos do
        for alias in info.Aliases do
            Assert.Equal(Some info.Lang, Language.tryOfId alias)

[<Fact>]
let ``languages are grouped contiguously and only C, C++ and Rust take compiler flags`` () =
    let groups = Language.all |> List.map Language.group
    // A group is one run of tabs: once it ends it never comes back.
    let runs =
        groups
        |> List.fold
            (fun acc g ->
                match acc with
                | h :: _ when h = g -> acc
                | _ -> g :: acc)
            []

    Assert.Equal(runs.Length, (List.distinct runs).Length)
    Assert.Equal<string list>([ "dotnet"; "fable"; "systems"; "scripting"; "functional" ], List.distinct groups)

    let flagged = Language.all |> List.filter Language.takesFlags
    Assert.Equal<Language list>([ Rust; C; Cpp ], flagged)

    for c in Compilers.all do
        if Language.takesFlags c.Language then
            Assert.NotEqual<string>("", c.DefaultOptions)
        else
            Assert.Equal("", c.DefaultOptions)

[<Fact>]
let ``every language has compilers with unique ids, and a sibling to fall back to except TypeScript and R`` () =
    let ids = Compilers.all |> List.map (fun c -> c.Id)
    Assert.Equal(ids.Length, (List.distinct ids).Length)
    Assert.Equal(45, ids.Length)

    for l in Language.all do
        let n = (Compilers.forLanguage l).Length

        if l = R || l = TypeScript then
            Assert.Equal(1, n)
        else
            Assert.True(n >= 2, Language.id l + " has no sibling compiler")

[<Fact>]
let ``the run request names the compiler's own language`` () =
    let json =
        Protocol.requestJson (Compilers.defaultFor VisualBasic) "" "Module M\nEnd Module" ""

    Assert.Contains("\"language\":\"vb\"", json)
    Assert.Contains("\"compiler\":\"dotnet90vbcoreclr\"", json)

[<Fact>]
let ``python tracebacks and syntax errors are pinned to the user's line`` () =
    let runtime =
        lines
            [ "Traceback (most recent call last):"
              "  File \"/app/output.s\", line 2, in <module>"
              "    undefined_name + 1"
              "    ^^^^^^^^^^^^^^"
              "NameError: name 'undefined_name' is not defined" ]

    let d = only (Problems.parse Python runtime)
    Assert.Equal("NameError", d.Code)
    Assert.Equal("name 'undefined_name' is not defined", d.Message)
    Assert.Equal(2, lineOf d)
    Assert.Equal("main.py", d.Range.Value.File)

    let syntax =
        lines
            [ "  File \"/app/output.s\", line 1"
              "    def f(:"
              "          ^"
              "SyntaxError: invalid syntax" ]

    let s = only (Problems.parse Python syntax)
    Assert.Equal("SyntaxError", s.Code)
    Assert.Equal(1, lineOf s)

[<Fact>]
let ``python frames inside the standard library are not blamed on the user`` () =
    let trace =
        lines
            [ "Traceback (most recent call last):"
              "  File \"/app/output.s\", line 3, in <module>"
              "    json.loads('x')"
              "  File \"/opt/compiler-explorer/python-3.13/lib/python3.13/json/__init__.py\", line 346, in loads"
              "    return _default_decoder.decode(s)"
              "json.decoder.JSONDecodeError: Expecting value: line 1 column 1 (char 0)" ]

    // location-first pairing keeps the last location seen before the message, so the library frame must not count
    Assert.Equal(3, lineOf (only (Problems.parse Python trace)))

[<Fact>]
let ``vb compile errors and unhandled exceptions parse`` () =
    let build =
        lines
            [ "<source>(3) : error BC30451: 'missing' is not declared. It may be inaccessible due to its protection level."
              ""
              "        Console.WriteLine(missing)"
              "                          ~~~~~~~" ]

    let d = only (Problems.parse VisualBasic build)
    Assert.Equal("BC30451", d.Code)
    Assert.Equal(Severity.Error, d.Severity)
    Assert.Equal(3, lineOf d)
    Assert.Contains("'missing' is not declared", d.Message)

    let run =
        lines
            [ "Unhandled exception. System.InvalidOperationException: boom"
              "   at CompilerExplorer.Program.Main() in /app/example.vb:line 3"
              "Program terminated with signal SIGABRT (6)" ]

    let r = only (Problems.parse VisualBasic run)
    Assert.Equal("System.InvalidOperationException", r.Code)
    Assert.Equal("boom", r.Message)
    Assert.Equal(3, lineOf r)

[<Fact>]
let ``dart compile errors list every error and the AOT summary is not one of them`` () =
    let build =
        lines
            [ "example.dart:3:9: Error: Undefined name 'y'."
              "  print(y);"
              "        ^"
              "example.dart:2:11: Error: A value of type 'String' can't be assigned to a variable of type 'int'."
              "  int x = 'a';"
              "          ^"
              "Error: AOT compilation failed"
              "Bad state: Generating AOT kernel dill failed!" ]

    let ds = Problems.parse Dart build
    Assert.Equal<int list>([ 3; 2 ], ds |> List.map lineOf)
    Assert.Equal<int list>([ 9; 11 ], ds |> List.map colOf)
    Assert.Contains("Undefined name 'y'", ds.Head.Message)

    let run =
        lines
            [ "Unhandled exception:"
              "Bad state: boom"
              "#0      main (file:///app/example.dart:2)"
              "#1      _delayEntrypointInvocation.<anonymous closure> (dart:isolate-patch/isolate_patch.dart:313)" ]

    let r = only (Problems.parse Dart run)
    Assert.Equal("Bad state: boom", r.Message)
    Assert.Equal(2, lineOf r)

[<Fact>]
let ``javac errors parse with their line and the error count line is dropped`` () =
    let build =
        lines
            [ "<source>:1: error: class Main is public, should be declared in a file named Main.java"
              "public class Main {"
              "       ^"
              "<source>:3: error: incompatible types: String cannot be converted to int"
              "        int x = \"a\";"
              "                ^"
              "2 errors" ]

    let ds = Problems.parse Java build
    Assert.Equal<int list>([ 1; 3 ], ds |> List.map lineOf)
    Assert.All(ds, fun d -> Assert.Equal(Severity.Error, d.Severity))
    Assert.DoesNotContain(ds, fun d -> d.Message.Contains "2 errors")

[<Fact>]
let ``a java exception is pinned to the user's frame`` () =
    // The JVM's documented uncaught-exception format; the live check in docs/operations/ide.md covers a real run.
    let run =
        lines
            [ "Exception in thread \"main\" java.lang.IllegalStateException: boom"
              "\tat Main.explode(Main.java:5)"
              "\tat Main.main(Main.java:9)" ]

    let d = only (Problems.parse Java run)
    Assert.Equal("java.lang.IllegalStateException", d.Code)
    Assert.Equal("boom", d.Message)
    Assert.Equal(5, lineOf d)

[<Fact>]
let ``go compile errors keep their columns and runtime panics are pinned to the user's line`` () =
    let build =
        lines
            [ "# command-line-arguments"
              "./example.go:4:2: declared and not used: x"
              "./example.go:4:7: undefined: undefinedThing" ]

    let ds = Problems.parse Go build
    Assert.Equal<int list>([ 2; 7 ], ds |> List.map colOf)

    Assert.Equal<string list>(
        [ "declared and not used: x"; "undefined: undefinedThing" ],
        ds |> List.map (fun d -> d.Message)
    )

    let run =
        lines
            [ "panic: runtime error: index out of range [3] with length 0"
              ""
              "goroutine 1 [running]:"
              "main.main()"
              "\t./example.go:5 +0x9" ]

    let p = only (Problems.parse Go run)
    Assert.Equal("panic", p.Code)
    Assert.Contains("index out of range", p.Message)
    Assert.Equal(5, lineOf p)

[<Fact>]
let ``ruby runtime errors and syntax errors parse`` () =
    let d =
        only (Problems.parse Ruby "/app/output.s:2:in '<main>': boom (RuntimeError)")

    Assert.Equal("boom (RuntimeError)", d.Message)
    Assert.Equal(2, lineOf d)

    let syntax =
        lines
            [ "/app/output.s: --> /app/output.s"
              ""
              "Unmatched `(', missing `)' ?"
              ""
              "/app/output.s:1: syntax errors found (SyntaxError)"
              "> 1 | def f("
              "    | ^~~ expected an `end` to close the `def` statement" ]

    let s = only (Problems.parse Ruby syntax)
    Assert.Equal(1, lineOf s)
    Assert.Contains("SyntaxError", s.Message)

[<Fact>]
let ``lua reports the error once, not once per traceback line`` () =
    let run =
        lines
            [ "/opt/compiler-explorer/lua-5.4.7/bin/lua: /app/output.s:2: boom"
              "stack traceback:"
              "\t[C]: in function 'error'"
              "\t/app/output.s:2: in main chunk"
              "\t[C]: in ?" ]

    let d = only (Problems.parse Lua run)
    Assert.Equal("boom", d.Message)
    Assert.Equal(2, lineOf d)

    let syntax =
        "/opt/compiler-explorer/lua-5.4.7/bin/lua: /app/output.s:1: unexpected symbol near '='"

    Assert.Equal("unexpected symbol near '='", (only (Problems.parse Lua syntax)).Message)

[<Fact>]
let ``php errors are read from stdout, where PHP prints them`` () =
    let raw: RawResult =
        { Engine = "PHP 8.3"
          BuildFailed = false
          BuildOutput = ""
          Stdout =
            lines
                [ "1"
                  "Fatal error: Uncaught Exception: boom in /home/wandbox/prog.php:3"
                  "Stack trace:"
                  "#0 {main}"
                  "  thrown in /home/wandbox/prog.php on line 3"
                  "" ]
          Stderr = ""
          ExitCode = 255
          TimedOut = false
          DurationMs = 40
          Cached = false }

    let report = Protocol.interpret Php raw
    Assert.False report.Ok
    let d = only report.Diagnostics
    Assert.Equal("Uncaught Exception: boom", d.Message)
    Assert.Equal(3, lineOf d)
    Assert.Equal(Severity.Error, d.Severity)

    let parseError =
        "\nParse error: syntax error, unexpected token \";\" in /home/wandbox/prog.php on line 2\n"

    Assert.Equal(2, lineOf (only (Problems.parse Php parseError)))

    // Only PHP is read this way: a Python program that prints something error-shaped is not a diagnostic.
    let py =
        { raw with
            Stdout = "Error: not a real error"
            ExitCode = 0 }

    Assert.Empty((Protocol.interpret Python py).Diagnostics)

[<Fact>]
let ``R errors parse with and without a call`` () =
    Assert.Equal("boom", (only (Problems.parse R "Error: boom\nExecution halted\n")).Message)

    let called =
        only (Problems.parse R "Error in undefined_fn(3) : could not find function \"undefined_fn\"")

    Assert.Equal("could not find function \"undefined_fn\"", called.Message)

[<Fact>]
let ``ghc errors carry their position and the explanation, not just the error code`` () =
    let build =
        lines
            [ "[1 of 2] Compiling Main             ( <source>, /app/example.o )"
              "<source>:2:22: error: [GHC-83865]"
              "    * Couldn't match type `Bool' with `[Char]'"
              "      Expected: String"
              "        Actual: Bool"
              "  |"
              "2 | main = putStrLn (1 + True)"
              "  |                      ^^^^" ]

    let d = only (Problems.parse Haskell build)
    Assert.Equal(2, lineOf d)
    Assert.Equal(22, colOf d)
    Assert.Contains("Couldn't match type", d.Message)

    let run =
        lines
            [ "output.s: Uncaught exception ghc-internal:GHC.Internal.Exception.ErrorCall:"
              ""
              "boom"
              ""
              "HasCallStack backtrace:"
              "  error, called at /app/example.hs:2:8 in main:Main"
              "" ]

    let r = only (Problems.parse Haskell run)
    Assert.Equal("boom", r.Message)
    Assert.Equal(2, lineOf r)

[<Fact>]
let ``ocaml errors use the one-based column and a failure names its exception`` () =
    let build =
        lines
            [ "File \"<source>\", line 1, characters 23-24:"
              "1 | let () = print_endline 1"
              "                           ^"
              "Error: The constant 1 has type int but an expression was expected of type"
              "         string" ]

    let d = only (Problems.parse OCaml build)
    Assert.Equal(1, lineOf d)
    Assert.Equal(24, colOf d)
    Assert.StartsWith("The constant 1 has type int", d.Message)

    let r = only (Problems.parse OCaml "Fatal error: exception Failure(\"boom\")")
    Assert.Equal("Failure(\"boom\")", r.Message)

[<Fact>]
let ``node errors are pinned to the script line and tsc errors to the TypeScript line`` () =
    let js =
        lines
            [ "/home/wandbox/prog.js:2"
              "null.foo"
              "     ^"
              ""
              "TypeError: Cannot read properties of null (reading 'foo')"
              "    at Object.<anonymous> (/home/wandbox/prog.js:2:6)"
              "    at Module._compile (node:internal/modules/cjs/loader:1469:14)"
              ""
              "Node.js v20.17.0" ]

    let d = only (Problems.parse JavaScript js)
    Assert.Equal("TypeError", d.Code)
    Assert.Equal(2, lineOf d)

    let tsc =
        "prog.ts(1,7): error TS2322: Type 'string' is not assignable to type 'number'.\n"

    let t = only (Problems.parse TypeScript tsc)
    Assert.Equal("TS2322", t.Code)
    Assert.Equal(1, lineOf t)
    Assert.Equal(7, colOf t)
    Assert.Equal("main.ts", t.Range.Value.File)

    // At run time the line numbers are those of the compiled JavaScript, so the diagnostic must not point into the editor.
    let compiled =
        "/home/wandbox/prog.js:2\nthrow new Error('boom');\n^\n\nError: boom\n    at Object.<anonymous> (/home/wandbox/prog.js:2:7)\n"

    let c = only (Problems.parse TypeScript compiled)
    Assert.Equal("boom", c.Message)
    Assert.True c.Range.IsNone

[<Fact>]
let ``the bracket check only runs for languages whose strings and comments it understands`` () =
    // Each of these is valid code that a naive bracket count would call unbalanced.
    Assert.Empty(Problems.bracketCheck Python "s = \"\"\"(\"\"\"\nprint(s)\n")
    Assert.Empty(Problems.bracketCheck Lua "--[[ ( ]]\nprint('x')\n")
    Assert.Empty(Problems.bracketCheck Haskell "{- ( -}\nmain :: IO ()\nmain = pure ()\n")
    Assert.Empty(Problems.bracketCheck OCaml "(* ( *)\nlet () = ()\n")
    Assert.Empty(Problems.bracketCheck Ruby "puts <<~EOS\n  (\nEOS\n")
    Assert.Empty(Problems.bracketCheck Php "<?php\necho <<<EOT\n(\nEOT;\n")
    Assert.NotEmpty(Problems.bracketCheck Java "class Main { void f() { }")
    Assert.NotEmpty(Problems.bracketCheck CSharp "class C { void F() { }")

[<Fact>]
let ``a missing entry point is a warning for the languages that need one and never for scripts`` () =
    let warns lang text =
        Problems.localChecks lang text
        |> List.exists (fun d -> d.Code = "local-entry" && d.Severity = Severity.Warning)

    Assert.True(warns Go "package main\n\nfunc helper() {}\n")
    Assert.True(warns Java "class Main { void helper() { } }")
    Assert.True(warns Dart "void helper() {}\n")
    Assert.True(warns VisualBasic "Module M\n    Sub Other()\n    End Sub\nEnd Module\n")
    Assert.True(warns Haskell "helper :: Int\nhelper = 1\n")

    Assert.False(warns Go "package main\n\nfunc main() {}\n")
    Assert.False(warns VisualBasic "Module M\n    sub main()\n    End Sub\nEnd Module\n")
    Assert.False(warns Haskell "main :: IO ()\nmain = pure ()\n")

    for l in [ JavaScript; TypeScript; Python; Ruby; Lua; Php; R; OCaml; FSharp; CSharp ] do
        Assert.False(warns l "", Language.id l + " has no required entry point")

[<Fact>]
let ``every language has at least one starter, keys are unique, and titles are not blank`` () =
    let keys = Templates.all |> List.map (fun t -> t.Key)
    Assert.Equal(keys.Length, (List.distinct keys).Length)
    Assert.Equal(34, keys.Length)

    for l in Language.all do
        Assert.NotEmpty(Templates.all |> List.filter (fun t -> t.Language = l))

    for t in Templates.all do
        Assert.False(System.String.IsNullOrWhiteSpace t.Title, t.Key)
        Assert.False(System.String.IsNullOrWhiteSpace t.Code, t.Key)
        Assert.True(Templates.tryFind t.Key |> Option.isSome)

[<Fact>]
let ``a build failure in any language is summarised the same way`` () =
    let raw: RawResult =
        { Engine = "Go 1.26"
          BuildFailed = true
          BuildOutput =
            "# command-line-arguments\n./example.go:4:2: declared and not used: x\n./example.go:4:7: undefined: undefinedThing"
          Stdout = ""
          Stderr = ""
          ExitCode = -1
          TimedOut = false
          DurationMs = 0
          Cached = false }

    let r = Protocol.interpret Go raw
    Assert.Equal(Build, r.Phase)
    Assert.Equal("Build failed: 2 errors", r.Summary)
    Assert.Equal(2, r.Diagnostics.Length)
