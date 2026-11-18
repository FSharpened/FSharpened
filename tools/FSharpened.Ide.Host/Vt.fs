namespace FSharpened.Ide.Host

open System
open System.Collections.Generic
open System.Text

/// A colour a program asked for: the terminal's own default, one of the 256 indexed colours, or a true colour.
type TermColor =
    | Default
    | Indexed of int
    | Rgb of byte * byte * byte

/// How one cell is drawn.
[<Struct>]
type CellStyle =
    { Fg: TermColor
      Bg: TermColor
      Bold: bool
      Dim: bool
      Italic: bool
      Underline: bool
      Inverse: bool
      Strike: bool }

/// One character position of the screen. `Cp` is the Unicode code point, 0 for an empty cell, and -1 for the second
/// half of a double-width character (nothing to draw there).
[<Struct>]
type Cell = { Cp: int; Style: CellStyle }

module Vt =
    let plain: CellStyle =
        { Fg = Default
          Bg = Default
          Bold = false
          Dim = false
          Italic = false
          Underline = false
          Inverse = false
          Strike = false }

    /// The sixteen basic colours (the common “VS Code” set, readable on a dark and, darkened, on a light background).
    let basic16Dark: (byte * byte * byte)[] =
        [| 0uy, 0uy, 0uy
           205uy, 49uy, 49uy
           13uy, 188uy, 121uy
           229uy, 229uy, 16uy
           36uy, 114uy, 200uy
           188uy, 63uy, 188uy
           17uy, 168uy, 205uy
           229uy, 229uy, 229uy
           102uy, 102uy, 102uy
           241uy, 76uy, 76uy
           35uy, 209uy, 139uy
           245uy, 245uy, 67uy
           59uy, 142uy, 234uy
           214uy, 112uy, 214uy
           41uy, 184uy, 219uy
           255uy, 255uy, 255uy |]

    let basic16Light: (byte * byte * byte)[] =
        [| 0uy, 0uy, 0uy
           205uy, 49uy, 49uy
           0uy, 134uy, 0uy
           148uy, 152uy, 0uy
           4uy, 81uy, 165uy
           188uy, 5uy, 188uy
           5uy, 152uy, 188uy
           85uy, 85uy, 85uy
           102uy, 102uy, 102uy
           205uy, 49uy, 49uy
           20uy, 206uy, 20uy
           181uy, 186uy, 0uy
           4uy, 81uy, 165uy
           188uy, 5uy, 188uy
           5uy, 152uy, 188uy
           60uy, 60uy, 60uy |]

    /// The colour of an indexed entry: 0-15 from `basic`, 16-231 the 6x6x6 cube, 232-255 the grey ramp.
    let indexedRgb (basic: (byte * byte * byte)[]) (n: int) : byte * byte * byte =
        if n < 0 then
            basic[0]
        elif n < 16 then
            basic[n]
        elif n < 232 then
            let level (v: int) =
                if v = 0 then 0uy else byte (55 + 40 * v)

            let c = n - 16
            level (c / 36), level (c / 6 % 6), level (c % 6)
        else
            let g = byte (8 + 10 * (min n 255 - 232))
            g, g, g

    /// Whether a code point takes two cells (East Asian wide characters, emoji).
    let isWide (cp: int) : bool =
        (cp >= 0x1100 && cp <= 0x115F)
        || (cp >= 0x2E80 && cp <= 0xA4CF && cp <> 0x303F)
        || (cp >= 0xAC00 && cp <= 0xD7A3)
        || (cp >= 0xF900 && cp <= 0xFAFF)
        || (cp >= 0xFE30 && cp <= 0xFE6F)
        || (cp >= 0xFF00 && cp <= 0xFF60)
        || (cp >= 0xFFE0 && cp <= 0xFFE6)
        || (cp >= 0x1F300 && cp <= 0x1F64F)
        || (cp >= 0x1F900 && cp <= 0x1F9FF)
        || (cp >= 0x20000 && cp <= 0x3FFFD)

    /// Whether a code point is drawn on the character before it (accents, joiners, variation selectors).
    let isZeroWidth (cp: int) : bool =
        (cp >= 0x0300 && cp <= 0x036F)
        || (cp >= 0x200B && cp <= 0x200F)
        || (cp >= 0xFE00 && cp <= 0xFE0F)
        || cp = 0x2060

    /// The DEC special graphics set (line drawing), used by programs that draw boxes with `ESC ( 0`.
    let lineDrawing (cp: int) : int =
        match char cp with
        | 'j' -> 0x2518
        | 'k' -> 0x2510
        | 'l' -> 0x250C
        | 'm' -> 0x2514
        | 'n' -> 0x253C
        | 'q' -> 0x2500
        | 't' -> 0x251C
        | 'u' -> 0x2524
        | 'v' -> 0x2534
        | 'w' -> 0x252C
        | 'x' -> 0x2502
        | 'a' -> 0x2592
        | '`' -> 0x25C6
        | 'f' -> 0x00B0
        | 'g' -> 0x00B1
        | '~' -> 0x00B7
        | 'o'
        | 'p' -> 0x23BA
        | 'r'
        | 's' -> 0x23BB
        | _ -> cp

