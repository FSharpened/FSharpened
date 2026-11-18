/// The keyboard shortcuts the person can change: read from `keybindings.json` in the data folder when the window starts,
/// shown in the Keyboard Shortcuts view (a list, a filter, and buttons to change, remove or reset), written back when
/// one is changed. Which chord runs which command is decided by `Keybindings` (pure); this file is the view and the file.
namespace FSharpened.Ide.Desktop

open System
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Platform.Storage
open Avalonia.Layout
open Avalonia.Media
open FSharpened.Ide.Host

type KeybindingsFeature(ui: Ui, commands: Commands) =
    let env = ui.Env
    let list = commands.Commands
    let ids = list |> List.map (fun c -> c.Id) |> Set.ofList
    let mutable problems: string list = []

    let titleOf (id: string) =
        list
        |> List.tryFind (fun c -> c.Id = id)
        |> Option.map (fun c -> c.Title)
        |> Option.defaultValue id

    let notify () =
        for h in ui.OnBindingsChanged do
            h ()

    let describeProblems () =
        match problems with
        | [] -> ""
        | first :: [] -> "keybindings.json: " + first
        | first :: rest -> sprintf "keybindings.json: %s (and %d more)" first rest.Length

    /// Reads the person's file over the shipped shortcuts. A file that is missing changes nothing.
    let load () =
        let json =
            try
                if File.Exists env.KeybindingsFile then
                    Some(File.ReadAllText env.KeybindingsFile)
                else
                    None
            with _ ->
                None

        match json with
        | None ->
            ui.Bindings <- Keybindings.defaults
            problems <- []
        | Some text ->
            let entries, why = Keybindings.parseUser text ids.Contains
            ui.Bindings <- Keybindings.build entries
            problems <- why

        notify ()

    /// Writes the file for a table and makes it the one in force.
    let save (table: Binding list) : bool =
        try
            Directory.CreateDirectory env.DataDir |> ignore
            let tmp = env.KeybindingsFile + ".tmp"
            File.WriteAllText(tmp, Keybindings.render (Keybindings.entriesFor table))
            File.Move(tmp, env.KeybindingsFile, true)
            ui.Bindings <- table
            problems <- []
            notify ()
            true
        with e ->
            ui.Say(sprintf "Could not write keybindings.json: %s" e.Message)
            false

    // ---- the view ---------------------------------------------------------------------------------------

    let filterBox = ui.TextBox "Filter shortcuts"
    let shortcutList = ListBox(Margin = Thickness(0.0, 4.0))
    let note = ui.Muted ""

    let shownKeys (id: string) =
        match ui.KeysFor id with
        | "" -> "no shortcut"
        | keys -> keys

    let selectedId () : string option =
        match shortcutList.SelectedItem with
        | :? TextBlock as t ->
            match t.Tag with
            | :? string as id -> Some id
            | _ -> None
        | _ -> None

    let refreshList () =
        let keep = selectedId ()

        let filter =
            (filterBox.Text |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant()

        shortcutList.Items.Clear()

        for c in list |> List.sortBy (fun c -> c.Title) do
            let keys = ui.KeysFor c.Id

            if
                filter = ""
                || c.Title.ToLowerInvariant().Contains filter
                || keys.ToLowerInvariant().Contains filter
            then
                let row =
                    TextBlock(
                        Text =
                            (if keys = "" then
                                 c.Title
                             else
                                 sprintf "%s    %s" c.Title keys),
                        Tag = c.Id,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = ui.Roles.Fg,
                        Margin = Thickness(2.0, 3.0)
                    )

                Access.nameOnly c.Title (sprintf "Shortcut: %s" (shownKeys c.Id)) row |> ignore
                shortcutList.Items.Add row |> ignore

                if Some c.Id = keep then
                    shortcutList.SelectedItem <- row

    let rebind (id: string) (chord: string) =
        let table, displaced = Keybindings.setChord ui.Bindings id chord

        if save table then
            note.Text <-
                match displaced with
                | [] -> sprintf "%s is now %s." (titleOf id) (Keybindings.display chord)
                | other ->
                    sprintf
                        "%s is now %s. %s no longer has it."
                        (titleOf id)
                        (Keybindings.display chord)
                        (other |> List.map titleOf |> String.concat ", ")

    let startCapture () =
        match selectedId () with
        | None -> note.Text <- "Choose a command in the list first."
        | Some id ->
            note.Text <- sprintf "Press the new shortcut for “%s”, or Escape to cancel." (titleOf id)

            ui.KeyCapture <-
                Some(fun key mods ->
                    match KeyChords.nameOf key with
                    | None -> () // a modifier on its own: keep waiting for the key
                    | Some _ when key = Key.Escape && mods = KeyModifiers.None ->
                        ui.KeyCapture <- None
                        note.Text <- "Cancelled."
                    | Some _ ->
                        ui.KeyCapture <- None

                        match KeyChords.chordOf key mods |> Option.map Keybindings.normalize with
                        | Some(Ok chord) -> rebind id chord
                        | Some(Error why) -> note.Text <- "That cannot be a shortcut: " + why
                        | None -> ())

    let removeSelected () =
        match selectedId () with
        | None -> note.Text <- "Choose a command in the list first."
        | Some id ->
            if save (Keybindings.unbind ui.Bindings id) then
                note.Text <- sprintf "%s has no shortcut now." (titleOf id)

    let resetAll () =
        if save Keybindings.defaults then
            note.Text <- "All shortcuts are back to the ones the editor ships with."

    let reload () =
        load ()

        note.Text <-
            (if problems.IsEmpty then
                 "Read keybindings.json."
             else
                 describeProblems ())

    let openFile () =
        try
            if not (File.Exists env.KeybindingsFile) then
                save ui.Bindings |> ignore

            ui.Window.Launcher.LaunchFileInfoAsync(FileInfo env.KeybindingsFile) |> ignore
        with e ->
            note.Text <- "Could not open the file: " + e.Message

    let button (label: string) (help: string) (click: unit -> unit) =
        let b = Button(Content = label, HorizontalAlignment = HorizontalAlignment.Stretch)
        Access.describe label help b |> ignore
        b.Click.Add(fun _ -> click ())
        b

    let build () : Control =
        Access.describe
            "Shortcuts"
            "The commands and the keys that run them. Choose one, then Change shortcut."
            shortcutList
        |> ignore

        Access.live note |> ignore
        filterBox.TextChanged.Add(fun _ -> refreshList ())
        refreshList ()

        let buttons =
            WrapPanel(Orientation = Orientation.Horizontal, Margin = Thickness(0.0, 4.0))

        for b in
            [ button "Change shortcut" "Press the keys you want for the chosen command." startCapture
              button "Remove shortcut" "Takes every shortcut away from the chosen command." removeSelected
              button "Reset all shortcuts" "Goes back to the shortcuts the editor ships with." resetAll
              button "Reload file" "Reads keybindings.json again." reload
              button "Open file" "Opens keybindings.json in your default editor." openFile ] do
            b.Margin <- Thickness(0.0, 0.0, 6.0, 4.0)
            buttons.Children.Add b

        let panel = DockPanel(Margin = Thickness(10.0, 0.0))
        DockPanel.SetDock(filterBox, Dock.Top)
        DockPanel.SetDock(buttons, Dock.Bottom)
        DockPanel.SetDock(note, Dock.Bottom)
        panel.Children.Add filterBox
        panel.Children.Add note
        panel.Children.Add buttons
        panel.Children.Add shortcutList
        panel :> Control

    let view = lazy (build ())

    do
        load ()

        if not problems.IsEmpty then
            ui.Say(describeProblems ())

        ui.ExtraViews["keys"] <- ("KEYBOARD SHORTCUTS", (fun () -> view.Value))

        ui.OnBindingsChanged.Add(fun () ->
            if view.IsValueCreated then
                refreshList ())

    member _.Reload = reload
    member _.Problems = problems

    /// Gives a command a shortcut as the view's button does: for the tests.
    member _.Rebind(id: string, chord: string) = rebind id chord

    member _.Remove(id: string) =
        save (Keybindings.unbind ui.Bindings id) |> ignore

    member _.ResetAll = resetAll
