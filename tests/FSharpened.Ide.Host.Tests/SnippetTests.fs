/// Snippets: the template becomes text with fields, lines take the indentation of where they go, and the person's
/// own snippet files are read.
module SnippetTests

open System.IO
open Xunit
open FSharpened.Ide
open FSharpened.Ide.Host

let private ctx: SnippetContext =
    { FileName = "app.fs"
      FilePath = "src/app.fs"
      Folder = "src"
      Selected = ""
      ProjectName = "proj"
      Year = 2026
      Month = 11
      Day = 18
      LineComment = "//" }

let private expand (body: string) =
    SnippetExpand.parse (Snippets.template body ctx).Template

[<Fact>]
let ``fields become places with their defaults, in order, and the caret ends at the final stop`` () =
    let e = expand "let ${1:name} ${2:arg} =\n\t${0}"
    Assert.Equal("let name arg =\n\t", e.Text)
    Assert.Equal<int list>([ 1; 2; 0 ], e.Stops |> List.map (fun s -> s.Index))
    Assert.Equal<(int * int) list>([ 4, 4 ], e.Stops[0].Ranges)
    Assert.Equal<(int * int) list>([ 9, 3 ], e.Stops[1].Ranges)
    Assert.Equal<(int * int) list>([ e.Text.Length, 0 ], e.Stops[2].Ranges)

[<Fact>]
let ``a field written twice is one place with linked copies`` () =
    let e = expand "for (int ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++)"
    Assert.Equal("for (int i = 0; i < n; i++)", e.Text)
    Assert.Equal(3, e.Stops.Length)
    Assert.Equal(3, e.Stops[0].Ranges.Length)

    for (o, l) in e.Stops[0].Ranges do
        Assert.Equal("i", e.Text.Substring(o, l))

[<Fact>]
let ``a snippet with no final stop ends with the caret at its end`` () =
    let e = expand "console.log(${1});"
    Assert.Equal("console.log();", e.Text)
    Assert.Equal<(int * int) list>([ 12, 0 ], e.Stops[0].Ranges)
    Assert.Equal<(int * int) list>([ e.Text.Length, 0 ], (List.last e.Stops).Ranges)

[<Fact>]
let ``variables are filled in when the snippet is made`` () =
    let e = expand "// ${TM_FILENAME} ${CURRENT_YEAR}"
    Assert.Equal("// app.fs 2026", e.Text)

[<Fact>]
let ``layout indents later lines and turns tabs into the indent unit`` () =
    let e = expand "if ${1:c} {\n\t${0}\n}" |> SnippetExpand.layout "    " "  " "\n"
    Assert.Equal("if c {\n      \n    }", e.Text)
    // the final stop is where the tab's line ended
    Assert.Equal<(int * int) list>([ 6 + 1 + 4 + 2, 0 ], (List.last e.Stops).Ranges)
    Assert.Equal<(int * int) list>([ 3, 1 ], e.Stops[0].Ranges)

[<Fact>]
let ``layout uses the document's line break`` () =
    let e = expand "a\nb" |> SnippetExpand.layout "" "\t" "\r\n"
    Assert.Equal("a\r\nb", e.Text)

[<Fact>]
let ``the word before the caret is the snippet prefix`` () =
    Assert.Equal(Some("for", 4), SnippetExpand.prefixBefore "let for" 7)
    Assert.Equal(Some("fo", 4), SnippetExpand.prefixBefore "let for" 6)
    Assert.Equal(None, SnippetExpand.prefixBefore "let " 4)
    Assert.Equal(Some("a-b", 0), SnippetExpand.prefixBefore "a-b" 3)

[<Fact>]
let ``a snippet is found by its exact label, built-in first`` () =
    let all = Snippets.forLanguage "fsharp" []
    Assert.True((SnippetExpand.find all "match").IsSome)
    Assert.True((SnippetExpand.find all "mat").IsNone)

    let mine =
        { Label = "match"
          Detail = "mine"
          Body = "x"
          Languages = [] }

    let both = Snippets.forLanguage "fsharp" [ mine ]
    Assert.Equal("match expression", (SnippetExpand.find both "match").Value.Detail)

[<Fact>]
let ``a snippet file in VS Code's format is read, with comments and several bodies`` () =
    let json =
        """
        {
          // a comment
          "Print": { "prefix": ["pp", "print"], "body": ["printfn \"$1\"", "$0"], "description": "print a line" },
          "NoPrefix": { "body": "x" },
          "NoBody": { "prefix": "nb" },
          "Both": { "prefix": "bo", "body": "b", "scope": "go, rust" },
        }
        """

    let defs, problems = SnippetFiles.parse "fsharp.json" json
    Assert.Equal(2, defs.Length)
    Assert.Equal(2, problems.Length)
    let print = defs |> List.find (fun d -> d.Name = "Print")
    Assert.Equal<string list>([ "pp"; "print" ], print.Prefixes)
    Assert.Equal("printfn \"$1\"\n$0", print.Body)
    // the file name says the language when the snippet does not
    Assert.Equal<string list>([ "fsharp" ], print.Scope)
    Assert.Equal<string list>([ "go"; "rust" ], (defs |> List.find (fun d -> d.Name = "Both")).Scope)

[<Fact>]
let ``a code-snippets file is for every language unless a snippet says otherwise`` () =
    let defs, _ =
        SnippetFiles.parse "mine.code-snippets" """{ "A": { "prefix": "a", "body": "b" } }"""

    Assert.Empty(defs[0].Scope)

[<Fact>]
let ``a snippet file that is not JSON is reported`` () =
    let defs, problems = SnippetFiles.parse "x.json" "{ nope"
    Assert.Empty defs
    Assert.Contains("not valid JSON", problems[0])

[<Fact>]
let ``snippets of a folder are offered in their language only`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fsh-snip-" + System.Guid.NewGuid().ToString("N").Substring(0, 8))

    Directory.CreateDirectory dir |> ignore

    try
        File.WriteAllText(
            Path.Combine(dir, "fsharp.json"),
            """{ "Hello": { "prefix": "hello", "body": "printfn \"hi\"" } }"""
        )

        File.WriteAllText(Path.Combine(dir, "broken.json"), "{")
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignored")
        let snippets, problems = SnippetFiles.loadFolder dir
        Assert.Single problems |> ignore
        Assert.Contains("broken.json", problems[0])
        Assert.Contains("hello", Snippets.forLanguage "fsharp" snippets |> List.map (fun s -> s.Label))
        Assert.DoesNotContain("hello", Snippets.forLanguage "go" snippets |> List.map (fun s -> s.Label))
        Assert.Equal<Snippet list>([], fst (SnippetFiles.loadFolder (Path.Combine(dir, "missing"))))
    finally
        Directory.Delete(dir, true)
