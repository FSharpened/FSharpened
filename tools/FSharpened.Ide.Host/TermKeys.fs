namespace FSharpened.Ide.Host

open System

/// A key as a terminal program sees it, free of any UI library: the desktop window turns its own key events into these.
type TermKey =
    /// A printable character (what was typed, one character or one pair of surrogates).
    | Char of string
    | Enter
    | Tab
    | Backspace
    | Escape
    | Up
    | Down
    | Left
    | Right
    | Home
    | End
    | PageUp
    | PageDown
    | Insert
    | Delete
    /// F1 to F12.
    | F of int

type TermMods = { Ctrl: bool; Alt: bool; Shift: bool }

module TermKeys =
    let noMods =
        { Ctrl = false
          Alt = false
          Shift = false }

    let private esc = "\u001b"

    /// The xterm modifier parameter: 1 + shift + 2 alt + 4 ctrl (so 1 means none).
    let private modParam (m: TermMods) =
        1
        + (if m.Shift then 1 else 0)
        + (if m.Alt then 2 else 0)
        + (if m.Ctrl then 4 else 0)

    let private anyMod (m: TermMods) = m.Ctrl || m.Alt || m.Shift

    /// A cursor-style key: `ESC [ A`, `ESC O A` in application mode, `ESC [ 1 ; 5 A` with modifiers.
    let private cursorKey (appCursor: bool) (m: TermMods) (final: char) =
        if anyMod m then sprintf "%s[1;%d%c" esc (modParam m) final
        elif appCursor then sprintf "%sO%c" esc final
        else sprintf "%s[%c" esc final

    /// A key that ends in `~`: `ESC [ 3 ~`, `ESC [ 3 ; 5 ~`.
    let private tildeKey (m: TermMods) (n: int) =
        if anyMod m then
            sprintf "%s[%d;%d~" esc n (modParam m)
        else
            sprintf "%s[%d~" esc n

    /// The control character for Ctrl plus a character, if there is one (Ctrl+C is 3, Ctrl+[ is Escape).
    let private control (c: char) : char option =
        let u = Char.ToUpperInvariant c

        if u >= 'A' && u <= 'Z' then
            Some(char (int u - 64))
        else
            match c with
            | ' '
            | '@'
            | '2' -> Some '\000'
            | '['
            | '3' -> Some '\u001b'
            | '\\'
            | '4' -> Some '\u001c'
            | ']'
            | '5' -> Some '\u001d'
            | '^'
            | '6' -> Some '\u001e'
            | '_'
            | '7'
            | '/' -> Some '\u001f'
            | '?'
            | '8' -> Some '\u007f'
            | _ -> None

    /// What to send the program for one key press. `appCursor` is the screen's application-cursor mode (arrows send
    /// `ESC O A`). `None` means the key means nothing to a terminal (the window may use it itself).
    let encode (appCursor: bool) (m: TermMods) (key: TermKey) : string option =
        let withAlt (s: string) = if m.Alt then esc + s else s

        match key with
        | Char s when s <> "" ->
            if m.Ctrl && s.Length = 1 then
                control s[0] |> Option.map (fun c -> withAlt (string c))
            else
                Some(withAlt s)
        | Char _ -> None
        | Enter -> Some(withAlt "\r")
        | Tab -> Some(if m.Shift then esc + "[Z" else withAlt "\t")
        | Backspace -> Some(withAlt (if m.Ctrl then "\b" else "\u007f"))
        | Escape -> Some esc
        | Up -> Some(cursorKey appCursor m 'A')
        | Down -> Some(cursorKey appCursor m 'B')
        | Right -> Some(cursorKey appCursor m 'C')
        | Left -> Some(cursorKey appCursor m 'D')
        | Home -> Some(cursorKey appCursor m 'H')
        | End -> Some(cursorKey appCursor m 'F')
        | Insert -> Some(tildeKey m 2)
        | Delete -> Some(tildeKey m 3)
        | PageUp -> Some(tildeKey m 5)
        | PageDown -> Some(tildeKey m 6)
        | F n when n >= 1 && n <= 4 ->
            let final = "PQRS"[n - 1]

            Some(
                if anyMod m then
                    sprintf "%s[1;%d%c" esc (modParam m) final
                else
                    sprintf "%sO%c" esc final
            )
        | F n when n >= 5 && n <= 12 ->
            let code =
                match n with
                | 5 -> 15
                | 6 -> 17
                | 7 -> 18
                | 8 -> 19
                | 9 -> 20
                | 10 -> 21
                | 11 -> 23
                | _ -> 24

            Some(tildeKey m code)
        | F _ -> None

    /// What to send for pasted text: line breaks become Enter, and with bracketed paste on the whole text is wrapped, so
    /// an editor or shell knows it was pasted and does not run it line by line.
    let paste (bracketed: bool) (text: string) : string =
        let body = text.Replace("\r\n", "\r").Replace('\n', '\r')

        if bracketed then
            esc + "[200~" + body.Replace(esc, "") + esc + "[201~"
        else
            body
