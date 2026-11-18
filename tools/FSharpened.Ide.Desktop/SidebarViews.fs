/// The sidebar's search, outline and tools views, search in files and replace.
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

type SidebarViews(ui: Ui, appearance: Appearance, statusBar: StatusBar, editorTabs: EditorTabs) =
    let docs = ui.Docs
    let env = ui.Env
    let fileTree = ui.FileTree
    let flat = ui.Flat
    let label = ui.Label
    let monoFont = ui.MonoFont
    let muted = ui.Muted
    let rebuildTabs = editorTabs.RebuildTabs
    let refreshGit = statusBar.RefreshGit
    let setOutput = ui.SetOutput
    let setSidebarVisible = appearance.SetSidebarVisible
    let showPanel = appearance.ShowPanel
    let sidebarHost = ui.SidebarHost
    let sidebarTitle = ui.SidebarTitle
    let textBox = ui.TextBox
    let window = ui.Window

    // ---- sidebar views ----------------------------------------------------------------------------
    let searchBox = textBox "Find in files"
    let replaceBox = textBox "Replace with"
    let includeBox = textBox "Files to include, e.g. src/**, *.go"
    let excludeBox = textBox "Files to exclude, e.g. **/test/**"
    let searchResults = ListBox(Background = Brushes.Transparent)
    let caseToggle = ToggleButton(Content = "Aa", Padding = Thickness(6.0, 2.0))
    let wordToggle = ToggleButton(Content = "ab", Padding = Thickness(6.0, 2.0))
    let regexToggle = ToggleButton(Content = ".*", Padding = Thickness(6.0, 2.0))
    let searchSummary = muted ""

    let outlineList = ListBox(Background = Brushes.Transparent)
    let toolsPanel = StackPanel(Spacing = 6.0, Margin = Thickness(12.0, 4.0))

    // what a screen reader says about the search view's controls (the text boxes are named by `ui.TextBox`)
    do
        Access.describe "Match case" "Only matches with the same upper and lower case." caseToggle
        |> ignore

        Access.describe "Match whole word" "Only matches that are whole words." wordToggle
        |> ignore

        Access.describe "Use regular expression" "Reads the search text as a regular expression." regexToggle
        |> ignore

        Access.describe "Search results" "Matches in files. Enter opens the one chosen." searchResults
        |> ignore

        Access.describe "Outline" "The symbols of the open file. Enter goes to the one chosen." outlineList
        |> ignore

        Access.describe "Search summary" "" searchSummary |> ignore

        Access.describe "Tools and languages" "Which programs the language packs found on this computer." toolsPanel
        |> ignore

    let textOf (b: TextBox) =
        b.Text |> Option.ofObj |> Option.defaultValue ""

    let searchOptions () : SearchOptions =
        { Query = searchBox.Text
          CaseSensitive = caseToggle.IsChecked = Nullable true
          WholeWord = wordToggle.IsChecked = Nullable true
          Regex = regexToggle.IsChecked = Nullable true }

    let runSearch () =
        match ui.Root with
        | Some r when textOf searchBox <> "" ->
            let opts = searchOptions ()
            let inc, exc = textOf includeBox, textOf excludeBox
            let openText = [ for d in docs -> d.Rel, d.Text ]

            searchSummary.Text <- "Searching…"

            task {
                let! found = Task.Run(fun () -> FolderSearch.runDefault r opts inc exc openText)
                let result = found.Result
                ui.LastSearchEngine <- FolderSearch.engineLabel found.Engine

                searchResults.Items.Clear()

                if result.Error <> "" then
                    searchSummary.Text <- result.Error
                else
                    searchSummary.Text <-
                        sprintf
                            "%d results in %d files%s · %s search"
                            result.Total
                            result.Files.Length
                            (if result.Truncated then " (more not shown)" else "")
                            (match found.Engine with
                             | NativeEngine -> "native"
                             | ManagedEngine _ -> "managed")

                    ToolTip.SetTip(searchSummary, ui.LastSearchEngine)

                    for f in result.Files do
                        searchResults.Items.Push(
                            TextBlock(
                                Text = f.Path,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = ui.Roles.Fg,
                                Margin = Thickness(0.0, 6.0, 0.0, 2.0)
                            )
                        )

                        for h in f.Hits do
                            let row =
                                TextBlock(
                                    Text = sprintf "%d:  %s" h.Line (h.Preview.Trim()),
                                    Foreground = ui.Roles.Muted,
                                    FontFamily = monoFont,
                                    FontSize = 12.0,
                                    Tag = (f.Path, h.Line, h.Column),
                                    Margin = Thickness(10.0, 0.0, 0.0, 0.0)
                                )

                            searchResults.Items.Push row
            }
            |> ignore
        | _ -> ()

    /// Applies a replace that has been confirmed: files open in the editor are changed there (one edit, so Undo works, and
    /// they stay unsaved); any other file is written to disk straight away.
    let applyReplace
        (r: string)
        (opts: SearchOptions)
        (replacement: string)
        (files: FileHits list)
        (openText: (string * string) list)
        =
        let outcome = Replace.apply r opts replacement files openText

        for rel, text in outcome.Editor do
            match docs |> Seq.tryFind (fun d -> d.Rel = rel) with
            | Some d ->
                d.Doc.Text <- text
                d.LspDirty <- true
            | None -> ()

        searchSummary.Text <-
            sprintf
                "Replaced %d matches in %d files%s"
                outcome.Matches
                outcome.Files
                (if outcome.Failed.IsEmpty then
                     ""
                 else
                     sprintf ", %d failed (see Output)" outcome.Failed.Length)

        if not outcome.Failed.IsEmpty then
            setOutput (String.concat "\n" outcome.Failed)
            showPanel "output"

        searchResults.Items.Clear()
        rebuildTabs ()
        refreshGit ()

    /// Replace every match, after saying how many there are and asking.
    let replaceAll () =
        match ui.Root with
        | Some r when textOf searchBox <> "" ->
            let opts = searchOptions ()
            let replacement = textOf replaceBox
            let inc, exc = textOf includeBox, textOf excludeBox
            let openText = [ for d in docs -> d.Rel, d.Text ]

            task {
                let! found = Task.Run(fun () -> FolderSearch.runDefault r opts inc exc openText)
                let result = found.Result

                if result.Error <> "" then
                    searchSummary.Text <- result.Error
                elif result.Files.IsEmpty then
                    searchSummary.Text <- "Nothing to replace"
                else
                    let! ok =
                        Dialogs.confirm
                            window
                            ui.Roles
                            "Replace in files"
                            (sprintf
                                "Replace %d matches in %d files with “%s”?\n\nFiles open in the editor are changed there and left unsaved, so Undo works. Other files are written to disk now and cannot be undone."
                                result.Total
                                result.Files.Length
                                replacement)
                            "Replace all"

                    if ok then
                        applyReplace r opts replacement result.Files openText
            }
            |> ignore
        | _ -> ()

    let fillOutline () =
        outlineList.Items.Clear()

        match ui.Active with
        | Some d ->
            for s in d.Outline do
                let row =
                    TextBlock(
                        Text = sprintf "%s%s  %s" (String(' ', s.Depth * 2)) s.Name s.Kind,
                        Foreground = ui.Roles.Fg,
                        FontFamily = monoFont,
                        FontSize = 12.0,
                        Tag = s.Line
                    )

                outlineList.Items.Push row
        | None -> ()

    let fillTools () =
        toolsPanel.Children.Clear()
        toolsPanel.Children.Add(muted "Looking for the programs the language packs use…")

        task {
            let! infos = Task.Run(fun () -> env.Box.Refresh())
            toolsPanel.Children.Clear()
            let found = infos |> List.filter (fun t -> t.Found)

            toolsPanel.Children.Add(
                label (
                    sprintf
                        "%d of %d tools found · %d language packs"
                        found.Length
                        infos.Length
                        env.Registry.Packs.Length
                )
            )

            for p in env.Registry.Problems do
                toolsPanel.Children.Add(
                    TextBlock(
                        Text = sprintf "⚠ %s: %s" (Path.GetFileName p.Origin) p.Message,
                        Foreground = ui.Roles.Warn,
                        TextWrapping = TextWrapping.Wrap
                    )
                )

            for t in infos |> List.sortBy (fun t -> not t.Found, t.Label) do
                let line =
                    if t.Found then
                        sprintf "✔ %s  %s" t.Label t.Version
                    else
                        sprintf "✖ %s%s" t.Label (if t.Install <> "" then "  → " + t.Install else "")

                toolsPanel.Children.Add(
                    TextBlock(
                        Text = line,
                        Foreground = (if t.Found then ui.Roles.Ok else ui.Roles.Muted),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12.0
                    )
                )
        }
        |> ignore

    let buildSearchView () : Control =
        let s = StackPanel(Spacing = 6.0, Margin = Thickness(10.0, 0.0))
        s.Children.Add searchBox
        s.Children.Add replaceBox
        let toggles = StackPanel(Orientation = Orientation.Horizontal, Spacing = 4.0)
        toggles.Children.Add caseToggle
        toggles.Children.Add wordToggle
        toggles.Children.Add regexToggle
        toggles.Children.Add(flat "Replace all" "Replace every match in the files listed" replaceAll)
        s.Children.Add toggles
        s.Children.Add includeBox
        s.Children.Add excludeBox
        s.Children.Add searchSummary
        let d = DockPanel()
        DockPanel.SetDock(s, Dock.Top)
        d.Children.Add s
        d.Children.Add searchResults
        d :> Control

    // built once: a control has one parent, and Search can be chosen again while the view is already showing
    let searchPane = lazy (buildSearchView ())
    let searchView () : Control = searchPane.Value

    let showSidebar (name: string) =
        ui.SidebarView <- name
        setSidebarVisible true

        let title, content =
            match name with
            | "search" -> "SEARCH", searchView ()
            | "outline" ->
                fillOutline ()
                "OUTLINE", (outlineList :> Control)
            | "tools" ->
                fillTools ()
                "TOOLS AND LANGUAGES", (ScrollViewer(Content = toolsPanel) :> Control)
            | other when ui.ExtraViews.ContainsKey other ->
                let title, build = ui.ExtraViews[other]
                title, build ()
            | _ -> "EXPLORER", (fileTree :> Control)

        sidebarTitle.Text <- title
        sidebarHost.Content <- content

        if name = "search" then
            searchBox.Focus() |> ignore

    do
        ui.RefreshSidebarFn <-
            (fun () ->
                if ui.SidebarView = "outline" then
                    fillOutline ())


    member _.OutlineList = outlineList
    member _.ReplaceBox = replaceBox
    member _.RunSearch = runSearch
    member _.SearchBox = searchBox
    member _.SearchResults = searchResults
    member _.ShowSidebar = showSidebar
