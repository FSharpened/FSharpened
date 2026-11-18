/// The language-server features in the real window (headless), against the scripted server of the host tests
/// (`fake_lsp.py`, which needs Python: without it these tests do nothing). A pack named "fake" makes `.fk` files use it.
module LspWindowTests

open System
open System.IO
open System.Text.Json
open Avalonia.Controls
open Avalonia.Input
open Xunit
open FSharpened.Ide.Desktop
open FSharpened.Ide.Host
open HeadlessHarness

let private python =
    lazy
        ([ "python"; "python3" ]
         |> List.tryFind (fun p ->
             match Proc.run [ p; "-c"; "print(1)" ] (Path.GetTempPath()) None (TimeSpan.FromSeconds 10.0) with
             | Ok r -> r.ExitCode = Some 0
             | _ -> false))

let private fakePack (ws: Workspace) (py: string) (extra: string list) =
    let json =
        JsonSerializer.Serialize(
            {| format = "fsharpened.language/1"
               id = "fake"
               name = "Fake"
               extensions = [| ".fk" |]
               scope = ""
               server =
                {| command = py
                   args = Path.Combine(AppContext.BaseDirectory, "fake_lsp.py") :: extra |> List.toArray
                   install = "python" |} |}
        )

    Directory.CreateDirectory ws.Env.UserPacks |> ignore
    File.WriteAllText(Path.Combine(ws.Env.UserPacks, "fake.json"), json)
    ws.Env.ReloadLanguages()

/// A window on a trusted folder with `.fk` files served by the scripted server; does nothing without Python.
let private withFake (extra: string list) (files: (string * string) list) (body: Session -> unit) =
    match python.Value with
    | None -> ()
    | Some py ->
        onUi (fun () ->
            use ws = new Workspace(files, true)
            fakePack ws py extra
            use s = new Session(ws)
            s.OpenFolder()
            body s)

let private text (s: Session) =
    (find<AvaloniaEdit.TextEditor> s.Window "code-editor").Text

let private status (s: Session) =
    (find<TextBlock> s.Window "status-message").Text

/// Opens a file and waits until the server has been told about it and is ready.
let private openFk (s: Session) (rel: string) =
    s.Shell.OpenFile(rel, 1, 1)
    until "the language server to be ready" 15000 (fun () -> (status s).Contains "is ready")
    pump 100

/// The text of the signature box: its pieces joined.
let private signatureLabel (box: Border) =
    String.Join(
        "",
        [ for i in (box.Child :?> TextBlock).Inlines ->
              match i with
              | :? Avalonia.Controls.Documents.Run as r -> r.Text
              | _ -> " " ]
    )

/// Waits for a task that finishes on the UI thread: the thread is kept pumping, never blocked.
let private finish (t: System.Threading.Tasks.Task) =
    until "the action to finish" 10000 (fun () -> t.IsCompleted)

let private code =
    "class Shape\n    fn area(w, h)\nfn helper(x, y)\nhelper(1, 2)   \nhelper(3, 4)\n"

[<Fact>]
let ``the server's outline shows in the breadcrumb where no pattern outline exists`` () =
    withFake [] [ "a.fk", code ] (fun s ->
        openFk s "a.fk"
        s.Shell.SetCaret(2, 10)

        until "the breadcrumb to name the symbols" 10000 (fun () ->
            (find<TextBlock> s.Window "breadcrumb").Text.Contains "Shape  ›  area"))

[<Fact>]
let ``Shift+F12 lists every use in the References panel and a click opens the place`` () =
    withFake [] [ "a.fk", code; "b.fk", "helper(5, 6)\n" ] (fun s ->
        openFk s "a.fk"
        s.Shell.SetCaret(4, 2) // on "helper"
        let editor = find<AvaloniaEdit.TextEditor> s.Window "code-editor"
        press editor Key.F12 KeyModifiers.Shift
        let list = find<ListBox> s.Window "references"
        until "the references" 10000 (fun () -> list.ItemCount = 4)
        Assert.Contains("4 references in 2 files", status s)
        // the panel tab is showing
        Assert.True list.IsEffectivelyVisible
        let rows = [ for i in list.Items -> (i :?> TextBlock).Text ]
        Assert.Contains(rows, fun r -> r.StartsWith "b.fk:1:1")
        Assert.Contains(rows, fun r -> r.StartsWith "a.fk:5:1")
        // choosing the row in b.fk opens that file at the place
        list.SelectedIndex <- rows |> List.findIndex (fun r -> r.StartsWith "b.fk")
        pump 100
        Assert.Equal(2, (find<StackPanel> s.Window "tabs").Children.Count)
        Assert.Equal("Ln 1, Col 1", (find<TextBlock> s.Window "cursor-position").Text))

