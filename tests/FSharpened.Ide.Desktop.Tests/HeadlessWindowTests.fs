/// The real window, on a temporary folder, without a display (Avalonia's headless platform). What a person would do
/// with the mouse and the keyboard is done by raising the same events, and what they would see is read back from the
/// controls. See `HeadlessHarness.fs`.
module HeadlessWindowTests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Media
open Avalonia.Styling
open Xunit
open FSharpened.Ide.Desktop
open HeadlessHarness

let private editorOf (s: Session) =
    find<AvaloniaEdit.TextEditor> s.Window "code-editor"

let private colourOf (b: IBrush) = (b :?> ISolidColorBrush).Color

let private click (b: Button) =
    b.RaiseEvent(RoutedEventArgs(Button.ClickEvent))

/// The dialogs the window opens while the body runs (they are shown modally, so the body must not wait for them).
let private watchDialogs () =
    let seen = List<Window>()
    let sub = Dialogs.shown.Subscribe(fun w -> seen.Add w)
    seen, sub

let private tabTitles (s: Session) =
    let strip = find<StackPanel> s.Window "tabs"

    [ for c in strip.Children do
          match c with
          | :? Border as b ->
              match b.Child with
              | :? StackPanel as row -> yield (row.Children[0] :?> TextBlock).Text
              | _ -> ()
          | _ -> () ]

// ---- folder and files -----------------------------------------------------------------------------

[<Fact>]
let ``opening a folder fills the tree with its folders first and then its files`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        let tree = find<TreeView> s.Window "folder-tree"
        let names = topLevelNames tree
        Assert.Equal("src", names.Head)
        Assert.Equal<string list>([ "README.md"; "notes.txt" ], names.Tail |> List.sort)
        // the folder's name is in the status bar and the welcome screen goes away only when a file opens
        Assert.Contains(Path.GetFileName s.Workspace.Root, (find<TextBlock> s.Window "folder-name").Text))

[<Fact>]
let ``a folder in the tree opens to show its files`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        let tree = find<TreeView> s.Window "folder-tree"

        let src =
            tree.Items |> Seq.cast<TreeViewItem> |> Seq.find (fun i -> rowText i = "src")

        src.IsExpanded <- true
        pump 100
        let names = [ for i in src.Items -> rowText i ] |> List.sort
        Assert.Equal<string list>([ "app.fs"; "util.go" ], names))

[<Fact>]
let ``opening a file shows its text in the editor and a tab for it`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)
        pump 50
        let editor = editorOf s
        Assert.True editor.IsVisible
        Assert.Equal(s.Workspace.Read "src/app.fs", editor.Document.Text)
        Assert.Equal<string list>([ "app.fs" ], tabTitles s)
        Assert.Equal("Ln 1, Col 1", (find<TextBlock> s.Window "cursor-position").Text)
        Assert.Equal("F#", (find<TextBlock> s.Window "language-of-the-open-file").Text)
        Assert.Contains("app.fs", (find<TextBlock> s.Window "breadcrumb").Text))

[<Fact>]
let ``a second file gets its own tab and switching keeps each one's text`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)
        s.Shell.OpenFile("notes.txt", 0, 0)
        pump 50
        Assert.Equal<string list>([ "app.fs"; "notes.txt" ], tabTitles s)
        Assert.Equal(s.Workspace.Read "notes.txt", (editorOf s).Document.Text)
        s.Shell.Run "previous-tab"
        Assert.Equal(s.Workspace.Read "src/app.fs", (editorOf s).Document.Text)
        s.Shell.Run "next-tab"
        Assert.Equal(s.Workspace.Read "notes.txt", (editorOf s).Document.Text))

