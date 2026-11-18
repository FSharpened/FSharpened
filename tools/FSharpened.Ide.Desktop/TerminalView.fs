/// The terminal panel's screen: a control that draws a `Screen` (the VT emulator in the host library) and turns key,
/// text and mouse events into what a terminal program expects (ADR-0023, PLAN P7.8).
namespace FSharpened.Ide.Desktop

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Media
open Avalonia.Threading
open FSharpened.Ide.Host

/// Avalonia key events as terminal keys. Pure, so it is tested without a window.
module TerminalKeys =
    let private mods (m: KeyModifiers) : TermMods =
        { Ctrl = m.HasFlag KeyModifiers.Control
          Alt = m.HasFlag KeyModifiers.Alt
          Shift = m.HasFlag KeyModifiers.Shift }

    /// The character a key stands for when Ctrl or Alt is held (printable keys without those arrive as text instead).
    let private character (key: Key) (shift: bool) : string option =
        if key >= Key.A && key <= Key.Z then
            let c = char (int 'a' + (int key - int Key.A))
            Some(string (if shift then Char.ToUpperInvariant c else c))
        elif key >= Key.D2 && key <= Key.D8 then
            Some(string (char (int '2' + (int key - int Key.D2))))
        else
            match key with
            | Key.Space -> Some " "
            | Key.OemOpenBrackets -> Some "["
            | Key.OemCloseBrackets -> Some "]"
            | Key.OemPipe
            | Key.OemBackslash -> Some "\\"
            | Key.OemQuestion -> Some "/"
            | Key.OemMinus -> Some(if shift then "_" else "-")
            | _ -> None

    /// The terminal key and modifiers for a key press, or `None` when the key is for the window, or when it is a
    /// printable character that arrives as text (the text event carries it, with the keyboard layout applied).
    let ofAvalonia (key: Key) (m: KeyModifiers) : (TermKey * TermMods) option =
        let tm = mods m

        if m.HasFlag KeyModifiers.Meta then
            None
        else
            let special =
                match key with
                | Key.Enter -> Some Enter
                | Key.Tab -> Some Tab
                | Key.Back -> Some Backspace
                | Key.Escape -> Some Escape
                | Key.Up -> Some Up
                | Key.Down -> Some Down
                | Key.Left -> Some Left
                | Key.Right -> Some Right
                | Key.Home -> Some Home
                | Key.End -> Some End
                | Key.PageUp -> Some PageUp
                | Key.PageDown -> Some PageDown
                | Key.Insert -> Some Insert
                | Key.Delete -> Some Delete
                | _ when key >= Key.F1 && key <= Key.F12 -> Some(F(int key - int Key.F1 + 1))
                | _ -> None

            match special with
            | Some k -> Some(k, tm)
            | None ->
                // Ctrl+Alt is how Windows reports AltGr, which types a character: leave it to the text event
                if (tm.Ctrl || tm.Alt) && not (tm.Ctrl && tm.Alt) then
                    character key tm.Shift |> Option.map (fun c -> Char c, tm)
                else
                    None

    /// Whether the window keeps a key for its own shortcut while the terminal has the focus (the way other editors let
    /// a few through): toggling the panel, the sidebar, the palette and quick open. Everything else is the shell's.
    let windowKeeps (key: Key) (m: KeyModifiers) : bool =
        let ctrl = m.HasFlag KeyModifiers.Control
        let shift = m.HasFlag KeyModifiers.Shift
        let alt = m.HasFlag KeyModifiers.Alt

        if not ctrl || alt then
            false
        elif shift then
            match key with
            | Key.P
            | Key.E
            | Key.F
            | Key.H
            | Key.M -> true
            | _ -> false
        else
            match key with
            | Key.OemTilde
            | Key.J
            | Key.B
            | Key.P
            | Key.OemPlus
            | Key.OemMinus -> true
            | _ -> false

