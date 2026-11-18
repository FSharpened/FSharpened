/// Keyboard shortcuts in the real window (headless): the key table decides what a key press runs, the person's
/// keybindings.json is laid over it, and the Keyboard Shortcuts view changes and saves them.
module KeybindingWindowTests

open System.IO
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Xunit
open FSharpened.Ide.Desktop
open FSharpened.Ide.Host
open HeadlessHarness

let private ctrl = KeyModifiers.Control
let private alt = KeyModifiers.Alt

// ---- from a key press to a chord -------------------------------------------------------------------

[<Fact>]
let ``a key press becomes a chord in the normal form`` () =
    Assert.Equal(Some "ctrl+shift+p", KeyChords.chordOf Key.P (ctrl ||| KeyModifiers.Shift))
    Assert.Equal(Some "ctrl+equal", KeyChords.chordOf Key.OemPlus ctrl)
    Assert.Equal(Some "ctrl+backquote", KeyChords.chordOf Key.OemTilde ctrl)
    Assert.Equal(Some "f5", KeyChords.chordOf Key.F5 KeyModifiers.None)
    Assert.Equal(Some "ctrl+alt+down", KeyChords.chordOf Key.Down (ctrl ||| alt))
    Assert.Equal(Some "ctrl+1", KeyChords.chordOf Key.D1 ctrl)
    // a modifier on its own is no chord
    Assert.Equal(None, KeyChords.chordOf Key.LeftCtrl ctrl)

[<Fact>]
let ``every shipped chord can be made by a key press`` () =
    // so each shortcut in the table can really be typed
    let typable =
        [ for k in System.Enum.GetValues<Key>() do
              for m in
                  [ KeyModifiers.None
                    ctrl
                    alt
                    KeyModifiers.Shift
                    ctrl ||| KeyModifiers.Shift
                    ctrl ||| alt
                    KeyModifiers.Shift ||| alt ] do
                  match KeyChords.chordOf k m with
                  | Some c -> yield c
                  | None -> () ]
        |> Set.ofList

    for b in Keybindings.defaults do
        Assert.True(typable.Contains b.Chord, sprintf "%s (%s) cannot be typed" b.Chord b.Command)

[<Fact>]
let ``the shipped keys still run what they ran`` () =
    Assert.Equal(Some "palette", Keymap.commandFor Key.P (ctrl ||| KeyModifiers.Shift) false)
    Assert.Equal(Some "quick-open", Keymap.commandFor Key.P ctrl false)
    Assert.Equal(Some "debug-start", Keymap.commandFor Key.F5 KeyModifiers.None false)
    Assert.Equal(Some "run", Keymap.commandFor Key.F5 ctrl false)
    Assert.Equal(None, Keymap.commandFor Key.F2 KeyModifiers.None false)
    Assert.Equal(Some "rename-symbol", Keymap.commandFor Key.F2 KeyModifiers.None true)
    // exactly the keys held: Ctrl+Alt+P is not Ctrl+P
    Assert.Equal(None, Keymap.commandFor Key.P (ctrl ||| alt) false)

// ---- the person's file --------------------------------------------------------------------------------

let private withKeys (json: string) (files: (string * string) list) (body: Session -> unit) =
    withPreparedWindow true files (fun ws -> File.WriteAllText(ws.Env.KeybindingsFile, json)) body

let private fontSize (s: Session) = s.Shell.Editor.FontSize

[<Fact>]
let ``a shortcut from the file runs its command, and a removed one no longer does`` () =
    withKeys """[ { "key": "alt+q", "command": "zoom-in" }, { "command": "-zoom-out" } ]""" [ "a.txt", "x\n" ] (fun s ->
        s.OpenFolder()
        s.Shell.OpenFile("a.txt", 1, 1)
        let before = fontSize s
        press s.Window Key.Q alt
        Assert.Equal(before + 1.0, fontSize s)
        // the shipped Ctrl+= still zooms in, and the removed Ctrl+- does nothing
        press s.Window Key.OemPlus ctrl
        Assert.Equal(before + 2.0, fontSize s)
        press s.Window Key.OemMinus ctrl
        Assert.Equal(before + 2.0, fontSize s))

[<Fact>]
let ``mistakes in the file are reported and the rest of it is used`` () =
    withKeys
        """[ { "key": "alt+q", "command": "nope" }, { "key": "alt+w", "command": "zoom-in" } ]"""
        [ "a.txt", "x\n" ]
        (fun s ->
            Assert.Single s.Shell.Keybindings.Problems |> ignore
            Assert.Contains("nope", s.Shell.Keybindings.Problems[0])
            Assert.Equal(Some "zoom-in", Keybindings.resolve s.Shell.Bindings "alt+w" false)
            Assert.Equal(None, Keybindings.resolve s.Shell.Bindings "alt+q" false))

[<Fact>]
let ``a file that is not JSON leaves the shipped shortcuts as they are`` () =
    withKeys "[ {" [ "a.txt", "x\n" ] (fun s ->
        Assert.Equal<Binding list>(Keybindings.defaults, s.Shell.Bindings)
        Assert.NotEmpty s.Shell.Keybindings.Problems)

// ---- the view ----------------------------------------------------------------------------------------------