[<Fact>]
let ``editing marks the tab and saving writes the file and marks it clean`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("notes.txt", 0, 0)
        let editor = editorOf s
        editor.Document.Insert(0, "NEW ")
        pump 20
        Assert.Equal<string list>([ "notes.txt ●" ], tabTitles s)
        Assert.NotEqual<string>(editor.Document.Text, s.Workspace.Read "notes.txt")
        s.Shell.Run "save"
        pump 20
        Assert.Equal<string list>([ "notes.txt" ], tabTitles s)
        Assert.Equal("NEW hello world\nanswer hello\n", s.Workspace.Read "notes.txt"))

// ---- palette and quick open ---------------------------------------------------------------------

[<Fact>]
let ``the command palette opens, lists commands and narrows as you type`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        let palette = find<Border> s.Window "command-palette"
        let input = find<TextBox> s.Window "command-palette-input"
        let results = find<ListBox> s.Window "command-palette-results"
        Assert.False palette.IsVisible
        press s.Window Key.P (KeyModifiers.Control ||| KeyModifiers.Shift)
        Assert.True palette.IsVisible
        Assert.StartsWith(">", input.Text)
        let all = results.ItemCount
        Assert.True(all > 10, sprintf "the palette lists the commands (%d rows)" all)
        input.Text <- ">save all"
        pump 20
        Assert.True(results.ItemCount < all)
        let first = AutomationProperties.GetName(results.Items[0] :?> Control)
        Assert.Contains("Save All", first)
        press input Key.Escape KeyModifiers.None
        Assert.False palette.IsVisible)

[<Fact>]
let ``choosing a palette row runs its command`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.Palette ">"
        let input = find<TextBox> s.Window "command-palette-input"
        let results = find<ListBox> s.Window "command-palette-results"
        input.Text <- ">toggle word wrap"
        pump 20
        let editor = editorOf s
        let before = editor.WordWrap
        press input Key.Enter KeyModifiers.None
        Assert.NotEqual(before, editor.WordWrap)
        Assert.False((find<Border> s.Window "command-palette").IsVisible)
        ignore results)

[<Fact>]
let ``quick open ranks files by what is typed and opens the chosen one`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        press s.Window Key.P KeyModifiers.Control
        let input = find<TextBox> s.Window "command-palette-input"
        let results = find<ListBox> s.Window "command-palette-results"
        // the list of files is read in the background
        until "quick open to list the files" 5000 (fun () ->
            input.Text <- ""
            input.Text <- " "
            input.Text <- ""
            results.ItemCount >= 4)

        input.Text <- "util"
        pump 20
        let first = AutomationProperties.GetName(results.Items[0] :?> Control)
        Assert.Equal("src/util.go", first)
        press input Key.Enter KeyModifiers.None
        pump 50
        Assert.Contains(s.Workspace.Read "src/util.go", (editorOf s).Document.Text)
        Assert.Equal<string list>([ "util.go" ], tabTitles s))

// ---- find and replace ---------------------------------------------------------------------------

[<Fact>]
let ``find and replace in the open file changes every match and can be undone`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("notes.txt", 0, 0)
        let editor = editorOf s
        s.Shell.Run "replace-in-file"
        let panel = s.Shell.SearchPanel
        Assert.True panel.IsReplaceMode
        panel.SearchPattern <- "hello"
        panel.ReplacePattern <- "bye"
        panel.ReplaceAll()
        Assert.Equal("bye world\nanswer bye\n", editor.Document.Text)
        Assert.Equal<string list>([ "notes.txt ●" ], tabTitles s)
        editor.Document.UndoStack.Undo()
        Assert.Equal("hello world\nanswer hello\n", editor.Document.Text))

// ---- search in files ----------------------------------------------------------------------------

[<Fact>]
let ``the search panel lists matches by file and opens one`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.Search("hello", "")
        let results = find<ListBox> s.Window "search-results"
        until "search results" 5000 (fun () -> results.ItemCount > 0)
        let rows = [ for i in results.Items -> (i :?> TextBlock).Text ]
        // a heading per file, then a row per matching line (the search ignores case: Hello and hello both match)
        for file in [ "src/app.fs"; "src/util.go"; "notes.txt" ] do
            Assert.Contains(file, rows)

        Assert.Equal(
            5,
            rows
            |> List.filter (fun r -> r.ToLowerInvariant().Contains "hello")
            |> List.length
        )

        let summary = (find<TextBlock> s.Window "search-summary").Text
        Assert.Contains("5 results in 3 files", summary)

        let hit =
            results.Items
            |> Seq.cast<TextBlock>
            |> Seq.find (fun t -> t.Tag :? (string * int * int))

        results.SelectedItem <- hit
        pump 50
        Assert.Equal(1, tabTitles s |> List.length))

