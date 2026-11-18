/// Open documents: the tab strip, activating, closing, saving and opening files.
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

type EditorTabs
    (
        ui: Ui,
        appearance: Appearance,
        statusBar: StatusBar,
        problems: Problems,
        languageServers: LanguageServers,
        hoverPanel: HoverPanel
    ) =
    let applyGrammar = appearance.ApplyGrammar
    let docs = ui.Docs
    let editor = ui.Editor
    let hideHover = hoverPanel.HideHover
    let menuItem = ui.MenuItem
    let outlineTimer = statusBar.OutlineTimer
    let packFor = appearance.PackFor
    let recomputeOutline = statusBar.RecomputeOutline
    let say = ui.Say
    let sessionOf = languageServers.SessionOf
    let setOutput = ui.SetOutput
    let showPanel = appearance.ShowPanel
    let syncLsp = languageServers.SyncLsp
    let tabStrip = ui.TabStrip
    let updateSquiggles = problems.UpdateSquiggles
    let updateStatus = statusBar.UpdateStatus
    let welcome = ui.Welcome
    let window = ui.Window

    let rec rebuildTabs () =
        tabStrip.Children.Clear()

        for d in docs |> Seq.toList do
            let isActive =
                (match ui.Active with
                 | Some a -> Object.ReferenceEquals(a, d)
                 | None -> false)

            let title =
                TextBlock(
                    Text = Status.tabTitle d.Rel d.Dirty d.MissingOnDisk,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (if isActive then ui.Roles.Fg else ui.Roles.Muted),
                    FontWeight = (if isActive then FontWeight.SemiBold else FontWeight.Normal)
                )

            let close =
                Button(
                    Content = "✕",
                    Padding = Thickness(4.0, 0.0),
                    Background = Brushes.Transparent,
                    BorderThickness = Thickness 0.0,
                    Foreground = ui.Roles.Muted,
                    FontSize = 11.0,
                    VerticalAlignment = VerticalAlignment.Center
                )

            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = 6.0)
            row.Children.Add title
            row.Children.Add close

            let tab =
                Border(
                    Child = row,
                    Padding = Thickness(12.0, 7.0, 6.0, 7.0),
                    BorderThickness = Thickness(0.0, 0.0, 1.0, (if isActive then 2.0 else 0.0)),
                    BorderBrush = (if isActive then ui.Roles.Brand else ui.Roles.Line),
                    Background = (if isActive then ui.Roles.EditorBg else Brushes.Transparent),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Focusable = true
                )

            // a tab is a control of its own for the keyboard and for a screen reader: the focus ring is drawn by the
            // `tab` style (App.fs), Enter or Space activates it, Delete closes it; Ctrl+PageUp and Ctrl+PageDown move along the strip
            tab.Classes.Add "tab"
            ToolTip.SetTip(tab, d.Rel)

            Access.describe
                (Status.tabName d.Rel d.Dirty d.MissingOnDisk)
                (sprintf "%s. Enter shows it, Delete closes it." d.Rel)
                tab
            |> Access.role AutomationControlType.TabItem
            |> ignore

            Avalonia.Automation.AutomationProperties.SetItemStatus(tab, (if isActive then "selected" else ""))

            Access.describe
                ("Close " + Path.GetFileName d.Rel)
                "Closes the file; asks first when it has unsaved changes."
                close
            |> ignore

            // the focus ring (keys only): the tab's border turns to the accent colour while the focus is on it
            let restBrush = tab.BorderBrush
            let restThickness = tab.BorderThickness

            tab.GotFocus.Add(fun e ->
                if e.NavigationMethod <> NavigationMethod.Pointer then
                    tab.BorderBrush <- ui.Roles.Accent
                    tab.BorderThickness <- Thickness 2.0)

            tab.LostFocus.Add(fun _ ->
                tab.BorderBrush <- restBrush
                tab.BorderThickness <- restThickness)

            tab.KeyDown.Add(fun e ->
                match e.Key with
                | Key.Enter
                | Key.Space ->
                    e.Handled <- true
                    activate (Some d)
                | Key.Delete ->
                    e.Handled <- true
                    closeDoc d
                | _ -> ())

            tab.PointerPressed.Add(fun e ->
                let p = e.GetCurrentPoint tab

                if p.Properties.IsMiddleButtonPressed then
                    closeDoc d
                elif p.Properties.IsLeftButtonPressed then
                    activate (Some d))

            let menu = ContextMenu()
            menu.Items.Push(menuItem "Close" "Ctrl+W" (fun () -> closeDoc d))

            menu.Items.Push(
                menuItem "Close Others" "" (fun () ->
                    for o in docs |> Seq.toList do
                        if not (Object.ReferenceEquals(o, d)) then
                            closeNow o)
            )

            menu.Items.Push(menuItem "Copy Relative Path" "" (fun () -> window.Clipboard.SetTextAsync d.Rel |> ignore))

            menu.Items.Push(
                menuItem "Copy Full Path" "" (fun () ->
                    match ui.Root with
                    | Some r -> window.Clipboard.SetTextAsync(Path.Combine(r, d.Rel)) |> ignore
                    | None -> ())
            )

            tab.ContextMenu <- menu
            close.Click.Add(fun _ -> closeDoc d)
            tabStrip.Children.Add tab

        welcome.IsVisible <- docs.Count = 0
        editor.IsVisible <- docs.Count > 0

    and activate (d: OpenDoc option) =
        ui.Active <- d
        hideHover ()

        match d with
        | Some doc ->
            editor.Document <- doc.Doc
            applyGrammar d
            recomputeOutline doc
            syncLsp doc
            editor.TextArea.Caret.Line <- doc.Line
            editor.TextArea.Caret.Column <- doc.Column

            editor.Options.IndentationSize <-
                (match doc.Pack with
                 | Some p -> p.IndentSize
                 | None -> ui.Prefs.Editor.TabSize)

            editor.Options.ConvertTabsToSpaces <-
                (match doc.Pack with
                 | Some p -> not p.UseTabs
                 | None -> ui.Prefs.Editor.InsertSpaces)

            editor.ScrollToLine doc.Line
            editor.Focus() |> ignore
        | None -> editor.Document <- TextDocument("")

        rebuildTabs ()
        updateStatus ()
        updateSquiggles ()
        ui.RefreshExecFn()
        ui.RefreshSidebarFn()

    and closeNow (d: OpenDoc) =
        ui.SaveBreakpointsFn d
        let index = docs.IndexOf d
        docs.Remove d |> ignore

        match ui.Root, sessionOf d with
        | Some r, Some s when d.LspOpen ->
            s.Client.DidClose(Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar)))
        | _ -> ()

        let wasActive =
            (match ui.Active with
             | Some a -> Object.ReferenceEquals(a, d)
             | None -> false)

        if wasActive then
            if docs.Count = 0 then
                activate None
            else
                activate (Some docs[max 0 (min (docs.Count - 1) (index - 1))])
        else
            rebuildTabs ()

    and closeDoc (d: OpenDoc) =
        if d.Dirty then
            task {
                let! ok =
                    Dialogs.confirm
                        window
                        ui.Roles
                        "Unsaved changes"
                        (sprintf "“%s” has changes that are not saved. Close it and lose them?" (Path.GetFileName d.Rel))
                        "Discard changes"

                if ok then
                    closeNow d
            }
            |> ignore
        else
            closeNow d

    let saveDoc (d: OpenDoc) : bool =
        match ui.Root with
        | None -> false
        | Some r ->
            match DiskWorkspace.writeText r d.Rel d.Text with
            | Ok() ->
                d.MarkSaved()

                match sessionOf d with
                | Some sess when d.LspOpen ->
                    syncLsp d
                    sess.Client.DidSave(Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar)))
                | _ -> ()

                true
            | Error e ->
                setOutput (sprintf "Could not save %s: %s" d.Rel e)
                showPanel "output"
                false

    let saveAll () =
        for d in docs do
            if d.Dirty then
                saveDoc d |> ignore

        rebuildTabs ()

    let openFile (rel: string) (line: int) (col: int) =
        match ui.Root with
        | None -> ()
        | Some r ->
            let existing = docs |> Seq.tryFind (fun d -> d.Rel = rel)

            let target =
                match existing with
                | Some d -> Some d
                | None ->
                    match DiskWorkspace.readText r rel with
                    | Ok text ->
                        let d = OpenDoc(rel, text, packFor rel)
                        ui.RestoreBreakpointsFn d

                        d.Doc.TextChanged.Add(fun _ ->
                            d.LspDirty <- true
                            outlineTimer.Stop()
                            outlineTimer.Start()
                            rebuildTabs ())

                        // the undo stack learns of the first edit only after `TextChanged` has fired, so the tab's
                        // "unsaved" dot is also redrawn when the stack says the text is no longer the original
                        d.Doc.UndoStack.PropertyChanged.Add(fun a ->
                            if a.PropertyName = "IsOriginalFile" then
                                rebuildTabs ())

                        docs.Add d
                        Some d
                    | Error e ->
                        say (sprintf "Cannot open %s: %s" rel e)
                        None

            match target with
            | Some d ->
                if line > 0 then
                    d.Line <- line
                    d.Column <- max 1 col

                activate (Some d)
            | None -> ()

    do ui.OpenFileFn <- openFile

    member _.Activate = activate
    member _.CloseDoc = closeDoc
    member _.CloseNow = closeNow
    member _.OpenFile = openFile
    member _.RebuildTabs = rebuildTabs
    member _.SaveAll = saveAll
    member _.SaveDoc = saveDoc
