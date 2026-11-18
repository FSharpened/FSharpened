/// The command list: every action the palette, the menus and the keys can run.
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

type Commands
    (
        ui: Ui,
        appearance: Appearance,
        terminalPanel: TerminalPanel,
        languageServers: LanguageServers,
        hoverPanel: HoverPanel,
        editorTabs: EditorTabs,
        runPanel: RunPanel,
        renameFeature: RenameFeature,
        serverFeatures: ServerFeatures,
        gitFeature: GitFeature,
        debugPanel: DebugPanel,
        explorer: Explorer,
        sidebarViews: SidebarViews,
        folderSession: FolderSession
    ) =
    let applyEditorPrefs = appearance.ApplyEditorPrefs
    let applyTheme = appearance.ApplyTheme
    let closeDoc = editorTabs.CloseDoc
    let closeFolder = folderSession.CloseFolder
    let completeNow = languageServers.CompleteNow
    let debugContinue = debugPanel.DebugContinue
    let debugPause = debugPanel.DebugPause
    let debugStartOrContinue = debugPanel.DebugStartOrContinue
    let debugStep = debugPanel.DebugStep
    let debugStop = debugPanel.DebugStop
    let env = ui.Env
    let formatCurrent = serverFeatures.FormatDocument
    let gotoDefinitionNow = languageServers.GotoDefinitionNow
    let newEntry = explorer.NewEntry
    let openFolderDialog = folderSession.OpenFolderDialog
    let rebuildTabs = editorTabs.RebuildTabs
    let activate = editorTabs.Activate
    let docs = ui.Docs
    let editor = ui.Editor
    let fileTree = ui.FileTree
    let renameSymbolNow = renameFeature.RenameSymbolNow
    let replaceBox = sidebarViews.ReplaceBox
    let restartTerminal = terminalPanel.RestartTerminal
    let runCurrent = runPanel.RunCurrent
    let saveAll = editorTabs.SaveAll
    let saveDoc = editorTabs.SaveDoc
    let savePrefs = appearance.SavePrefs
    let say = ui.Say
    let searchPanel = ui.SearchPanel
    let setPanelVisible = appearance.SetPanelVisible
    let setSidebarVisible = appearance.SetSidebarVisible
    let showHoverAtCaret = hoverPanel.ShowHoverAtCaret
    let showPanel = appearance.ShowPanel
    let showSidebar = sidebarViews.ShowSidebar
    let termView = ui.TermView
    let toggleBreakpoint = debugPanel.ToggleBreakpoint
    let window = ui.Window

    // ---- command palette ---------------------------------------------------------------------------
    let setFont (delta: int) =
        ui.Prefs <-
            { ui.Prefs with
                Editor =
                    Settings.sanitize
                        { ui.Prefs.Editor with
                            FontSize = ui.Prefs.Editor.FontSize + delta } }

        applyEditorPrefs ()
        savePrefs ()

    let toggle (pick: EditorSettings -> bool) (set: EditorSettings -> bool -> EditorSettings) =
        ui.Prefs <-
            { ui.Prefs with
                Editor = set ui.Prefs.Editor (not (pick ui.Prefs.Editor)) }

        applyEditorPrefs ()
        savePrefs ()

    /// The next (or, with a negative step, the previous) open file, wrapping round.
    let cycleTab (step: int) =
        if docs.Count > 1 then
            let i =
                match ui.Active with
                | Some a -> docs.IndexOf a
                | None -> 0

            activate (Some docs[(i + step + docs.Count) % docs.Count])

    let commands: Command list =
        [ { Id = "open-folder"
            Title = "File: Open Folder…"
            Keys = "Ctrl+O"
            Run = openFolderDialog }
          { Id = "new-file"
            Title = "File: New File…"
            Keys = "Ctrl+N"
            Run = (fun () -> newEntry "" false) }
          { Id = "save"
            Title = "File: Save"
            Keys = "Ctrl+S"
            Run =
              (fun () ->
                  ui.Active
                  |> Option.iter (fun d ->
                      saveDoc d |> ignore
                      rebuildTabs ())) }
          { Id = "save-all"
            Title = "File: Save All"
            Keys = "Ctrl+Shift+S"
            Run = saveAll }
          { Id = "close-tab"
            Title = "View: Close Editor"
            Keys = "Ctrl+W"
            Run = (fun () -> ui.Active |> Option.iter closeDoc) }
          { Id = "next-tab"
            Title = "View: Next Editor"
            Keys = "Ctrl+PageDown"
            Run = (fun () -> cycleTab 1) }
          { Id = "previous-tab"
            Title = "View: Previous Editor"
            Keys = "Ctrl+PageUp"
            Run = (fun () -> cycleTab -1) }
          { Id = "focus-editor"
            Title = "View: Focus Editor"
            Keys = "Ctrl+1"
            Run = (fun () -> editor.Focus() |> ignore) }
          { Id = "focus-explorer"
            Title = "View: Focus Explorer"
            Keys = "Ctrl+0"
            Run =
              (fun () ->
                  showSidebar "files"
                  fileTree.Focus() |> ignore) }
          { Id = "close-folder"
            Title = "File: Close Folder"
            Keys = ""
            Run = closeFolder }
          { Id = "run"
            Title = "Run: Run This File"
            Keys = "Ctrl+F5"
            Run = (fun () -> runCurrent Runner.defaultTimeoutSec) }
          { Id = "debug-start"
            Title = "Debug: Start Debugging"
            Keys = "F5"
            Run = debugStartOrContinue }
          { Id = "debug-continue"
            Title = "Debug: Continue"
            Keys = ""
            Run = debugContinue }
          { Id = "debug-step-over"
            Title = "Debug: Step Over"
            Keys = "F10"
            Run = (fun () -> debugStep "Step over" (fun c t -> c.Next t)) }
          { Id = "debug-step-into"
            Title = "Debug: Step Into"
            Keys = "F11"
            Run = (fun () -> debugStep "Step into" (fun c t -> c.StepIn t)) }
          { Id = "debug-step-out"
            Title = "Debug: Step Out"
            Keys = "Shift+F11"
            Run = (fun () -> debugStep "Step out" (fun c t -> c.StepOut t)) }
          { Id = "debug-pause"
            Title = "Debug: Pause"
            Keys = ""
            Run = debugPause }
          { Id = "debug-stop"
            Title = "Debug: Stop Debugging"
            Keys = "Shift+F5"
            Run = debugStop }
          { Id = "toggle-breakpoint"
            Title = "Debug: Toggle Breakpoint"
            Keys = "F9"
            Run = toggleBreakpoint }
          { Id = "debug-panel"
            Title = "Debug: Show Debug Panel"
            Keys = ""
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "debug") }
          { Id = "rename-symbol"
            Title = "Rename Symbol (language server)"
            Keys = "F2"
            Run = renameSymbolNow }
          { Id = "git-view"
            Title = "Git: Show Source Control"
            Keys = "Ctrl+Shift+G"
            Run = (fun () -> showSidebar "git") }
          { Id = "git-stage-file"
            Title = "Git: Stage Current File"
            Keys = ""
            Run = gitFeature.StageFile }
          { Id = "git-unstage-file"
            Title = "Git: Unstage Current File"
            Keys = ""
            Run = gitFeature.UnstageFile }
          { Id = "git-commit"
            Title = "Git: Commit Staged Changes"
            Keys = ""
            Run =
              (fun () ->
                  showSidebar "git"
                  gitFeature.Commit()) }
          { Id = "git-switch-branch"
            Title = "Git: Switch Branch…"
            Keys = ""
            Run = gitFeature.SwitchBranch }
          { Id = "git-new-branch"
            Title = "Git: Create New Branch…"
            Keys = ""
            Run = gitFeature.NewBranch }
          { Id = "git-blame"
            Title = "Git: Toggle Blame"
            Keys = ""
            Run = gitFeature.ToggleBlame }
          { Id = "git-refresh"
            Title = "Git: Refresh"
            Keys = ""
            Run = gitFeature.Refresh }
          { Id = "trust-folder"
            Title = "Run: Trust This Folder…"
            Keys = ""
            Run = gitFeature.TrustFolder }
          { Id = "run-long"
            Title = "Run: Run This File (120 s limit)"
            Keys = ""
            Run = (fun () -> runCurrent Runner.maxTimeoutSec) }
          { Id = "show-hover"
            Title = "Editor: Show Hover"
            Keys = ""
            Run = showHoverAtCaret }
          { Id = "format"
            Title = "Format Document"
            Keys = "Shift+Alt+F"
            Run = formatCurrent }
          { Id = "complete"
            Title = "Trigger Suggest (language server)"
            Keys = "Ctrl+Space"
            Run = completeNow }
          { Id = "definition"
            Title = "Go to Definition (language server)"
            Keys = "F12"
            Run = gotoDefinitionNow }
          { Id = "references"
            Title = "Find All References (language server)"
            Keys = "Shift+F12"
            Run = serverFeatures.FindReferences }
          { Id = "signature-help"
            Title = "Show Signature Help (language server)"
            Keys = "Ctrl+Shift+Space"
            Run = serverFeatures.ShowSignatureHelp }
          { Id = "quick-fix"
            Title = "Quick Fix… (language server)"
            Keys = "Ctrl+."
            Run = serverFeatures.QuickFix }
          { Id = "references-panel"
            Title = "View: Show References"
            Keys = ""
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "references") }
          { Id = "quick-open"
            Title = "Go to File…"
            Keys = "Ctrl+P"
            Run = (fun () -> ui.ShowPaletteFn "") }
          { Id = "goto-line"
            Title = "Go to Line…"
            Keys = "Ctrl+G"
            Run = (fun () -> ui.ShowPaletteFn ":") }
          { Id = "goto-symbol"
            Title = "Go to Symbol in Editor…"
            Keys = "Ctrl+Shift+O"
            Run = (fun () -> ui.ShowPaletteFn "@") }
          { Id = "palette"
            Title = "Show All Commands"
            Keys = "Ctrl+Shift+P"
            Run = (fun () -> ui.ShowPaletteFn ">") }
          { Id = "find"
            Title = "Find in File"
            Keys = "Ctrl+F"
            Run =
              (fun () ->
                  searchPanel.Open()
                  ()) }
          { Id = "find-files"
            Title = "Search: Find in Files"
            Keys = "Ctrl+Shift+F"
            Run = (fun () -> showSidebar "search") }
          { Id = "replace-in-file"
            Title = "Edit: Replace in This File"
            Keys = "Ctrl+H"
            Run =
              (fun () ->
                  searchPanel.IsReplaceMode <- true
                  searchPanel.Open()) }
          { Id = "replace-in-files"
            Title = "Search: Replace in Files"
            Keys = "Ctrl+Shift+H"
            Run =
              (fun () ->
                  showSidebar "search"
                  replaceBox.Focus() |> ignore) }
          { Id = "terminal"
            Title = "Terminal: Open"
            Keys = "Ctrl+`"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "terminal"
                  termView.Focus() |> ignore) }
          { Id = "terminal-restart"
            Title = "Terminal: Restart"
            Keys = ""
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "terminal"
                  restartTerminal ()) }
          { Id = "terminal-clear"
            Title = "Terminal: Clear"
            Keys = ""
            Run = (fun () -> termView.Clear()) }
          { Id = "explorer"
            Title = "View: Show Explorer"
            Keys = "Ctrl+Shift+E"
            Run = (fun () -> showSidebar "files") }
          { Id = "outline"
            Title = "View: Show Outline"
            Keys = ""
            Run = (fun () -> showSidebar "outline") }
          { Id = "tools"
            Title = "View: Show Tools and Languages"
            Keys = ""
            Run = (fun () -> showSidebar "tools") }
          { Id = "toggle-sidebar"
            Title = "View: Toggle Side Bar"
            Keys = "Ctrl+B"
            Run = (fun () -> setSidebarVisible (not ui.SidebarShown)) }
          { Id = "toggle-panel"
            Title = "View: Toggle Panel"
            Keys = "Ctrl+J"
            Run = (fun () -> setPanelVisible (not ui.PanelShown)) }
          { Id = "problems"
            Title = "View: Show Problems"
            Keys = "Ctrl+Shift+M"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "problems") }
          { Id = "output"
            Title = "View: Show Output"
            Keys = ""
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "output") }
          { Id = "zoom-in"
            Title = "View: Increase Font Size"
            Keys = "Ctrl+="
            Run = (fun () -> setFont 1) }
          { Id = "zoom-out"
            Title = "View: Decrease Font Size"
            Keys = "Ctrl+-"
            Run = (fun () -> setFont -1) }
          { Id = "wrap"
            Title = "View: Toggle Word Wrap"
            Keys = "Alt+Z"
            Run = (fun () -> toggle (fun e -> e.WordWrap) (fun e v -> { e with WordWrap = v })) }
          { Id = "whitespace"
            Title = "View: Toggle Render Whitespace"
            Keys = ""
            Run = (fun () -> toggle (fun e -> e.RenderWhitespace) (fun e v -> { e with RenderWhitespace = v })) }
          { Id = "line-numbers"
            Title = "View: Toggle Line Numbers"
            Keys = ""
            Run = (fun () -> toggle (fun e -> e.LineNumbers) (fun e v -> { e with LineNumbers = v })) }
          { Id = "breadcrumbs"
            Title = "View: Toggle Breadcrumbs"
            Keys = ""
            Run = (fun () -> toggle (fun e -> e.Breadcrumbs) (fun e v -> { e with Breadcrumbs = v })) }
          { Id = "refresh-tools"
            Title = "Tools: Refresh Installed Tools"
            Keys = ""
            Run = (fun () -> showSidebar "tools") }
          { Id = "reload-packs"
            Title = "Languages: Reload Language Packs"
            Keys = ""
            Run =
              (fun () ->
                  env.ReloadLanguages()
                  showSidebar "tools") }
          { Id = "packs-folder"
            Title = "Languages: Open My Language Packs Folder"
            Keys = ""
            Run =
              (fun () ->
                  Directory.CreateDirectory env.UserPacks |> ignore
                  window.Launcher.LaunchDirectoryInfoAsync(DirectoryInfo env.UserPacks) |> ignore) }
          { Id = "revoke-trust"
            Title = "Run: Revoke Trust for This Folder"
            Keys = ""
            Run =
              (fun () ->
                  match ui.Root with
                  | Some r ->
                      env.Trust.Revoke r
                      say "Trust revoked: running code here will ask again"
                  | None -> ()) } ]
        @ [ for t in Palette.choices ->
                { Id = "theme-" + t.Id
                  Title = "Color Theme: " + t.Name
                  Keys = ""
                  Run = (fun () -> applyTheme t) } ]


    member _.Commands = commands
