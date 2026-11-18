/// Language-server sessions: starting them, syncing documents, completion and go to definition.
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

type LanguageServers(ui: Ui, problems: Problems, terminalPanel: TerminalPanel) =
    let docs = ui.Docs
    let editor = ui.Editor
    let env = ui.Env
    let lspDiagnostics = ui.LspDiagnostics
    let lspFailed = ui.LspFailed
    let lspSessions = ui.LspSessions
    let lspStarting = ui.LspStarting
    let say = ui.Say
    let stopTerminal = terminalPanel.StopTerminal
    let storeLspDiagnostics = problems.StoreLspDiagnostics

    let sessionOf (d: OpenDoc) : LspSession option =
        match d.Pack with
        | Some p ->
            match lspSessions.TryGetValue p.Id with
            | true, s when s.Client.IsOpen -> Some s
            | _ -> None
        | None -> None

    let rec syncLsp (d: OpenDoc) =
        match ui.Root, d.Pack with
        | Some r, Some pack when pack.Server.IsSome ->
            match sessionOf d with
            | Some s ->
                let full = Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar))

                if not d.LspOpen then
                    s.Client.DidOpen(full, pack.Id, d.Text)
                    d.LspOpen <- true
                elif d.LspDirty then
                    s.Client.DidChange(full, d.Text)

                d.LspDirty <- false
            | None ->
                if
                    env.Trust.IsTrusted r
                    && not (lspFailed.Contains pack.Id)
                    && lspStarting.Add pack.Id
                then
                    let spec = pack.Server.Value
                    say (sprintf "Starting %s…" spec.Command)

                    task {
                        let! started =
                            Task.Run(fun () ->
                                match
                                    Lsp.start spec (fun p ds ->
                                        Dispatcher.UIThread.Post(fun () -> storeLspDiagnostics p ds))
                                with
                                | Ok s ->
                                    if s.Client.Initialize(r).Result then
                                        Ok s
                                    else
                                        (s.Stop()
                                         Error(sprintf "%s did not answer" spec.Command))
                                | Error e -> Error e)

                        lspStarting.Remove pack.Id |> ignore

                        match started with
                        | Ok s ->
                            lspSessions[pack.Id] <- s
                            say (sprintf "Language server %s is ready" spec.Command)

                            for doc in docs |> Seq.toList do
                                if doc.Pack.IsSome && doc.Pack.Value.Id = pack.Id then
                                    doc.LspOpen <- false
                                    syncLsp doc

                                    if ui.Active = Some doc then
                                        for h in ui.OnDocShown do
                                            h doc
                        | Error e ->
                            lspFailed.Add pack.Id |> ignore
                            say (sprintf "No language server for %s: %s" pack.Name (e.Split('\n')[0]))
                    }
                    |> ignore
        | _ -> ()


    let stopServers () =
        ui.StopDebugFn "Debugger stopped"

        for kv in lspSessions do
            kv.Value.Stop()

        stopTerminal ()

        lspSessions.Clear()
        lspFailed.Clear()
        lspStarting.Clear()
        lspDiagnostics.Clear()
        ui.LspRaw.Clear()

    let completeNow () =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            match sessionOf d with
            | None -> say "No language server is running for this file"
            | Some s ->
                syncLsp d
                let caret = editor.TextArea.Caret

                let pos =
                    { Line = caret.Line - 1
                      Character = caret.Column - 1 }

                let full = Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar))

                task {
                    let! items = s.Client.Completion(full, pos)

                    if items.IsEmpty then
                        say "No suggestions"
                    else
                        let doc = editor.Document
                        let start = TextAt.completionStart doc.GetCharAt editor.CaretOffset
                        let win = CompletionWindow(editor.TextArea)
                        win.StartOffset <- start

                        for i in items |> Seq.truncate 200 do
                            win.CompletionList.CompletionData.Add(LspItem i)

                        win.Show()
                }
                |> ignore
        | _ -> ()

    let gotoDefinitionNow () =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            match sessionOf d with
            | None -> say "No language server is running for this file"
            | Some s ->
                syncLsp d
                let caret = editor.TextArea.Caret
                let full = Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar))

                task {
                    let! found =
                        s.Client.Definition(
                            full,
                            { Line = caret.Line - 1
                              Character = caret.Column - 1 }
                        )

                    match found with
                    | [] -> say "No definition found"
                    | loc :: _ ->
                        let rel = Path.GetRelativePath(r, loc.Path).Replace('\\', '/')

                        if rel.StartsWith ".." then
                            say (sprintf "The definition is outside this folder: %s" loc.Path)
                        else
                            ui.OpenFileFn rel (loc.Range.Start.Line + 1) (loc.Range.Start.Character + 1)
                }
                |> ignore
        | _ -> ()

    member _.CompleteNow = completeNow
    member _.GotoDefinitionNow = gotoDefinitionNow
    member _.SessionOf = sessionOf
    member _.StopServers = stopServers
    member _.SyncLsp = syncLsp
