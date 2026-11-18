module DecisionTests

open System
open System.IO
open Xunit
open Avalonia.Input
open FSharpened.Diagnostics
open FSharpened.Ide.Desktop

// ---- paths ------------------------------------------------------------------------------------------

[<Fact>]
let ``child and parent are inverse at every depth`` () =
    Assert.Equal("a", Paths.child "" "a")
    Assert.Equal("a/b", Paths.child "a" "b")
    Assert.Equal("a", Paths.parent "a/b")
    Assert.Equal("", Paths.parent "a")
    Assert.Equal("a/b", Paths.parent "a/b/c.fs")

[<Fact>]
let ``relInside accepts files in the folder and refuses the outside`` () =
    let root = Path.Combine(Path.GetTempPath(), "fsh-root")
    Assert.Equal(Some "src/a.fs", Paths.relInside root (Path.Combine(root, "src", "a.fs")))
    Assert.Equal(None, Paths.relInside root (Path.Combine(root, "..", "other", "a.fs")))
    Assert.Equal(None, Paths.relInside root (Path.GetTempPath()))

[<Fact>]
let ``fullPath uses the platform's separator`` () =
    let root = Path.Combine(Path.GetTempPath(), "fsh-root")
    Assert.Equal(Path.Combine(root, "src", "a.fs"), Paths.fullPath root "src/a.fs")

[<Fact>]
let ``rootName is the folder's own name even with a trailing separator`` () =
    let root = Path.Combine(Path.GetTempPath(), "my-project")
    Assert.Equal("my-project", Paths.rootName (Some root))
    Assert.Equal("my-project", Paths.rootName (Some(root + string Path.DirectorySeparatorChar)))
    Assert.Equal("", Paths.rootName None)

[<Theory>]
[<InlineData("/work/p/src/a.fs", "src/a.fs")>]
[<InlineData("./src/a.fs", "src/a.fs")>]
[<InlineData("src/a.fs", "src/a.fs")>]
[<InlineData("C:\\work\\p\\src\\a.fs", "src/a.fs")>]
let ``a tool's file name is made relative to the folder`` (file: string, expected: string) =
    let root = if file.StartsWith "C:" then "C:\\work\\p" else "/work/p"

    let found =
        Paths.ofDiagnosticFile root file (fun rel -> rel = expected) (fun _ -> None)

    Assert.Equal(Some expected, found)

[<Fact>]
let ``a file that is not at the named path is found by its name`` () =
    let found =
        Paths.ofDiagnosticFile "/work/p" "build/obj/a.fs" (fun _ -> false) (fun name ->
            if name = "a.fs" then Some "src/a.fs" else None)

    Assert.Equal(Some "src/a.fs", found)

    let missing =
        Paths.ofDiagnosticFile "/work/p" "x/none.fs" (fun _ -> false) (fun _ -> None)

    Assert.Equal(None, missing)

// ---- text -------------------------------------------------------------------------------------------

[<Theory>]
[<InlineData("let foo_bar = 1", 6, "foo_bar")>]
[<InlineData("let foo_bar = 1", 4, "foo_bar")>]
[<InlineData("let foo_bar = 1", 11, "foo_bar")>]
[<InlineData("let foo_bar = 1", 12, "")>]
[<InlineData("", 0, "")>]
[<InlineData("abc", 99, "abc")>]
let ``wordAt finds the identifier at or touching the offset`` (text: string, offset: int, expected: string) =
    Assert.Equal(expected, TextAt.wordAt text offset)

[<Fact>]
let ``a completion replaces the word before the caret`` () =
    let text = "let value = foo.ba"
    Assert.Equal(16, TextAt.completionStart (fun i -> text[i]) text.Length)
    Assert.Equal(4, TextAt.completionStart (fun i -> text[i]) 4)
    Assert.Equal(0, TextAt.completionStart (fun i -> text[i]) 0)

// ---- brackets ---------------------------------------------------------------------------------------

