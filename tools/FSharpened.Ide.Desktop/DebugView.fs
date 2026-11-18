/// What the debug panel says, kept free of controls so it can be tested (ADR-0023).
module FSharpened.Ide.Desktop.DebugView

open System
open System.IO
open FSharpened.Ide.Host

/// What the session is doing, for the toolbar and the status bar.
type Phase =
    | Idle
    | Starting
    | Running
    | Paused

/// Which buttons make sense. Stepping needs a stopped program; stopping needs a session; starting needs neither.
let canStart (p: Phase) = p = Idle || p = Paused
let canStep (p: Phase) = p = Paused
let canPause (p: Phase) = p = Running
let canStop (p: Phase) = p <> Idle

/// "compute  prog.py:12" for a call stack row.
let frameLabel (root: string) (f: DapFrame) : string =
    if f.FramePath = "" || f.FrameLine <= 0 then
        f.FrameName
    else
        let shown =
            try
                let rel = Path.GetRelativePath(root, f.FramePath)

                if rel.StartsWith ".." || Path.IsPathRooted rel then
                    Path.GetFileName f.FramePath
                else
                    rel.Replace('\\', '/')
            with _ ->
                Path.GetFileName f.FramePath

        sprintf "%s  %s:%d" f.FrameName shown f.FrameLine

/// The path of a frame's file relative to the opened folder (forward slashes), when it is inside it.
let relOfFrame (root: string) (f: DapFrame) : string option =
    if f.FramePath = "" then
        None
    else
        try
            let rel = Path.GetRelativePath(root, f.FramePath)

            if rel.StartsWith ".." || Path.IsPathRooted rel then
                None
            else
                Some(rel.Replace('\\', '/'))
        with _ ->
            None

/// "name = value" with the type in brackets when there is one; a long value is cut.
let variableLabel (v: DapVariable) : string =
    let value =
        let one = v.Value.Replace("\r", " ").Replace("\n", " ")

        if one.Length > 160 then
            one.Substring(0, 160) + "…"
        else
            one

    if v.VarType <> "" then
        sprintf "%s = %s   [%s]" v.VarName value v.VarType
    else
        sprintf "%s = %s" v.VarName value

/// The status text for a phase.
let statusText (p: Phase) (detail: string) : string =
    match p with
    | Idle -> ""
    | Starting -> "Debugger starting…"
    | Running -> "Debugger running"
    | Paused ->
        if detail = "" then
            "Debugger paused"
        else
            "Paused: " + detail

/// Keeps the end of a console that grows without bound.
let capConsole (limit: int) (current: string) (added: string) : string =
    let joined = current + added

    if joined.Length > limit then
        joined.Substring(joined.Length - limit)
    else
        joined

/// A one-line reason for a stop: "breakpoint", "step", or the adapter's own words.
let stopDetail (reason: string) (text: string) : string =
    match reason, text with
    | "", "" -> ""
    | r, "" -> r
    | "", t -> t
    | r, t when t.StartsWith(r, StringComparison.OrdinalIgnoreCase) -> t
    | r, t -> sprintf "%s (%s)" r t

/// Output categories worth showing in the console: the program's own output and the debugger's messages, not the
/// adapter's telemetry.
let showsOutput (category: string) = category <> "telemetry"