[<Fact>]
let ``search can be limited by the include and exclude patterns`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.Search("hello", "")
        let results = find<ListBox> s.Window "search-results"
        until "search results" 5000 (fun () -> results.ItemCount > 0)
        let include' = find<TextBox> s.Window "files-to-include"
        include'.Text <- "*.txt"
        press (find<TextBox> s.Window "find-in-files") Key.Enter KeyModifiers.None

        until "narrowed results" 5000 (fun () ->
            (find<TextBlock> s.Window "search-summary").Text.Contains "2 results in 1 file"))

// ---- theme, panels, status ------------------------------------------------------------------------

[<Fact>]
let ``switching the theme changes the window, the editor and the system variant`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)
        let dark = Palette.find "dark"
        let light = Palette.find "light"
        s.Shell.SetTheme "light"
        pump 50
        Assert.Equal("light", s.Shell.ThemeName)
        Assert.Equal(colourOf (Palette.ofTheme light).Bg, colourOf s.Window.Background)
        Assert.Equal(colourOf (Palette.ofTheme light).EditorBg, colourOf (editorOf s).Background)
        Assert.Equal(ThemeVariant.Light, Application.Current.RequestedThemeVariant)
        s.Shell.Run("theme-" + dark.Id))

[<Fact>]
let ``the theme chosen is remembered in the settings file`` () =
    withWindow false sampleFiles (fun s ->
        s.Shell.SetTheme "monokai"
        pump 20
        let prefs = s.Workspace.Env.LoadPrefs()
        Assert.Equal("monokai", prefs.Editor.Theme))

[<Fact>]
let ``the bottom panel tabs show one panel at a time`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()

        let contents =
            [ "output", "output-contents"
              "problems", "problems-contents"
              "input", "input-contents"
              "debug", "debug-contents" ]

        for (tab, shown) in contents do
            click (find<Button> s.Window (tab + "-panel"))

            for (_, other) in contents do
                Assert.Equal((other = shown), (find<Control> s.Window other).IsVisible)

            Assert.Equal("selected", AutomationProperties.GetItemStatus(find<Button> s.Window (tab + "-panel")))

        // and Ctrl+J hides and shows the panel
        click (find<Button> s.Window "output-panel")
        press s.Window Key.J KeyModifiers.Control
        s.Window.UpdateLayout()
        pump 100
        Assert.Equal(0.0, (find<Control> s.Window "output-contents").Bounds.Height)
        press s.Window Key.J KeyModifiers.Control
        s.Window.UpdateLayout()
        pump 100
        Assert.True((find<Control> s.Window "output-contents").Bounds.Height > 0.0))

// ---- the terminal and the trust question -----------------------------------------------------

[<Fact>]
let ``the terminal tab asks to trust an untrusted folder before it starts a shell`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        let dialogs, sub = watchDialogs ()
        use _ = sub
        click (find<Button> s.Window "terminal-panel")
        until "the trust question" 3000 (fun () -> dialogs.Count > 0)
        let question = dialogs[0]
        Assert.Equal("Trust this folder?", question.Title)
        Assert.True((find<Control> s.Window "terminal-contents").IsVisible)
        // nothing was written to the trust list yet and no shell is running
        Assert.False(s.Workspace.Env.Trust.IsTrusted s.Workspace.Root)
        let cancel = find<Button> question "cancel"
        click cancel
        pump 100
        Assert.False(s.Workspace.Env.Trust.IsTrusted s.Workspace.Root))

[<Fact>]
let ``a trusted folder starts the terminal without a question`` () =
    withWindow true sampleFiles (fun s ->
        s.OpenFolder()
        let dialogs, sub = watchDialogs ()
        use _ = sub
        s.Shell.Run "terminal-clear"
        pump 50
        Assert.Empty dialogs)

