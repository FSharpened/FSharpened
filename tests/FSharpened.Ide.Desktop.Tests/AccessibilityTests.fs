/// What a screen reader and the keyboard get from the real window: every control a person can operate has a name,
/// the regions are marked, and what the mouse can do the keyboard can do. The walk over the visual tree is the
/// regression net: a control added without a name makes `every interactive control ...` fail with its path.
module AccessibilityTests

open System
open System.Collections.Generic
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.VisualTree
open Xunit
open FSharpened.Ide.Desktop
open HeadlessHarness

let private click (b: Button) =
    b.RaiseEvent(RoutedEventArgs(Button.ClickEvent))

/// Every control below the root plus the items of the context menus the controls carry (a menu is not part of the tree
/// until it opens).
let private withMenus (root: Control) : Control list =
    let all = descendants root

    [ yield! all
      for c in all do
          match c.ContextMenu with
          | null -> ()
          | menu ->
              for m in descendants menu do
                  yield m ]

/// A control a person operates: a control of ours (not a part of another control's template) that takes the keyboard
/// focus, or is one of the kinds that always does.
let private isInteractive (c: Control) =
    isNull c.TemplatedParent
    && (match c with
        | :? RepeatButton -> false
        | :? ScrollBar -> false
        | :? Button
        | :? ToggleButton
        | :? TextBox
        | :? ListBox
        | :? TreeView
        | :? ComboBox
        | :? MenuItem
        | :? AvaloniaEdit.TextEditor
        | :? TerminalView
        | :? GridSplitter -> true
        | :? ListBoxItem
        | :? TreeViewItem -> false // named by the row's own text
        | _ -> c.Focusable && c.IsTabStop)

let private describe (c: Control) =
    let nameOf (v: Visual) =
        match v with
        | :? Control as c when not (isNull c.Name) -> "#" + c.Name
        | _ -> ""

    let rec path (v: Visual) =
        if isNull v then
            ""
        else
            path (v.GetVisualParent()) + "/" + v.GetType().Name + nameOf v

    let text =
        match c with
        | :? ContentControl as cc when (cc.Content :? string) -> sprintf " \"%s\"" (cc.Content :?> string)
        | :? TextBox as t when t.PlaceholderText <> null -> sprintf " [placeholder: %s]" t.PlaceholderText
        | :? HeaderedItemsControl as h when (h.Header :? string) -> sprintf " \"%s\"" (h.Header :?> string)
        | :? MenuItem as m when (m.Header :? string) -> sprintf " \"%s\"" (m.Header :?> string)
        | _ -> ""

    path (c :> Visual) + text

/// The interactive controls with no usable accessible name.
let private unnamed (root: Control) : string list =
    withMenus root
    |> List.filter isInteractive
    |> List.filter (fun c -> not (Access.isMeaningful (AutomationProperties.GetName c)))
    |> List.map describe
    |> List.distinct

let private assertAllNamed (what: string) (root: Control) =
    match unnamed root with
    | [] -> ()
    | bad ->
        failwithf
            "%s: %d interactive control(s) without an accessible name:\n  %s"
            what
            bad.Length
            (String.Join("\n  ", bad))

// ---- names ---------------------------------------------------------------------------------------

[<Fact>]
let ``every interactive control of a fresh window has a name`` () =
    withWindow false sampleFiles (fun s ->
        pump 100
        assertAllNamed "fresh window" s.Window
        Assert.True((withMenus s.Window |> List.filter isInteractive |> List.length) > 20))

