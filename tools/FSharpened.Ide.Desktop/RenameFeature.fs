/// Rename symbol through the language server: prepare, ask, preview, apply to open buffers and files.
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

type RenameFeature(ui: Ui, languageServers: LanguageServers, editorTabs: EditorTabs) =
    let docs = ui.Docs
    let editor = ui.Editor
    let rebuildTabs = editorTabs.RebuildTabs
    let say = ui.Say
    let sessionOf = languageServers.SessionOf
    let syncLsp = languageServers.SyncLsp
    let window = ui.Window

    // ---- rename (language server) ------------------------------------------------------------------
    /// A new name given on the command line, so a screenshot can show the preview without a person typing.
    let mutable renameTo: string option = None

    let fullPathOf = Paths.fullPath
    let relInside = Paths.relInside

    /// The text a rename plans against: the editor's when the file is open, otherwise what is on disk. A file outside
    /// the opened folder is never touched.
    let readForRename (r: string) (path: string) : Result<string, string> =
        match relInside r path with
        | None -> Error "is outside the open folder"
        | Some rel ->
            match docs |> Seq.tryFind (fun d -> d.Rel = rel) with
            | Some d -> Ok d.Text
            | None -> DiskWorkspace.readText r rel

    /// Writes the planned changes: open files are edited in the editor (one undoable edit each, left unsaved), the
    /// others go to disk through the workspace. Returns what happened, in words.
    let applyRename (r: string) (changes: FileChange list) : string =
        let failed = List<string>()
        let mutable done' = 0

        for c in changes do
            match relInside r c.Path with
            | None -> failed.Add(sprintf "%s is outside the open folder" c.Path)
            | Some rel ->
                match docs |> Seq.tryFind (fun d -> d.Rel = rel) with
                | Some d ->
                    if d.Text <> c.Before then
                        failed.Add(sprintf "%s changed while the preview was open" rel)
                    else
                        use _edit = d.Doc.RunUpdate()

                        for (a, b, n) in List.rev c.Spans do
                            d.Doc.Replace(a, b - a, n)

                        done' <- done' + 1
                | None ->
                    match DiskWorkspace.writeText r rel c.After with
                    | Ok() -> done' <- done' + 1
                    | Error e -> failed.Add(sprintf "%s: %s" rel e)

        if failed.Count = 0 then
            sprintf "Renamed in %s" (Edits.summary changes)
        else
            sprintf "Renamed in %d file(s); %s" done' (String.Join("; ", failed))

    /// The identifier under a position, for when the server does not say what it is renaming.
    let wordAt = TextAt.wordAt

    let renameSymbolNow () =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            match sessionOf d with
            | None ->
                syncLsp d // starts the server if the folder is trusted

                say (
                    sprintf
                        "Rename needs a language server for %s; none is running yet"
                        (match d.Pack with
                         | Some p -> p.Name
                         | None -> "this file")
                )
            | Some s when s.Client.RenameSupport = NoRename ->
                Dialogs.info
                    window
                    ui.Roles
                    "Rename is not available"
                    (sprintf
                        "The language server for %s does not support renaming symbols."
                        (match d.Pack with
                         | Some p -> p.Name
                         | None -> "this file"))
                |> ignore
            | Some s ->
                syncLsp d
                let caret = editor.TextArea.Caret

                let pos =
                    { Line = caret.Line - 1
                      Character = caret.Column - 1 }

                let full = fullPathOf r d.Rel
                let offset = editor.CaretOffset

                task {
                    let! prep = s.Client.PrepareRename(full, pos)

                    match prep with
                    | Error m -> do! Dialogs.info window ui.Roles "Cannot rename here" m
                    | Ok p ->
                        let text = d.Text

                        let current =
                            match p.Placeholder, p.Range with
                            | Some n, _ -> n
                            | None, Some rg ->
                                let st = Edits.lineStarts text

                                match Edits.offsetOf text st rg.Start, Edits.offsetOf text st rg.End with
                                | Ok a, Ok b when b > a -> text.Substring(a, b - a)
                                | _ -> wordAt text offset
                            | None, None -> wordAt text offset

                        let! name =
                            match renameTo with
                            | Some n -> Task.FromResult(Some n)
                            | None ->
                                Dialogs.ask
                                    window
                                    ui.Roles
                                    "Rename symbol"
                                    (sprintf "New name for “%s”:" current)
                                    current
                                    Edits.checkName

                        match name with
                        | Some n when n <> current ->
                            let! res = s.Client.Rename(full, pos, n)

                            match res with
                            | Error m -> do! Dialogs.info window ui.Roles "Rename failed" m
                            | Ok we ->
                                match Edits.plan (readForRename r) we with
                                | Error m -> do! Dialogs.info window ui.Roles "Rename failed" m
                                | Ok [] -> say "The server found nothing to rename"
                                | Ok changes ->
                                    let rows =
                                        changes
                                        |> List.map (fun c ->
                                            (match relInside r c.Path with
                                             | Some rel -> rel
                                             | None -> c.Path),
                                            c.Count,
                                            Edits.changedLines c.Before c.After)

                                    let! yes =
                                        Dialogs.preview
                                            window
                                            ui.Roles
                                            (sprintf "Rename “%s” to “%s”" current n)
                                            (sprintf
                                                "%s. Files open in the editor change there and stay unsaved, so Undo works; the others are written now."
                                                (Edits.summary changes))
                                            rows
                                            "Rename"

                                    if yes then
                                        say (applyRename r changes)
                                        rebuildTabs ()
                        | _ -> ()
                }
                |> ignore
        | _ -> say "Open a file in a folder first"


    member _.FullPathOf = fullPathOf
    member _.RenameSymbolNow = renameSymbolNow

    member _.RenameTo
        with get () = renameTo
        and set v = renameTo <- v
