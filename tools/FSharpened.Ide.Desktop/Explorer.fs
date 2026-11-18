/// The folder tree: building nodes, icons, Git colours, the context menus and file operations.
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

type Explorer(ui: Ui, editorTabs: EditorTabs) =
    let closeNow = editorTabs.CloseNow
    let docs = ui.Docs
    let expanded = ui.Expanded
    let fileTree = ui.FileTree
    let menuItem = ui.MenuItem
    let muted = ui.Muted
    let rebuildTabs = editorTabs.RebuildTabs
    let window = ui.Window

    // ---- file tree ---------------------------------------------------------------------------------
    let childRel = Paths.child
    let parentOf = Paths.parent

    let refreshFileCache () =
        match ui.Root with
        | Some r ->
            Task.Run(fun () -> DiskWorkspace.allFiles r 6000 |> fst)
            |> fun t ->
                t.ContinueWith(
                    (fun (x: Task<Entry list>) -> Dispatcher.UIThread.Post(fun () -> ui.FileCache <- x.Result)),
                    TaskContinuationOptions.OnlyOnRanToCompletion
                )
            |> ignore
        | None -> ui.FileCache <- []

    let rec fillNode (parent: ItemsControl) (rel: string) =
        parent.Items.Clear()

        match ui.Root with
        | None -> ()
        | Some r ->
            match DiskWorkspace.list r rel with
            | Error e -> parent.Items.Push(TreeViewItem(Header = muted e))
            | Ok entries ->
                for e in entries do
                    let item = TreeViewItem(Tag = e)

                    let name =
                        TextBlock(
                            Text = e.Name,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontWeight = (if e.IsDir then FontWeight.SemiBold else FontWeight.Normal)
                        )

                    name.Foreground <-
                        if e.IsDir then
                            (if ui.GitChanges |> Map.exists (fun k _ -> k = e.Rel || k.StartsWith(e.Rel + "/")) then
                                 ui.Roles.Warn
                             else
                                 ui.Roles.Fg)
                        else
                            match ui.GitChanges.TryFind e.Rel with
                            | Some Untracked -> ui.Roles.Ok
                            | Some Deleted
                            | Some Conflicted -> ui.Roles.Bad
                            | Some _ -> ui.Roles.Warn
                            | None -> ui.Roles.Fg

                    item.Header <- name

                    // everything the context menu does is also on keys while the row has the focus
                    item.KeyDown.Add(fun ev ->
                        match ev.Key with
                        | Key.Enter when not e.IsDir ->
                            ev.Handled <- true
                            ui.OpenFileFn e.Rel 0 0
                        | Key.F2 ->
                            ev.Handled <- true
                            renameEntry e
                        | Key.Delete ->
                            ev.Handled <- true
                            deleteEntry e
                        | _ -> ())

                    let menu = ContextMenu()
                    buildTreeMenu menu e
                    item.ContextMenu <- menu

                    if e.IsDir then
                        if expanded.Contains e.Rel then
                            fillNode item e.Rel
                            item.IsExpanded <- true
                        else
                            item.Items.Push(TreeViewItem(Header = "…"))

                        item.PropertyChanged.Add(fun a ->
                            if a.Property = TreeViewItem.IsExpandedProperty then
                                if item.IsExpanded then
                                    expanded.Add e.Rel |> ignore
                                    fillNode item e.Rel
                                else
                                    expanded.Remove e.Rel |> ignore)
                    else
                        item.DoubleTapped.Add(fun ev ->
                            ev.Handled <- true
                            ui.OpenFileFn e.Rel 0 0)

                    parent.Items.Push item

    and buildTreeMenu (menu: ContextMenu) (e: Entry) =
        let dir = if e.IsDir then e.Rel else parentOf e.Rel

        if not e.IsDir then
            menu.Items.Push(menuItem "Open" "" (fun () -> ui.OpenFileFn e.Rel 0 0))

        menu.Items.Push(menuItem "New File…" "" (fun () -> newEntry dir false))
        menu.Items.Push(menuItem "New Folder…" "" (fun () -> newEntry dir true))
        menu.Items.Push(Separator())
        menu.Items.Push(menuItem "Rename…" "" (fun () -> renameEntry e))
        menu.Items.Push(menuItem "Delete…" "" (fun () -> deleteEntry e))
        menu.Items.Push(Separator())
        menu.Items.Push(menuItem "Copy Relative Path" "" (fun () -> window.Clipboard.SetTextAsync e.Rel |> ignore))

        menu.Items.Push(
            menuItem "Copy Full Path" "" (fun () ->
                match ui.Root with
                | Some r ->
                    window.Clipboard.SetTextAsync(Path.Combine(r, e.Rel.Replace('/', Path.DirectorySeparatorChar)))
                    |> ignore
                | None -> ())
        )

        menu.Items.Push(
            menuItem "Reveal in File Manager" "" (fun () ->
                match ui.Root with
                | Some r ->
                    let full =
                        Path.Combine(
                            r,
                            (if e.IsDir then e.Rel else parentOf e.Rel).Replace('/', Path.DirectorySeparatorChar)
                        )

                    if Directory.Exists full then
                        window.Launcher.LaunchDirectoryInfoAsync(DirectoryInfo full) |> ignore
                | None -> ())
        )

    and refreshTree () =
        match ui.Root with
        | None -> fileTree.Items.Clear()
        | Some _ -> fillNode fileTree ""

    and newEntry (dir: string) (isFolder: bool) =
        match ui.Root with
        | None -> ()
        | Some r ->
            task {
                let! name =
                    Dialogs.ask
                        window
                        ui.Roles
                        (if isFolder then "New folder" else "New file")
                        (if dir = "" then
                             "Name (in the folder root):"
                         else
                             sprintf "Name (in %s):" dir)
                        ""
                        (fun v -> if v.Trim() = "" then "Enter a name" else "")

                match name with
                | Some n ->
                    let rel = childRel dir (n.Trim().Replace('\\', '/'))

                    let result =
                        if isFolder then
                            DiskWorkspace.createFolder r rel
                        else
                            DiskWorkspace.createFile r rel

                    match result with
                    | Ok() ->
                        if dir <> "" then
                            expanded.Add dir |> ignore

                        refreshTree ()
                        refreshFileCache ()

                        if not isFolder then
                            ui.OpenFileFn rel 0 0
                    | Error e -> do! Dialogs.info window ui.Roles "Cannot create it" e
                | None -> ()
            }
            |> ignore

    and renameEntry (e: Entry) =
        match ui.Root with
        | None -> ()
        | Some r ->
            task {
                let! name =
                    Dialogs.ask window ui.Roles "Rename" "New name:" e.Name (fun v ->
                        if v.Trim() = "" || v.Contains "/" || v.Contains "\\" then
                            "Enter a name without slashes"
                        else
                            "")

                match name with
                | Some n when n.Trim() <> e.Name ->
                    let target = childRel (parentOf e.Rel) (n.Trim())

                    match DiskWorkspace.move r e.Rel target with
                    | Ok() ->
                        for d in docs do
                            if d.Rel = e.Rel then
                                d.Rel <- target
                            elif d.Rel.StartsWith(e.Rel + "/") then
                                d.Rel <- target + d.Rel.Substring e.Rel.Length

                        refreshTree ()
                        refreshFileCache ()
                        rebuildTabs ()
                    | Error err -> do! Dialogs.info window ui.Roles "Cannot rename" err
                | _ -> ()
            }
            |> ignore

    and deleteEntry (e: Entry) =
        match ui.Root with
        | None -> ()
        | Some r ->
            task {
                let! yes =
                    Dialogs.confirm
                        window
                        ui.Roles
                        "Delete"
                        (sprintf
                            "Permanently delete “%s”%s? This cannot be undone and does not use the recycle bin."
                            e.Name
                            (if e.IsDir then " and everything in it" else ""))
                        "Delete"

                if yes then
                    match DiskWorkspace.delete r e.Rel with
                    | Ok() ->
                        for d in docs |> Seq.toList do
                            if d.Rel = e.Rel || d.Rel.StartsWith(e.Rel + "/") then
                                closeNow d

                        refreshTree ()
                        refreshFileCache ()
                    | Error err -> do! Dialogs.info window ui.Roles "Cannot delete" err
            }
            |> ignore


    member _.NewEntry = newEntry
    member _.RefreshFileCache = refreshFileCache
    member _.RefreshTree = refreshTree
