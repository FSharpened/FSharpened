/// Snippets in the editor: type a snippet's name and press Tab, or choose one from the list; its fields are filled in
/// one after the other with Tab (Shift+Tab goes back, Esc stops), and copies of a field follow what is typed in it. The
/// snippet text and its places come from `SnippetExpand` (pure); this file puts them in the document and follows them.
namespace FSharpened.Ide.Desktop

open System
open System.IO
open Avalonia.Input
open Avalonia.Interactivity
open AvaloniaEdit.Document
open AvaloniaEdit.Editing
open FSharpened.Ide
open FSharpened.Ide.Host

/// A field of the snippet being filled in: where it and its linked copies are, kept with the text as it changes.
type private Field =
    { Index: int
      Spots: (TextAnchor * TextAnchor) list }

type private Session =
    {
        Doc: TextDocument
        Fields: Field list
        /// The whole snippet's span: leaving it ends the session.
        First: TextAnchor
        Last: TextAnchor
        mutable At: int
    }

type SnippetFeature(ui: Ui) =
    let editor = ui.Editor
    let area = editor.TextArea
    let mutable imported: Snippet list = []
    let mutable problems: string list = []
    let mutable session: Session option = None
    let mutable busy = false

    let load () =
        let snippets, why = SnippetFiles.loadFolder ui.Env.UserSnippets
        imported <- snippets
        problems <- why

    let languageId () =
        match ui.Active with
        | Some d ->
            match d.Pack with
            | Some p -> p.Id
            | None -> ""
        | None -> ""

    /// The snippets offered for the open file.
    let available () =
        Snippets.forLanguage (languageId ()) imported

    let context (selected: string) : SnippetContext =
        let rel = ui.Active |> Option.map (fun d -> d.Rel) |> Option.defaultValue ""
        let now = DateTime.Now

        { FileName = Path.GetFileName rel
          FilePath = rel
          Folder =
            (match Path.GetDirectoryName rel with
             | null -> ""
             | d -> d.Replace('\\', '/'))
          Selected = selected
          ProjectName = ui.Root |> Option.map Path.GetFileName |> Option.defaultValue ""
          Year = now.Year
          Month = now.Month
          Day = now.Day
          LineComment =
            ui.Active
            |> Option.bind (fun d -> d.Pack)
            |> Option.bind (fun p -> p.LineComment)
            |> Option.defaultValue "//" }

    let indentUnit () =
        let e = ui.Prefs.Editor

        if e.InsertSpaces then
            String(' ', max 1 e.TabSize)
        else
            "\t"

    // ---- the fields -------------------------------------------------------------------------------------

    let endSession () = session <- None

    let spotOffsets (a: TextAnchor, b: TextAnchor) = a.Offset, b.Offset

    /// False once the text of the snippet was removed (undo, say): its places are gone, so filling it in is over.
    let alive (s: Session) =
        not s.First.IsDeleted
        && not s.Last.IsDeleted
        && s.Fields
           |> List.forall (fun f -> f.Spots |> List.forall (fun (a, b) -> not a.IsDeleted && not b.IsDeleted))

    /// Selects a field (its first spot) and ends the session at the last stop.
    let goTo (s: Session) (i: int) =
        let f = s.Fields[i]
        s.At <- i
        let start, stop = spotOffsets f.Spots[0]
        busy <- true

        try
            if start = stop then
                area.ClearSelection()
            else
                area.Selection <- Selection.Create(area, start, stop)

            area.Caret.Offset <- stop
            area.Caret.BringCaretToView()
        finally
            busy <- false

        if f.Index = 0 then
            endSession ()

    /// Keeps linked copies of the current field the same as the field being typed in.
    let mirror (s: Session) =
        if not (alive s) then
            endSession ()
        else
            let f = s.Fields[s.At]

            match f.Spots with
            | main :: copies when not copies.IsEmpty ->
                let a, b = spotOffsets main
                let text = s.Doc.GetText(a, max 0 (b - a))
                busy <- true

                // the copy joins the undo step of the typing that changed the field
                s.Doc.UndoStack.StartContinuedUndoGroup()

                try
                    for spot in copies do
                        let ca, cb = spotOffsets spot

                        if s.Doc.GetText(ca, max 0 (cb - ca)) <> text then
                            s.Doc.Replace(ca, max 0 (cb - ca), text)
                finally
                    s.Doc.UndoStack.EndUndoGroup()
                    busy <- false
            | _ -> ()

    let onDocChanged (_: DocumentChangeEventArgs) =
        match session with
        | Some s when not busy && Object.ReferenceEquals(s.Doc, editor.Document) ->
            // a document cannot be changed while it is announcing a change, so the copies follow a moment later
            Avalonia.Threading.Dispatcher.UIThread.Post(fun () ->
                match session with
                | Some now when Object.ReferenceEquals(now, s) -> mirror s
                | _ -> ())
        | _ -> ()

    let onCaret () =
        match session with
        | Some s when not busy ->
            if not (alive s) then
                endSession ()
            else
                let o = area.Caret.Offset

                if o < s.First.Offset || o > s.Last.Offset then
                    endSession ()
        | _ -> ()

    // ---- inserting --------------------------------------------------------------------------------------

    /// Puts a snippet in place of [start, stop) and starts filling in its fields.
    let insertAt (snippet: Snippet) (start: int) (stop: int) =
        let doc = editor.Document
        let text = doc.Text

        let selected =
            if stop > start then
                text.Substring(start, stop - start)
            else
                ""

        let template = Snippets.template snippet.Body (context selected)

        let brk = if text.Contains "\r\n" then "\r\n" else "\n"

        let expanded =
            SnippetExpand.parse template.Template
            |> SnippetExpand.layout (Carets.leadingIndent text start) (indentUnit ()) brk

        endSession ()
        busy <- true

        try
            doc.Replace(start, stop - start, expanded.Text)
        finally
            busy <- false

        let anchor (o: int) (move: AnchorMovementType) =
            let a = doc.CreateAnchor(start + o)
            a.MovementType <- move
            a

        let fields =
            [ for st in expanded.Stops ->
                  { Index = st.Index
                    Spots =
                      [ for (o, l) in st.Ranges ->
                            anchor o AnchorMovementType.BeforeInsertion,
                            anchor (o + l) AnchorMovementType.AfterInsertion ] } ]

        let s =
            { Doc = doc
              Fields = fields
              First = anchor 0 AnchorMovementType.BeforeInsertion
              Last = anchor expanded.Text.Length AnchorMovementType.AfterInsertion
              At = 0 }

        session <- Some s
        goTo s 0

        if not template.Warnings.IsEmpty then
            ui.Say("Snippet: " + template.Warnings.Head)

    /// Replaces the word before the caret by the snippet of that name; false when there is none.
    let tryExpandAtCaret () : bool =
        match ui.Active with
        | Some _ when area.Selection.IsEmpty ->
            let text = editor.Document.Text
            let caret = area.Caret.Offset

            match SnippetExpand.prefixBefore text caret with
            | Some(word, start) ->
                match SnippetExpand.find (available ()) word with
                | Some snippet ->
                    insertAt snippet start caret
                    true
                | None -> false
            | None -> false
        | _ -> false

    /// Inserts a chosen snippet at the caret, over the selection if there is one.
    let insertChosen (snippet: Snippet) =
        match ui.Active with
        | Some _ ->
            let caret = area.Caret.Offset

            let start, stop =
                if area.Selection.IsEmpty then
                    caret, caret
                else
                    let seg = area.Selection.SurroundingSegment
                    seg.Offset, seg.EndOffset

            insertAt snippet start stop
            editor.Focus() |> ignore
        | None -> ui.Say "Open a file first"

    /// Tab and Shift+Tab move between fields; Esc leaves the snippet. True when the key was used.
    let onKey (e: KeyEventArgs) : bool =
        match session with
        | Some s when Object.ReferenceEquals(s.Doc, editor.Document) ->
            match e.Key, e.KeyModifiers with
            | Key.Tab, KeyModifiers.None ->
                if s.At + 1 < s.Fields.Length then
                    goTo s (s.At + 1)
                else
                    endSession ()

                true
            | Key.Tab, KeyModifiers.Shift ->
                goTo s (max 0 (s.At - 1))
                true
            | Key.Escape, KeyModifiers.None ->
                endSession ()
                false
            | _ -> false
        | Some _ ->
            endSession ()
            false
        | None ->
            // Tab after a snippet's name, unless a pop-up (the completion list) has the keyboard
            let popupOpen =
                not (Object.ReferenceEquals(area.ActiveInputHandler, area.DefaultInputHandler))

            e.Key = Key.Tab
            && e.KeyModifiers = KeyModifiers.None
            && not popupOpen
            && tryExpandAtCaret ()

    do
        load ()

        area.AddHandler(
            InputElement.KeyDownEvent,
            EventHandler<KeyEventArgs>(fun _ e ->
                if onKey e then
                    e.Handled <- true),
            RoutingStrategies.Tunnel
        )

        let changed = EventHandler<DocumentChangeEventArgs>(fun _ e -> onDocChanged e)

        area.DocumentChanged.Add(fun args ->
            if not (isNull args.OldDocument) then
                args.OldDocument.Changed.RemoveHandler changed

            if not (isNull args.NewDocument) then
                args.NewDocument.Changed.AddHandler changed

            endSession ())

        if not (isNull editor.Document) then
            editor.Document.Changed.AddHandler changed

        area.Caret.PositionChanged.Add(fun _ -> onCaret ())

    /// Reads the person's snippet files again; says what was wrong with them.
    member _.Reload() =
        load ()

        match problems with
        | [] -> ui.Say(sprintf "%d snippets from your files" imported.Length)
        | first :: rest ->
            ui.Say(
                if rest.IsEmpty then
                    first
                else
                    sprintf "%s (and %d more)" first rest.Length
            )

    member _.Available = available
    member _.Insert = insertChosen
    member _.Problems = problems

    /// Expands the snippet named before the caret, as Tab does: for the tests.
    member _.ExpandAtCaret() = tryExpandAtCaret ()

    member _.InSession = session.IsSome

    /// The field Tab would leave and its index, for the tests: None when no snippet is being filled in.
    member _.CurrentField = session |> Option.map (fun s -> s.Fields[s.At].Index)