[<Fact>]
let ``an opening bracket gets its closer`` () =
    Assert.Equal(Some ')', Brackets.closingFor "(")
    Assert.Equal(Some ']', Brackets.closingFor "[")
    Assert.Equal(Some '}', Brackets.closingFor "{")
    Assert.Equal(None, Brackets.closingFor "a")
    Assert.Equal(None, Brackets.closingFor "((")
    Assert.Equal(None, Brackets.closingFor "")

[<Fact>]
let ``the closer is added only before the end, white space or another closer`` () =
    Assert.True(Brackets.shouldInsert None)
    Assert.True(Brackets.shouldInsert (Some ' '))
    Assert.True(Brackets.shouldInsert (Some '\n'))
    Assert.True(Brackets.shouldInsert (Some ')'))
    Assert.False(Brackets.shouldInsert (Some 'x'))
    Assert.False(Brackets.shouldInsert (Some '('))

[<Fact>]
let ``typing a closer before the same closer steps over it`` () =
    Assert.True(Brackets.shouldStepOver ")" (Some ')'))
    Assert.True(Brackets.shouldStepOver "}" (Some '}'))
    Assert.False(Brackets.shouldStepOver ")" (Some ']'))
    Assert.False(Brackets.shouldStepOver ")" None)
    Assert.False(Brackets.shouldStepOver "a" (Some 'a'))

// ---- disk changes -----------------------------------------------------------------------------------

[<Fact>]
let ``an unmodified file takes the disk's new text`` () =
    Assert.Equal(Reload, DiskSync.decide "new" false "old" "old" None)

[<Fact>]
let ``an unmodified file whose text is already the disk's is left alone`` () =
    Assert.Equal(Leave, DiskSync.decide "same" false "same" "same" None)

[<Fact>]
let ``a file with unsaved edits asks when the disk text is new`` () =
    Assert.Equal(Ask, DiskSync.decide "theirs" true "mine" "base" None)

[<Fact>]
let ``the question is not asked twice for the same disk text`` () =
    Assert.Equal(Leave, DiskSync.decide "theirs" true "mine" "base" (Some "theirs"))
    Assert.Equal(Ask, DiskSync.decide "theirs 2" true "mine" "base" (Some "theirs"))

[<Fact>]
let ``the IDE's own save is not a conflict`` () =
    // after a save the file on disk is what the IDE last wrote, so a watcher event for it asks nothing
    Assert.Equal(Leave, DiskSync.decide "base" true "mine" "base" None)

// ---- status text ------------------------------------------------------------------------------------

[<Fact>]
let ``the git item shows the branch and the number of changes`` () =
    Assert.Equal("⎇ main", Status.git (Some "main") 0)
    Assert.Equal("⎇ main  ● 3", Status.git (Some "main") 3)
    Assert.Equal("", Status.git None 5)

[<Fact>]
let ``position and breadcrumb read like the website's`` () =
    Assert.Equal("Ln 4, Col 9", Status.position 4 9)
    Assert.Equal("src  ›  app.fs  ›  main", Status.breadcrumb "src/app.fs" [ "main" ])
    Assert.Equal("a.fs", Status.breadcrumb "a.fs" [])

[<Fact>]
let ``a tab says when it has unsaved edits or is gone from disk`` () =
    Assert.Equal("a.fs", Status.tabTitle "src/a.fs" false false)
    Assert.Equal("a.fs ●", Status.tabTitle "src/a.fs" true false)
    Assert.Equal("a.fs (deleted on disk)", Status.tabTitle "src/a.fs" false true)
    Assert.Equal("a.fs ● (deleted on disk)", Status.tabTitle "src/a.fs" true true)

[<Fact>]
let ``a screen reader hears the state of a tab in words`` () =
    Assert.Equal("a.fs", Status.tabName "src/a.fs" false false)
    Assert.Equal("a.fs, unsaved changes", Status.tabName "src/a.fs" true false)
    Assert.Equal("a.fs, deleted on disk", Status.tabName "src/a.fs" false true)

let private diag (sev: Severity) : StructuredDiagnostic = StructuredDiagnostic.create "X" sev "m"

