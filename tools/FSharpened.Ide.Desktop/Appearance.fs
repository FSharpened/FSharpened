/// Panels and sidebar visibility, editor preferences, grammar and theme.
namespace FSharpened.Ide.Desktop

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Avalonia
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

type Appearance(ui: Ui) =
    let bottomRow = ui.BottomRow
    let bottomSplitRow = ui.BottomSplitRow
    let crumbs = ui.Crumbs
    let editor = ui.Editor
    let env = ui.Env
    let hooks = ui.Hooks
    let monoFont = ui.MonoFont
    let panelTabs = ui.PanelTabs
    let registryOptions = ui.RegistryOptions
    let sidebarColumn = ui.SidebarColumn
    let splitColumn = ui.SplitColumn
    let termView = ui.TermView
    let textMate = ui.TextMate
    let textMateTheme = ui.TextMateTheme
    let window = ui.Window

    let showPanel (name: string) =
        ui.CurrentPanel <- name

        if name = "terminal" then
            ui.TerminalShownFn()

        for (n, button, content) in panelTabs do
            content.IsVisible <- (n = name)
            button.FontWeight <- (if n = name then FontWeight.Bold else FontWeight.Normal)

            Avalonia.Automation.AutomationProperties.SetItemStatus(button, (if n = name then "selected" else ""))

    let setPanelVisible (v: bool) =
        ui.PanelShown <- v
        bottomRow.Height <- (if v then GridLength 230.0 else GridLength 0.0)
        bottomSplitRow.Height <- (if v then GridLength 4.0 else GridLength 0.0)

    let setSidebarVisible (v: bool) =
        ui.SidebarShown <- v
        sidebarColumn.Width <- (if v then GridLength 280.0 else GridLength 0.0)
        splitColumn.Width <- (if v then GridLength 4.0 else GridLength 0.0)

    let packFor (rel: string) : LanguagePack option = Languages.forFile env.Registry rel

    let rootName () = Paths.rootName ui.Root

    let applyEditorPrefs () =
        let e = ui.Prefs.Editor
        editor.FontSize <- float e.FontSize
        editor.ShowLineNumbers <- e.LineNumbers
        editor.WordWrap <- e.WordWrap
        editor.Options.ShowSpaces <- e.RenderWhitespace
        editor.Options.ShowTabs <- e.RenderWhitespace
        editor.Options.IndentationSize <- e.TabSize
        editor.Options.ConvertTabsToSpaces <- e.InsertSpaces
        termView.SetFont(monoFont, float (max 8 (e.FontSize - 1)))
        crumbs.IsVisible <- e.Breadcrumbs

    let savePrefs () = env.SavePrefs ui.Prefs

    let applyGrammar (d: OpenDoc option) =
        try
            match d with
            | Some(doc: OpenDoc) when
                (match doc.Pack with
                 | Some p -> p.Scope <> ""
                 | None -> false)
                ->
                textMate.SetGrammar doc.Pack.Value.Scope
            | Some doc ->
                let ext = Path.GetExtension doc.Rel
                let scope = registryOptions.GetScopeByExtension ext
                textMate.SetGrammar(if String.IsNullOrEmpty scope then null else scope)
            | None -> textMate.SetGrammar null
        with _ ->
            ()

    let applyTheme (t: ThemeSpec) =
        ui.Theme <- t
        ui.Roles <- Palette.ofTheme t

        ui.Prefs <-
            { ui.Prefs with
                Editor = { ui.Prefs.Editor with Theme = t.Id } }

        match Application.Current with
        | null -> ()
        | app ->
            app.RequestedThemeVariant <-
                (if ui.Roles.Dark then
                     ThemeVariant.Dark
                 else
                     ThemeVariant.Light)

        try
            textMate.SetTheme(registryOptions.LoadTheme(textMateTheme t))
        with _ ->
            ()

        editor.Background <- ui.Roles.EditorBg
        editor.Foreground <- ui.Roles.Fg
        editor.LineNumbersForeground <- ui.Roles.Muted
        editor.TextArea.SelectionBrush <- ui.Roles.Selection
        window.Background <- ui.Roles.Bg

        for h in hooks do
            h ui.Roles

        savePrefs ()

    member _.ApplyEditorPrefs = applyEditorPrefs
    member _.ApplyGrammar = applyGrammar
    member _.ApplyTheme = applyTheme
    member _.PackFor = packFor
    member _.RootName = rootName
    member _.SavePrefs = savePrefs
    member _.SetPanelVisible = setPanelVisible
    member _.SetSidebarVisible = setSidebarVisible
    member _.ShowPanel = showPanel
