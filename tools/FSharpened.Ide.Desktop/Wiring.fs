/// Connects the controls to the parts: timers, editor events, keys, closing.
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

type Wiring
    (
        ui: Ui,
        appearance: Appearance,
        statusBar: StatusBar,
        languageServers: LanguageServers,
        hoverPanel: HoverPanel,
        editorTabs: EditorTabs,
        runPanel: RunPanel,
        renameFeature: RenameFeature,
        debugPanel: DebugPanel,
        explorer: Explorer,
        sidebarViews: SidebarViews,
        folderSession: FolderSession,
        commandPalette: CommandPalette,
        menus: Menus,
        layout: Layout
    ) =
    let applyEditorPrefs = appearance.ApplyEditorPrefs
    let applyTheme = appearance.ApplyTheme
    let command = menus.Command
    let debugStartOrContinue = debugPanel.DebugStartOrContinue
    let docs = ui.Docs
    let editor = ui.Editor
    let formatCurrent = runPanel.FormatCurrent
    let hideHover = hoverPanel.HideHover
    let hidePalette = commandPalette.HidePalette
    let hooks = ui.Hooks
    let hoverBox = hoverPanel.HoverBox
    let hoverTimer = hoverPanel.HoverTimer
    let layout = layout.Layout
    let menuItem = ui.MenuItem
    let openFile = editorTabs.OpenFile
    let outlineList = sidebarViews.OutlineList
    let outlineTimer = statusBar.OutlineTimer
    let palette = ui.Palette
    let rebuildTabs = editorTabs.RebuildTabs
    let recomputeOutline = statusBar.RecomputeOutline
    let refreshTree = explorer.RefreshTree
    let renameSymbolNow = renameFeature.RenameSymbolNow
    let runCurrent = runPanel.RunCurrent
    let runSearch = sidebarViews.RunSearch
    let searchBox = sidebarViews.SearchBox
    let searchResults = sidebarViews.SearchResults
    let showHoverAt = hoverPanel.ShowHoverAt
    let showSidebar = sidebarViews.ShowSidebar
    let squiggles = ui.Squiggles
    let stopServers = languageServers.StopServers
    let stopWatching = folderSession.StopWatching
    let syncLsp = languageServers.SyncLsp
    let termView = ui.TermView
    let toggleBreakpoint = debugPanel.ToggleBreakpoint
    let updateStatus = statusBar.UpdateStatus
    let window = ui.Window

    // ---- wiring -----------------------------------------------------------------------------------
    do
        ui.RefreshTreeFn <- refreshTree
        editor.TextArea.TextView.BackgroundRenderers.Add squiggles
        hooks.Add(fun _ -> refreshTree ())
        window.Content <- layout ()
        window.Icon <- null
        applyTheme ui.Theme
        applyEditorPrefs ()
        showSidebar "files"
        rebuildTabs ()

        outlineTimer.Tick.Add(fun _ ->
            outlineTimer.Stop()

            match ui.Active with
            | Some d ->
                recomputeOutline d

                if d.LspDirty then
                    syncLsp d

                updateStatus ()
                ui.RefreshSidebarFn()

                for h in ui.OnSettled do
                    h d
            | None -> ())

        editor.TextArea.Caret.PositionChanged.Add(fun _ -> updateStatus ())

        // hover: rest the pointer on code for a moment and a box appears; moving, typing or leaving hides it
        hoverTimer.Tick.Add(fun _ ->
            hoverTimer.Stop()
            let pos = editor.GetPositionFromPoint hoverPanel.HoverPoint

            if pos.HasValue then
                showHoverAt
                    pos.Value.Line
                    pos.Value.Column
                    (Point(hoverPanel.HoverPoint.X + 12.0, hoverPanel.HoverPoint.Y + 20.0)))

        editor.PointerMoved.Add(fun e ->
            let p = e.GetPosition editor

            if hoverBox.IsVisible then
                if
                    abs (p.X - hoverPanel.HoverPoint.X) > 4.0
                    || abs (p.Y - hoverPanel.HoverPoint.Y) > 4.0
                then
                    hideHover ()
                    hoverPanel.HoverPoint <- p
                    hoverTimer.Start()
            else
                hoverPanel.HoverPoint <- p
                hoverTimer.Stop()
                hoverTimer.Start())

        editor.PointerExited.Add(fun _ -> hideHover ())
        editor.TextArea.TextEntered.Add(fun _ -> hideHover ())
        editor.TextArea.TextView.ScrollOffsetChanged.Add(fun _ -> hideHover ())

        // type a closing bracket after an opening one, and step over it when it is typed again
        editor.TextArea.TextEntered.Add(fun e ->
            if ui.Prefs.Editor.AutoCloseBrackets && e.Text.Length = 1 then
                match Brackets.closingFor e.Text with
                | Some c ->
                    let doc = editor.Document
                    let at = editor.CaretOffset
                    let next = if at < doc.TextLength then Some(doc.GetCharAt at) else None

                    if Brackets.shouldInsert next then
                        doc.Insert(at, string c)
                        editor.CaretOffset <- at
                | None -> ())

        editor.TextArea.TextEntering.Add(fun e ->
            if ui.Prefs.Editor.AutoCloseBrackets && e.Text.Length = 1 then
                let doc = editor.Document
                let at = editor.CaretOffset
                let next = if at < doc.TextLength then Some(doc.GetCharAt at) else None

                if Brackets.shouldStepOver e.Text next then
                    editor.CaretOffset <- at + 1
                    e.Handled <- true)

        // the editor's own context menu
        let editorMenu = ContextMenu()
        editorMenu.Items.Push(menuItem "Cut" "Ctrl+X" (fun () -> editor.Cut()))
        editorMenu.Items.Push(menuItem "Copy" "Ctrl+C" (fun () -> editor.Copy()))
        editorMenu.Items.Push(menuItem "Paste" "Ctrl+V" (fun () -> editor.Paste()))
        editorMenu.Items.Push(menuItem "Select All" "Ctrl+A" (fun () -> editor.SelectAll()))
        editorMenu.Items.Push(Separator())
        editorMenu.Items.Push(menuItem "Go to Symbol…" "Ctrl+Shift+O" (fun () -> ui.ShowPaletteFn "@"))
        editorMenu.Items.Push(menuItem "Format Document" "Shift+Alt+F" (fun () -> (command "format").Run()))
        editorMenu.Items.Push(menuItem "Find All References" "Shift+F12" (fun () -> (command "references").Run()))
        editorMenu.Items.Push(menuItem "Quick Fix…" "Ctrl+OemPeriod" (fun () -> (command "quick-fix").Run()))
        editorMenu.Items.Push(menuItem "Rename Symbol" "F2" renameSymbolNow)
        editorMenu.Items.Push(menuItem "Toggle Breakpoint" "F9" toggleBreakpoint)
        editorMenu.Items.Push(menuItem "Run This File" "Ctrl+F5" (fun () -> runCurrent Runner.defaultTimeoutSec))
        editorMenu.Items.Push(menuItem "Start Debugging" "F5" debugStartOrContinue)
        editorMenu.Items.Push(menuItem "Command Palette…" "Ctrl+Shift+P" (fun () -> ui.ShowPaletteFn ">"))
        editor.ContextMenu <- editorMenu

        searchBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                runSearch ())

        searchResults.SelectionChanged.Add(fun _ ->
            match searchResults.SelectedItem with
            | :? TextBlock as t ->
                match t.Tag with
                | :? (string * int * int) as hit ->
                    let (p, l, c) = hit
                    openFile p l c
                | _ -> ()
            | _ -> ())

        outlineList.SelectionChanged.Add(fun _ ->
            match outlineList.SelectedItem with
            | :? TextBlock as t ->
                match t.Tag with
                | :? int as line ->
                    editor.TextArea.Caret.Line <- line
                    editor.ScrollToLine line
                    editor.Focus() |> ignore
                | _ -> ()
            | _ -> ())

        // keys that work wherever the focus is
        window.AddHandler(
            InputElement.KeyDownEvent,
            EventHandler<KeyEventArgs>(fun _ e ->
                // a shell has its own use for most Ctrl keys: while it has the focus only a few shortcuts stay with the window
                let terminalOwnsKey =
                    termView.IsFocused && not (TerminalKeys.windowKeeps e.Key e.KeyModifiers)

                if not terminalOwnsKey then
                    match Keymap.commandFor e.Key e.KeyModifiers editor.IsKeyboardFocusWithin with
                    | Some id ->
                        e.Handled <- true
                        (command id).Run()
                    | None ->
                        if e.Key = Key.Escape && palette.IsVisible then
                            hidePalette ()),
            RoutingStrategies.Tunnel
        )

        window.Closing.Add(fun e ->
            if not ui.Quitting && docs |> Seq.exists (fun d -> d.Dirty) then
                e.Cancel <- true

                task {
                    let! ok =
                        Dialogs.confirm
                            window
                            ui.Roles
                            "Unsaved changes"
                            "Some files have changes that are not saved. Quit and lose them?"
                            "Quit"

                    if ok then
                        ui.Quitting <- true
                        window.Close()
                }
                |> ignore
            else
                stopWatching ()
                stopServers ())