let private clickButton (s: Session) (id: string) =
    (find<Button> s.Window id).RaiseEvent(Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent))

let private showKeys (s: Session) =
    s.Shell.Run "keybindings"
    let filter = find<TextBox> s.Window "filter-shortcuts"
    let list = find<ListBox> s.Window "shortcuts"
    filter, list

[<Fact>]
let ``the view lists commands with their shortcuts, filters them, and every control has a name`` () =
    withWindow true [ "a.txt", "x\n" ] (fun s ->
        let filter, list = showKeys s
        Assert.True(list.ItemCount > 40)
        filter.Text <- "word wrap"
        until "the filter" 3000 (fun () -> list.ItemCount = 1)

        match list.Items[0] with
        | :? TextBlock as t -> Assert.Contains("Alt+Z", t.Text)
        | _ -> failwith "a row is a text"

        let host = find<ContentControl> s.Window "sidebar"

        let bad =
            [ for c in descendants host do
                  match c with
                  | :? Button
                  | :? TextBox
                  | :? ListBox ->
                      if not (Access.isMeaningful (AutomationProperties.GetName c)) then
                          yield c.GetType().Name
                  | _ -> () ]

        Assert.Empty bad)

[<Fact>]
let ``changing a shortcut in the view saves it, takes it from the old key and from whoever had the new one`` () =
    withWindow true [ "a.txt", "x\n" ] (fun s ->
        let filter, list = showKeys s
        filter.Text <- "word wrap"
        until "the filter" 3000 (fun () -> list.ItemCount = 1)
        list.SelectedIndex <- 0
        clickButton s "change-shortcut"
        // the next key is the new shortcut, not a command: Ctrl+S would have saved
        press s.Window Key.S ctrl

        Assert.Equal(Some "wrap", Keybindings.resolve s.Shell.Bindings "ctrl+s" false)

        Assert.Equal(
            None,
            Keybindings.resolve s.Shell.Bindings "ctrl+s" false
            |> Option.filter ((=) "save")
        )

        Assert.Equal(None, Keybindings.resolve s.Shell.Bindings "alt+z" false)
        Assert.Equal<string list>([ "ctrl+s" ], Keybindings.chordsOf s.Shell.Bindings "wrap")
        Assert.Empty(Keybindings.chordsOf s.Shell.Bindings "save")

        // it is in the file, and a window started later reads it
        let entries, problems =
            Keybindings.parseUser (File.ReadAllText s.Workspace.Env.KeybindingsFile) (fun _ -> true)

        Assert.Empty problems
        Assert.Contains(entries, fun e -> e.Command = "wrap" && e.Key = "ctrl+s" && not e.Remove)
        Assert.Contains(entries, fun e -> e.Command = "wrap" && e.Key = "alt+z" && e.Remove)

        // the list shows the new shortcut
        match list.Items[0] with
        | :? TextBlock as t -> Assert.Contains("Ctrl+S", t.Text)
        | _ -> failwith "a row is a text")

[<Fact>]
let ``Escape while waiting for a shortcut cancels, and a key that cannot be a shortcut is refused`` () =
    withWindow true [ "a.txt", "x\n" ] (fun s ->
        let filter, list = showKeys s
        filter.Text <- "word wrap"
        until "the filter" 3000 (fun () -> list.ItemCount = 1)
        list.SelectedIndex <- 0
        clickButton s "change-shortcut"
        press s.Window Key.Escape KeyModifiers.None
        Assert.Equal<string list>([ "alt+z" ], Keybindings.chordsOf s.Shell.Bindings "wrap")
        Assert.False(File.Exists s.Workspace.Env.KeybindingsFile)
        clickButton s "change-shortcut"
        // a plain letter would take over typing
        press s.Window Key.Q KeyModifiers.None
        Assert.Equal<string list>([ "alt+z" ], Keybindings.chordsOf s.Shell.Bindings "wrap")
        Assert.False(File.Exists s.Workspace.Env.KeybindingsFile))

[<Fact>]
let ``remove takes the shortcut away, and reset brings the shipped ones back`` () =
    withWindow true [ "a.txt", "x\n" ] (fun s ->
        let filter, list = showKeys s
        filter.Text <- "word wrap"
        until "the filter" 3000 (fun () -> list.ItemCount = 1)
        list.SelectedIndex <- 0
        clickButton s "remove-shortcut"
        Assert.Empty(Keybindings.chordsOf s.Shell.Bindings "wrap")
        Assert.NotEqual<Binding list>(Keybindings.defaults, s.Shell.Bindings)
        clickButton s "reset-all-shortcuts"
        Assert.Equal<Binding list>(Keybindings.defaults, s.Shell.Bindings)
        Assert.Equal("[]\n", File.ReadAllText s.Workspace.Env.KeybindingsFile))

[<Fact>]
let ``the palette and ui show the shortcut in force`` () =
    withWindow true [ "a.txt", "x\n" ] (fun s ->
        Assert.Equal("Ctrl+S", s.Shell.Keys "save")
        s.Shell.Keybindings.Rebind("save", "ctrl+alt+s")
        Assert.Equal("Ctrl+Alt+S", s.Shell.Keys "save")
        s.Shell.Keybindings.Remove "save"
        Assert.Equal("", s.Shell.Keys "save"))