[<Fact>]
let ``every interactive control has a name with a folder, tabs and the explorer open`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        let tree = find<TreeView> s.Window "folder-tree"

        let src =
            tree.Items |> Seq.cast<TreeViewItem> |> Seq.find (fun i -> rowText i = "src")

        src.IsExpanded <- true
        s.Shell.OpenFile("src/app.fs", 0, 0)
        s.Shell.OpenFile("notes.txt", 0, 0)
        pump 100
        assertAllNamed "folder open" s.Window
        // the tabs are among them
        Assert.True(
            (withMenus s.Window
             |> List.exists (fun c -> AutomationProperties.GetName c = "app.fs"))
        ))

[<Fact>]
let ``every interactive control has a name in each sidebar view and each bottom panel`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)

        for view in [ "find-files"; "outline"; "tools"; "explorer" ] do
            s.Shell.Run view
            pump 150
            assertAllNamed ("sidebar " + view) s.Window

        s.Shell.Search("hello", "")
        pump 300
        assertAllNamed "search results" s.Window

        for panel in [ "output"; "problems"; "input"; "debug" ] do
            click (find<Button> s.Window (panel + "-panel"))
            assertAllNamed ("panel " + panel) s.Window)

[<Fact>]
let ``the command palette and its rows are named`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.Palette ">"
        let results = find<ListBox> s.Window "command-palette-results"
        Assert.True(results.ItemCount > 10)
        assertAllNamed "palette" s.Window

        for row in results.Items |> Seq.cast<Control> |> Seq.truncate 20 do
            Assert.True(Access.isMeaningful (AutomationProperties.GetName row), "a palette row has no name"))

[<Fact>]
let ``the dialogs the window opens are named, with named buttons`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()

        let dialogs, sub =
            (let l = List<Window>() in l, Dialogs.shown.Subscribe(fun w -> l.Add w))

        use _ = sub
        click (find<Button> s.Window "terminal-panel")
        until "the trust question" 3000 (fun () -> dialogs.Count > 0)
        let d = dialogs[0]
        Assert.Equal("Trust this folder?", AutomationProperties.GetName d)
        assertAllNamed "trust dialog" d

        let names =
            descendants d
            |> List.choose (fun c ->
                match c with
                | :? Button as b -> Some(AutomationProperties.GetName b)
                | _ -> None)

        Assert.Contains("Cancel", names)
        Assert.Contains("Trust and open terminal", names)
        click (find<Button> d "cancel")
        pump 50)

[<Fact>]
let ``a name is more than symbols: glyph buttons say what they do`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)

        let names =
            withMenus s.Window
            |> List.filter isInteractive
            |> List.map AutomationProperties.GetName

        // the close buttons of the panel and of a tab are "✕" on screen
        Assert.Contains("Close panel", names)
        Assert.Contains("Close app.fs", names)
        // the activity bar is glyphs only
        for n in [ "Explorer"; "Search"; "Outline"; "Tools and languages" ] do
            Assert.Contains(n, names))

[<Fact>]
let ``names that help are written for people: no names end in a shortcut or are left as a type name`` () =
    withWindow false sampleFiles (fun s ->
        pump 100

        for c in withMenus s.Window |> List.filter isInteractive do
            let n = AutomationProperties.GetName c

            Assert.False(
                Text.RegularExpressions.Regex.IsMatch(n, @"\((Ctrl|Alt|Shift|F\d+)[^)]*\)$"),
                sprintf "the name '%s' carries a shortcut" n
            )

            Assert.False(n.Contains "Avalonia", sprintf "the name '%s' is a type name" n))

// ---- regions and roles ---------------------------------------------------------------------------

[<Fact>]
let ``the regions of the window are marked for navigation`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()

        let landmark (id: string) =
            AutomationProperties.GetLandmarkType(find<Control> s.Window id)

        Assert.Equal(Nullable AutomationLandmarkType.Main, landmark "code-editor")
        Assert.Equal(Nullable AutomationLandmarkType.Navigation, landmark "activity-bar")
        Assert.Equal(Nullable AutomationLandmarkType.Navigation, landmark "sidebar")
        Assert.Equal(Nullable AutomationLandmarkType.Navigation, landmark "main-menu")

        Assert.Equal(
            Nullable AutomationControlType.StatusBar,
            AutomationProperties.GetControlTypeOverride(find<Control> s.Window "status-bar")
        )

        Assert.Equal(
            Nullable AutomationControlType.Tab,
            AutomationProperties.GetControlTypeOverride(find<Control> s.Window "tabs")
        )

        Assert.Equal(
            Nullable AutomationControlType.Tab,
            AutomationProperties.GetControlTypeOverride(find<Control> s.Window "panel-tabs")
        ))

