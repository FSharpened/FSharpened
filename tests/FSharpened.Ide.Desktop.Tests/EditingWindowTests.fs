/// Several cursors and snippets in the real window (headless): adding cursors with the commands and the keys, typing and
/// deleting at all of them as one undo step, snippets by Tab and from the list, with linked fields.
module EditingWindowTests

open System.IO
open Avalonia.Controls
open Avalonia.Input
open Xunit
open FSharpened.Ide.Desktop
open FSharpened.Ide.Host
open HeadlessHarness

let private editorOf (s: Session) =
    find<AvaloniaEdit.TextEditor> s.Window "code-editor"

let private caretAreaOf (s: Session) = (editorOf s).TextArea :> InputElement

let private ctrl = KeyModifiers.Control

let private open1 (s: Session) (rel: string) =
    s.OpenFolder()
    s.Shell.OpenFile(rel, 1, 1)
    pump 50

// ---- several cursors -------------------------------------------------------------------------------

[<Fact>]
let ``cursors added below are typed at together, drawn in the status bar and undone in one step`` () =
    withWindow true [ "a.txt", "one\ntwo\nthree\n" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(1, 1)
        s.Shell.Run "cursor-below"
        s.Shell.Run "cursor-below"
        Assert.Equal(3, s.Shell.Editing.Cursors.Count)
        Assert.Equal("3 cursors", (find<TextBlock> s.Window "number-of-cursors").Text)

        typeText (caretAreaOf s) "X"
        Assert.Equal("Xone\nXtwo\nXthree\n", (editorOf s).Text)
        Assert.Equal(3, s.Shell.Editing.Cursors.Count)

        // the primary is the last one added; it stands after its X
        Assert.Equal(3, (editorOf s).TextArea.Caret.Line)
        Assert.Equal(2, (editorOf s).TextArea.Caret.Column)

        // one undo takes back the typing at every cursor
        (editorOf s).Document.UndoStack.Undo()
        Assert.Equal("one\ntwo\nthree\n", (editorOf s).Text))

[<Fact>]
let ``backspace, delete and enter act at every cursor`` () =
    withWindow true [ "a.txt", "  ab\n  cd\n" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(1, 5)
        s.Shell.Run "cursor-below"
        press (caretAreaOf s) Key.Back KeyModifiers.None
        Assert.Equal("  a\n  c\n", (editorOf s).Text)
        press (caretAreaOf s) Key.Enter KeyModifiers.None
        Assert.Equal("  a\n  \n  c\n  \n", (editorOf s).Text)
        Assert.Equal(2, s.Shell.Editing.Cursors.Count))

[<Fact>]
let ``select next match with Ctrl+D takes the word, then each following one, and typing replaces them all`` () =
    withWindow true [ "a.txt", "foo bar foo\nfoo baz\n" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(1, 2)
        press (caretAreaOf s) Key.D ctrl
        Assert.Equal("foo", (editorOf s).TextArea.Selection.GetText())
        Assert.Equal(1, s.Shell.Editing.Cursors.Count)
        press (caretAreaOf s) Key.D ctrl
        press (caretAreaOf s) Key.D ctrl
        Assert.Equal(3, s.Shell.Editing.Cursors.Count)
        typeText (caretAreaOf s) "qux"
        Assert.Equal("qux bar qux\nqux baz\n", (editorOf s).Text))

[<Fact>]
let ``select all matches, and Escape goes back to one cursor`` () =
    withWindow true [ "a.txt", "x = x + x\n" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(1, 1)
        s.Shell.Run "select-all-matches"
        Assert.Equal(3, s.Shell.Editing.Cursors.Count)
        press (caretAreaOf s) Key.Escape KeyModifiers.None
        Assert.Equal(1, s.Shell.Editing.Cursors.Count)
        Assert.Equal("", (find<TextBlock> s.Window "number-of-cursors").Text)
        // typing now goes to the one cursor only
        typeText (caretAreaOf s) "y"
        Assert.Equal(1, (editorOf s).Text |> Seq.filter ((=) 'y') |> Seq.length))

[<Fact>]
let ``the other cursors follow text that changes some other way`` () =
    withWindow true [ "a.txt", "aa\nbb\ncc\n" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(2, 1)
        s.Shell.Run "cursor-below"
        // text inserted before them moves them along
        (editorOf s).Document.Insert(0, "0123\n")
        let lines = s.Shell.Editing.Cursors.Extras |> List.map (fun c -> c.Caret)
        Assert.Equal<int list>([ 5 + 3 ], lines))