/// Colour resolution for the terminal: indexed and true colours over the theme's default foreground and background.
module TerminalColors =
    let toColor (r: byte, g: byte, b: byte) = Color.FromRgb(r, g, b)

    /// The colour of a terminal colour. `bold` brightens the eight basic foreground colours, as most terminals do.
    let resolve (basic: (byte * byte * byte)[]) (fallback: Color) (bold: bool) (c: TermColor) : Color =
        match c with
        | Default -> fallback
        | Rgb(r, g, b) -> Color.FromRgb(r, g, b)
        | Indexed n ->
            let n = if bold && n < 8 then n + 8 else n
            toColor (Vt.indexedRgb basic n)

    /// A colour blended toward another (dim text is the foreground half-way to the background).
    let blend (a: Color) (b: Color) (t: float) : Color =
        let mix (x: byte) (y: byte) =
            byte (float x * (1.0 - t) + float y * t)

        Color.FromRgb(mix a.R b.R, mix a.G b.G, mix a.B b.B)

/// The terminal control. Feed it output with `Feed` (from any thread); it raises `Input` with what to send the shell and
/// `SizeChanged2` when the number of columns or rows changes.
type TerminalView() as this =
    inherit Control()

    let maxPending = 4_000_000
    let pending = StringBuilder()
    let pendingLock = obj ()
    let mutable scheduled = false

    let mutable screen: Screen = Unchecked.defaultof<Screen>
    let mutable cellW = 8.0
    let mutable cellH = 16.0

    let mutable font =
        FontFamily "Cascadia Code, Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"

    let mutable fontSize = 13.0
    let mutable fg = Colors.Silver
    let mutable bg = Colors.Black
    let mutable selectionColor = Color.FromArgb(90uy, 80uy, 140uy, 255uy)
    let mutable basic = Vt.basic16Dark
    let mutable scrollOffset = 0
    let mutable lastScrollback = 0
    let mutable selStart: (int * int) option = None
    let mutable selEnd: (int * int) option = None
    let mutable selecting = false
    let brushes = Dictionary<Color, IBrush>()

    let brush (c: Color) : IBrush =
        match brushes.TryGetValue c with
        | true, b -> b
        | _ ->
            let b = SolidColorBrush c :> IBrush
            brushes[c] <- b
            b

    let typeface (s: CellStyle) =
        Typeface(
            font,
            (if s.Italic then FontStyle.Italic else FontStyle.Normal),
            (if s.Bold then FontWeight.Bold else FontWeight.Normal)
        )

    let formatted (text: string) (s: CellStyle) (color: Color) =
        FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface s, fontSize, brush color)

    let measure () =
        let ft = formatted "MMMMMMMMMM" Vt.plain fg
        cellW <- max 1.0 (ft.Width / 10.0)
        cellH <- max 1.0 (Math.Ceiling ft.Height)

    let input = Event<string>()
    let resized = Event<int * int>()

    do
        screen <- Screen(100, 24, (fun reply -> input.Trigger reply))
        measure ()
        this.Focusable <- true
        this.ClipToBounds <- true
        this.Cursor <- new Cursor(StandardCursorType.Ibeam)

    /// The cell under a point of the control, as (row, column).
    let cellAt (p: Point) =
        let r = max 0 (min (screen.Rows - 1) (int (p.Y / cellH)))
        let c = max 0 (min (screen.Cols - 1) (int (p.X / cellW)))
        r, c

    /// The row shown at screen line `i`, counting the scrollback that is scrolled into view.
    let displayRow (i: int) : Cell[] =
        let idx = screen.ScrollbackCount - scrollOffset + i

        if idx < 0 then
            Array.empty
        elif idx < screen.ScrollbackCount then
            screen.ScrollbackRow idx
        else
            screen.Row(idx - screen.ScrollbackCount)

    let selectionText () : string option =
        match selStart, selEnd with
        | Some a, Some b when a <> b ->
            let (r0, c0), (r1, c1) = if a <= b then a, b else b, a
            let sb = StringBuilder()

            for r in r0..r1 do
                let row = displayRow r
                let from = if r = r0 then c0 else 0
                let upTo = if r = r1 then c1 else row.Length - 1
                let line = StringBuilder()

                for c in from .. min upTo (row.Length - 1) do
                    if row[c].Cp > 0 then
                        line.Append(Char.ConvertFromUtf32 row[c].Cp) |> ignore
                    elif row[c].Cp = 0 then
                        line.Append ' ' |> ignore

                sb.Append(line.ToString().TrimEnd()) |> ignore

                if r < r1 then
                    sb.Append '\n' |> ignore

            Some(sb.ToString())
        | _ -> None

    let clearSelection () =
        selStart <- None
        selEnd <- None

    let send (text: string) =
        scrollOffset <- 0
        input.Trigger text

    let drain () =
        let text =
            lock pendingLock (fun () ->
                let t = pending.ToString()
                pending.Clear() |> ignore
                scheduled <- false
                t)

        if text <> "" then
            screen.Feed text
            // keep what is being read where it is when more arrives below
            let count = screen.ScrollbackCount

            if scrollOffset > 0 then
                scrollOffset <- min count (scrollOffset + max 0 (count - lastScrollback))

            lastScrollback <- count
            clearSelection ()
            this.InvalidateVisual()

    let recomputeSizeOf (w: float) (h: float) =
        if w > 0.0 && h > 0.0 then
            let cols = max 2 (int (w / cellW))
            let rows = max 1 (int (h / cellH))

            if cols <> screen.Cols || rows <> screen.Rows then
                screen.Resize(cols, rows)
                scrollOffset <- min scrollOffset screen.ScrollbackCount
                resized.Trigger((cols, rows))
                this.InvalidateVisual()

    let recomputeSize () =
        recomputeSizeOf this.Bounds.Width this.Bounds.Height

    /// What to send the shell: typed keys, pasted text and the replies a program asked for.
    [<CLIEvent>]
    member _.Input = input.Publish

    /// The columns and rows that now fit in the control.
    [<CLIEvent>]
    member _.Resized = resized.Publish

    member _.Cols = screen.Cols
    member _.Rows = screen.Rows
    member _.Screen = screen
    member _.Title = screen.Title

    /// Hands output to the screen. Safe from any thread; drawing happens once per burst on the UI thread.
    member _.Feed(text: string) =
        let post =
            lock pendingLock (fun () ->
                if pending.Length < maxPending then
                    pending.Append text |> ignore

                let p = not scheduled
                scheduled <- true
                p)

        if post then
            Dispatcher.UIThread.Post(drain, DispatcherPriority.Background)

    /// Empties the screen and the scrollback (the shell's prompt comes back on its next line).
    member _.Clear() =
        screen.Feed "\u001b[2J\u001b[3J\u001b[H"
        scrollOffset <- 0
        lastScrollback <- 0
        clearSelection ()
        this.InvalidateVisual()

    /// Applies the theme: the default colours, whether the sixteen basic colours are the dark or the light set, and the
    /// selection colour.
    member _.SetColors(foreground: Color, background: Color, dark: bool, selection: Color) =
        fg <- foreground
        bg <- background
        basic <- if dark then Vt.basic16Dark else Vt.basic16Light
        selectionColor <- selection
        this.InvalidateVisual()

    member _.SetFont(family: FontFamily, size: float) =
        font <- family
        fontSize <- size
        measure ()
        recomputeSize ()
        this.InvalidateVisual()

    /// Sends text as if pasted (line breaks become Enter; wrapped when the program asked for bracketed paste).
    member _.Paste(text: string) =
        if text <> "" then
            send (TermKeys.paste screen.BracketedPaste text)

    /// Copies the selection to the clipboard; false when nothing is selected.
    member _.Copy() : bool =
        match selectionText () with
        | Some t ->
            match TopLevel.GetTopLevel this with
            | null -> false
            | top ->
                top.Clipboard.SetTextAsync t |> ignore
                clearSelection ()
                this.InvalidateVisual()
                true
        | None -> false

    member private _.PasteFromClipboard() =
        match TopLevel.GetTopLevel this with
        | null -> ()
        | top ->
            task {
                let! text = top.Clipboard.TryGetTextAsync()

                if not (isNull text) then
                    Dispatcher.UIThread.Post(fun () -> this.Paste text)
            }
            |> ignore

    override _.ArrangeOverride(finalSize: Size) =
        // `Bounds` still holds the old size here: the new one is the argument
        recomputeSizeOf finalSize.Width finalSize.Height
        base.ArrangeOverride finalSize

    override _.Render(ctx: DrawingContext) =
        let w = this.Bounds.Width
        let h = this.Bounds.Height
        ctx.FillRectangle(brush bg, Rect(0.0, 0.0, w, h))
        let focused = this.IsFocused
        let sel = selectionText ()
        let dim (c: Color) = TerminalColors.blend c bg 0.45

        for i in 0 .. screen.Rows - 1 do
            let row = displayRow i
            let y = float i * cellH
            let n = min row.Length screen.Cols
            let mutable c = 0

            while c < n do
                let start = c
                let first = row[c]
                let st = first.Style
                let wide = first.Cp > 0xFFFF || Vt.isWide first.Cp
                c <- c + 1

                // a run is a stretch of cells that look alike; a wide character is a run by itself
                while not wide
                      && c < n
                      && row[c].Style = st
                      && not (row[c].Cp > 0xFFFF || Vt.isWide row[c].Cp) do
                    c <- c + 1

                let span = c - start
                let spanCells = if wide then 2 else span
                let mutable fgc = TerminalColors.resolve basic fg st.Bold st.Fg
                let mutable bgc = TerminalColors.resolve basic bg false st.Bg

                if st.Inverse then
                    let t = fgc
                    fgc <- bgc
                    bgc <- t

                if st.Dim then
                    fgc <- dim fgc

                let x = float start * cellW

                if bgc <> bg || st.Inverse then
                    ctx.FillRectangle(brush bgc, Rect(x, y, float spanCells * cellW, cellH))

                let text =
                    let sb = StringBuilder()

                    for k in start .. c - 1 do
                        let cp = row[k].Cp

                        if cp = 0 then
                            sb.Append ' ' |> ignore
                        elif cp > 0 then
                            sb.Append(Char.ConvertFromUtf32 cp) |> ignore

                    sb.ToString()

                if text.Trim() <> "" then
                    ctx.DrawText(formatted text st fgc, Point(x, y))

                if st.Underline then
                    ctx.DrawLine(
                        Pen(brush fgc, 1.0),
                        Point(x, y + cellH - 2.0),
                        Point(x + float spanCells * cellW, y + cellH - 2.0)
                    )

                if st.Strike then
                    ctx.DrawLine(
                        Pen(brush fgc, 1.0),
                        Point(x, y + cellH / 2.0),
                        Point(x + float spanCells * cellW, y + cellH / 2.0)
                    )

        // the selection
        match selStart, selEnd with
        | Some a, Some b when sel.IsSome ->
            let (r0, c0), (r1, c1) = if a <= b then a, b else b, a

            for r in r0..r1 do
                let from = if r = r0 then c0 else 0
                let upTo = if r = r1 then c1 else screen.Cols - 1

                ctx.FillRectangle(
                    brush selectionColor,
                    Rect(float from * cellW, float r * cellH, float (upTo - from + 1) * cellW, cellH)
                )
        | _ -> ()

        // the cursor (only on the live screen)
        if scrollOffset = 0 && screen.CursorVisible then
            let r =
                Rect(float screen.CursorX * cellW, float screen.CursorY * cellH, cellW, cellH)

            if focused then
                ctx.FillRectangle(brush (Color.FromArgb(170uy, fg.R, fg.G, fg.B)), r)
            else
                ctx.DrawRectangle(Pen(brush fg, 1.0), r.Deflate 0.5)

    override _.OnGotFocus(e) =
        base.OnGotFocus e
        this.InvalidateVisual()

    override _.OnLostFocus(e) =
        base.OnLostFocus e
        this.InvalidateVisual()

    override _.OnKeyDown(e: KeyEventArgs) =
        let m = e.KeyModifiers
        let ctrl = m.HasFlag KeyModifiers.Control
        let shift = m.HasFlag KeyModifiers.Shift

        match e.Key with
        | Key.V when ctrl && shift ->
            e.Handled <- true
            this.PasteFromClipboard()
        | Key.Insert when shift && not ctrl ->
            e.Handled <- true
            this.PasteFromClipboard()
        | Key.C when ctrl && shift ->
            e.Handled <- true
            this.Copy() |> ignore
        | Key.C when ctrl && (selectionText ()).IsSome ->
            // with something selected Ctrl+C copies, as other terminals do; otherwise it is the interrupt
            e.Handled <- true
            this.Copy() |> ignore
        | Key.PageUp when shift ->
            e.Handled <- true
            scrollOffset <- min screen.ScrollbackCount (scrollOffset + max 1 (screen.Rows - 1))
            this.InvalidateVisual()
        | Key.PageDown when shift ->
            e.Handled <- true
            scrollOffset <- max 0 (scrollOffset - max 1 (screen.Rows - 1))
            this.InvalidateVisual()
        | _ ->
            if not (TerminalKeys.windowKeeps e.Key m) then
                match TerminalKeys.ofAvalonia e.Key m with
                | Some(k, tm) ->
                    match TermKeys.encode screen.AppCursor tm k with
                    | Some s ->
                        e.Handled <- true
                        clearSelection ()
                        send s
                    | None -> ()
                | None -> ()

    override _.OnTextInput(e: TextInputEventArgs) =
        if not (String.IsNullOrEmpty e.Text) then
            e.Handled <- true
            clearSelection ()
            send e.Text

    override _.OnPointerWheelChanged(e: PointerWheelEventArgs) =
        e.Handled <- true
        let lines = int (Math.Round(e.Delta.Y * 3.0))

        if screen.InAlternateScreen then
            // a full-screen program has no scrollback: the wheel moves its cursor, as xterm's alternate scroll does
            let key = if lines > 0 then Up else Down

            match TermKeys.encode screen.AppCursor TermKeys.noMods key with
            | Some s -> input.Trigger(String.replicate (abs lines) s)
            | None -> ()
        else
            scrollOffset <- max 0 (min screen.ScrollbackCount (scrollOffset + lines))
            clearSelection ()
            this.InvalidateVisual()

    override _.OnPointerPressed(e: PointerPressedEventArgs) =
        this.Focus() |> ignore
        let p = e.GetCurrentPoint this

        if p.Properties.IsLeftButtonPressed then
            let cell = cellAt p.Position
            selStart <- Some cell
            selEnd <- Some cell
            selecting <- true
            e.Pointer.Capture this |> ignore
            this.InvalidateVisual()
        elif p.Properties.IsRightButtonPressed then
            // right click copies a selection, or pastes when there is none
            if not (this.Copy()) then
                this.PasteFromClipboard()

    override _.OnPointerMoved(e: PointerEventArgs) =
        if selecting then
            selEnd <- Some(cellAt (e.GetPosition this))
            this.InvalidateVisual()

    override _.OnPointerReleased(e: PointerReleasedEventArgs) =
        if selecting then
            selecting <- false
            e.Pointer.Capture null |> ignore