type private ParseState =
    | Ground
    | Escape
    | Designate of char
    | Csi
    | Osc
    | OscEscape
    | Ignored
    | IgnoredEscape

/// A terminal screen: a grid of cells fed with what a program prints, as a VT100/xterm-style terminal would show it
/// (ADR-0023 / PLAN P7.8). It understands cursor movement, erasing, scrolling regions, colours (16, 256 and true colour),
/// bold/italic/underline/inverse, line drawing, the alternate screen, bracketed paste and the replies a shell asks for
/// (cursor position, device attributes). Mouse reporting, double-width lines and sixel images are not supported.
/// Not thread-safe: use one thread, or lock around it.
type Screen(cols0: int, rows0: int, reply: string -> unit) =
    let maxScrollback = 5000
    let mutable cols = max 1 cols0
    let mutable rows = max 1 rows0

    let blankRow (n: int) (bg: CellStyle) : Cell[] = Array.create n { Cp = 0; Style = bg }

    let mutable main: Cell[][] = Array.init rows (fun _ -> blankRow cols Vt.plain)
    let mutable alt: Cell[][] = Array.init rows (fun _ -> blankRow cols Vt.plain)
    let mutable grid = main
    let mutable inAlt = false
    let scrollback = List<Cell[]>()
    let mutable cx = 0
    let mutable cy = 0
    let mutable wrapPending = false
    let mutable style = Vt.plain
    let mutable top = 0
    let mutable bottom = rows - 1
    let mutable saved = (0, 0, Vt.plain, false)
    let mutable savedMain = (0, 0)
    let mutable appCursor = false
    let mutable bracketed = false
    let mutable autowrap = true
    let mutable cursorVisible = true
    let mutable origin = false
    let mutable insertMode = false
    let mutable lineDraw = false
    let mutable title = ""
    let mutable version = 0
    let mutable lastPrinted = 32

    // the parser
    let mutable state = Ground
    let args = List<int>()
    let mutable argAcc = -1
    let mutable priv = '\000'
    let mutable colon = false
    let mutable inter = false
    let oscText = StringBuilder()
    let mutable highSurrogate = 0

    let bgStyle () = { Vt.plain with Bg = style.Bg }

    let scrollUp (n: int) =
        for _ in 1..n do
            let gone = grid[top]

            if not inAlt && top = 0 then
                scrollback.Add gone

                if scrollback.Count > maxScrollback then
                    scrollback.RemoveAt 0

            for r in top .. bottom - 1 do
                grid[r] <- grid[r + 1]

            grid[bottom] <- blankRow cols (bgStyle ())

    let scrollDown (n: int) =
        for _ in 1..n do
            for r in bottom .. -1 .. top + 1 do
                grid[r] <- grid[r - 1]

            grid[top] <- blankRow cols (bgStyle ())

    let lineFeed () =
        if cy = bottom then
            scrollUp 1
        elif cy < rows - 1 then
            cy <- cy + 1

    let clampCursor () =
        cx <- max 0 (min (cols - 1) cx)
        cy <- max 0 (min (rows - 1) cy)

    let moveTo (r: int) (c: int) =
        wrapPending <- false

        if origin then
            cy <- max top (min bottom (top + r))
        else
            cy <- max 0 (min (rows - 1) r)

        cx <- max 0 (min (cols - 1) c)

    let eraseCells (row: int) (fromCol: int) (toCol: int) =
        let bg = bgStyle ()

        for c in max 0 fromCol .. min (cols - 1) toCol do
            grid[row][c] <- { Cp = 0; Style = bg }

    let put (cp: int) (width: int) =
        if wrapPending && autowrap then
            cx <- 0
            wrapPending <- false
            lineFeed ()
        elif wrapPending then
            wrapPending <- false

        // a double-width character that does not fit wraps first
        if width = 2 && cx = cols - 1 && autowrap then
            cx <- 0
            lineFeed ()

        if insertMode then
            let row = grid[cy]

            for c in cols - 1 .. -1 .. cx + width do
                row[c] <- row[c - width]

        grid[cy][cx] <- { Cp = cp; Style = style }

        if width = 2 && cx + 1 < cols then
            grid[cy][cx + 1] <- { Cp = -1; Style = style }

        if cx + width >= cols then
            cx <- cols - 1
            wrapPending <- true
        else
            cx <- cx + width

        lastPrinted <- cp

    let print (cp0: int) =
        let cp =
            if lineDraw && cp0 >= 0x5f && cp0 <= 0x7e then
                Vt.lineDrawing cp0
            else
                cp0

        if Vt.isZeroWidth cp then
            ()
        else
            put cp (if Vt.isWide cp then 2 else 1)

    let switchScreen (toAlt: bool) =
        if toAlt <> inAlt then
            if toAlt then
                savedMain <- (cx, cy)
                grid <- alt
                inAlt <- true

                for r in 0 .. rows - 1 do
                    alt[r] <- blankRow cols Vt.plain

                cx <- 0
                cy <- 0
            else
                grid <- main
                inAlt <- false
                cx <- fst savedMain
                cy <- snd savedMain
                clampCursor ()

            top <- 0
            bottom <- rows - 1
            wrapPending <- false

    let arg (i: int) (def: int) =
        if i < args.Count && args[i] > 0 then args[i] else def

    let setMode (on: bool) =
        for p in args do
            if priv = '?' then
                match p with
                | 1 -> appCursor <- on
                | 6 ->
                    origin <- on
                    moveTo 0 0
                | 7 -> autowrap <- on
                | 25 -> cursorVisible <- on
                | 47
                | 1047 -> switchScreen on
                | 1049 -> switchScreen on
                | 2004 -> bracketed <- on
                | _ -> ()
            elif priv = '\000' then
                match p with
                | 4 -> insertMode <- on
                | _ -> ()

    let sgr () =
        if args.Count = 0 then
            style <- Vt.plain
        else
            let mutable i = 0

            let extended () : TermColor option =
                // 38;5;n  38;2;r;g;b  and the colon forms 38:2::r:g:b
                if i + 1 < args.Count then
                    match args[i + 1] with
                    | 5 when i + 2 < args.Count ->
                        let c = Indexed(max 0 (min 255 args[i + 2]))
                        i <- i + 2
                        Some c
                    | 2 ->
                        let skip = if colon && i + 5 < args.Count then 1 else 0

                        let at k =
                            byte (max 0 (min 255 args[i + 2 + skip + k]))

                        if i + 4 + skip < args.Count then
                            let c = Rgb(at 0, at 1, at 2)
                            i <- i + 4 + skip
                            Some c
                        else
                            i <- args.Count
                            None
                    | _ ->
                        i <- i + 1
                        None
                else
                    i <- args.Count
                    None

            while i < args.Count do
                let p = max 0 args[i]

                match p with
                | 0 -> style <- Vt.plain
                | 1 -> style <- { style with Bold = true }
                | 2 -> style <- { style with Dim = true }
                | 3 -> style <- { style with Italic = true }
                | 4
                | 21 -> style <- { style with Underline = true }
                | 7 -> style <- { style with Inverse = true }
                | 9 -> style <- { style with Strike = true }
                | 22 -> style <- { style with Bold = false; Dim = false }
                | 23 -> style <- { style with Italic = false }
                | 24 -> style <- { style with Underline = false }
                | 27 -> style <- { style with Inverse = false }
                | 29 -> style <- { style with Strike = false }
                | _ when p >= 30 && p <= 37 -> style <- { style with Fg = Indexed(p - 30) }
                | 38 ->
                    match extended () with
                    | Some c -> style <- { style with Fg = c }
                    | None -> ()
                | 39 -> style <- { style with Fg = Default }
                | _ when p >= 40 && p <= 47 -> style <- { style with Bg = Indexed(p - 40) }
                | 48 ->
                    match extended () with
                    | Some c -> style <- { style with Bg = c }
                    | None -> ()
                | 49 -> style <- { style with Bg = Default }
                | _ when p >= 90 && p <= 97 -> style <- { style with Fg = Indexed(p - 90 + 8) }
                | _ when p >= 100 && p <= 107 -> style <- { style with Bg = Indexed(p - 100 + 8) }
                | _ -> ()

                i <- i + 1

    let reset () =
        main <- Array.init rows (fun _ -> blankRow cols Vt.plain)
        alt <- Array.init rows (fun _ -> blankRow cols Vt.plain)
        grid <- main
        inAlt <- false
        scrollback.Clear()
        cx <- 0
        cy <- 0
        wrapPending <- false
        style <- Vt.plain
        top <- 0
        bottom <- rows - 1
        appCursor <- false
        bracketed <- false
        autowrap <- true
        cursorVisible <- true
        origin <- false
        insertMode <- false
        lineDraw <- false

    let csi (final: char) =
        let n = arg 0 1

        match final with
        | '@' ->
            let row = grid[cy]
            let k = min n (cols - cx)

            for c in cols - 1 .. -1 .. cx + k do
                row[c] <- row[c - k]

            eraseCells cy cx (cx + k - 1)
        | 'A' ->
            wrapPending <- false
            cy <- max (if cy >= top then top else 0) (cy - n)
        | 'B'
        | 'e' ->
            wrapPending <- false
            cy <- min (if cy <= bottom then bottom else rows - 1) (cy + n)
        | 'C'
        | 'a' ->
            wrapPending <- false
            cx <- min (cols - 1) (cx + n)
        | 'D' ->
            wrapPending <- false
            cx <- max 0 (cx - n)
        | 'E' ->
            wrapPending <- false
            cx <- 0
            cy <- min (rows - 1) (cy + n)
        | 'F' ->
            wrapPending <- false
            cx <- 0
            cy <- max 0 (cy - n)
        | 'G'
        | '`' ->
            wrapPending <- false
            cx <- max 0 (min (cols - 1) (n - 1))
        | 'H'
        | 'f' -> moveTo (arg 0 1 - 1) (arg 1 1 - 1)
        | 'I' ->
            for _ in 1..n do
                cx <- min (cols - 1) ((cx / 8 + 1) * 8)
        | 'Z' ->
            for _ in 1..n do
                cx <- max 0 ((cx - 1) / 8 * 8)
        | 'J' ->
            match (if args.Count = 0 then 0 else max 0 args[0]) with
            | 0 ->
                eraseCells cy cx (cols - 1)

                for r in cy + 1 .. rows - 1 do
                    eraseCells r 0 (cols - 1)
            | 1 ->
                for r in 0 .. cy - 1 do
                    eraseCells r 0 (cols - 1)

                eraseCells cy 0 cx
            | 2 ->
                for r in 0 .. rows - 1 do
                    eraseCells r 0 (cols - 1)
            | 3 -> scrollback.Clear()
            | _ -> ()
        | 'K' ->
            match (if args.Count = 0 then 0 else max 0 args[0]) with
            | 0 -> eraseCells cy cx (cols - 1)
            | 1 -> eraseCells cy 0 cx
            | 2 -> eraseCells cy 0 (cols - 1)
            | _ -> ()
        | 'L' when cy >= top && cy <= bottom ->
            let savedTop = top
            top <- cy
            scrollDown (min n (bottom - cy + 1))
            top <- savedTop
            cx <- 0
        | 'M' when cy >= top && cy <= bottom ->
            let savedTop = top
            top <- cy
            scrollUp (min n (bottom - cy + 1))
            top <- savedTop
            cx <- 0
        | 'P' ->
            let row = grid[cy]
            let k = min n (cols - cx)

            for c in cx .. cols - 1 - k do
                row[c] <- row[c + k]

            eraseCells cy (cols - k) (cols - 1)
        | 'S' -> scrollUp n
        | 'T' -> scrollDown n
        | 'X' -> eraseCells cy cx (cx + n - 1)
        | 'b' ->
            for _ in 1..n do
                print lastPrinted
        | 'd' -> moveTo (arg 0 1 - 1) cx
        | 'm' -> sgr ()
        | 'n' ->
            match (if args.Count = 0 then 0 else args[0]) with
            | 5 -> reply "\u001b[0n"
            | 6 -> reply (sprintf "\u001b[%d;%dR" (cy + 1 - (if origin then top else 0)) (cx + 1))
            | _ -> ()
        | 'c' when priv = '\000' -> reply "\u001b[?1;2c"
        | 'c' when priv = '>' -> reply "\u001b[>0;0;0c"
        | 'r' when priv = '\000' ->
            let t = arg 0 1 - 1
            let b = (if args.Count > 1 && args[1] > 0 then args[1] else rows) - 1

            if t < b && b < rows then
                top <- t
                bottom <- b
                moveTo 0 0
        | 's' when priv = '\000' -> saved <- (cx, cy, style, origin)
        | 'u' when priv = '\000' ->
            let x, y, s, o = saved
            cx <- x
            cy <- y
            style <- s
            origin <- o
            wrapPending <- false
            clampCursor ()
        | 'h' -> setMode true
        | 'l' -> setMode false
        | _ -> ()

    let esc (c: char) =
        match c with
        | 'D' -> lineFeed ()
        | 'E' ->
            cx <- 0
            lineFeed ()
        | 'M' ->
            if cy = top then
                scrollDown 1
            elif cy > 0 then
                cy <- cy - 1
        | '7' -> saved <- (cx, cy, style, origin)
        | '8' ->
            let x, y, s, o = saved
            cx <- x
            cy <- y
            style <- s
            origin <- o
            wrapPending <- false
            clampCursor ()
        | 'c' -> reset ()
        | _ -> ()

    let finishOsc () =
        let t = oscText.ToString()
        let semi = t.IndexOf ';'

        if semi > 0 then
            match t.Substring(0, semi) with
            | "0"
            | "2" -> title <- t.Substring(semi + 1)
            | _ -> ()

        oscText.Clear() |> ignore

    let startSequence (next: ParseState) =
        args.Clear()
        argAcc <- -1
        priv <- '\000'
        colon <- false
        inter <- false
        state <- next

    let control (c: char) =
        match c with
        | '\b' ->
            if cx > 0 then
                cx <- cx - 1

            wrapPending <- false
        | '\t' ->
            wrapPending <- false
            cx <- min (cols - 1) ((cx / 8 + 1) * 8)
        | '\n'
        | '\u000b'
        | '\u000c' -> lineFeed ()
        | '\r' ->
            cx <- 0
            wrapPending <- false
        | '\u000e' -> () // shift out: only the one graphics set is supported, chosen by ESC ( 0
        | '\u000f' -> ()
        | _ -> ()

    let step (cp: int) =
        if cp < 0x20 || cp = 0x7f then
            match state with
            | Osc ->
                if cp = 7 then
                    finishOsc ()
                    state <- Ground
                elif cp = 0x1b then
                    state <- OscEscape
            | Ignored ->
                if cp = 0x1b then
                    state <- IgnoredEscape
            | OscEscape
            | IgnoredEscape -> if cp = 0x1b then () else state <- Ground
            | _ ->
                if cp = 0x1b then
                    startSequence Escape
                elif cp = 0x18 || cp = 0x1a then
                    state <- Ground
                elif cp <> 0x7f then
                    control (char cp)
        else
            match state with
            | Ground -> print cp
            | Escape ->
                if cp > 0xffff then
                    state <- Ground
                else
                    match char cp with
                    | '[' -> startSequence Csi
                    | ']' ->
                        oscText.Clear() |> ignore
                        state <- Osc
                    | 'P'
                    | 'X'
                    | '^'
                    | '_' -> state <- Ignored
                    | '('
                    | ')'
                    | '*'
                    | '+' -> state <- Designate(char cp)
                    | '#'
                    | ' '
                    | '%' -> state <- Designate '#'
                    | c ->
                        esc c
                        state <- Ground
            | Designate which ->
                if which = '(' then
                    lineDraw <- (cp = int '0')

                state <- Ground
            | Csi ->
                let c = if cp > 0xffff then '\000' else char cp

                if c >= '0' && c <= '9' then
                    argAcc <- (if argAcc < 0 then 0 else argAcc) * 10 + (cp - int '0')

                    if argAcc > 99999 then
                        argAcc <- 99999
                elif c = ';' || c = ':' then
                    args.Add argAcc
                    argAcc <- -1

                    if c = ':' then
                        colon <- true
                elif c = '?' || c = '>' || c = '<' || c = '=' then
                    priv <- c
                elif c >= ' ' && c <= '/' then
                    inter <- true
                elif c >= '@' && c <= '~' then
                    if argAcc >= 0 || args.Count > 0 then
                        args.Add argAcc

                    // an intermediate byte (such as the space in `CSI 2 SP q`) marks sequences nothing here uses
                    if not inter then
                        csi c

                    state <- Ground
                else
                    state <- Ground
            | Osc ->
                if oscText.Length < 4096 then
                    oscText.Append(Char.ConvertFromUtf32 cp) |> ignore
            | OscEscape ->
                // ESC \ ends the string; anything else abandons it
                if cp = int '\\' then
                    finishOsc ()
                    state <- Ground
                else
                    oscText.Clear() |> ignore
                    state <- Ground
            | Ignored -> ()
            | IgnoredEscape -> state <- Ground

    member _.Cols = cols
    member _.Rows = rows
    member _.CursorX = cx
    member _.CursorY = cy
    /// Whether the cursor is to be drawn (programs hide it while they paint).
    member _.CursorVisible = cursorVisible
    /// Whether the arrow keys send `ESC O A` (application mode) rather than `ESC [ A`.
    member _.AppCursor = appCursor
    /// Whether pasted text is to be wrapped in `ESC [ 200 ~` ... `ESC [ 201 ~`.
    member _.BracketedPaste = bracketed
    /// Whether a full-screen program is showing (the alternate screen), which has no scrollback.
    member _.InAlternateScreen = inAlt
    member _.Title = title
    /// Goes up with every change, so a view knows it must draw again.
    member _.Version = version
    member _.ScrollbackCount = if inAlt then 0 else scrollback.Count
    member _.Row(r: int) : Cell[] = grid[r]
    member _.ScrollbackRow(i: int) : Cell[] = scrollback[i]

    /// Takes what the program printed. Surrogate pairs may be split across calls.
    member _.Feed(text: string) =
        for ch in text do
            if Char.IsHighSurrogate ch then
                highSurrogate <- int ch
            elif Char.IsLowSurrogate ch && highSurrogate <> 0 then
                step (Char.ConvertToUtf32(char highSurrogate, ch))
                highSurrogate <- 0
            else
                highSurrogate <- 0
                step (int ch)

        version <- version + 1

    /// Changes the size, keeping what fits. Lines pushed off the top of the main screen go to the scrollback.
    member _.Resize(newCols: int, newRows: int) =
        let nc = max 1 newCols
        let nr = max 1 newRows

        if nc <> cols || nr <> rows then
            let fit (g: Cell[][]) (dropTop: int) =
                Array.init nr (fun r ->
                    let src = r + dropTop

                    let row = blankRow nc Vt.plain

                    if src >= 0 && src < g.Length then
                        Array.blit g[src] 0 row 0 (min nc g[src].Length)

                    row)

            let drop = if nr < rows then max 0 (cy - nr + 1) else 0

            if not inAlt then
                for r in 0 .. drop - 1 do
                    scrollback.Add main[r]

            let wasAlt = inAlt
            main <- fit main (if wasAlt then 0 else drop)
            alt <- fit alt (if wasAlt then drop else 0)
            grid <- if wasAlt then alt else main
            cy <- cy - drop
            cols <- nc
            rows <- nr
            top <- 0
            bottom <- rows - 1
            wrapPending <- false
            clampCursor ()
            version <- version + 1

    /// The text of one screen row, without trailing blanks (for tests and for copying).
    member _.RowText(r: int) : string =
        let sb = StringBuilder()

        for c in grid[r] do
            if c.Cp > 0 then
                sb.Append(Char.ConvertFromUtf32 c.Cp) |> ignore
            elif c.Cp = 0 then
                sb.Append ' ' |> ignore

        sb.ToString().TrimEnd()

    /// The visible screen as lines, without trailing blank lines.
    member this.Text() : string =
        let lines = [ for r in 0 .. rows - 1 -> this.RowText r ]
        String.Join("\n", lines).TrimEnd('\n')

    /// The text of a scrollback line.
    member _.ScrollbackText(i: int) : string =
        let sb = StringBuilder()

        for c in scrollback[i] do
            if c.Cp > 0 then
                sb.Append(Char.ConvertFromUtf32 c.Cp) |> ignore
            elif c.Cp = 0 then
                sb.Append ' ' |> ignore

        sb.ToString().TrimEnd()