[<Fact>]
let ``another file's tab starts with one cursor`` () =
    withWindow true [ "a.txt", "a\na\n"; "b.txt", "b\n" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(1, 1)
        s.Shell.Run "cursor-below"
        Assert.Equal(2, s.Shell.Editing.Cursors.Count)
        s.Shell.OpenFile("b.txt", 1, 1)
        pump 50
        Assert.Equal(1, s.Shell.Editing.Cursors.Count))

// ---- snippets ---------------------------------------------------------------------------------------

let private mine =
    """{ "Pair": { "prefix": "pair", "body": "(${1:a}, ${1:a})$0", "description": "a pair" },
         "Block": { "prefix": "blk", "body": ["if ${1:cond} {", "\t$0", "}"], "description": "a block" } }"""

let private withSnippets (files: (string * string) list) (body: Session -> unit) =
    withPreparedWindow
        true
        files
        (fun ws ->
            Directory.CreateDirectory ws.Env.UserSnippets |> ignore
            File.WriteAllText(Path.Combine(ws.Env.UserSnippets, "mine.code-snippets"), mine))
        body

[<Fact>]
let ``Tab after a snippet's name expands it, selects the first field, and linked copies follow what is typed`` () =
    withSnippets [ "a.txt", "pair" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(1, 5)
        press (caretAreaOf s) Key.Tab KeyModifiers.None
        Assert.Equal("(a, a)", (editorOf s).Text)
        Assert.True s.Shell.Editing.Snippets.InSession
        Assert.Equal("a", (editorOf s).TextArea.Selection.GetText())

        typeText (caretAreaOf s) "z"
        until "the copy to follow" 3000 (fun () -> (editorOf s).Text = "(z, z)")

        // Tab goes to the end of the snippet and finishes it
        press (caretAreaOf s) Key.Tab KeyModifiers.None
        Assert.False s.Shell.Editing.Snippets.InSession
        Assert.Equal(6, (editorOf s).TextArea.Caret.Offset))

[<Fact>]
let ``a snippet takes the indentation of its line and the editor's indent, and Escape stops filling it in`` () =
    withSnippets [ "a.txt", "    blk" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(1, 8)
        press (caretAreaOf s) Key.Tab KeyModifiers.None
        Assert.Equal("    if cond {\n        \n    }", (editorOf s).Text)
        Assert.Equal("cond", (editorOf s).TextArea.Selection.GetText())
        Assert.Equal(Some 1, s.Shell.Editing.Snippets.CurrentField)
        press (caretAreaOf s) Key.Escape KeyModifiers.None
        Assert.False s.Shell.Editing.Snippets.InSession)

[<Fact>]
let ``a snippet is one undo step`` () =
    withSnippets [ "a.txt", "pair" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(1, 5)
        press (caretAreaOf s) Key.Tab KeyModifiers.None
        (editorOf s).Document.UndoStack.Undo()
        Assert.Equal("pair", (editorOf s).Text))

[<Fact>]
let ``a word that is no snippet's name leaves Tab alone`` () =
    withSnippets [ "a.txt", "nothing" ] (fun s ->
        open1 s "a.txt"
        s.Shell.SetCaret(1, 8)
        press (caretAreaOf s) Key.Tab KeyModifiers.None
        Assert.False s.Shell.Editing.Snippets.InSession
        Assert.Equal("nothing", (editorOf s).Text))

[<Fact>]
let ``Insert Snippet lists the snippets of the file's language and your own, and inserts the chosen one`` () =
    withSnippets [ "a.txt", "" ] (fun s ->
        open1 s "a.txt"
        s.Shell.Palette "$"
        let list = find<ListBox> s.Window "command-palette-results"
        until "the snippets" 3000 (fun () -> list.ItemCount >= 2)
        s.Shell.Palette "$pai"
        Assert.Equal(1, list.ItemCount)
        press (find<TextBox> s.Window "command-palette-input") Key.Enter KeyModifiers.None
        Assert.Equal("(a, a)", (editorOf s).Text))

[<Fact>]
let ``snippet files with mistakes are reported and the good ones still work`` () =
    withPreparedWindow
        true
        [ "a.txt", "pair" ]
        (fun ws ->
            Directory.CreateDirectory ws.Env.UserSnippets |> ignore
            File.WriteAllText(Path.Combine(ws.Env.UserSnippets, "mine.code-snippets"), mine)
            File.WriteAllText(Path.Combine(ws.Env.UserSnippets, "broken.json"), "{ nope"))
        (fun s ->
            Assert.Single s.Shell.Editing.Snippets.Problems |> ignore
            Assert.Contains("broken.json", s.Shell.Editing.Snippets.Problems[0])
            Assert.Contains("pair", s.Shell.Editing.Snippets.Available() |> List.map (fun x -> x.Label)))
