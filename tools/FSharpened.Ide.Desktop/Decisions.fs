/// Decisions the window makes that need no control: where a path is, which key runs which command, what the status
/// bar says, whether a change on disk reloads or asks. Kept apart from the Avalonia code so they can be tested with
/// plain values (`tests/FSharpened.Ide.Desktop.Tests/DecisionTests.fs`).
namespace FSharpened.Ide.Desktop

open System
open System.IO
open Avalonia.Input
open FSharpened.Ide.Host

/// Paths inside the opened folder. Relative paths always use `/`.
module Paths =
    /// `parent/name`, or just `name` at the top.
    let child (parent: string) (name: string) =
        if parent = "" then name else parent + "/" + name

    /// The folder part of a relative path ("" at the top).
    let parent (rel: string) =
        match rel.LastIndexOf '/' with
        | -1 -> ""
        | i -> rel.Substring(0, i)

    /// The absolute path of a relative one, with the platform's separator.
    let fullPath (root: string) (rel: string) =
        Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))

    /// `path` relative to `root` with `/`, or None when it is outside the folder.
    let relInside (root: string) (path: string) : string option =
        let rel = Path.GetRelativePath(root, path).Replace('\\', '/')

        if rel.StartsWith ".." || Path.IsPathRooted rel then
            None
        else
            Some rel

    /// The folder's own name, without a trailing separator.
    let rootName (root: string option) =
        match root with
        | Some r -> Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        | None -> ""

    /// Where a file named in a tool's output is in the folder. `exists` says whether a relative path is a file;
    /// `byName` finds a file by its name alone (the last resort, for tools that print only a name).
    let ofDiagnosticFile
        (root: string)
        (file: string)
        (exists: string -> bool)
        (byName: string -> string option)
        : string option =
        let f = file.Replace('\\', '/')
        let rr = root.Replace('\\', '/').TrimEnd('/') + "/"

        let rel =
            if f.StartsWith(rr, StringComparison.OrdinalIgnoreCase) then
                f.Substring rr.Length
            elif f.StartsWith "./" then
                f.Substring 2
            else
                f

        if exists rel then Some rel else byName (Path.GetFileName f)

/// Words in text.
module TextAt =
    let isIdentifierChar (c: char) = Char.IsLetterOrDigit c || c = '_'

    /// The identifier the offset is in or touches ("" when there is none).
    let wordAt (text: string) (offset: int) : string =
        let mutable a = min offset text.Length
        let mutable b = a

        while a > 0 && isIdentifierChar text[a - 1] do
            a <- a - 1

        while b < text.Length && isIdentifierChar text[b] do
            b <- b + 1

        text.Substring(a, b - a)

    /// Where the word before the caret starts: the part of it a completion replaces.
    let completionStart (charAt: int -> char) (caret: int) : int =
        let mutable start = caret

        while start > 0 && isIdentifierChar (charAt (start - 1)) do
            start <- start - 1

        start

/// Typing a closing bracket after an opening one, and stepping over it when it is typed again.
module Brackets =
    /// The closing bracket to add after `typed`, or None.
    let closingFor (typed: string) : char option =
        if typed.Length <> 1 then
            None
        else
            match typed[0] with
            | '(' -> Some ')'
            | '[' -> Some ']'
            | '{' -> Some '}'
            | _ -> None

    /// Whether to insert the closing bracket: only when what follows is the end, white space or another closer.
    let shouldInsert (next: char option) =
        match next with
        | None -> true
        | Some c -> Char.IsWhiteSpace c || c = ')' || c = ']' || c = '}'

    /// Typing a closer right before the same closer steps over it instead of adding a second.
    let shouldStepOver (typed: string) (next: char option) =
        (typed = ")" || typed = "]" || typed = "}") && next = Some(typed[0])

/// What a change on disk does to an open file that is still there.
type DiskAction =
    /// No unsaved edits here: take the disk's text.
    | Reload
    /// Unsaved edits and a new disk text: ask which to keep.
    | Ask
    | Leave

module DiskSync =
    /// `diskText` is what is on disk now; `dirty` whether the editor has unsaved edits; `text` the editor's text;
    /// `lastDisk` what the IDE last read or wrote; `keptOver` the disk text the person already chose to keep their
    /// edits over (so the question is not asked twice for the same text).
    let decide (diskText: string) (dirty: bool) (text: string) (lastDisk: string) (keptOver: string option) =
        if not dirty && diskText <> text then
            Reload
        elif dirty && diskText <> lastDisk && keptOver <> Some diskText then
            Ask
        else
            Leave

