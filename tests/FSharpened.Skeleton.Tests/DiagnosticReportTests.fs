module FSharpened.Skeleton.Tests.DiagnosticReportTests

open System
open System.IO
open Xunit
open FSharpened.Diagnostics
open FSharpened.Analyzers

let private repoRoot () =
    let rec up (d: DirectoryInfo) =
        if isNull d then
            failwith "FSharpened.slnx not found above the test binaries"
        elif File.Exists(Path.Combine(d.FullName, "FSharpened.slnx")) then
            d.FullName
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let private fixture name =
    File.ReadAllText(Path.Combine(repoRoot (), "vscode", "test", "fixtures", name))

let private codes (fs: Finding list) =
    fs |> List.map (fun f -> f.Diagnostic.Code)

[<Fact>]
let ``a platform conditional is FSH0001 with its range and no mechanical fix`` () =
    let found = PolicyRules.check "a.fs" "module A\n  #if LINUX\nlet x = 1\n#endif\n"
    let f = Assert.Single found
    Assert.Equal("FSH0001", f.Diagnostic.Code)
    Assert.Equal(Warning, f.Diagnostic.Severity)
    let r = f.Diagnostic.Range.Value
    Assert.Equal((2, 2, 11), (r.StartLine, r.StartColumn, r.EndColumn))
    Assert.Empty f.Edits
    Assert.Contains("#FSH0001", f.Diagnostic.DocsUrl.Value, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``a conditional that is not about a platform is left alone`` () =
    Assert.Empty(PolicyRules.check "a.fs" "#if DEBUG\nlet x = 1\n#endif\n#if NET8_0_OR_GREATER\n#endif\n")

[<Fact>]
let ``negated and combined platform conditions are found`` () =
    let found = PolicyRules.check "a.fs" "#if !WINDOWS && DEBUG\n#endif\n#elif (OSX)\n"
    Assert.Equal<string list>([ "FSH0001"; "FSH0001" ], codes found)

[<Fact>]
let ``printfn of a plain literal is FSH0002 with an edit that swaps the function`` () =
    let f =
        Assert.Single(PolicyRules.check "a.fs" "let g () =\n    printfn \"hello\"\n")

    Assert.Equal("FSH0002", f.Diagnostic.Code)
    Assert.Equal(Info, f.Diagnostic.Severity)
    let e = Assert.Single f.Edits
    Assert.Equal("System.Console.Out.WriteLine", e.NewText)
    Assert.Equal((2, 4, 11), (e.Range.StartLine, e.Range.StartColumn, e.Range.EndColumn))

[<Fact>]
let ``printf with a format specifier, in a comment, or as a longer identifier is not flagged`` () =
    let text =
        "printfn \"%d\" 1\n// printfn \"x\"\nlet myprintfn = 1\nlet y = x.printfn \"a\"\n"

    Assert.Empty(PolicyRules.check "a.fs" text)

[<Fact>]
let ``windows line endings give the same line numbers`` () =
    let f = Assert.Single(PolicyRules.check "a.fs" "a\r\n#if WINDOWS\r\n")
    Assert.Equal(2, f.Diagnostic.Range.Value.StartLine)

[<Fact>]
let ``json escapes quotes, backslashes and control characters`` () =
    let d =
        { StructuredDiagnostic.create "X1" Error "a \"b\" \ c\n\u0001" with
            Range =
                Some
                    { File = "C:\\p\\f.fs"
                      StartLine = 1
                      StartColumn = 0
                      EndLine = 1
                      EndColumn = 3 } }

    let json = DiagnosticReport.toJson "t" [ { Diagnostic = d; Edits = [] } ]
    let expected = "\"message\":\"a \\\"b\\\" \\\\ c\\n\\u0001\""
    Assert.Contains(expected, json)
    Assert.Contains("\"file\":\"C:\\\\p\\\\f.fs\"", json)
    Assert.Contains("\"severity\":\"error\"", json)
    Assert.StartsWith("{\"schema\":\"fsharpened.diagnostics/1\"", json)

[<Fact>]
let ``an empty report is still a valid document`` () =
    Assert.Equal(
        "{\"schema\":\"fsharpened.diagnostics/1\",\"tool\":\"t\",\"diagnostics\":[]}\n",
        DiagnosticReport.toJson "t" []
    )

[<Fact>]
let ``the sample file produces exactly the golden report the editor tests parse`` () =
    let report =
        DiagnosticReport.toJson "fsh golden" (PolicyRules.check "Sample.fs" (fixture "Sample.fs"))

    // Set FSH_UPDATE_GOLDEN=1 to rewrite the golden file after an intentional format change.
    if Environment.GetEnvironmentVariable "FSH_UPDATE_GOLDEN" = "1" then
        File.WriteAllText(Path.Combine(repoRoot (), "vscode", "test", "fixtures", "analyze-sample.golden.json"), report)

    let golden = fixture "analyze-sample.golden.json"

    Assert.Equal(golden.Replace("\r\n", "\n"), report)

[<Fact>]
let ``text output is one line per diagnostic with a one-based column`` () =
    let text = DiagnosticReport.toText (PolicyRules.check "a.fs" "#if LINUX\n")
    Assert.StartsWith("a.fs(1,1): warning FSH0001:", text)
