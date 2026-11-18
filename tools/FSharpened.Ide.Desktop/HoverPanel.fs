/// The hover box: problems under the pointer plus what the language server says.
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

type HoverPanel(ui: Ui, problems: Problems, languageServers: LanguageServers) =
    let editor = ui.Editor
    let lspDiagnostics = ui.LspDiagnostics
    let monoFont = ui.MonoFont
    let relOfDiagnosticFile = problems.RelOfDiagnosticFile
    let sessionOf = languageServers.SessionOf
    let syncLsp = languageServers.SyncLsp
    let themed = ui.Themed

    // ---- hover ------------------------------------------------------------------------------------
    let hoverText =
        TextBlock(TextWrapping = TextWrapping.Wrap, FontFamily = monoFont, FontSize = 12.0)

    let hoverBox =
        Border(
            Child = hoverText,
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 560.0,
            Padding = Thickness(10.0, 6.0),
            CornerRadius = CornerRadius 4.0,
            BorderThickness = Thickness 1.0
        )

    do
        themed (fun r ->
            hoverBox.Background <- r.Card
            hoverBox.BorderBrush <- r.Line
            hoverText.Foreground <- r.Fg)

    let hoverTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 500.0)
    let mutable hoverPoint = Point(0.0, 0.0)
    let mutable hoverSeq = 0

    let hideHover () =
        hoverTimer.Stop()
        hoverSeq <- hoverSeq + 1
        hoverBox.IsVisible <- false

    /// The problems under a position and what the language server says about it, in a box beside it.
    let showHoverAt (line: int) (col: int) (at: Point) =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            hoverSeq <- hoverSeq + 1
            let mine = hoverSeq
            let rel = d.Rel

            let own =
                [ match lspDiagnostics.TryGetValue rel with
                  | true, l -> yield! l
                  | _ -> ()
                  yield!
                      ui.RunDiagnostics
                      |> List.filter (fun x ->
                          match x.Range with
                          | Some rg -> relOfDiagnosticFile rg.File = Some rel
                          | None -> false) ]

            let problems = Hover.problemsAt own line col

            task {
                let! server =
                    match sessionOf d with
                    | Some s when d.LspOpen ->
                        syncLsp d
                        let full = Path.Combine(r, rel.Replace('/', Path.DirectorySeparatorChar))
                        s.Client.Hover(full, { Line = line - 1; Character = col - 1 })
                    | _ -> Task.FromResult(None: string option)

                if mine = hoverSeq then
                    match Hover.compose problems server with
                    | Some text ->
                        hoverText.Text <- text
                        hoverBox.Margin <- Thickness(max 0.0 at.X, max 0.0 at.Y, 0.0, 0.0)
                        hoverBox.IsVisible <- true
                    | None -> hoverBox.IsVisible <- false
            }
            |> ignore
        | _ -> ()

    let showHoverAtCaret () =
        let c = editor.TextArea.Caret
        let tv = editor.TextArea.TextView

        let v =
            tv.GetVisualPosition(TextViewPosition(c.Line, c.Column), VisualYPosition.LineBottom)

        match tv.TranslatePoint(Point(v.X - tv.ScrollOffset.X, v.Y - tv.ScrollOffset.Y), editor) with
        | p when p.HasValue -> showHoverAt c.Line c.Column p.Value
        | _ -> ()

    member _.HideHover = hideHover
    member _.HoverBox = hoverBox

    member _.HoverPoint
        with get () = hoverPoint
        and set v = hoverPoint <- v

    member _.HoverTimer = hoverTimer
    member _.ShowHoverAt = showHoverAt
    member _.ShowHoverAtCaret = showHoverAtCaret
