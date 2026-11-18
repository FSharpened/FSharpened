/// The menu bar and the activity bar.
namespace FSharpened.Ide.Desktop

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Styling
open Avalonia.Threading
open AvaloniaEdit.CodeCompletion
open AvaloniaEdit
open AvaloniaEdit.Document
open AvaloniaEdit.Rendering
open AvaloniaEdit.Search
open AvaloniaEdit.TextMate
open TextMateSharp.Grammars
open FSharpened.Ide
open FSharpened.Ide.Host

type Menus(ui: Ui, appearance: Appearance, sidebarViews: SidebarViews, folderSession: FolderSession, commands: Commands)
    =
    let applyTheme = appearance.ApplyTheme
    let commands = commands.Commands
    let menuItem = ui.MenuItem
    let openFolder = folderSession.OpenFolder
    let setSidebarVisible = appearance.SetSidebarVisible
    let showSidebar = sidebarViews.ShowSidebar
    let themed = ui.Themed
    let window = ui.Window

    // ---- menus and activity bar -------------------------------------------------------------------
    let command (id: string) =
        commands |> List.find (fun c -> c.Id = id)

    let item (id: string) =
        let c = command id
        menuItem (c.Title.Substring(c.Title.IndexOf ':' + 1).Trim()) c.Keys c.Run

    /// A menu item is named by its header, without the access-key underscore.
    let named (m: MenuItem) =
        Access.nameOnly (Access.labelName (string m.Header)) "" m

    let buildMenu () =
        let bar = Menu()

        Access.describe "Main menu" "File, Edit, View, Run, Debug and Tools." bar
        |> Access.landmark AutomationLandmarkType.Navigation
        |> ignore

        themed (fun r ->
            bar.Background <- r.Card
            bar.Foreground <- r.Fg)

        let top (header: string) (items: Control list) =
            let m = named (MenuItem(Header = header))

            for i in items do
                m.Items.Push i

            bar.Items.Push m

        let recent = named (MenuItem(Header = "Open Recent"))

        recent.SubmenuOpened.Add(fun _ ->
            recent.Items.Clear()

            if ui.Prefs.Recent.IsEmpty then
                recent.Items.Push(named (MenuItem(Header = "(none)", IsEnabled = false)))

            for p in ui.Prefs.Recent do
                recent.Items.Push(menuItem p "" (fun () -> openFolder p)))

        recent.Items.Push(named (MenuItem(Header = "(none)", IsEnabled = false)))

        let themeMenu = named (MenuItem(Header = "Color Theme"))

        for t in Palette.choices do
            themeMenu.Items.Push(menuItem t.Name "" (fun () -> applyTheme t))

        top
            "_File"
            [ item "open-folder"
              recent
              Separator()
              item "new-file"
              item "save"
              item "save-all"
              Separator()
              item "close-tab"
              item "close-folder"
              Separator()
              menuItem "Exit" "Alt+F4" (fun () -> window.Close()) ]

        top "_Edit" [ item "find"; item "find-files"; Separator(); item "format" ]

        top
            "_View"
            [ item "palette"
              item "quick-open"
              item "goto-line"
              item "goto-symbol"
              Separator()
              item "explorer"
              item "find-files"
              item "outline"
              item "problems"
              item "output"
              item "tools"
              Separator()
              item "toggle-sidebar"
              item "toggle-panel"
              Separator()
              themeMenu
              item "zoom-in"
              item "zoom-out"
              item "wrap"
              item "whitespace"
              item "line-numbers"
              item "breadcrumbs" ]

        top "_Run" [ item "run"; item "run-long"; Separator(); item "revoke-trust" ]

        top
            "_Debug"
            [ item "debug-start"
              item "debug-continue"
              item "debug-pause"
              item "debug-stop"
              Separator()
              item "debug-step-over"
              item "debug-step-into"
              item "debug-step-out"
              Separator()
              item "toggle-breakpoint"
              item "debug-panel" ]

        top "_Tools" [ item "refresh-tools"; item "reload-packs"; item "packs-folder" ]
        bar

    let activityButton (glyph: string) (tip: string) (view: string) =
        let b =
            Button(
                Content = glyph,
                FontSize = 20.0,
                Width = 46.0,
                Height = 46.0,
                Background = Brushes.Transparent,
                BorderThickness = Thickness 0.0,
                HorizontalContentAlignment = HorizontalAlignment.Center
            )

        ToolTip.SetTip(b, tip)

        Access.describe (Access.plainName tip) ("Shows the " + Access.plainName tip + " view of the sidebar.") b
        |> ignore

        themed (fun r -> b.Foreground <- r.Muted)

        b.Click.Add(fun _ ->
            if ui.SidebarShown && ui.SidebarView = view then
                setSidebarVisible false
            else
                showSidebar view)

        b


    member _.ActivityButton = activityButton
    member _.BuildMenu = buildMenu
    member _.Command = command
