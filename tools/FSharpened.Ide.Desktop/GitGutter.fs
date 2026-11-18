/// What git draws beside the editor's text: a thin bar per added or modified line and a notch where lines were removed
/// (the diff gutter), and, when asked for, who last changed each line (blame).
namespace FSharpened.Ide.Desktop

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Media
open AvaloniaEdit.Editing
open AvaloniaEdit.Rendering
open FSharpened.Ide.Host

module internal GitGutterLayout =
    /// The top and height of the visual line, in the margin's own coordinates.
    let lineBox (view: TextView) (vl: VisualLine) =
        let top =
            vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop)
            - view.VerticalOffset

        top, vl.Height

/// The diff margin: `marks` says what to draw for each 1-based line.
type DiffMargin(marks: unit -> Map<int, LineMark>) as this =
    inherit AbstractMargin()

    let onLines = EventHandler(fun _ _ -> this.InvalidateVisual())
    let mutable added: IBrush = Brushes.Green
    let mutable modified: IBrush = Brushes.DodgerBlue
    let mutable removed: IBrush = Brushes.Red

    member _.AddedBrush
        with get () = added
        and set v = added <- v

    member _.ModifiedBrush
        with get () = modified
        and set v = modified <- v

    member _.RemovedBrush
        with get () = removed
        and set v = removed <- v

    override _.MeasureOverride(_: Size) = Size(6.0, 0.0)

    override this.OnTextViewChanged(oldView: TextView, newView: TextView) =
        if not (isNull oldView) then
            oldView.VisualLinesChanged.RemoveHandler onLines

        base.OnTextViewChanged(oldView, newView)

        if not (isNull newView) then
            newView.VisualLinesChanged.AddHandler onLines

        this.InvalidateVisual()

    override this.Render(context: DrawingContext) =
        let view = this.TextView

        if not (isNull view) && view.VisualLinesValid then
            let all = marks ()

            if not all.IsEmpty then
                for vl in view.VisualLines do
                    match all.TryFind vl.FirstDocumentLine.LineNumber with
                    | Some mark ->
                        let top, height = GitGutterLayout.lineBox view vl

                        match mark with
                        | AddedLine -> context.FillRectangle(added, Rect(1.0, top, 3.0, height))
                        | ModifiedLine -> context.FillRectangle(modified, Rect(1.0, top, 3.0, height))
                        | RemovedAbove ->
                            // a small triangle pointing at the gap above the line
                            let g = StreamGeometry()
                            use c = g.Open()
                            c.BeginFigure(Point(0.0, top - 3.0), true)
                            c.LineTo(Point(5.0, top))
                            c.LineTo(Point(0.0, top + 3.0))
                            c.EndFigure(true)
                            context.DrawGeometry(removed, null, g)
                    | None -> ()

/// The blame margin: `label` is the short text for a 1-based line ("" when unknown), `detail` the tooltip.
type BlameMargin(label: int -> string, detail: int -> string) as this =
    inherit AbstractMargin()

    let onLines = EventHandler(fun _ _ -> this.InvalidateVisual())
    let mutable ink: IBrush = Brushes.Gray
    let typeface = Typeface "Segoe UI, Inter, sans-serif"

    member _.Ink
        with get () = ink
        and set v = ink <- v

    override _.MeasureOverride(_: Size) = Size(200.0, 0.0)

    override this.OnTextViewChanged(oldView: TextView, newView: TextView) =
        if not (isNull oldView) then
            oldView.VisualLinesChanged.RemoveHandler onLines

        base.OnTextViewChanged(oldView, newView)

        if not (isNull newView) then
            newView.VisualLinesChanged.AddHandler onLines

        this.InvalidateVisual()

    override this.Render(context: DrawingContext) =
        let view = this.TextView

        if not (isNull view) && view.VisualLinesValid then
            for vl in view.VisualLines do
                match label vl.FirstDocumentLine.LineNumber with
                | "" -> ()
                | text ->
                    let top, _ = GitGutterLayout.lineBox view vl

                    let formatted =
                        FormattedText(
                            text,
                            Globalization.CultureInfo.CurrentUICulture,
                            FlowDirection.LeftToRight,
                            typeface,
                            11.0,
                            ink
                        )

                    formatted.MaxTextWidth <- 192.0
                    formatted.MaxLineCount <- 1
                    context.DrawText(formatted, Point(6.0, top + 1.0))

    override this.OnPointerMoved(e: PointerEventArgs) =
        base.OnPointerMoved e
        let view = this.TextView

        if not (isNull view) then
            let vl =
                view.GetVisualLineFromVisualTop(e.GetPosition(this).Y + view.VerticalOffset)

            ToolTip.SetTip(
                this,
                (if isNull vl then
                     null
                 else
                     match detail vl.FirstDocumentLine.LineNumber with
                     | "" -> null
                     | t -> box t)
            )