[<Fact>]
let ``the status message is a polite live region and help text explains the main controls`` () =
    withWindow false sampleFiles (fun s ->
        Assert.Equal(
            AutomationLiveSetting.Polite,
            AutomationProperties.GetLiveSetting(find<Control> s.Window "status-message")
        )

        for id in
            [ "code-editor"
              "folder-tree"
              "terminal"
              "command-palette-input"
              "run-this-file" ] do
            let help = AutomationProperties.GetHelpText(find<Control> s.Window id)
            Assert.False(String.IsNullOrWhiteSpace help, sprintf "%s has no help text" id))

[<Fact>]
let ``the tab strip marks the open file as selected and the others not`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)
        s.Shell.OpenFile("notes.txt", 0, 0)
        pump 50

        let tabs =
            (find<StackPanel> s.Window "tabs").Children |> Seq.cast<Control> |> Seq.toList

        let status = tabs |> List.map AutomationProperties.GetItemStatus
        Assert.Equal<string list>([ ""; "selected" ], status)

        Assert.All(
            tabs,
            fun t ->
                Assert.Equal(Nullable AutomationControlType.TabItem, AutomationProperties.GetControlTypeOverride t)
        ))

[<Fact>]
let ``an unsaved file's tab says so to a screen reader`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("notes.txt", 0, 0)
        let editor = find<AvaloniaEdit.TextEditor> s.Window "code-editor"
        editor.Document.Insert(0, "x")
        pump 50
        let tab = (find<StackPanel> s.Window "tabs").Children[0]
        Assert.Contains("unsaved", AutomationProperties.GetName tab))

// ---- the keyboard --------------------------------------------------------------------------------

[<Fact>]
let ``every interactive control can take the keyboard focus`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)
        pump 100

        let stuck =
            withMenus s.Window
            |> List.filter isInteractive
            // lists, trees and the editor hand the focus to a row or the text area inside them
            |> List.filter (fun c ->
                match c with
                | :? Button
                | :? ToggleButton
                | :? TextBox
                | :? ComboBox
                | :? MenuItem
                | :? TerminalView -> not c.Focusable
                | _ -> false)
            |> List.map describe

        if not stuck.IsEmpty then
            failwithf "controls that cannot take the focus: %s" (String.Join(" | ", stuck)))

[<Fact>]
let ``a tab is reached with the keyboard: Enter shows it, Delete closes it`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)
        s.Shell.OpenFile("notes.txt", 0, 0)
        pump 50

        let tab (i: int) =
            (find<StackPanel> s.Window "tabs").Children[i] :?> Border

        Assert.True((tab 0).Focusable)
        press (tab 0) Key.Enter KeyModifiers.None
        pump 50
        let editor = find<AvaloniaEdit.TextEditor> s.Window "code-editor"
        Assert.Equal(s.Workspace.Read "src/app.fs", editor.Document.Text)
        press (tab 0) Key.Delete KeyModifiers.None
        pump 50
        Assert.Equal(1, (find<StackPanel> s.Window "tabs").Children.Count))

