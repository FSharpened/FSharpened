/// Opening and closing a folder, and following changes made outside the IDE.
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

type FolderSession
    (
        ui: Ui,
        appearance: Appearance,
        statusBar: StatusBar,
        problems: Problems,
        languageServers: LanguageServers,
        editorTabs: EditorTabs,
        explorer: Explorer,
        sidebarViews: SidebarViews
    ) =
    let closeNow = editorTabs.CloseNow
    let docs = ui.Docs
    let env = ui.Env
    let expanded = ui.Expanded
    let rebuildTabs = editorTabs.RebuildTabs
    let refreshFileCache = explorer.RefreshFileCache
    let refreshGit = statusBar.RefreshGit
    let refreshProblems = problems.RefreshProblems
    let refreshTree = explorer.RefreshTree
    let rootName = appearance.RootName
    let savePrefs = appearance.SavePrefs
    let say = ui.Say
    let setOutput = ui.SetOutput
    let showSidebar = sidebarViews.ShowSidebar
    let statusFolder = ui.StatusFolder
    let statusWatch = ui.StatusWatch
    let stopServers = languageServers.StopServers
    let window = ui.Window

    // ---- folder -----------------------------------------------------------------------------------
    let conflictOpen = HashSet<string>()

    /// A file with unsaved edits changed on disk: ask which side wins. Keeping the editor's version is remembered for
    /// that disk text, so the same outside change does not ask again.
    let askConflict (r: string) (d: OpenDoc) (diskText: string) =
        if conflictOpen.Add d.Rel then
            task {
                let! reload =
                    Dialogs.confirm
                        window
                        ui.Roles
                        "File changed on disk"
                        (sprintf
                            "“%s” was changed outside the IDE, and it has unsaved edits here.\n\nReload it from disk and lose the edits, or keep the editor's version (saving then overwrites the disk)?"
                            (Path.GetFileName d.Rel))
                        "Reload from disk"

                conflictOpen.Remove d.Rel |> ignore

                if reload && docs.Contains d then
                    match DiskWorkspace.readText r d.Rel with
                    | Ok t ->
                        d.Reload t
                        d.LspDirty <- true
                        d.KeptOverDisk <- None
                        d.MissingOnDisk <- false
                        say (sprintf "Reloaded %s from disk" (Path.GetFileName d.Rel))
                    | Error _ -> ()
                else
                    d.KeptOverDisk <- Some diskText

                rebuildTabs ()
            }
            |> ignore

    /// Brings the open files in line with the disk: an unmodified one is reloaded, a modified one asks, a deleted one
    /// stays open and is marked. `paths` limits the look to those files; None looks at every open file.
    let syncOpenFiles (paths: Set<string> option) =
        match ui.Root with
        | Some r ->
            let mutable reloaded = []
            let mutable touched = false

            for d in docs |> Seq.toList do
                if paths.IsNone || paths.Value.Contains d.Rel then
                    match DiskWorkspace.resolve r d.Rel with
                    | Ok full when not (File.Exists full) ->
                        if not d.MissingOnDisk then
                            d.MissingOnDisk <- true
                            touched <- true
                            say (sprintf "%s was deleted or moved on disk" (Path.GetFileName d.Rel))
                    | _ ->
                        if d.MissingOnDisk then
                            d.MissingOnDisk <- false
                            touched <- true

                        match DiskWorkspace.readText r d.Rel with
                        | Ok t when not d.Dirty && t <> d.Text ->
                            d.Reload t
                            d.LspDirty <- true
                            reloaded <- Path.GetFileName d.Rel :: reloaded
                        | Ok t when d.Dirty && t <> d.DiskText && d.KeptOverDisk <> Some t -> askConflict r d t
                        | _ -> ()

            if not reloaded.IsEmpty then
                say (sprintf "Reloaded %s from disk" (String.Join(", ", List.rev reloaded)))

            if touched || not reloaded.IsEmpty then
                rebuildTabs ()
        | None -> ()

    /// Something changed on disk that the IDE did not do itself: refresh the explorer, git and quick open, and the
    /// open files it touches.
    let onDiskChanged (r: string) (change: DiskChange) =
        if ui.Root = Some r then
            match change with
            | SomePaths paths ->
                if paths.Count = 1 then
                    say (sprintf "%s changed on disk" (Seq.head paths))
                else
                    say (sprintf "%d files changed on disk" paths.Count)

                syncOpenFiles (Some paths)
            | Everything ->
                say "Many files changed on disk"
                syncOpenFiles None

            refreshGit ()
            refreshFileCache ()

    let stopWatching () =
        ui.WatchSub |> Option.iter (fun s -> s.Dispose())
        ui.WatchSub <- None

        match ui.Watcher with
        | Some w ->
            w.Watcher.Dispose()
            ui.Watcher <- None
        | None -> ()

        statusWatch.Text <- ""

    let startWatching (r: string) =
        stopWatching ()

        try
            let running = FolderWatch.start true r
            ui.Watcher <- Some running

            ui.WatchSub <-
                Some(
                    FolderWatch.subscribe running (fun change ->
                        Dispatcher.UIThread.Post(fun () -> onDiskChanged r change))
                )

            statusWatch.Text <-
                match running.Engine with
                | NativeWatch -> "👁 native"
                | PollingWatch _ -> "👁 polling"

            ToolTip.SetTip(
                statusWatch,
                "Watching the folder for changes made outside the IDE: "
                + FolderWatch.engineLabel running.Engine
            )
        with e ->
            statusWatch.Text <- "👁 off"
            ToolTip.SetTip(statusWatch, "The folder is not watched: " + e.Message)

    let openFolder (path: string) =
        if Directory.Exists path then
            for d in docs |> Seq.toList do
                closeNow d

            stopServers ()
            ui.RunDiagnostics <- []
            ui.Root <- Some(Path.GetFullPath path)
            expanded.Clear()
            window.Title <- sprintf "%s — FSharpened" (rootName ())
            statusFolder.Text <- "📁 " + rootName ()
            ui.Prefs <- Prefs.remember ui.Root.Value ui.Prefs
            savePrefs ()
            refreshTree ()
            refreshFileCache ()
            startWatching ui.Root.Value
            refreshProblems ()
            refreshGit ()
            setOutput ""
            showSidebar "files"

            say (
                if env.Trust.IsTrusted ui.Root.Value then
                    "Folder is trusted to run code"
                else
                    "Running code here will ask for your trust"
            )

    let closeFolder () =
        for d in docs |> Seq.toList do
            closeNow d

        stopWatching ()
        stopServers ()
        ui.RunDiagnostics <- []
        refreshProblems ()
        ui.Root <- None
        refreshGit ()
        ui.FileCache <- []
        refreshTree ()
        statusFolder.Text <- "No folder open"
        window.Title <- "FSharpened"

    let openFolderDialog () =
        task {
            let! picked =
                window.StorageProvider.OpenFolderPickerAsync(
                    FolderPickerOpenOptions(Title = "Open Folder", AllowMultiple = false)
                )

            if picked.Count > 0 then
                match picked[0].TryGetLocalPath() with
                | null -> ()
                | p -> openFolder p
        }
        |> ignore


    member _.CloseFolder = closeFolder
    member _.OpenFolder = openFolder
    member _.OpenFolderDialog = openFolderDialog
    member _.StopWatching = stopWatching