/// Text for the status bar and the tabs.
module Status =
    open FSharpened.Diagnostics

    /// `⎇ main  ● 3`, or "" with no branch.
    let git (branch: string option) (changes: int) =
        match branch with
        | Some b -> sprintf "⎇ %s%s" b (if changes = 0 then "" else sprintf "  ● %d" changes)
        | None -> ""

    let position (line: int) (col: int) = sprintf "Ln %d, Col %d" line col

    /// The error and warning counts of a problems list.
    let problemCounts (list: StructuredDiagnostic list) : int * int =
        list |> List.filter (fun d -> d.Severity = Severity.Error) |> List.length,
        list |> List.filter (fun d -> d.Severity = Severity.Warning) |> List.length

    let problems (list: StructuredDiagnostic list) =
        let e, w = problemCounts list
        sprintf "✖ %d   ⚠ %d" e w

    /// The breadcrumb: the file's folders and name, then the symbols the caret is inside.
    let breadcrumb (rel: string) (trail: string list) =
        String.Join("  ›  ", (rel.Split('/') |> Array.toList) @ trail)

    /// A tab's title: the file name, a dot when it has unsaved edits, a note when it is gone from disk.
    let tabTitle (rel: string) (dirty: bool) (missing: bool) =
        Path.GetFileName rel
        + (if dirty then " ●" else "")
        + (if missing then " (deleted on disk)" else "")

    /// A language server's severity number as a diagnostic severity.
    let severityOf (n: int) =
        match n with
        | 1 -> Severity.Error
        | 2 -> Severity.Warning
        | _ -> Severity.Info

/// The window's shortcuts: a key and what is held down give a command id (see `Commands`).
module Keymap =
    /// The command a key press runs, or None. `editorFocused` is whether the editor has the keyboard focus (F2 renames
    /// only there). The terminal's claim on keys is decided before this (`TerminalKeys.windowKeeps`).
    let commandFor (key: Key) (mods: KeyModifiers) (editorFocused: bool) : string option =
        let ctrl = mods.HasFlag KeyModifiers.Control
        let shift = mods.HasFlag KeyModifiers.Shift
        let alt = mods.HasFlag KeyModifiers.Alt

        match key with
        | Key.P when ctrl && shift -> Some "palette"
        | Key.P when ctrl -> Some "quick-open"
        | Key.G when ctrl -> Some "goto-line"
        | Key.O when ctrl && shift -> Some "goto-symbol"
        | Key.O when ctrl -> Some "open-folder"
        | Key.S when ctrl && shift -> Some "save-all"
        | Key.S when ctrl -> Some "save"
        | Key.N when ctrl -> Some "new-file"
        | Key.W when ctrl -> Some "close-tab"
        | Key.B when ctrl -> Some "toggle-sidebar"
        | Key.J when ctrl -> Some "toggle-panel"
        | Key.E when ctrl && shift -> Some "explorer"
        | Key.F when ctrl && shift -> Some "find-files"
        | Key.OemTilde when ctrl -> Some "terminal"
        | Key.H when ctrl && shift -> Some "replace-in-files"
        | Key.H when ctrl -> Some "replace-in-file"
        | Key.M when ctrl && shift -> Some "problems"
        | Key.Space when ctrl -> Some "complete"
        | Key.F12 -> Some "definition"
        | Key.F5 when ctrl -> Some "run"
        | Key.F5 when shift -> Some "debug-stop"
        | Key.F5 -> Some "debug-start"
        | Key.F9 -> Some "toggle-breakpoint"
        | Key.F10 -> Some "debug-step-over"
        | Key.F11 when shift -> Some "debug-step-out"
        | Key.F11 -> Some "debug-step-into"
        | Key.F2 when editorFocused -> Some "rename-symbol"
        | Key.F when shift && alt -> Some "format"
        | Key.Z when alt -> Some "wrap"
        | Key.OemPlus when ctrl -> Some "zoom-in"
        | Key.OemMinus when ctrl -> Some "zoom-out"
        | _ -> None
