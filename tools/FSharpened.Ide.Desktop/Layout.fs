/// Builds the control tree: activity bar, sidebar, tabs, editor, bottom panels, palette overlay, status bar.
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

type Layout
    (
        ui: Ui,
        appearance: Appearance,
        problems: Problems,
        hoverPanel: HoverPanel,
        editorTabs: EditorTabs,
        runPanel: RunPanel,
        debugPanel: DebugPanel,
        folderSession: FolderSession,
        commandPalette: CommandPalette,
        menus: Menus
    ) =
    let activityButton = menus.ActivityButton
    let bodyGrid = ui.BodyGrid
    let bottomRow = ui.BottomRow
    let bottomSplitRow = ui.BottomSplitRow
    let buildMenu = menus.BuildMenu
    let crumbs = ui.Crumbs
    let dbgButtons = ui.DbgButtons
    let dbgConsole = ui.DbgConsole
    let dbgInput = ui.DbgInput
    let dbgStack = ui.DbgStack
    let dbgStatus = ui.DbgStatus
    let dbgVars = ui.DbgVars
    let debugContinue = debugPanel.DebugContinue
    let debugPause = debugPanel.DebugPause
    let debugStartOrContinue = debugPanel.DebugStartOrContinue
    let debugStep = debugPanel.DebugStep
    let debugStop = debugPanel.DebugStop
    let editor = ui.Editor
    let evaluateInput = debugPanel.EvaluateInput
    let flagsBox = ui.FlagsBox
    let flat = ui.Flat
    let hidePalette = commandPalette.HidePalette
    let hoverBox = hoverPanel.HoverBox
    let inputBox = ui.InputBox
    let mainGrid = ui.MainGrid
    let muted = ui.Muted
    let openFile = editorTabs.OpenFile
    let openFolderDialog = folderSession.OpenFolderDialog
    let outputBox = ui.OutputBox
    let palette = ui.Palette
    let paletteInput = ui.PaletteInput
    let paletteList = ui.PaletteList
    let panelBorder = ui.PanelBorder
    let panelTabs = ui.PanelTabs
    let problemsBox = ui.ProblemsBox
    let refreshPalette = commandPalette.RefreshPalette
    let refreshProblems = problems.RefreshProblems
    let relOfDiagnosticFile = problems.RelOfDiagnosticFile
    let restyleBox = ui.RestyleBox
    let runCurrent = runPanel.RunCurrent
    let say = ui.Say
    let selectFrame = debugPanel.SelectFrame
    let setOutput = ui.SetOutput
    let setPanelVisible = appearance.SetPanelVisible
    let setPhase = debugPanel.SetPhase
    let showPanel = appearance.ShowPanel
    let sidebarColumn = ui.SidebarColumn
    let sidebarHost = ui.SidebarHost
    let sidebarTitle = ui.SidebarTitle
    let splitColumn = ui.SplitColumn
    let statusFolder = ui.StatusFolder
    let statusGit = ui.StatusGit
    let statusLang = ui.StatusLang
    let statusPos = ui.StatusPos
    let statusProblems = ui.StatusProblems
    let statusRun = ui.StatusRun
    let statusWatch = ui.StatusWatch
    let tabStrip = ui.TabStrip
    let termView = ui.TermView
    let themed = ui.Themed
    let welcome = ui.Welcome

    // ---- build the tree ---------------------------------------------------------------------------
    let layout () =
        // activity bar
        let activity = StackPanel()
        activity.Children.Add(activityButton "🗂" "Explorer (Ctrl+Shift+E)" "files")
        activity.Children.Add(activityButton "🔍" "Search (Ctrl+Shift+F)" "search")
        activity.Children.Add(activityButton "⎇" "Source control" "git")
        activity.Children.Add(activityButton "☰" "Outline" "outline")
        activity.Children.Add(activityButton "🛠" "Tools and languages" "tools")

        let activityBorder =
            Border(Child = activity, BorderThickness = Thickness(0.0, 0.0, 1.0, 0.0))

        Access.describe
            "Activity bar"
            "Choose the explorer, search, outline or tools view of the sidebar."
            activityBorder
        |> Access.landmark AutomationLandmarkType.Navigation
        |> ignore

        themed (fun r ->
            activityBorder.Background <- r.Card
            activityBorder.BorderBrush <- r.Line)

        // sidebar
        let sidebarBody = DockPanel()
        DockPanel.SetDock(sidebarTitle, Dock.Top)
        themed (fun r -> sidebarTitle.Foreground <- r.Muted)
        sidebarBody.Children.Add sidebarTitle
        sidebarBody.Children.Add sidebarHost
        let sidebar = panelBorder sidebarBody (fun r -> r.Card)

        let vsplit =
            GridSplitter(Width = 4.0, ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent)

        Access.describe "Resize sidebar" "Drag, or press Left and Right, to change the sidebar's width." vsplit
        |> ignore

        // tab strip with run controls
        let runButton =
            Button(
                Content = "▶  Run",
                Padding = Thickness(12.0, 4.0),
                Margin = Thickness(6.0, 4.0),
                FontWeight = FontWeight.SemiBold
            )

        themed (fun r ->
            runButton.Background <- r.Brand
            runButton.Foreground <- r.BrandFg)

        ToolTip.SetTip(runButton, "Run this file (Ctrl+F5)")

        Access.describe "Run this file" "Runs the open file with its language's tools. Shortcut Ctrl+F5." runButton
        |> Access.shortcut "Ctrl+F5"
        |> ignore

        runButton.Click.Add(fun _ -> runCurrent Runner.defaultTimeoutSec)
        themed (restyleBox flagsBox)
        flagsBox.Margin <- Thickness(0.0, 4.0)

        let runBar = StackPanel(Orientation = Orientation.Horizontal)
        runBar.Children.Add flagsBox
        runBar.Children.Add runButton
        DockPanel.SetDock(runBar, Dock.Right)

        let tabRow = DockPanel()
        tabRow.Children.Add runBar

        tabRow.Children.Add(
            ScrollViewer(
                Content = tabStrip,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            )
            |> Access.describe "Open files" "Scrolls the tab strip."
        )

        let tabBorder =
            Border(Child = tabRow, BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0))

        themed (fun r ->
            tabBorder.Background <- r.Card
            tabBorder.BorderBrush <- r.Line
            crumbs.Foreground <- r.Muted)

        // editor with welcome screen behind
        let title =
            TextBlock(
                Text = "FSharpened",
                FontSize = 34.0,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            )

        themed (fun r -> title.Foreground <- r.Brand)
        welcome.Children.Add title
        welcome.Children.Add(muted "A native F# IDE for any language that has a language pack")

        let openBtn =
            Button(
                Content = "Open Folder…",
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = Thickness(18.0, 8.0)
            )

        themed (fun r ->
            openBtn.Background <- r.Brand
            openBtn.Foreground <- r.BrandFg)

        Access.describe "Open folder" "Choose a folder to work in. Shortcut Ctrl+O." openBtn
        |> ignore

        openBtn.Click.Add(fun _ -> openFolderDialog ())
        welcome.Children.Add openBtn
        welcome.Children.Add(muted "Ctrl+Shift+P  commands      Ctrl+P  go to file      Ctrl+F5  run      F5  debug")

        let editorHost = Grid()
        editorHost.Children.Add welcome
        editorHost.Children.Add editor
        editorHost.Children.Add hoverBox
        editorHost.Children.Add ui.SignatureBox
        editorHost.Children.Add ui.Lightbulb
        editor.IsVisible <- false
        themed (fun r -> editorHost.Background <- r.EditorBg)

        // the focus ring (keys only) round the editor
        let editorRing = Border(Child = editorHost) |> Access.focusRing
        themed (fun r -> editorRing.BorderBrush <- r.Accent)

        let editorColumn = DockPanel()
        DockPanel.SetDock(tabBorder, Dock.Top)
        DockPanel.SetDock(crumbs, Dock.Top)
        editorColumn.Children.Add tabBorder
        editorColumn.Children.Add crumbs
        editorColumn.Children.Add editorRing

        // bottom panel
        let tabButton (name: string) (title: string) (content: Control) =
            let b =
                Button(
                    Content = title,
                    Background = Brushes.Transparent,
                    BorderThickness = Thickness 0.0,
                    Padding = Thickness(12.0, 6.0)
                )

            themed (fun r -> b.Foreground <- r.Fg)

            Access.describe
                (title.Substring(0, 1) + title.Substring(1).ToLowerInvariant() + " panel")
                ("Shows the " + title.ToLowerInvariant() + " panel.")
                b
            |> Access.role AutomationControlType.TabItem
            |> ignore

            b.Click.Add(fun _ -> showPanel name)
            panelTabs.Add((name, b, content))
            b

        let outputHost = Grid()
        outputHost.Children.Add outputBox
        let problemsHost = Grid()
        problemsHost.Children.Add problemsBox
        let referencesHost = Grid()
        referencesHost.Children.Add ui.ReferencesBox
        let inputHost = Grid()
        inputHost.Children.Add inputBox
        let termHost = Border(Child = termView) |> Access.focusRing
        themed (fun r -> termHost.BorderBrush <- r.Accent)

        themed (fun r ->
            outputBox.Background <- r.EditorBg
            outputBox.Foreground <- r.Fg
            inputBox.Background <- r.EditorBg
            inputBox.Foreground <- r.Fg

            let colorOf (b: IBrush) =
                match b with
                | :? ISolidColorBrush as s -> s.Color
                | _ -> Colors.Magenta

            termView.SetColors(colorOf r.Fg, colorOf r.EditorBg, r.Dark, colorOf r.Selection))

        let headerButtons = StackPanel(Orientation = Orientation.Horizontal)

        Access.describe "Panel tabs" "Output, problems, references, input, terminal and debug." headerButtons
        |> Access.role AutomationControlType.Tab
        |> ignore

        Access.describe "Output contents" "" outputHost |> ignore
        Access.describe "Problems contents" "" problemsHost |> ignore
        Access.describe "References contents" "" referencesHost |> ignore
        Access.describe "Input contents" "" inputHost |> ignore
        Access.describe "Terminal contents" "" termHost |> ignore
        headerButtons.Children.Add(tabButton "output" "OUTPUT" outputHost)
        headerButtons.Children.Add(tabButton "problems" "PROBLEMS" problemsHost)
        headerButtons.Children.Add(tabButton "references" "REFERENCES" referencesHost)
        headerButtons.Children.Add(tabButton "input" "INPUT" inputHost)
        headerButtons.Children.Add(tabButton "terminal" "TERMINAL" termHost)

        // the debug panel: toolbar on top, then call stack | variables | console
        let debugHost = DockPanel()
        Access.describe "Debug contents" "" debugHost |> ignore

        let tool (id: string) (text: string) (tip: string) (click: unit -> unit) =
            let b = flat text tip click
            dbgButtons[id] <- b
            b

        let toolbar = StackPanel(Orientation = Orientation.Horizontal)

        Access.describe "Debug toolbar" "Start, continue, step, pause and stop the program." toolbar
        |> Access.role AutomationControlType.ToolBar
        |> ignore

        toolbar.Children.Add(tool "start" "▶ Start" "Start debugging (F5)" debugStartOrContinue)
        toolbar.Children.Add(tool "continue" "▶ Continue" "Continue (F5)" debugContinue)

        toolbar.Children.Add(
            tool "over" "⤼ Over" "Step over (F10)" (fun () -> debugStep "Step over" (fun c t -> c.Next t))
        )

        toolbar.Children.Add(
            tool "into" "⤓ Into" "Step into (F11)" (fun () -> debugStep "Step into" (fun c t -> c.StepIn t))
        )

        toolbar.Children.Add(
            tool "out" "⤒ Out" "Step out (Shift+F11)" (fun () -> debugStep "Step out" (fun c t -> c.StepOut t))
        )

        toolbar.Children.Add(tool "pause" "⏸ Pause" "Pause the program" debugPause)
        toolbar.Children.Add(tool "stop" "⏹ Stop" "Stop debugging (Shift+F5)" debugStop)
        toolbar.Children.Add dbgStatus
        themed (fun r -> dbgStatus.Foreground <- r.Muted)
        setPhase DebugView.Idle ""

        let toolbarBorder =
            Border(Child = toolbar, BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0))

        themed (fun r ->
            toolbarBorder.BorderBrush <- r.Line
            toolbarBorder.Background <- r.Card)

        DockPanel.SetDock(toolbarBorder, Dock.Top)

        let column (title: string) (body: Control) =
            let head = muted title
            head.FontSize <- 11.0
            head.FontWeight <- FontWeight.SemiBold
            head.Margin <- Thickness(10.0, 4.0)
            let dock = DockPanel()
            DockPanel.SetDock(head, Dock.Top)
            dock.Children.Add head
            dock.Children.Add body
            dock

        let consoleDock = DockPanel()
        DockPanel.SetDock(dbgInput, Dock.Bottom)
        consoleDock.Children.Add dbgInput
        consoleDock.Children.Add dbgConsole

        let debugGrid = Grid()
        debugGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        debugGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength 1.0))
        debugGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.3, GridUnitType.Star)))
        debugGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength 1.0))
        debugGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.6, GridUnitType.Star)))
        let stackColumn = column "CALL STACK" dbgStack
        let varsColumn = column "VARIABLES" dbgVars
        let consoleColumn = column "DEBUG CONSOLE" consoleDock
        Grid.SetColumn(stackColumn, 0)
        Grid.SetColumn(varsColumn, 2)
        Grid.SetColumn(consoleColumn, 4)
        debugGrid.Children.Add stackColumn
        debugGrid.Children.Add varsColumn
        debugGrid.Children.Add consoleColumn

        for c in [ 1; 3 ] do
            let line = Border(Width = 1.0)
            themed (fun r -> line.Background <- r.Line)
            Grid.SetColumn(line, c)
            debugGrid.Children.Add line

        debugHost.Children.Add toolbarBorder
        debugHost.Children.Add debugGrid

        themed (fun r ->
            dbgConsole.Background <- r.EditorBg
            dbgConsole.Foreground <- r.Fg
            dbgInput.Background <- r.EditorBg
            dbgInput.Foreground <- r.Fg
            dbgInput.BorderBrush <- r.Line)

        headerButtons.Children.Add(tabButton "debug" "DEBUG" debugHost)

        dbgStack.SelectionChanged.Add(fun _ ->
            match dbgStack.SelectedItem with
            | :? TextBlock as t ->
                match t.Tag with
                | :? DapFrame as f -> selectFrame f
                | _ -> ()
            | _ -> ())

        dbgInput.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                let text = dbgInput.Text |> Option.ofObj |> Option.defaultValue ""
                dbgInput.Text <- ""
                evaluateInput text)

        let closePanel = flat "✕" "Close panel (Ctrl+J)" (fun () -> setPanelVisible false)
        DockPanel.SetDock(closePanel, Dock.Right)

        let clearBtn =
            flat "Clear" "Clear output and problems" (fun () ->
                setOutput ""
                ui.RunDiagnostics <- []
                refreshProblems ())

        DockPanel.SetDock(clearBtn, Dock.Right)
        let header = DockPanel()
        header.Children.Add closePanel
        header.Children.Add clearBtn
        header.Children.Add headerButtons

        let headerBorder =
            Border(Child = header, BorderThickness = Thickness(0.0, 1.0, 0.0, 1.0))

        themed (fun r ->
            headerBorder.Background <- r.Card
            headerBorder.BorderBrush <- r.Line)

        let panelContent = Grid()
        panelContent.Children.Add outputHost
        panelContent.Children.Add problemsHost
        panelContent.Children.Add referencesHost
        panelContent.Children.Add inputHost
        panelContent.Children.Add termHost
        panelContent.Children.Add debugHost
        let panelBody = DockPanel()
        DockPanel.SetDock(headerBorder, Dock.Top)
        panelBody.Children.Add headerBorder
        panelBody.Children.Add panelContent
        let panelBorderBox = panelBorder panelBody (fun r -> r.EditorBg)
        showPanel "output"

        problemsBox.SelectionChanged.Add(fun _ ->
            match problemsBox.SelectedItem with
            | :? StackPanel as row ->
                match row.Tag with
                | :? FSharpened.Diagnostics.StructuredDiagnostic as d ->
                    match d.Range with
                    | Some range ->
                        match relOfDiagnosticFile range.File with
                        | Some rel -> openFile rel range.StartLine range.StartColumn
                        | None -> say (sprintf "%s is not in the open folder" range.File)
                    | None -> ()
                | _ -> ()
            | _ -> ())

        let hsplit =
            GridSplitter(
                Height = 4.0,
                ResizeDirection = GridResizeDirection.Rows,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch
            )

        Access.describe "Resize bottom panel" "Drag, or press Up and Down, to change the panel's height." hsplit
        |> ignore

        mainGrid.RowDefinitions.Add(RowDefinition(Height = GridLength.Star))
        mainGrid.RowDefinitions.Add bottomSplitRow
        mainGrid.RowDefinitions.Add bottomRow
        Grid.SetRow(editorColumn, 0)
        Grid.SetRow(hsplit, 1)
        Grid.SetRow(panelBorderBox, 2)
        mainGrid.Children.Add editorColumn
        mainGrid.Children.Add hsplit
        mainGrid.Children.Add panelBorderBox

        bodyGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength 46.0))
        bodyGrid.ColumnDefinitions.Add sidebarColumn
        bodyGrid.ColumnDefinitions.Add splitColumn
        bodyGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        Grid.SetColumn(activityBorder, 0)
        Grid.SetColumn(sidebar, 1)
        Grid.SetColumn(vsplit, 2)
        Grid.SetColumn(mainGrid, 3)
        bodyGrid.Children.Add activityBorder
        bodyGrid.Children.Add sidebar
        bodyGrid.Children.Add vsplit
        bodyGrid.Children.Add mainGrid

        // palette overlay
        let paletteBody = StackPanel(Spacing = 6.0, Margin = Thickness 8.0)
        paletteBody.Children.Add paletteInput
        paletteBody.Children.Add paletteList
        palette.Child <- paletteBody

        themed (fun r ->
            palette.Background <- r.Card
            palette.BorderBrush <- r.Line
            restyleBox paletteInput r)

        paletteInput.TextChanged.Add(fun _ ->
            if palette.IsVisible then
                refreshPalette ())

        paletteInput.KeyDown.Add(fun e ->
            match e.Key with
            | Key.Escape ->
                e.Handled <- true
                hidePalette ()
            | Key.Down ->
                e.Handled <- true
                paletteList.SelectedIndex <- min (paletteList.Items.Count - 1) (paletteList.SelectedIndex + 1)
            | Key.Up ->
                e.Handled <- true
                paletteList.SelectedIndex <- max 0 (paletteList.SelectedIndex - 1)
            | Key.Enter ->
                e.Handled <- true
                let i = paletteList.SelectedIndex

                if i >= 0 && i < commandPalette.PaletteActions.Length then
                    commandPalette.PaletteActions[i]()
            | _ -> ())

        paletteList.DoubleTapped.Add(fun _ ->
            let i = paletteList.SelectedIndex

            if i >= 0 && i < commandPalette.PaletteActions.Length then
                commandPalette.PaletteActions[i]())

        // status bar: folder, branch and the latest message on the left (the message is cut with an ellipsis when it is
        // too long), problems, position and language on the right
        let status = Grid(Margin = Thickness(10.0, 0.0))
        status.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        status.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        let right = StackPanel(Orientation = Orientation.Horizontal, Spacing = 18.0)
        right.Children.Add statusProblems
        right.Children.Add ui.StatusCursors
        right.Children.Add statusPos
        right.Children.Add statusLang
        Grid.SetColumn(right, 1)
        let left = DockPanel(LastChildFill = true)
        statusFolder.Margin <- Thickness(0.0, 0.0, 18.0, 0.0)
        statusGit.Margin <- Thickness(0.0, 0.0, 18.0, 0.0)
        statusWatch.Margin <- Thickness(0.0, 0.0, 18.0, 0.0)
        statusRun.TextTrimming <- TextTrimming.CharacterEllipsis
        statusRun.Margin <- Thickness(0.0, 0.0, 18.0, 0.0)
        DockPanel.SetDock(statusFolder, Dock.Left)
        DockPanel.SetDock(statusGit, Dock.Left)
        DockPanel.SetDock(statusWatch, Dock.Left)
        left.Children.Add statusFolder
        left.Children.Add statusGit
        left.Children.Add statusWatch
        left.Children.Add statusRun
        Grid.SetColumn(left, 0)
        status.Children.Add left
        status.Children.Add right
        let statusBorder = Border(Child = status, Height = 24.0)

        Access.describe
            "Status bar"
            "The open folder, Git branch, the latest message, problems, position and language."
            statusBorder
        |> Access.role AutomationControlType.StatusBar
        |> ignore

        themed (fun r ->
            statusBorder.Background <- r.Brand

            for t in
                [ statusFolder
                  statusGit
                  statusWatch
                  statusRun
                  ui.StatusCursors
                  statusPos
                  statusLang
                  statusProblems ] do
                t.Foreground <- r.BrandFg
                t.FontSize <- 12.0)

        // the whole window
        let menu = buildMenu ()
        let content = Grid()
        content.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        content.RowDefinitions.Add(RowDefinition(Height = GridLength.Star))
        content.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        Grid.SetRow(menu, 0)
        Grid.SetRow(bodyGrid, 1)
        Grid.SetRow(statusBorder, 2)
        content.Children.Add menu
        content.Children.Add bodyGrid
        content.Children.Add statusBorder

        let overlay = Grid()
        overlay.Children.Add content
        overlay.Children.Add palette
        overlay


    member _.Layout = layout
