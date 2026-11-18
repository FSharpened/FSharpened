/// What the editor decides about language-server answers: signature emphasis, the references list, the quick-fix order,
/// formatting options and the keys. No window.
module LspDecisionTests

open System.IO
open Avalonia.Input
open Xunit
open FSharpened.Ide.Desktop
open FSharpened.Ide.Host

let private sigHelp label (parameters: string list) active : SignatureHelp =
    { Signatures =
        [ { Label = label
            Doc = "sig doc"
            Parameters = [ for p in parameters -> { Label = p; Doc = "doc of " + p } ]
            ActiveParameter = None } ]
      ActiveSignature = 0
      ActiveParameter = active }

[<Fact>]
let ``the active parameter is cut out of the signature for bold`` () =
    let h = sigHelp "add(a: int, b: int)" [ "a: int"; "b: int" ] 1
    Assert.Equal(("add(a: int, ", "b: int", ")"), SignatureView.parts h)
    Assert.Equal("doc of b: int", SignatureView.documentation h)
    // no parameter named, or one the label does not contain, is no emphasis
    Assert.Equal(("f()", "", ""), SignatureView.parts (sigHelp "f()" [] 0))
    Assert.Equal(("f(x)", "", ""), SignatureView.parts (sigHelp "f(x)" [ "zzz" ] 0))
    Assert.Equal("sig doc", SignatureView.documentation (sigHelp "f()" [] 0))

[<Fact>]
let ``overloads are counted and the trigger characters come from the server or default to a bracket and a comma`` () =
    let one = sigHelp "f(x)" [ "x" ] 0
    Assert.Equal("", SignatureView.counter one)

    let two =
        { one with
            Signatures = one.Signatures @ one.Signatures
            ActiveSignature = 1 }

    Assert.Equal("2 of 2", SignatureView.counter two)
    Assert.True(SignatureView.isTrigger [] "(")
    Assert.True(SignatureView.isTrigger [] ",")
    Assert.False(SignatureView.isTrigger [] "a")
    Assert.True(SignatureView.isTrigger [ "<" ] "<")
    Assert.False(SignatureView.isTrigger [ "<" ] "(")
    Assert.False(SignatureView.isTrigger [] "(a")

let private loc (path: string) l c : LspLocation =
    { Path = path
      Range =
        { Start = { Line = l; Character = c }
          End = { Line = l; Character = c + 1 } } }

[<Fact>]
let ``references are listed by file and line, once each, and only inside the folder`` () =
    let root = Path.Combine(Path.GetTempPath(), "fsh-refs")

    let hits =
        ReferenceRows.inside
            root
            [ loc (Path.Combine(root, "src", "b.rs")) 4 2
              loc (Path.Combine(root, "a.rs")) 9 0
              loc (Path.Combine(root, "a.rs")) 1 3
              loc (Path.Combine(root, "a.rs")) 1 3
              loc (Path.Combine(Path.GetTempPath(), "elsewhere", "c.rs")) 0 0 ]

    Assert.Equal<(string * int * int) list>([ "a.rs", 2, 4; "a.rs", 10, 1; "src/b.rs", 5, 3 ], hits)
    Assert.Equal("3 references in 2 files", ReferenceRows.summary hits)
    Assert.Equal("1 reference", ReferenceRows.summary [ "a.rs", 1, 1 ])
    Assert.Equal("2 references in 1 file", ReferenceRows.summary [ "a.rs", 1, 1; "a.rs", 2, 1 ])
    Assert.Equal("No references found", ReferenceRows.summary [])
    Assert.Equal("a.rs:2:4   let x = total()", ReferenceRows.row "a.rs" 2 4 "    let x = total()  ")

let private action title kind preferred (edit: bool) (command: bool) (disabled: string option) : LspCodeAction =
    { Title = title
      Kind = kind
      IsPreferred = preferred
      Edit = (if edit then Some { Files = []; Unsupported = [] } else None)
      HasCommand = command
      Disabled = disabled
      Raw = "{}" }

[<Fact>]
let ``quick fixes are ordered preferred first and the ones that cannot run last, with the reason in the title`` () =
    let refactor = action "Extract" "refactor.extract" false true false None
    let fix = action "Fix it" "quickfix" false true false None
    let best = action "Best fix" "quickfix" true true false None
    let cmd = action "Run command" "" false false true None
    let off = action "Not now" "refactor" false true false (Some "not in a function")
    let resolvable = action "Later" "refactor" false false false None

    Assert.Equal<string list>(
        [ "Best fix"; "Fix it"; "Extract"; "Later"; "Run command"; "Not now" ],
        QuickFix.order [ refactor; cmd; fix; off; resolvable; best ]
        |> List.map (fun a -> a.Title)
    )

    Assert.True(QuickFix.applicable resolvable)
    Assert.False(QuickFix.applicable cmd)
    Assert.False(QuickFix.applicable off)
    Assert.Equal("Not now (not in a function)", QuickFix.menuTitle off)
    Assert.Contains("not supported", QuickFix.menuTitle cmd)
    Assert.Equal("Fix it", QuickFix.menuTitle fix)

[<Fact>]
let ``only the diagnostics of the lines asked about go with a code action request`` () =
    let diag l1 l2 : LspDiagnostic =
        { Range =
            { Start = { Line = l1; Character = 0 }
              End = { Line = l2; Character = 1 } }
          Severity = 1
          Message = "m"
          Code = ""
          Source = "" }

    let all = [ diag 0 0; diag 2 4; diag 7 7 ]
    Assert.Equal(1, (QuickFix.onLine 3 all).Length)
    Assert.Equal(1, (QuickFix.onLine 0 all).Length)
    Assert.Empty(QuickFix.onLine 5 all)

[<Fact>]
let ``formatting uses the pack's indent, else the editor setting`` () =
    let prefs = FSharpened.Ide.Settings.defaults

    Assert.Equal(
        { TabSize = prefs.TabSize
          InsertSpaces = prefs.InsertSpaces },
        ServerFormat.options None prefs
    )

[<Fact>]
let ``the new shortcuts are on the keys VS Code users expect`` () =
    let key k m = Keymap.commandFor k m true

    Assert.Equal(Some "references", key Key.F12 KeyModifiers.Shift)
    Assert.Equal(Some "definition", key Key.F12 KeyModifiers.None)
    Assert.Equal(Some "quick-fix", key Key.OemPeriod KeyModifiers.Control)
    Assert.Equal(Some "signature-help", key Key.Space (KeyModifiers.Control ||| KeyModifiers.Shift))
    Assert.Equal(Some "complete", key Key.Space KeyModifiers.Control)
