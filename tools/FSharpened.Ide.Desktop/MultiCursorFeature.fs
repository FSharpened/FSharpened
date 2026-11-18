/// Several cursors in the editor. AvaloniaEdit has one caret; the others are kept here, drawn over the text, and typing,
/// deleting, pasting, moving and copying act on all of them as one undo step. What each key does to the cursors is
/// decided by `Cursors` (pure, tested without a window); this file connects it to the editor's events.
namespace FSharpened.Ide.Desktop

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Interactivity
open Avalonia.Media
open AvaloniaEdit
open AvaloniaEdit.Document
open AvaloniaEdit.Editing
open AvaloniaEdit.Rendering
open FSharpened.Ide.Host

/// Draws the cursors other than the primary: a bar for a caret and a band behind a selection.
type ExtraCursorsRenderer(editor: TextEditor, extras: unit -> EditCursor list) =
    interface IBackgroundRenderer with
        member _.Layer = KnownLayer.Selection

        member _.Draw(textView: TextView, dc: DrawingContext) =
            let cursors = extras ()
            let doc = textView.Document

            if not cursors.IsEmpty && not (isNull doc) && textView.VisualLinesValid then
                let selection = editor.TextArea.SelectionBrush
                let ink = editor.Foreground

                for c in cursors do
                    if
                        not (Carets.isEmpty c)
                        && c.Caret <= doc.TextLength
                        && c.Anchor <= doc.TextLength
                    then
                        let builder = BackgroundGeometryBuilder(AlignToWholePixels = true)

                        builder.AddSegment(
                            textView,
                            TextSegment(StartOffset = Carets.start c, Length = Carets.stop c - Carets.start c)
                        )

                        match builder.CreateGeometry() with
                        | null -> ()
                        | g -> dc.DrawGeometry(selection, null, g)

                for vl in textView.VisualLines do
                    let first = vl.FirstDocumentLine.Offset
                    let last = vl.LastDocumentLine.EndOffset

                    for c in cursors do
                        if c.Caret >= first && c.Caret <= last then
                            let col = vl.GetVisualColumn(c.Caret - first)
                            let p = vl.GetVisualPosition(col, VisualYPosition.LineTop) - textView.ScrollOffset
                            dc.FillRectangle(ink, Rect(p.X, p.Y, 1.5, vl.Height))

