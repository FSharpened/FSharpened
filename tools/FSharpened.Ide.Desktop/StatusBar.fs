/// The status bar's position, language and breadcrumb text, the outline timer and the Git item.
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

type StatusBar(ui: Ui) =
    let crumbs = ui.Crumbs
    let editor = ui.Editor
    let statusGit = ui.StatusGit
    let statusLang = ui.StatusLang
    let statusPos = ui.StatusPos

    let updateStatus () =
        match ui.Active with
        | Some d ->
            let p = editor.TextArea.Caret
            d.Line <- p.Line
            d.Column <- p.Column
            statusPos.Text <- Status.position p.Line p.Column

            statusLang.Text <-
                match d.Pack with
                | Some pack -> pack.Name
                | None -> "Plain text"

            let trail = Symbols.enclosing d.Outline p.Line |> List.map (fun s -> s.Name)
            crumbs.Text <- Status.breadcrumb d.Rel trail
        | None ->
            statusPos.Text <- ""
            statusLang.Text <- ""
            crumbs.Text <- ""

    let recomputeOutline (d: OpenDoc) =
        d.Outline <-
            try
                Symbols.outlineOfPath d.Rel d.Text
            with _ ->
                []

    let outlineTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 350.0)

    /// Reads the branch and the changes. Git runs only in a trusted folder: a repository's own configuration and hooks
    /// can start programs, so an untrusted folder is shown as having no git at all.
    let refreshGit () =
        match ui.Root with
        | Some r when ui.Env.Trust.IsTrusted r ->
            task {
                let! branch, entries =
                    Task.Run(fun () ->
                        Git.branch r,
                        (match GitOps.entries r with
                         | Ok e -> e
                         | Error _ -> []))

                ui.GitBranch <- branch
                ui.GitEntries <- entries

                // the explorer's mark is the staged change, else the unstaged one
                ui.GitChanges <-
                    entries
                    |> List.choose (fun e ->
                        match e.Staged |> Option.orElse e.Unstaged with
                        | Some c -> Some(e.Path, c)
                        | None -> None)
                    |> Map.ofList

                statusGit.Text <- Status.git branch ui.GitChanges.Count
                ui.RefreshTreeFn()

                for h in ui.OnGitChanged do
                    h ()
            }
            |> ignore
        | _ ->
            ui.GitBranch <- None
            ui.GitChanges <- Map.empty
            ui.GitEntries <- []
            statusGit.Text <- ""
            ui.RefreshTreeFn()

            for h in ui.OnGitChanged do
                h ()

    member _.OutlineTimer = outlineTimer
    member _.RecomputeOutline = recomputeOutline
    member _.RefreshGit = refreshGit
    member _.UpdateStatus = updateStatus
