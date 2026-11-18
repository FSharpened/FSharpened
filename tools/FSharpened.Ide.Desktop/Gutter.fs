/// What the debugger draws on the editor: a red dot per breakpoint in a margin left of the line numbers (a click toggles
/// it), and a band behind the line the program stopped on.
namespace FSharpened.Ide.Desktop

open Avalonia
open Avalonia.Input
open Avalonia.Media
open AvaloniaEdit.Editing
open AvaloniaEdit.Rendering
open AvaloniaEdit.Document

module internal GutterShapes =
    let arrowAt (cx: float) (cy: float) : Geometry =
        let g = StreamGeometry()
        use c = g.Open()
        c.BeginFigure(Point(cx - 6.0, cy - 5.0), true)
        c.LineTo(Point(cx + 1.0, cy - 5.0))
        c.LineTo(Point(cx + 7.0, cy))
        c.LineTo(Point(cx + 1.0, cy + 5.0))
        c.LineTo(Point(cx - 6.0, cy + 5.0))
        c.EndFigure(true)
        g :> Geometry

/// The breakpoint margin. `lines` says which 1-based lines have a breakpoint, `toggle` is called with the line a click hit.
type BreakpointMargin(lines: unit -> int list, toggle: int -> unit) as this =
    inherit AbstractMargin()

    let mutable dot: IBrush = Brushes.Red
    let mutable arrow: IBrush = Brushes.Gold
    let mutable stopped = 0

    let onLines = System.EventHandler(fun _ _ -> this.InvalidateVisual())

    do this.Cursor <- new Cursor(StandardCursorType.Hand)

    member _.DotBrush
        with get () = dot
        and set v = dot <- v

    member _.ArrowBrush
        with get () = arrow
        and set v = arrow <- v

    /// The line the program is stopped on (shown as an arrow), or 0.
    member this.StoppedLine
        with get () = stopped
        and set (v: int) =
            stopped <- v
            this.InvalidateVisual()

    override _.MeasureOverride(_: Size) = Size(18.0, 0.0)

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
            let marked = lines () |> Set.ofList

            for vl in view.VisualLines do
                let line = vl.FirstDocumentLine.LineNumber

                let top =
                    vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop)
                    - view.VerticalOffset

                let height = vl.Height
                let size = min 12.0 (height - 4.0)
                let cx = 9.0
                let cy = top + height / 2.0

                if marked.Contains line then
                    context.DrawEllipse(dot, null, Point(cx, cy), size / 2.0, size / 2.0)

                if line = stopped then
                    context.DrawGeometry(arrow, null, GutterShapes.arrowAt cx cy)

    override this.OnPointerPressed(e: PointerPressedEventArgs) =
        base.OnPointerPressed e
        let view = this.TextView

        if
            not e.Handled
            && not (isNull view)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
        then
            let y = e.GetPosition(this).Y + view.VerticalOffset
            let vl = view.GetVisualLineFromVisualTop y

            if not (isNull vl) then
                toggle vl.FirstDocumentLine.LineNumber
                e.Handled <- true

/// A band behind the line a stopped program is on.
type ExecutionLine() =
    member val Line = 0 with get, set
    member val Brush: IBrush = Brushes.Transparent with get, set

    interface IBackgroundRenderer with
        member _.Layer = KnownLayer.Background

        member this.Draw(textView: TextView, context: DrawingContext) =
            if this.Line > 0 && textView.VisualLinesValid && not (isNull textView.Document) then
                if this.Line <= textView.Document.LineCount then
                    let dl = textView.Document.GetLineByNumber this.Line
                    let seg = TextSegment(StartOffset = dl.Offset, Length = max 1 dl.Length)

                    for r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg) do
                        context.FillRectangle(this.Brush, Rect(0.0, r.Top, max textView.Bounds.Width r.Right, r.Height))