[<Fact>]
let ``typing an opening bracket shows the signature with the current parameter, and Escape closes it`` () =
    withFake [] [ "a.fk", code ] (fun s ->
        openFk s "a.fk"
        let editor = find<AvaloniaEdit.TextEditor> s.Window "code-editor"
        let box = find<Border> s.Window "signature-help"
        // a new line at the end: "helper(" then "1, "
        editor.Document.Insert(editor.Document.TextLength, "helper")
        editor.CaretOffset <- editor.Document.TextLength
        editor.TextArea.PerformTextInput "("
        until "the signature box" 10000 (fun () -> box.IsVisible)
        let label = signatureLabel box
        Assert.StartsWith("helper(x, y)", label)
        Assert.Contains("the x", label)
        // after a comma the second parameter is active
        editor.TextArea.PerformTextInput "1"
        editor.TextArea.PerformTextInput ","

        until "the second parameter" 10000 (fun () ->
            let inl = (box.Child :?> TextBlock).Inlines
            inl.Count > 1 && (inl[1] :?> Avalonia.Controls.Documents.Run).Text = "y")

        press editor.TextArea Key.Escape KeyModifiers.None
        Assert.False box.IsVisible)

[<Fact>]
let ``a quick fix shows the lightbulb, applies as one undoable edit, and a command-only action is not offered as runnable``
    ()
    =
    withFake [] [ "a.fk", code ] (fun s ->
        openFk s "a.fk"
        s.Shell.SetCaret(4, 3) // "helper(1, 2)   " has trailing spaces
        let bulb = find<Button> s.Window "quick-fix"
        // (the bulb is looked for a moment after the caret moves, by a timer the headless loop does not run)
        s.Shell.RefreshFixes()
        until "the lightbulb" 10000 (fun () -> bulb.IsVisible)
        let offered = s.Shell.OfferedFixes
        Assert.Contains(("Remove trailing whitespace", true), offered)
        Assert.Contains(("Run server command", false), offered)
        // the preferred quick fix comes first
        Assert.Equal("Remove trailing whitespace", fst offered.Head)
        let before = text s
        Assert.Contains("helper(1, 2)   \n", before)
        finish (s.Shell.ApplyFix "Remove trailing whitespace")
        pump 50
        Assert.Contains("helper(1, 2)\n", text s)
        Assert.Contains("Applied", status s)
        // the edit is in the editor's undo history, not written to disk
        Assert.Equal(code, s.Workspace.Read "a.fk")
        let doc = (find<AvaloniaEdit.TextEditor> s.Window "code-editor").Document
        doc.UndoStack.Undo()
        Assert.Equal(before, text s))

[<Fact>]
let ``a fix that the server resolves later is resolved and applied`` () =
    withFake [] [ "a.fk", code ] (fun s ->
        openFk s "a.fk"
        s.Shell.SetCaret(3, 6) // on "helper" in "fn helper(x, y)"
        s.Shell.RefreshFixes()

        until "the refactoring" 10000 (fun () ->
            s.Shell.OfferedFixes |> List.exists (fun (t, _) -> t.StartsWith "Convert"))

        finish (s.Shell.ApplyFix "Convert 'helper' to upper case")
        pump 50
        Assert.Contains("fn HELPER(x, y)", text s))

[<Fact>]
let ``Format Document uses the server when it formats and the pack's formatter when it does not`` () =
    withFake [] [ "a.fk", code ] (fun s ->
        openFk s "a.fk"
        s.Shell.Run "format"
        until "the formatted text" 10000 (fun () -> not ((text s).Contains "   \n"))
        Assert.Contains("helper(1, 2)\n", text s)
        Assert.Contains("by the language server", status s)
        s.Shell.Run "format"
        until "already formatted" 10000 (fun () -> (status s).Contains "Already formatted"))

    // a server that cannot format: the pack has no formatter either, so the IDE says so
    withFake [ "--basic" ] [ "a.fk", code ] (fun s ->
        openFk s "a.fk"
        s.Shell.Run "format"
        until "the fallback" 5000 (fun () -> (status s).Contains "No formatter is set up")
        Assert.Contains("   \n", text s))

[<Fact>]
let ``without a trusted folder no server starts and the features say what is missing`` () =
    match python.Value with
    | None -> ()
    | Some py ->
        onUi (fun () ->
            use ws = new Workspace([ "a.fk", code ], false)
            fakePack ws py []
            use s = new Session(ws)
            s.OpenFolder()
            s.Shell.OpenFile("a.fk", 1, 1)
            pump 300
            s.Shell.Run "references"
            Assert.Contains("needs a language server", status s)
            Assert.Empty s.Shell.OfferedFixes)
