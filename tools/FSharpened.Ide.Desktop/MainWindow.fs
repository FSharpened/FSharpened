/// The IDE window: the website IDE's layout (activity bar, sidebar, tabs, editor, panel, status bar, command palette) as
/// native controls over the same F# core. Imperative on purpose: one place that builds the tree and one that reacts.
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

type Shell(env: HostEnv) =
    let ui = Ui(env)
    let appearance = Appearance(ui)
    let statusBar = StatusBar(ui)
    let problems = Problems(ui)
    let terminalPanel = TerminalPanel(ui)
    let languageServers = LanguageServers(ui, problems, terminalPanel)
    let hoverPanel = HoverPanel(ui, problems, languageServers)

    let editorTabs =
        EditorTabs(ui, appearance, statusBar, problems, languageServers, hoverPanel)

    let gitFeature = GitFeature(ui, statusBar, editorTabs)
    let runPanel = RunPanel(ui, appearance, problems, languageServers, editorTabs)
    let renameFeature = RenameFeature(ui, languageServers, editorTabs)

    let serverFeatures =
        ServerFeatures(ui, appearance, statusBar, languageServers, runPanel, renameFeature)

    let debugPanel =
        DebugPanel(ui, appearance, languageServers, editorTabs, renameFeature)

    let explorer = Explorer(ui, editorTabs)
    let sidebarViews = SidebarViews(ui, appearance, statusBar, editorTabs)

    let folderSession =
        FolderSession(ui, appearance, statusBar, problems, languageServers, editorTabs, explorer, sidebarViews)

    let commands =
        Commands(
            ui,
            appearance,
            terminalPanel,
            languageServers,
            hoverPanel,
            editorTabs,
            runPanel,
            renameFeature,
            serverFeatures,
            gitFeature,
            debugPanel,
            explorer,
            sidebarViews,
            folderSession
        )

    let commandPalette = CommandPalette(ui, commands)
    let menus = Menus(ui, appearance, sidebarViews, folderSession, commands)

    let layout =
        Layout(
            ui,
            appearance,
            problems,
            hoverPanel,
            editorTabs,
            runPanel,
            debugPanel,
            folderSession,
            commandPalette,
            menus
        )

    let wiring =
        Wiring(
            ui,
            appearance,
            statusBar,
            languageServers,
            hoverPanel,
            editorTabs,
            runPanel,
            renameFeature,
            debugPanel,
            explorer,
            sidebarViews,
            folderSession,
            commandPalette,
            menus,
            layout
        )

    let allBreakpoints = debugPanel.AllBreakpoints
    let applyTheme = appearance.ApplyTheme
    let bpLinesOf = debugPanel.BpLinesOf
    let breakpointLines = debugPanel.BreakpointLines
    let closedBreakpoints = debugPanel.ClosedBreakpoints
    let command = menus.Command
    let docs = ui.Docs
    let editor = ui.Editor
    let env = ui.Env
    let openFile = editorTabs.OpenFile
    let openFolder = folderSession.OpenFolder
    let registryOptions = ui.RegistryOptions
    let replaceBox = sidebarViews.ReplaceBox
    let runSearch = sidebarViews.RunSearch
    let say = ui.Say
    let searchBox = sidebarViews.SearchBox
    let sendTerminalLine = terminalPanel.SendTerminalLine
    let showSidebar = sidebarViews.ShowSidebar
    let toggleBreakpointAt = debugPanel.ToggleBreakpointAt
    let window = ui.Window

    member _.Window = window

    /// The ids of every command (the palette, the menus and the keys run these).
    member _.CommandIds = commands.Commands |> List.map (fun c -> c.Id)

    /// The editor's find and replace panel (for the tests).
    member _.SearchPanel = ui.SearchPanel
    member _.Env = env
    member _.Editor = editor
    member _.OpenFolder(path: string) = openFolder path
    member _.OpenFile(rel: string, line: int, col: int) = openFile rel line col
    member _.Run(id: string) = (command id).Run()
    member _.Palette(text: string) = ui.ShowPaletteFn text
    /// Sends a line to the terminal's shell (for the screenshot switches).
    member _.TerminalSend(line: string) = sendTerminalLine line

    /// Fills the search panel and runs the search (for the screenshot switches and the tests).
    member _.Search(query: string, replacement: string) =
        showSidebar "search"
        searchBox.Text <- query
        replaceBox.Text <- replacement
        runSearch ()

    /// Sets a breakpoint (1-based line) in a file of the folder, for the screenshot switches and the tests.
    member _.SetBreakpoint(rel: string, line: int) =
        match docs |> Seq.tryFind (fun d -> d.Rel = rel) with
        | Some d ->
            if not (List.contains line (bpLinesOf d)) then
                toggleBreakpointAt d line
        | None -> closedBreakpoints[rel] <- (Breakpoints.toggle (breakpointLines rel) line)

    /// Puts the caret on a line and column (1-based) of the active file.
    member _.SetCaret(line: int, col: int) =
        editor.TextArea.Caret.Line <- line
        editor.TextArea.Caret.Column <- col
        editor.ScrollToLine line

    /// The new name `rename-symbol` uses without asking, for the screenshot switches.
    member _.RenameTo
        with get () = renameFeature.RenameTo
        and set (v: string option) = renameFeature.RenameTo <- v

    /// Git's gutter, blame and actions, for the tests and the screenshot switches.
    member _.Git = gitFeature

    /// The quick fixes the server offered at the caret, as (title, can be applied), and applying one by its title:
    /// for the tests and the screenshot switches.
    member _.OfferedFixes = serverFeatures.OfferedFixes
    member _.RefreshFixes() = serverFeatures.RefreshFixes()
    member _.ApplyFix(title: string) = serverFeatures.ApplyFix title

    /// The breakpoints now set, as (file, lines).
    member _.Breakpoints = allBreakpoints ()

    member _.Say = say
    member _.TextMateReady = registryOptions
    member _.ThemeName = ui.Theme.Id
    member _.SetTheme(id: string) = applyTheme (Palette.find id)

    /// What the editor looks like right now, for the screenshot check.
    member _.Diagnostics() =
        let tv = editor.TextArea.TextView

        sprintf
            "watch=%s search=%s editor bounds=%A visible=%b docLen=%d textView bounds=%A visualLines=%b fg=%A bg=%A fontSize=%f lineNumbers=%b"
            (match ui.Watcher with
             | Some w -> FolderWatch.engineLabel w.Engine
             | None -> "none")
            ui.LastSearchEngine
            editor.Bounds
            editor.IsVisible
            editor.Document.TextLength
            tv.Bounds
            tv.VisualLinesValid
            editor.Foreground
            editor.Background
            editor.FontSize
            editor.ShowLineNumbers

    member _.ForceQuit() =
        ui.Quitting <- true
        window.Close()
