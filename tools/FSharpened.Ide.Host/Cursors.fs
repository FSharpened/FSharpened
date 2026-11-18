/// Several cursors in one text: where they are, how typing, deleting, moving and selecting act on all of them at once.
/// Pure: the functions take the text and the cursors and return the edits to make and the cursors afterwards, so the
/// window only has to apply them (as one undo step) and draw the carets.
namespace FSharpened.Ide.Host

open System

/// A selection: `Anchor` is where it started, `Caret` where the caret stands (the caret may be before the anchor).
/// Offsets count characters from the start of the text. Empty when both are the same.
type EditCursor = { Anchor: int; Caret: int }

/// A replacement of `Length` characters at `Offset` (in the text before the edit) by `Text`.
type CursorEdit =
    { Offset: int
      Length: int
      Text: string }

/// The cursors, sorted by position and not overlapping; `Primary` indexes the one the editor shows as its own caret.
type CursorSet =
    { Cursors: EditCursor list
      Primary: int }

/// What an operation did: the edits (ascending, in the old text's offsets) and the cursors in the new text.
type CursorChange =
    { Edits: CursorEdit list
      Set: CursorSet }

type Motion =
    | MoveLeft
    | MoveRight
    | MoveUp
    | MoveDown
    | MoveLineStart
    | MoveLineEnd
    | MoveWordLeft
    | MoveWordRight

