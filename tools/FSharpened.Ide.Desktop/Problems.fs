/// The problems list, the underlines in the editor and where a diagnostic's file is in the folder.
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

type Problems(ui: Ui) =
    let editor = ui.Editor
    let lspDiagnostics = ui.LspDiagnostics
    let problemsBox = ui.ProblemsBox
    let squiggles = ui.Squiggles
    let statusProblems = ui.StatusProblems

    // ---- diagnostics and positions ----------------------------------------------------------------
    let relOfDiagnosticFile (file: string) : string option =
        match ui.Root with
        | None -> None
        | Some r ->
            Paths.ofDiagnosticFile
                r
                file
                (fun rel ->
                    match DiskWorkspace.resolve r rel with
                    | Ok p -> File.Exists p
                    | _ -> false)
                (fun name ->
                    ui.FileCache
                    |> List.tryFind (fun e -> e.Name = name)
                    |> Option.map (fun e -> e.Rel))

    let renderProblems (list: FSharpened.Diagnostics.StructuredDiagnostic list) =
        ui.ProblemsList <- list
        problemsBox.Items.Clear()

        for d in list do
            let colour =
                match d.Severity with
                | FSharpened.Diagnostics.Severity.Error -> ui.Roles.Bad
                | FSharpened.Diagnostics.Severity.Warning -> ui.Roles.Warn
                | _ -> ui.Roles.Muted

            let glyph =
                match d.Severity with
                | FSharpened.Diagnostics.Severity.Error -> "✖"
                | FSharpened.Diagnostics.Severity.Warning -> "⚠"
                | _ -> "ℹ"

            let where =
                match d.Range with
                | Some r -> sprintf "%s:%d:%d" r.File r.StartLine r.StartColumn
                | None -> ""

            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0, Tag = d)
            row.Children.Add(TextBlock(Text = glyph, Foreground = colour))

            let msg =
                TextBlock(
                    Text =
                        (if d.Code <> "" then
                             d.Code + "  " + d.Message
                         else
                             d.Message),
                    Foreground = ui.Roles.Fg
                )

            row.Children.Add msg
            row.Children.Add(TextBlock(Text = where, Foreground = ui.Roles.Muted))
            problemsBox.Items.Push row

        statusProblems.Text <- Status.problems list


    // ---- language servers and the problems list ---------------------------------------------------
    let toStructured (rel: string) (d: LspDiagnostic) : FSharpened.Diagnostics.StructuredDiagnostic =
        let sev = Status.severityOf d.Severity

        { FSharpened.Diagnostics.StructuredDiagnostic.create d.Code sev d.Message with
            Range =
                Some
                    { File = rel
                      StartLine = d.Range.Start.Line + 1
                      StartColumn = d.Range.Start.Character + 1
                      EndLine = d.Range.End.Line + 1
                      EndColumn = d.Range.End.Character + 1 } }

    let refreshProblems () =
        renderProblems (
            ui.RunDiagnostics
            @ [ for kv in lspDiagnostics do
                    yield! kv.Value ]
        )

    let updateSquiggles () =
        match ui.Active with
        | Some d ->
            let doc = d.Doc

            let offsetOf (line: int) (col: int) =
                let l = max 1 (min doc.LineCount line)
                let ln = doc.GetLineByNumber l
                ln.Offset + max 0 (min ln.Length (col - 1))

            squiggles.Ranges <-
                match lspDiagnostics.TryGetValue d.Rel with
                | true, list ->
                    [ for x in list do
                          match x.Range with
                          | Some r ->
                              let a = offsetOf r.StartLine r.StartColumn
                              let b = offsetOf r.EndLine r.EndColumn

                              let brush =
                                  match x.Severity with
                                  | FSharpened.Diagnostics.Severity.Error -> ui.Roles.Bad
                                  | FSharpened.Diagnostics.Severity.Warning -> ui.Roles.Warn
                                  | _ -> ui.Roles.Accent

                              yield (a, max 1 (b - a), brush)
                          | None -> () ]
                | _ -> []
        | None -> squiggles.Ranges <- []

        editor.TextArea.TextView.InvalidateLayer KnownLayer.Selection

    let storeLspDiagnostics (path: string) (list: LspDiagnostic list) =
        match ui.Root with
        | Some r ->
            let rel = Path.GetRelativePath(r, path).Replace('\\', '/')

            if not (rel.StartsWith "..") && not (Path.IsPathRooted rel) then
                if list.IsEmpty then
                    lspDiagnostics.Remove rel |> ignore
                else
                    lspDiagnostics[rel] <- list |> List.map (toStructured rel)

                refreshProblems ()
                updateSquiggles ()
        | None -> ()

    member _.RefreshProblems = refreshProblems
    member _.RelOfDiagnosticFile = relOfDiagnosticFile
    member _.StoreLspDiagnostics = storeLspDiagnostics
    member _.UpdateSquiggles = updateSquiggles
