module HoverTests

open Xunit
open FSharpened.Diagnostics
open FSharpened.Ide.Desktop

let private range (l1: int) (c1: int) (l2: int) (c2: int) : SourceRange =
    { File = "main.go"
      StartLine = l1
      StartColumn = c1
      EndLine = l2
      EndColumn = c2 }

let private problem (severity: Severity) (code: string) (message: string) (r: SourceRange) : StructuredDiagnostic =
    { Code = code
      Severity = severity
      Range = Some r
      Message = message
      OffendingConstruct = None
      SuggestedFix = None
      DocsUrl = None
      Context = Map.empty }

[<Fact>]
let ``a position is covered by a range from its start up to, not including, its end`` () =
    let r = range 3 5 3 12
    Assert.True(Hover.covers r 3 5)
    Assert.True(Hover.covers r 3 11)
    Assert.False(Hover.covers r 3 12)
    Assert.False(Hover.covers r 3 4)
    Assert.False(Hover.covers r 2 8)
    Assert.False(Hover.covers r 4 8)

[<Fact>]
let ``a range over several lines covers the middle lines whatever the column`` () =
    let r = range 2 10 5 3
    Assert.True(Hover.covers r 2 10)
    Assert.False(Hover.covers r 2 9)
    Assert.True(Hover.covers r 3 1)
    Assert.True(Hover.covers r 4 200)
    Assert.True(Hover.covers r 5 2)
    Assert.False(Hover.covers r 5 3)

[<Fact>]
let ``an empty range covers its own column, so a problem that points at a place can still be hovered`` () =
    let r = range 7 4 7 4
    Assert.True(Hover.covers r 7 4)
    Assert.False(Hover.covers r 7 5)

[<Fact>]
let ``problems under the pointer are listed worst first with their code`` () =
    let list =
        [ problem Severity.Warning "W1" "unused" (range 1 1 1 20)
          problem Severity.Error "UndeclaredName" "undefined: x" (range 1 5 1 6)
          problem Severity.Error "Far" "elsewhere" (range 9 1 9 5) ]

    Assert.Equal<string list>([ "error UndeclaredName: undefined: x"; "warning W1: unused" ], Hover.problemsAt list 1 5)

    Assert.Empty(Hover.problemsAt list 5 1)

[<Fact>]
let ``the box joins problems and the server's text, drops markdown fences, and says nothing when there is nothing`` () =
    Assert.Equal(None, Hover.compose [] None)
    Assert.Equal(None, Hover.compose [] (Some "  \n "))

    let text =
        Hover.compose [ "error: x" ] (Some "```go\nfunc F()\n```\nDoes the thing.")

    Assert.Equal(Some "error: x\n\nfunc F()\nDoes the thing.", text)

[<Fact>]
let ``a very long server answer is cut so the box stays a box`` () =
    match Hover.compose [] (Some(System.String('x', 5000))) with
    | Some t ->
        Assert.True(t.Length <= Hover.maxChars + 1)
        Assert.EndsWith("…", t)
    | None -> failwith "expected text"
