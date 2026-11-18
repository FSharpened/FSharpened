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
            statusPos.Text <- sprintf "Ln %d, Col %d" p.Line p.Column

            statusLang.Text <-
                match d.Pack with
                | Some pack -> pack.Name
                | None -> "Plain text"

            let trail = Symbols.enclosing d.Outline p.Line |> List.map (fun s -> s.Name)
            let parts = (d.Rel.Split('/') |> Array.toList) @ trail
            crumbs.Text <- String.Join("  ›  ", parts)
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

    let refreshGit () =
        match ui.Root with
        | Some r ->
            task {
                let! branch, changes = Task.Run(fun () -> Git.branch r, Git.status r |> Map.ofList)
                ui.GitBranch <- branch
                ui.GitChanges <- changes

                statusGit.Text <-
                    match branch with
                    | Some b ->
                        sprintf
                            "⎇ %s%s"
                            b
                            (if changes.IsEmpty then
                                 ""
                             else
                                 sprintf "  ● %d" changes.Count)
                    | None -> ""

                ui.RefreshTreeFn()
            }
            |> ignore
        | None ->
            ui.GitBranch <- None
            ui.GitChanges <- Map.empty
            statusGit.Text <- ""

    member _.OutlineTimer = outlineTimer
    member _.RecomputeOutline = recomputeOutline
    member _.RefreshGit = refreshGit
    member _.UpdateStatus = updateStatus