module Carets =
    /// More cursors than this are not made: each one costs a repaint and an edit.
    let maxCursors = 500

    let start (c: EditCursor) = min c.Anchor c.Caret
    let stop (c: EditCursor) = max c.Anchor c.Caret
    let isEmpty (c: EditCursor) = c.Anchor = c.Caret
    let caretAt (offset: int) = { Anchor = offset; Caret = offset }

    let single (c: EditCursor) = { Cursors = [ c ]; Primary = 0 }

    let primary (s: CursorSet) = s.Cursors[s.Primary]

    /// The cursors that are not the primary one.
    let extras (s: CursorSet) =
        s.Cursors
        |> List.indexed
        |> List.filter (fun (i, _) -> i <> s.Primary)
        |> List.map snd

    /// The primary cursor first, then the others in order: the shape the window keeps its own caret and the extras in.
    let ofPrimaryAndExtras (primary: EditCursor) (extras: EditCursor list) : CursorSet =
        let all = primary :: extras

        let order =
            all
            |> List.indexed
            |> List.sortBy (fun (_, c) -> start c, stop c)
            |> List.map fst

        let sorted = order |> List.map (fun i -> all[i])

        { Cursors = sorted
          Primary = order |> List.findIndex ((=) 0) }

    // ---- reading the text --------------------------------------------------------------------------

    let private isWordChar (c: char) = Char.IsLetterOrDigit c || c = '_'

    /// The offset where the line containing `offset` starts.
    let lineStart (text: string) (offset: int) =
        let o = max 0 (min offset text.Length)
        if o = 0 then 0 else text.LastIndexOf('\n', o - 1) + 1

    /// The offset where the line containing `offset` ends (before its line break).
    let lineEnd (text: string) (offset: int) =
        let o = max 0 (min offset text.Length)

        match text.IndexOfAny([| '\r'; '\n' |], o) with
        | -1 -> text.Length
        | i -> i

    /// The word the offset touches, as (start, end), or None between two non-word characters.
    let wordAt (text: string) (offset: int) : (int * int) option =
        let o = max 0 (min offset text.Length)
        let mutable s = o
        let mutable e = o

        while s > 0 && isWordChar text[s - 1] do
            s <- s - 1

        while e < text.Length && isWordChar text[e] do
            e <- e + 1

        if e > s then Some(s, e) else None

    /// The whitespace the line starts with, up to `offset`.
    let leadingIndent (text: string) (offset: int) : string =
        let ls = lineStart text offset
        let mutable i = ls

        while i < offset && i < text.Length && (text[i] = ' ' || text[i] = '\t') do
            i <- i + 1

        text.Substring(ls, i - ls)

    // ---- keeping the set tidy -----------------------------------------------------------------------

    /// Clamps the cursors into the text, sorts them and joins the ones that touch or overlap (two carets at the same
    /// place become one); the primary stays with whatever it was joined into.
    let private tidyTo (length: int) (set: CursorSet) : CursorSet =
        let clamp o = max 0 (min o length)

        let items =
            set.Cursors
            |> List.mapi (fun i c ->
                { Anchor = clamp c.Anchor
                  Caret = clamp c.Caret },
                i = set.Primary)
            |> List.sortBy (fun (c, _) -> start c, stop c)

        let merged = ResizeArray<EditCursor * bool>()

        for (c, isPrimary) in items do
            if merged.Count > 0 then
                let (last, lastPrimary) = merged[merged.Count - 1]

                // overlapping selections join; two empty carets at one place join; touching selections stay apart
                if start c < stop last || (start c = stop last && (isEmpty c || isEmpty last)) then
                    let joined =
                        if isEmpty last && isEmpty c then
                            last
                        elif last.Caret >= last.Anchor then
                            { Anchor = start last
                              Caret = max (stop last) (stop c) }
                        else
                            { Anchor = max (stop last) (stop c)
                              Caret = start last }

                    merged[merged.Count - 1] <- joined, (lastPrimary || isPrimary)
                else
                    merged.Add((c, isPrimary))
            else
                merged.Add((c, isPrimary))

        let cursors = merged |> Seq.map fst |> Seq.truncate maxCursors |> Seq.toList

        let p =
            match merged |> Seq.tryFindIndex snd with
            | Some i when i < cursors.Length -> i
            | _ -> 0

        { Cursors = cursors; Primary = p }

    let tidy (text: string) (set: CursorSet) : CursorSet = tidyTo text.Length set

    // ---- adding cursors -----------------------------------------------------------------------------

    /// One more cursor on the line below (`step` = 1) or above (-1) the lowest or highest one, at the same column
    /// (or the end of that line when it is shorter). It becomes the primary. None when there is no such line.
    let addOnLine (text: string) (step: int) (set: CursorSet) : CursorSet option =
        let ref =
            if step > 0 then
                set.Cursors |> List.maxBy (fun c -> c.Caret)
            else
                set.Cursors |> List.minBy (fun c -> c.Caret)

        let ls = lineStart text ref.Caret
        let column = ref.Caret - ls

        let target =
            if step > 0 then
                match text.IndexOf('\n', lineEnd text ref.Caret) with
                | -1 -> None
                | nl -> Some(nl + 1)
            elif ls = 0 then
                None
            else
                Some(lineStart text (ls - 1))

        match target with
        | None -> None
        | Some t ->
            let e = lineEnd text t
            let spot = min (t + column) e

            if set.Cursors.Length >= maxCursors then
                None
            else
                let all = set.Cursors @ [ caretAt spot ]

                Some(
                    tidy
                        text
                        { Cursors = all
                          Primary = all.Length - 1 }
                )

    /// The text a cursor selects.
    let selectedText (text: string) (c: EditCursor) =
        text.Substring(start c, stop c - start c)

    /// The next place the primary's selected text occurs (the word under an empty primary is selected first), added
    /// as a cursor and made the primary. Wraps round the end; None when there is nothing to select or every
    /// occurrence already has a cursor.
    let addNextMatch (text: string) (set: CursorSet) : CursorSet option =
        let p = primary set

        if isEmpty p then
            match wordAt text p.Caret with
            | Some(s, e) ->
                let cs =
                    set.Cursors
                    |> List.mapi (fun i c -> if i = set.Primary then { Anchor = s; Caret = e } else c)

                Some(tidy text { set with Cursors = cs })
            | None -> None
        else
            let needle = selectedText text p
            let taken = set.Cursors |> List.map (fun c -> start c, stop c) |> Set.ofList

            let rec search (from: int) (wrapped: bool) =
                match text.IndexOf(needle, from, StringComparison.Ordinal) with
                | -1 -> if wrapped then None else search 0 true
                | i when wrapped && i >= stop p -> None
                | i when taken.Contains(i, i + needle.Length) -> search (i + needle.Length) wrapped
                | i -> Some i

            match search (stop p) false with
            | Some i when set.Cursors.Length < maxCursors ->
                let all =
                    set.Cursors
                    @ [ { Anchor = i
                          Caret = i + needle.Length } ]

                Some(
                    tidy
                        text
                        { Cursors = all
                          Primary = all.Length - 1 }
                )
            | _ -> None

    /// A cursor on every occurrence of the primary's selected text (or of the word under an empty primary). The
    /// primary stays the one it was. None when there is nothing to match.
    let selectAllMatches (text: string) (set: CursorSet) : CursorSet option =
        let p = primary set

        let needle =
            if isEmpty p then
                wordAt text p.Caret |> Option.map (fun (s, e) -> text.Substring(s, e - s))
            else
                Some(selectedText text p)

        match needle with
        | None
        | Some "" -> None
        | Some n ->
            let found = ResizeArray<EditCursor>()
            let mutable i = text.IndexOf(n, 0, StringComparison.Ordinal)

            while i >= 0 && found.Count < maxCursors do
                found.Add { Anchor = i; Caret = i + n.Length }
                i <- text.IndexOf(n, i + n.Length, StringComparison.Ordinal)

            if found.Count = 0 then
                None
            else
                let at =
                    found
                    |> Seq.tryFindIndex (fun c -> start c <= p.Caret && p.Caret <= stop c)
                    |> Option.defaultValue 0

                Some
                    { Cursors = Seq.toList found
                      Primary = at }

    // ---- editing at every cursor --------------------------------------------------------------------

    /// Makes one edit per cursor and works out where the cursors are afterwards. An edit that would overlap the one
    /// before it is cut short, so no character is deleted twice.
    let private editAll (text: string) (set: CursorSet) (plan: EditCursor -> CursorEdit) : CursorChange =
        let tidySet = tidy text set
        let edits = ResizeArray<CursorEdit>()
        let after = ResizeArray<EditCursor>()
        let mutable delta = 0
        let mutable prevEnd = 0

        for c in tidySet.Cursors do
            let e0 = plan c
            let s = max e0.Offset prevEnd
            let eEnd = max s (e0.Offset + e0.Length)

            let e =
                { Offset = s
                  Length = eEnd - s
                  Text = e0.Text }

            edits.Add e
            after.Add(caretAt (e.Offset + delta + e.Text.Length))
            delta <- delta + e.Text.Length - e.Length
            prevEnd <- eEnd

        { Edits = Seq.toList edits
          Set =
            tidyTo
                (text.Length + delta)
                { Cursors = Seq.toList after
                  Primary = tidySet.Primary } }

    /// Applies edits (ascending, in the old text's offsets) to a text.
    let apply (text: string) (edits: CursorEdit list) : string =
        let sb = Text.StringBuilder()
        let mutable pos = 0

        for e in edits do
            sb.Append(text, pos, e.Offset - pos).Append(e.Text) |> ignore
            pos <- e.Offset + e.Length

        sb.Append(text, pos, text.Length - pos).ToString()

    /// Typed text replaces each selection (and goes in at each caret).
    let insert (text: string) (typed: string) (set: CursorSet) : CursorChange =
        editAll text set (fun c ->
            { Offset = start c
              Length = stop c - start c
              Text = typed })

    /// Pasted text. When it has as many lines as there are cursors, each cursor gets one line, in order.
    let paste (text: string) (pasted: string) (set: CursorSet) : CursorChange =
        let lines = pasted.Replace("\r\n", "\n").Split('\n')
        let n = (tidy text set).Cursors.Length

        if n > 1 && lines.Length = n then
            let mutable i = -1

            editAll text set (fun c ->
                i <- i + 1

                { Offset = start c
                  Length = stop c - start c
                  Text = lines[i] })
        else
            insert text pasted set

    /// Backspace: the selection, or the character before the caret.
    let backspace (text: string) (set: CursorSet) : CursorChange =
        editAll text set (fun c ->
            if not (isEmpty c) then
                { Offset = start c
                  Length = stop c - start c
                  Text = "" }
            elif c.Caret = 0 then
                { Offset = 0; Length = 0; Text = "" }
            else
                let two = c.Caret >= 2 && text[c.Caret - 2] = '\r' && text[c.Caret - 1] = '\n'
                let n = if two then 2 else 1

                { Offset = c.Caret - n
                  Length = n
                  Text = "" })

    /// Delete: the selection, or the character after the caret.
    let delete (text: string) (set: CursorSet) : CursorChange =
        editAll text set (fun c ->
            if not (isEmpty c) then
                { Offset = start c
                  Length = stop c - start c
                  Text = "" }
            elif c.Caret >= text.Length then
                { Offset = c.Caret
                  Length = 0
                  Text = "" }
            else
                let two =
                    text[c.Caret] = '\r' && c.Caret + 1 < text.Length && text[c.Caret + 1] = '\n'

                { Offset = c.Caret
                  Length = (if two then 2 else 1)
                  Text = "" })

    /// Enter: a line break, and the indentation of the line the cursor is on.
    let newline (text: string) (lineBreak: string) (set: CursorSet) : CursorChange =
        editAll text set (fun c ->
            { Offset = start c
              Length = stop c - start c
              Text = lineBreak + leadingIndent text (start c) })

    // ---- moving ---------------------------------------------------------------------------------------

    let private wordLeft (text: string) (o: int) =
        let mutable i = o

        while i > 0 && not (isWordChar text[i - 1]) do
            i <- i - 1

        while i > 0 && isWordChar text[i - 1] do
            i <- i - 1

        i

    let private wordRight (text: string) (o: int) =
        let mutable i = o

        while i < text.Length && not (isWordChar text[i]) do
            i <- i + 1

        while i < text.Length && isWordChar text[i] do
            i <- i + 1

        i

    let private move1 (text: string) (m: Motion) (o: int) : int =
        match m with
        | MoveLeft ->
            if o >= 2 && text[o - 2] = '\r' && text[o - 1] = '\n' then
                o - 2
            else
                max 0 (o - 1)
        | MoveRight ->
            if o + 1 < text.Length && text[o] = '\r' && text[o + 1] = '\n' then
                o + 2
            else
                min text.Length (o + 1)
        | MoveLineStart -> lineStart text o
        | MoveLineEnd -> lineEnd text o
        | MoveWordLeft -> wordLeft text o
        | MoveWordRight -> wordRight text o
        | MoveUp ->
            let ls = lineStart text o

            if ls = 0 then
                0
            else
                let prev = lineStart text (ls - 1)
                min (prev + (o - ls)) (lineEnd text prev)
        | MoveDown ->
            let ls = lineStart text o

            match text.IndexOf('\n', lineEnd text o) with
            | -1 -> text.Length
            | nl -> min (nl + 1 + (o - ls)) (lineEnd text (nl + 1))

    /// Every caret moves. Without `extend` a selection collapses to the end the motion points at (left or up: its
    /// start; right or down: its end), with it the anchors stay and the carets move.
    let move (text: string) (m: Motion) (extend: bool) (set: CursorSet) : CursorSet =
        let moved =
            set.Cursors
            |> List.map (fun c ->
                if extend then
                    { c with Caret = move1 text m c.Caret }
                elif not (isEmpty c) && (m = MoveLeft || m = MoveUp) then
                    caretAt (start c)
                elif not (isEmpty c) && (m = MoveRight || m = MoveDown) then
                    caretAt (stop c)
                else
                    caretAt (move1 text m c.Caret))

        tidy text { set with Cursors = moved }