let private fshTestPack (ws: Workspace) =
    // a pack for a made-up extension whose run plan uses the dotnet host, which is on every machine that runs these
    // tests; a pack may only name a bare program, so the folder it lives in is put on this process's PATH
    let dotnetDir =
        Path.GetFullPath(
            Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")
        )

    let sep = string Path.PathSeparator

    let path =
        Environment.GetEnvironmentVariable "PATH"
        |> Option.ofObj
        |> Option.defaultValue ""

    if not (path.Split(Path.PathSeparator) |> Array.contains dotnetDir) then
        Environment.SetEnvironmentVariable("PATH", dotnetDir + sep + path)

    let json =
        JsonSerializer.Serialize(
            {| format = "fsharpened.language/1"
               id = "fshtest"
               name = "FSH test"
               extensions = [| ".fshtest" |]
               scope = ""
               tools =
                [| {| id = "dotnet"
                      label = ".NET"
                      candidates = [| [| "dotnet" |] |]
                      version = [| "--version" |]
                      install = "install .NET" |} |]
               run =
                [| {| steps =
                       [| {| phase = "run"
                             tool = "dotnet"
                             args = [| "--version" |] |} |] |} |] |}
        )

    Directory.CreateDirectory ws.Env.UserPacks |> ignore
    File.WriteAllText(Path.Combine(ws.Env.UserPacks, "fshtest.json"), json)
    ws.Env.ReloadLanguages()

[<Fact>]
let ``running a file in an untrusted folder asks first and runs nothing when refused`` () =
    onUi (fun () ->
        use ws = new Workspace(sampleFiles @ [ "x.fshtest", "hi" ], false)
        fshTestPack ws
        use s = new Session(ws)
        s.OpenFolder()
        s.Shell.OpenFile("x.fshtest", 0, 0)
        let dialogs, sub = watchDialogs ()
        use _ = sub
        s.Shell.Run "run"
        until "the trust question" 8000 (fun () -> dialogs.Count > 0)
        Assert.Equal("Trust this folder?", dialogs[0].Title)
        Assert.False(ws.Env.Trust.IsTrusted ws.Root)
        // refusing: nothing ran, so the output stays empty
        click (find<Button> dialogs[0] "cancel")
        pump 200
        Assert.Equal("", (find<TextBox> s.Window "output").Text |> Option.ofObj |> Option.defaultValue "")
        Assert.False(ws.Env.Trust.IsTrusted ws.Root))

[<Fact>]
let ``agreeing to the question trusts the folder and runs the file`` () =
    onUi (fun () ->
        use ws = new Workspace(sampleFiles @ [ "x.fshtest", "hi" ], false)
        fshTestPack ws
        use s = new Session(ws)
        s.OpenFolder()
        s.Shell.OpenFile("x.fshtest", 0, 0)
        let dialogs, sub = watchDialogs ()
        use _ = sub
        s.Shell.Run "run"
        until "the trust question" 8000 (fun () -> dialogs.Count > 0)
        click (find<Button> dialogs[0] "trust-and-run")
        until "the folder to be trusted" 3000 (fun () -> ws.Env.Trust.IsTrusted ws.Root)
        // `dotnet x.fshtest`... the run plan passes --version, so the output holds a version number
        until "the program's output" 20000 (fun () ->
            let t =
                (find<TextBox> s.Window "output").Text |> Option.ofObj |> Option.defaultValue ""

            t.Contains "exit" || t.Length > 0))

// ---- keys ----------------------------------------------------------------------------------------

[<Fact>]
let ``every command id the key map can name is a command of the window`` () =
    withWindow false sampleFiles (fun s ->
        let known = s.Shell.CommandIds

        let ids =
            [ for key in Enum.GetValues<Key>() do
                  for mods in
                      [ KeyModifiers.None
                        KeyModifiers.Control
                        KeyModifiers.Control ||| KeyModifiers.Shift
                        KeyModifiers.Shift
                        KeyModifiers.Alt
                        KeyModifiers.Shift ||| KeyModifiers.Alt ] do
                      match Keymap.commandFor key mods true with
                      | Some id -> yield id
                      | None -> () ]
            |> List.distinct

        Assert.True(ids.Length > 25)

        for id in ids do
            Assert.True(known |> List.contains id, sprintf "the key map names '%s', which is not a command" id))

[<Fact>]
let ``Ctrl+Shift+F and Ctrl+Shift+E switch the sidebar between search and the explorer`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()

        let title () =
            (find<TextBlock> s.Window "sidebar-title").Text

        Assert.Equal("EXPLORER", title ())
        press s.Window Key.F (KeyModifiers.Control ||| KeyModifiers.Shift)
        Assert.Equal("SEARCH", title ())
        press s.Window Key.E (KeyModifiers.Control ||| KeyModifiers.Shift)
        Assert.Equal("EXPLORER", title ()))
