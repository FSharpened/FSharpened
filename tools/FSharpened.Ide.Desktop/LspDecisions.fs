/// What the editor decides about the language server's answers, apart from drawing it: how a signature is split for
/// emphasis, how references are listed and counted, which quick fixes are offered and in what order. Pure, so tests
/// need no window.
namespace FSharpened.Ide.Desktop

open System
open System.IO
open FSharpened.Ide
open FSharpened.Ide.Host

module SignatureView =
    /// The active signature's label as (before, the active parameter, after). The parameter is empty when the server
    /// names none or its text is not found in the label.
    let parts (h: SignatureHelp) : string * string * string =
        let s = h.Signatures[h.ActiveSignature]

        match LspFeatureParse.activeParameterLabel h with
        | Some p ->
            match s.Label.IndexOf(p, StringComparison.Ordinal) with
            | -1 -> s.Label, "", ""
            | i -> s.Label.Substring(0, i), p, s.Label.Substring(i + p.Length)
        | None -> s.Label, "", ""

    /// "1 of 3" when the call has several signatures (overloads), otherwise "".
    let counter (h: SignatureHelp) : string =
        if h.Signatures.Length > 1 then
            sprintf "%d of %d" (h.ActiveSignature + 1) h.Signatures.Length
        else
            ""

    /// The documentation of the active parameter, else of the signature, else "".
    let documentation (h: SignatureHelp) : string =
        let s = h.Signatures[h.ActiveSignature]
        let i = defaultArg s.ActiveParameter h.ActiveParameter

        if i >= 0 && i < s.Parameters.Length && s.Parameters[i].Doc <> "" then
            s.Parameters[i].Doc
        else
            s.Doc

    /// Typing this opens or refreshes signature help: the characters the server named, or "(" and "," when it named none.
    let isTrigger (triggers: string list) (typed: string) : bool =
        let list = if triggers.IsEmpty then [ "("; "," ] else triggers
        typed.Length = 1 && List.contains typed list

module ReferenceRows =
    /// A location of the folder, as (relative path, 1-based line, 1-based column); places outside the folder are dropped.
    let inside (root: string) (locs: LspLocation list) : (string * int * int) list =
        [ for l in locs do
              match Paths.relInside root l.Path with
              | Some rel -> yield rel, l.Range.Start.Line + 1, l.Range.Start.Character + 1
              | None -> () ]
        |> List.distinct
        |> List.sortBy (fun (p, l, c) -> p.ToLowerInvariant(), l, c)

    let summary (hits: (string * int * int) list) : string =
        let files = hits |> List.map (fun (p, _, _) -> p) |> List.distinct |> List.length

        match hits.Length, files with
        | 0, _ -> "No references found"
        | 1, _ -> "1 reference"
        | n, 1 -> sprintf "%d references in 1 file" n
        | n, f -> sprintf "%d references in %d files" n f

    /// One row of the references list: where, then the line's own text.
    let row (rel: string) (line: int) (col: int) (text: string) : string =
        sprintf "%s:%d:%d   %s" rel line col (text.Trim())

module QuickFix =
    /// An action the editor can carry out: not disabled, and either carrying an edit or able to get one by resolving.
    /// One that only runs a server command is shown (so the person knows it exists) but not run.
    let applicable (a: LspCodeAction) : bool =
        a.Disabled.IsNone && (a.Edit.IsSome || not a.HasCommand)

    let private rank (a: LspCodeAction) =
        if not (applicable a) then 3
        elif a.IsPreferred then 0
        elif a.Kind.StartsWith "quickfix" then 1
        else 2

    /// Preferred fixes first, then other quick fixes, then refactorings; the ones that cannot be run last. The server's
    /// order is kept inside each group.
    let order (actions: LspCodeAction list) : LspCodeAction list = actions |> List.sortBy rank

    /// What the menu shows for an action that cannot be run, so the reason is on the screen.
    let menuTitle (a: LspCodeAction) : string =
        match a.Disabled with
        | Some why -> sprintf "%s (%s)" a.Title why
        | None when a.Edit.IsNone && a.HasCommand -> a.Title + " (runs a server command: not supported)"
        | None -> a.Title

    /// The diagnostics of one line, as the server sent them, to go with a code action request.
    let onLine (line0: int) (all: LspDiagnostic list) : LspDiagnostic list =
        all
        |> List.filter (fun d -> d.Range.Start.Line <= line0 && line0 <= d.Range.End.Line)

module ServerFormat =
    /// The indent the file uses: its pack's, else the editor setting.
    let options (pack: LanguagePack option) (prefs: EditorSettings) : FormatOptions =
        match pack with
        | Some p ->
            { TabSize = max 1 p.IndentSize
              InsertSpaces = not p.UseTabs }
        | None ->
            { TabSize = prefs.TabSize
              InsertSpaces = prefs.InsertSpaces }