[<Fact>]
let ``problems are counted by severity`` () =
    let list =
        [ diag Severity.Error
          diag Severity.Warning
          diag Severity.Error
          diag Severity.Info ]

    Assert.Equal((2, 1), Status.problemCounts list)
    Assert.Equal("✖ 2   ⚠ 1", Status.problems list)
    Assert.Equal("✖ 0   ⚠ 0", Status.problems [])

[<Fact>]
let ``a language server's severity numbers map to ours`` () =
    Assert.Equal(Severity.Error, Status.severityOf 1)
    Assert.Equal(Severity.Warning, Status.severityOf 2)
    Assert.Equal(Severity.Info, Status.severityOf 3)
    Assert.Equal(Severity.Info, Status.severityOf 4)
    Assert.Equal(Severity.Info, Status.severityOf 0)

// ---- keys -------------------------------------------------------------------------------------------

let private ctrl = KeyModifiers.Control
let private shift = KeyModifiers.Shift
let private alt = KeyModifiers.Alt

let private mods (spec: string) =
    (if spec.Contains 'C' then ctrl else KeyModifiers.None)
    ||| (if spec.Contains 'S' then shift else KeyModifiers.None)
    ||| (if spec.Contains 'A' then alt else KeyModifiers.None)

[<Theory>]
[<InlineData(Key.P, "CS", "palette")>]
[<InlineData(Key.P, "C", "quick-open")>]
[<InlineData(Key.G, "C", "goto-line")>]
[<InlineData(Key.O, "CS", "goto-symbol")>]
[<InlineData(Key.O, "C", "open-folder")>]
[<InlineData(Key.S, "C", "save")>]
[<InlineData(Key.S, "CS", "save-all")>]
[<InlineData(Key.N, "C", "new-file")>]
[<InlineData(Key.W, "C", "close-tab")>]
[<InlineData(Key.B, "C", "toggle-sidebar")>]
[<InlineData(Key.E, "CS", "explorer")>]
[<InlineData(Key.F, "CS", "find-files")>]
[<InlineData(Key.H, "C", "replace-in-file")>]
[<InlineData(Key.H, "CS", "replace-in-files")>]
[<InlineData(Key.M, "CS", "problems")>]
[<InlineData(Key.Space, "C", "complete")>]
[<InlineData(Key.OemTilde, "C", "terminal")>]
[<InlineData(Key.J, "C", "toggle-panel")>]
[<InlineData(Key.F5, "", "debug-start")>]
[<InlineData(Key.F5, "S", "debug-stop")>]
[<InlineData(Key.F5, "C", "run")>]
[<InlineData(Key.F9, "", "toggle-breakpoint")>]
[<InlineData(Key.F10, "", "debug-step-over")>]
[<InlineData(Key.F11, "", "debug-step-into")>]
[<InlineData(Key.F11, "S", "debug-step-out")>]
[<InlineData(Key.F12, "", "definition")>]
[<InlineData(Key.OemPlus, "C", "zoom-in")>]
[<InlineData(Key.OemMinus, "C", "zoom-out")>]
let ``shortcuts map to command ids`` (key: Key, spec: string, expected: string) =
    Assert.Equal(Some expected, Keymap.commandFor key (mods spec) false)

[<Fact>]
let ``shift alt F formats, alt Z wraps`` () =
    Assert.Equal(Some "format", Keymap.commandFor Key.F (shift ||| alt) false)
    Assert.Equal(Some "wrap", Keymap.commandFor Key.Z alt false)

[<Fact>]
let ``F2 renames only while the editor has the focus`` () =
    Assert.Equal(Some "rename-symbol", Keymap.commandFor Key.F2 KeyModifiers.None true)
    Assert.Equal(None, Keymap.commandFor Key.F2 KeyModifiers.None false)

[<Fact>]
let ``plain letters and unknown chords run nothing`` () =
    Assert.Equal(None, Keymap.commandFor Key.P KeyModifiers.None false)
    Assert.Equal(None, Keymap.commandFor Key.Q ctrl false)
    Assert.Equal(None, Keymap.commandFor Key.Escape KeyModifiers.None false)