[<Fact>]
let ``a tab shows a focus ring when the focus arrives by key and not by a click`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)
        pump 50
        let tab = (find<StackPanel> s.Window "tabs").Children[0] :?> Border
        let before = tab.BorderThickness

        let focus (how: NavigationMethod) =
            tab.RaiseEvent(FocusChangedEventArgs(InputElement.GotFocusEvent, NavigationMethod = how, Source = tab))

        focus NavigationMethod.Pointer
        Assert.Equal(before, tab.BorderThickness)
        focus NavigationMethod.Tab
        Assert.Equal(Thickness 2.0, tab.BorderThickness)
        tab.RaiseEvent(FocusChangedEventArgs(InputElement.LostFocusEvent, Source = tab))
        Assert.Equal(before, tab.BorderThickness))

[<Fact>]
let ``the terminal and the editor are ringed when the keyboard focus enters them`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("src/app.fs", 0, 0)
        let editor = find<AvaloniaEdit.TextEditor> s.Window "code-editor"
        let ring = editor.Parent.Parent :?> Border
        // the focus is somewhere else first
        (find<TreeView> s.Window "folder-tree").Focus() |> ignore
        pump 20
        Assert.Equal(Thickness 0.0, ring.BorderThickness)
        // the real focus lands on the text area inside the editor, and the event bubbles up from there
        editor.TextArea.Focus(NavigationMethod.Tab) |> ignore
        Assert.True((ring.BorderThickness = Thickness 2.0), "the editor's ring")

        let terminal = find<TerminalView> s.Window "terminal"
        let tring = terminal.Parent :?> Border

        terminal.RaiseEvent(
            FocusChangedEventArgs(
                InputElement.GotFocusEvent,
                NavigationMethod = NavigationMethod.Tab,
                Source = terminal
            )
        )

        Assert.True((tring.BorderThickness = Thickness 2.0), "the terminal's ring"))

[<Fact>]
let ``everything the explorer's context menu does is on a key: Enter, F2 and Delete`` () =
    withWindow false sampleFiles (fun s ->
        s.OpenFolder()
        let tree = find<TreeView> s.Window "folder-tree"

        let notes =
            tree.Items
            |> Seq.cast<TreeViewItem>
            |> Seq.find (fun i -> rowText i = "notes.txt")
        // Enter opens a file
        press notes Key.Enter KeyModifiers.None
        pump 50
        Assert.Equal(1, (find<StackPanel> s.Window "tabs").Children.Count)
        // F2 asks for a new name, Delete asks before it deletes
        let dialogs, sub =
            (let l = List<Window>() in l, Dialogs.shown.Subscribe(fun w -> l.Add w))

        use _ = sub
        press notes Key.F2 KeyModifiers.None
        until "the rename question" 2000 (fun () -> dialogs.Count = 1)
        Assert.Equal("Rename", dialogs[0].Title)
        dialogs[0].Close(None: string option)
        pump 50
        press notes Key.Delete KeyModifiers.None
        until "the delete question" 2000 (fun () -> dialogs.Count = 2)
        Assert.Equal("Delete", dialogs[1].Title)
        dialogs[1].Close false
        pump 50
        Assert.True(IO.File.Exists(s.Workspace.Path "notes.txt")))

[<Fact>]
let ``the actions of the mouse-only parts have commands and keys`` () =
    withWindow false sampleFiles (fun s ->
        let ids = s.Shell.CommandIds
        // tabs, the explorer and the editor can be reached and left without the mouse
        for id in
            [ "next-tab"
              "previous-tab"
              "focus-editor"
              "focus-explorer"
              "close-tab"
              "terminal"
              "problems"
              "output"
              "palette"
              "quick-open"
              "toggle-breakpoint" ] do
            Assert.Contains(id, ids)

        Assert.Equal(Some "next-tab", Keymap.commandFor Key.PageDown KeyModifiers.Control false)
        Assert.Equal(Some "previous-tab", Keymap.commandFor Key.PageUp KeyModifiers.Control false)
        Assert.Equal(Some "focus-editor", Keymap.commandFor Key.D1 KeyModifiers.Control false)
        Assert.Equal(Some "focus-explorer", Keymap.commandFor Key.D0 KeyModifiers.Control false))