type MultiCursorFeature(ui: Ui) =
    let editor = ui.Editor
    let area = editor.TextArea
    let mutable extras: EditCursor list = []
    let mutable applying = false

    let text () = editor.Document.Text

    let lineBreak (t: string) =
        if t.Contains "\r\n" then "\r\n" else "\n"

    /// The editor's own caret and selection as a cursor.
    let primary () : EditCursor =
        let caret = area.Caret.Offset

        if area.Selection.IsEmpty then
            Carets.caretAt caret
        else
            let seg = area.Selection.SurroundingSegment

            if caret = seg.EndOffset then
                { Anchor = seg.Offset; Caret = caret }
            else
                { Anchor = seg.EndOffset
                  Caret = caret }

    let current () =
        Carets.ofPrimaryAndExtras (primary ()) extras

    let repaint () =
        area.TextView.InvalidateLayer KnownLayer.Selection

        ui.StatusCursors.Text <-
            if extras.IsEmpty then
                ""
            else
                sprintf "%d cursors" (extras.Length + 1)

        ui.StatusCursors.IsVisible <- not extras.IsEmpty

    /// Makes `set` what the editor shows: its primary is the editor's caret and selection, the rest are the extras.
    let publish (set: CursorSet) =
        let p = Carets.primary set
        extras <- Carets.extras set

        if Carets.isEmpty p then
            area.ClearSelection()
        else
            area.Selection <- Selection.Create(area, p.Anchor, p.Caret)

        area.Caret.Offset <- p.Caret
        area.Caret.BringCaretToView()
        repaint ()

    let clear () =
        if not extras.IsEmpty then
            extras <- []
            repaint ()

    let hasExtras () = not extras.IsEmpty

    /// Makes the edits, from the end backwards so the offsets of the earlier ones stay right, in one undo step.
    let applyChange (change: CursorChange) =
        let d = editor.Document
        applying <- true

        try
            d.BeginUpdate()

            try
                for e in List.rev change.Edits do
                    d.Replace(e.Offset, e.Length, e.Text)
            finally
                d.EndUpdate()

            publish change.Set
        finally
            applying <- false

    let indentUnit () =
        let e = ui.Prefs.Editor

        if e.InsertSpaces then
            String(' ', max 1 e.TabSize)
        else
            "\t"

    // ---- the commands ---------------------------------------------------------------------------------

    let addLine (step: int) =
        match Carets.addOnLine (text ()) step (current ()) with
        | Some set -> publish set
        | None -> ()

    let addNext () =
        match Carets.addNextMatch (text ()) (current ()) with
        | Some set -> publish set
        | None -> ui.Say "No more matches"

    let addAll () =
        match Carets.selectAllMatches (text ()) (current ()) with
        | Some set ->
            publish set
            ui.Say(sprintf "%d cursors, one for each match" set.Cursors.Length)
        | None -> ui.Say "Nothing to match: put the caret on a word or select some text"

    // ---- the clipboard --------------------------------------------------------------------------------

    let selectedTexts () =
        let t = text ()

        (current ()).Cursors
        |> List.filter (fun c -> not (Carets.isEmpty c))
        |> List.map (Carets.selectedText t)

    let clipboard () =
        match TopLevel.GetTopLevel editor with
        | null -> None
        | top -> Option.ofObj top.Clipboard

    let copy () =
        match clipboard (), selectedTexts () with
        | Some cb, (_ :: _ as texts) -> cb.SetTextAsync(String.Join(lineBreak (text ()), texts)) |> ignore
        | _ -> ()

    let paste () =
        match clipboard () with
        | Some cb ->
            task {
                let! pasted = cb.TryGetTextAsync()

                if not (String.IsNullOrEmpty pasted) then
                    applyChange (Carets.paste (text ()) pasted (current ()))
            }
            |> ignore
        | None -> ()

    // ---- the editor's events ---------------------------------------------------------------------------

    let motionOf (key: Key) (ctrl: bool) : Motion option =
        match key with
        | Key.Left -> Some(if ctrl then MoveWordLeft else MoveLeft)
        | Key.Right -> Some(if ctrl then MoveWordRight else MoveRight)
        | Key.Up when not ctrl -> Some MoveUp
        | Key.Down when not ctrl -> Some MoveDown
        | Key.Home when not ctrl -> Some MoveLineStart
        | Key.End when not ctrl -> Some MoveLineEnd
        | _ -> None

    let onKey (e: KeyEventArgs) =
        if hasExtras () then
            let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control
            let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
            let alt = e.KeyModifiers.HasFlag KeyModifiers.Alt
            let plain = not ctrl && not alt && not (e.KeyModifiers.HasFlag KeyModifiers.Meta)
            let t = text ()

            let handled =
                match e.Key with
                | Key.Escape when e.KeyModifiers = KeyModifiers.None ->
                    clear ()
                    true
                | Key.Back when plain ->
                    applyChange (Carets.backspace t (current ()))
                    true
                | Key.Delete when plain && not shift ->
                    applyChange (Carets.delete t (current ()))
                    true
                | Key.Enter when plain ->
                    applyChange (Carets.newline t (lineBreak t) (current ()))
                    true
                | Key.Tab when e.KeyModifiers = KeyModifiers.None ->
                    applyChange (Carets.insert t (indentUnit ()) (current ()))
                    true
                | Key.C when ctrl && not alt && not shift ->
                    copy ()
                    true
                | Key.X when ctrl && not alt && not shift ->
                    copy ()
                    applyChange (Carets.delete t (current ()))
                    true
                | Key.V when ctrl && not alt && not shift ->
                    paste ()
                    true
                | k when not alt ->
                    match motionOf k ctrl with
                    | Some m ->
                        publish (Carets.move t m shift (current ()))
                        true
                    | None -> false
                | _ -> false

            if handled then
                e.Handled <- true

    let onTextInput (e: TextInputEventArgs) =
        if
            hasExtras ()
            && not (String.IsNullOrEmpty e.Text)
            && e.Text |> Seq.forall (fun c -> not (Char.IsControl c))
        then
            applyChange (Carets.insert (text ()) e.Text (current ()))
            e.Handled <- true

    /// Ctrl+Alt+click adds a cursor where the pointer is; any other click goes back to one cursor.
    let onPointer (e: PointerPressedEventArgs) =
        let props = e.GetCurrentPoint(area.TextView).Properties

        if props.IsLeftButtonPressed then
            let wanted = KeyModifiers.Control ||| KeyModifiers.Alt

            if
                e.KeyModifiers.HasFlag KeyModifiers.Control
                && e.KeyModifiers.HasFlag KeyModifiers.Alt
            then
                let at = e.GetPosition area.TextView
                let pos = area.TextView.GetPosition(at + area.TextView.ScrollOffset)

                if pos.HasValue then
                    let offset = editor.Document.GetOffset(pos.Value.Line, pos.Value.Column)
                    let all = (current ()).Cursors @ [ Carets.caretAt offset ]

                    publish (
                        Carets.tidy
                            (text ())
                            { Cursors = all
                              Primary = all.Length - 1 }
                    )

                    e.Handled <- true
            elif not (e.KeyModifiers.HasFlag wanted) then
                clear ()

    /// The other cursors follow the text when it is changed some other way (a format, a rename, a reload).
    let onDocChanged (e: DocumentChangeEventArgs) =
        if not applying && not extras.IsEmpty then
            let move (o: int) =
                e.GetNewOffset(o, AnchorMovementType.AfterInsertion)

            extras <-
                extras
                |> List.map (fun c ->
                    { Anchor = move c.Anchor
                      Caret = move c.Caret })

            repaint ()

    do
        area.TextView.BackgroundRenderers.Add(ExtraCursorsRenderer(editor, (fun () -> extras)))

        area.AddHandler(
            InputElement.KeyDownEvent,
            EventHandler<KeyEventArgs>(fun _ e -> onKey e),
            RoutingStrategies.Tunnel
        )

        area.AddHandler(
            InputElement.TextInputEvent,
            EventHandler<TextInputEventArgs>(fun _ e -> onTextInput e),
            RoutingStrategies.Tunnel
        )

        area.AddHandler(
            InputElement.PointerPressedEvent,
            EventHandler<PointerPressedEventArgs>(fun _ e -> onPointer e),
            RoutingStrategies.Tunnel
        )

        // one handler for every document: tabs swap the editor's document, and the cursors belong to the one shown
        let changed = EventHandler<DocumentChangeEventArgs>(fun _ e -> onDocChanged e)

        area.DocumentChanged.Add(fun args ->
            if not (isNull args.OldDocument) then
                args.OldDocument.Changed.RemoveHandler changed

            if not (isNull args.NewDocument) then
                args.NewDocument.Changed.AddHandler changed

            clear ())

        if not (isNull editor.Document) then
            editor.Document.Changed.AddHandler changed

    member _.AddCursorBelow() = addLine 1
    member _.AddCursorAbove() = addLine -1
    member _.SelectNextMatch() = addNext ()
    member _.SelectAllMatches() = addAll ()
    member _.Clear() = clear ()

    /// The cursors other than the editor's own caret: for the tests.
    member _.Extras = extras
    member _.Count = extras.Length + 1
