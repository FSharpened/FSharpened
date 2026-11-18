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
        editorFeatures: EditorFeatures,
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
            Run = openFolderDialog }
          { Id = "new-file"
            Title = "File: New File…"
            Run = (fun () -> newEntry "" false) }
          { Id = "save"
            Title = "File: Save"
            Run =
              (fun () ->
                  ui.Active
                  |> Option.iter (fun d ->
                      saveDoc d |> ignore
                      rebuildTabs ())) }
          { Id = "save-all"
            Title = "File: Save All"
            Run = saveAll }
          { Id = "close-tab"
            Title = "View: Close Editor"
            Run = (fun () -> ui.Active |> Option.iter closeDoc) }
          { Id = "next-tab"
            Title = "View: Next Editor"
            Run = (fun () -> cycleTab 1) }
          { Id = "previous-tab"
            Title = "View: Previous Editor"
            Run = (fun () -> cycleTab -1) }
          { Id = "focus-editor"
            Title = "View: Focus Editor"
            Run = (fun () -> editor.Focus() |> ignore) }
          { Id = "focus-explorer"
            Title = "View: Focus Explorer"
            Run =
              (fun () ->
                  showSidebar "files"
                  fileTree.Focus() |> ignore) }
          { Id = "close-folder"
            Title = "File: Close Folder"
            Run = closeFolder }
          { Id = "run"
            Title = "Run: Run This File"
            Run = (fun () -> runCurrent Runner.defaultTimeoutSec) }
          { Id = "debug-start"
            Title = "Debug: Start Debugging"
            Run = debugStartOrContinue }
          { Id = "debug-continue"
            Title = "Debug: Continue"
            Run = debugContinue }
          { Id = "debug-step-over"
            Title = "Debug: Step Over"
            Run = (fun () -> debugStep "Step over" (fun c t -> c.Next t)) }
          { Id = "debug-step-into"
            Title = "Debug: Step Into"
            Run = (fun () -> debugStep "Step into" (fun c t -> c.StepIn t)) }
          { Id = "debug-step-out"
            Title = "Debug: Step Out"
            Run = (fun () -> debugStep "Step out" (fun c t -> c.StepOut t)) }
          { Id = "debug-pause"
            Title = "Debug: Pause"
            Run = debugPause }
          { Id = "debug-stop"
            Title = "Debug: Stop Debugging"
            Run = debugStop }
          { Id = "toggle-breakpoint"
            Title = "Debug: Toggle Breakpoint"
            Run = toggleBreakpoint }
          { Id = "debug-panel"
            Title = "Debug: Show Debug Panel"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "debug") }
          { Id = "rename-symbol"
            Title = "Rename Symbol (language server)"
            Run = renameSymbolNow }
          { Id = "git-view"
            Title = "Git: Show Source Control"
            Run = (fun () -> showSidebar "git") }
          { Id = "git-stage-file"
            Title = "Git: Stage Current File"
            Run = gitFeature.StageFile }
          { Id = "git-unstage-file"
            Title = "Git: Unstage Current File"
            Run = gitFeature.UnstageFile }
          { Id = "git-commit"
            Title = "Git: Commit Staged Changes"
            Run =
              (fun () ->
                  showSidebar "git"
                  gitFeature.Commit()) }
          { Id = "git-switch-branch"
            Title = "Git: Switch Branch…"
            Run = gitFeature.SwitchBranch }
          { Id = "git-new-branch"
            Title = "Git: Create New Branch…"
            Run = gitFeature.NewBranch }
          { Id = "git-blame"
            Title = "Git: Toggle Blame"
            Run = gitFeature.ToggleBlame }
          { Id = "git-refresh"
            Title = "Git: Refresh"
            Run = gitFeature.Refresh }
          { Id = "trust-folder"
            Title = "Run: Trust This Folder…"
            Run = gitFeature.TrustFolder }
          { Id = "run-long"
            Title = "Run: Run This File (120 s limit)"
            Run = (fun () -> runCurrent Runner.maxTimeoutSec) }
          { Id = "show-hover"
            Title = "Editor: Show Hover"
            Run = showHoverAtCaret }
          { Id = "format"
            Title = "Format Document"
            Run = formatCurrent }
          { Id = "complete"
            Title = "Trigger Suggest (language server)"
            Run = completeNow }
          { Id = "definition"
            Title = "Go to Definition (language server)"
            Run = gotoDefinitionNow }
          { Id = "references"
            Title = "Find All References (language server)"
            Run = serverFeatures.FindReferences }
          { Id = "signature-help"
            Title = "Show Signature Help (language server)"
            Run = serverFeatures.ShowSignatureHelp }
          { Id = "quick-fix"
            Title = "Quick Fix… (language server)"
            Run = serverFeatures.QuickFix }
          { Id = "cursor-below"
            Title = "Cursors: Add Cursor Below"
            Run = editorFeatures.Cursors.AddCursorBelow }
          { Id = "cursor-above"
            Title = "Cursors: Add Cursor Above"
            Run = editorFeatures.Cursors.AddCursorAbove }
          { Id = "select-next-match"
            Title = "Cursors: Add Selection to Next Match"
            Run = editorFeatures.Cursors.SelectNextMatch }
          { Id = "select-all-matches"
            Title = "Cursors: Select All Matches"
            Run = editorFeatures.Cursors.SelectAllMatches }
          { Id = "clear-cursors"
            Title = "Cursors: Back to One Cursor"
            Run = editorFeatures.Cursors.Clear }
          { Id = "insert-snippet"
            Title = "Snippets: Insert Snippet…"
            Run = (fun () -> ui.ShowPaletteFn "$") }
          { Id = "reload-snippets"
            Title = "Snippets: Reload My Snippets"
            Run = editorFeatures.Snippets.Reload }
          { Id = "snippets-folder"
            Title = "Snippets: Open My Snippets Folder"
            Run =
              (fun () ->
                  Directory.CreateDirectory env.UserSnippets |> ignore

                  window.Launcher.LaunchDirectoryInfoAsync(DirectoryInfo env.UserSnippets)
                  |> ignore) }
          { Id = "keybindings"
            Title = "Preferences: Keyboard Shortcuts"
            Run = (fun () -> showSidebar "keys") }
          { Id = "references-panel"
            Title = "View: Show References"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "references") }
          { Id = "quick-open"
            Title = "Go to File…"
            Run = (fun () -> ui.ShowPaletteFn "") }
          { Id = "goto-line"
            Title = "Go to Line…"
            Run = (fun () -> ui.ShowPaletteFn ":") }
          { Id = "goto-symbol"
            Title = "Go to Symbol in Editor…"
            Run = (fun () -> ui.ShowPaletteFn "@") }
          { Id = "palette"
            Title = "Show All Commands"
            Run = (fun () -> ui.ShowPaletteFn ">") }
          { Id = "find"
            Title = "Find in File"
            Run =
              (fun () ->
                  searchPanel.Open()
                  ()) }
          { Id = "find-files"
            Title = "Search: Find in Files"
            Run = (fun () -> showSidebar "search") }
          { Id = "replace-in-file"
            Title = "Edit: Replace in This File"
            Run =
              (fun () ->
                  searchPanel.IsReplaceMode <- true
                  searchPanel.Open()) }
          { Id = "replace-in-files"
            Title = "Search: Replace in Files"
            Run =
              (fun () ->
                  showSidebar "search"
                  replaceBox.Focus() |> ignore) }
          { Id = "terminal"
            Title = "Terminal: Open"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "terminal"
                  termView.Focus() |> ignore) }
          { Id = "terminal-restart"
            Title = "Terminal: Restart"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "terminal"
                  restartTerminal ()) }
          { Id = "terminal-clear"
            Title = "Terminal: Clear"
            Run = (fun () -> termView.Clear()) }
          { Id = "explorer"
            Title = "View: Show Explorer"
            Run = (fun () -> showSidebar "files") }
          { Id = "outline"
            Title = "View: Show Outline"
            Run = (fun () -> showSidebar "outline") }
          { Id = "tools"
            Title = "View: Show Tools and Languages"
            Run = (fun () -> showSidebar "tools") }
          { Id = "toggle-sidebar"
            Title = "View: Toggle Side Bar"
            Run = (fun () -> setSidebarVisible (not ui.SidebarShown)) }
          { Id = "toggle-panel"
            Title = "View: Toggle Panel"
            Run = (fun () -> setPanelVisible (not ui.PanelShown)) }
          { Id = "problems"
            Title = "View: Show Problems"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "problems") }
          { Id = "output"
            Title = "View: Show Output"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "output") }
          { Id = "zoom-in"
            Title = "View: Increase Font Size"
            Run = (fun () -> setFont 1) }
          { Id = "zoom-out"
            Title = "View: Decrease Font Size"
            Run = (fun () -> setFont -1) }
          { Id = "wrap"
            Title = "View: Toggle Word Wrap"
            Run = (fun () -> toggle (fun e -> e.WordWrap) (fun e v -> { e with WordWrap = v })) }
          { Id = "whitespace"
            Title = "View: Toggle Render Whitespace"
            Run = (fun () -> toggle (fun e -> e.RenderWhitespace) (fun e v -> { e with RenderWhitespace = v })) }
          { Id = "line-numbers"
            Title = "View: Toggle Line Numbers"
            Run = (fun () -> toggle (fun e -> e.LineNumbers) (fun e v -> { e with LineNumbers = v })) }
          { Id = "breadcrumbs"
            Title = "View: Toggle Breadcrumbs"
            Run = (fun () -> toggle (fun e -> e.Breadcrumbs) (fun e v -> { e with Breadcrumbs = v })) }
          { Id = "refresh-tools"
            Title = "Tools: Refresh Installed Tools"
            Run = (fun () -> showSidebar "tools") }
          { Id = "reload-packs"
            Title = "Languages: Reload Language Packs"
            Run =
              (fun () ->
                  env.ReloadLanguages()
                  showSidebar "tools") }
          { Id = "packs-folder"
            Title = "Languages: Open My Language Packs Folder"
            Run =
              (fun () ->
                  Directory.CreateDirectory env.UserPacks |> ignore
                  window.Launcher.LaunchDirectoryInfoAsync(DirectoryInfo env.UserPacks) |> ignore) }
          { Id = "revoke-trust"
            Title = "Run: Revoke Trust for This Folder"
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
                  Run = (fun () -> applyTheme t) } ]


    member _.Commands = commands
