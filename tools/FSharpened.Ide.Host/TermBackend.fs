namespace FSharpened.Ide.Host

open System
open System.Collections.Generic
open System.Text
open FSharpened.Platform

/// What the terminal panel talks to, whichever way the shell was started: keystrokes go in through `Write`, output
/// comes out through the callback given when it was opened.
type TermBackend =
    {
        Shell: ShellChoice
        /// True for a real pseudo-terminal, false for the line-oriented pipe fallback.
        IsPty: bool
        IsRunning: unit -> bool
        Write: string -> unit
        Resize: int * int -> unit
        Stop: unit -> unit
    }

/// A line editor for the pipe fallback, which has no terminal to do it: it echoes what is typed, handles Backspace,
/// Ctrl+C and the Up/Down history, and hands each finished line to `submit`. Output goes to `echo` as terminal text.
type LineEditor(echo: string -> unit, submit: string -> unit) =
    let line = StringBuilder()
    let history = List<string>()
    let mutable at = 0
    let mutable pending = ""

    let erase () =
        if line.Length > 0 then
            echo (String.replicate line.Length "\b \b")
            line.Clear() |> ignore

    let replace (text: string) =
        erase ()
        line.Append text |> ignore
        echo text

    member _.Current = line.ToString()

    member _.Feed(text: string) =
        for ch in text do
            if pending <> "" then
                pending <- pending + string ch

                // `ESC [ A` and `ESC [ B` move through the history; any other `ESC [ ...` is swallowed
                if pending.Length = 2 && ch <> '[' then
                    pending <- ""
                elif pending.Length >= 3 && ch >= '@' && ch <= '~' then
                    match ch with
                    | 'A' when history.Count > 0 ->
                        at <- max 0 (at - 1)
                        replace history[at]
                    | 'B' when history.Count > 0 ->
                        at <- min history.Count (at + 1)
                        replace (if at >= history.Count then "" else history[at])
                    | _ -> ()

                    pending <- ""
            else
                match ch with
                | '\u001b' -> pending <- "\u001b"
                | '\r'
                | '\n' ->
                    let l = line.ToString()
                    line.Clear() |> ignore
                    echo "\r\n"

                    if l.Trim() <> "" && (history.Count = 0 || history[history.Count - 1] <> l) then
                        history.Add l

                    at <- history.Count
                    submit l
                | '\u007f'
                | '\b' ->
                    if line.Length > 0 then
                        line.Length <- line.Length - 1
                        echo "\b \b"
                | '\u0003' ->
                    line.Clear() |> ignore
                    echo "^C\r\n"
                | c when c >= ' ' ->
                    line.Append c |> ignore
                    echo (string c)
                | _ -> ()

/// One shell on a real pseudo-terminal (ConPTY on Windows, a pty on Linux and macOS): it sees a terminal, so colours,
/// full-screen programs, line editing and Ctrl+C work. What the shell prints arrives raw, with its escape sequences,
/// through `onOutput` on a pool thread (feed it to a `Screen`); `onExit` is called once with the exit code.
type PtySession(shell: ShellChoice, cwd: string, cols: int, rows: int, onOutput: string -> unit, onExit: int -> unit) =
    let mutable pty: IPty option = None
    let mutable stopped = false
    let decoder = UTF8Encoding(false).GetDecoder()

    member _.Shell = shell
    member _.IsRunning = pty |> Option.exists (fun p -> p.IsRunning)

    /// Environment variables to add for the shell (a null value removes one), set before `Start`.
    member val Env: (string * string) list = [] with get, set

    /// Starts the shell in `cwd`. The folder must already be trusted: this runs whatever is typed into it.
    member this.Start() : Result<unit, string> =
        let spec: PtySpec =
            { Argv = shell.Argv
              Cwd = cwd
              Env = this.Env
              Cols = cols
              Rows = rows }

        let data (bytes: byte[]) =
            // one reader thread calls this, so the decoder keeps a half-received character between calls
            let chars =
                Array.zeroCreate<char> (decoder.GetCharCount(bytes, 0, bytes.Length, false))

            let n = decoder.GetChars(bytes, 0, bytes.Length, chars, 0, false)

            if n > 0 && not stopped then
                onOutput (String(chars, 0, n))

        match
            (try
                Pty.start spec data (fun code ->
                    if not stopped then
                        onExit code)
             with e ->
                 Error e.Message)
        with
        | Ok p ->
            pty <- Some p
            Ok()
        | Error e -> Error e

    /// Sends keystrokes (already encoded, see `TermKeys`).
    member _.Write(text: string) =
        match pty with
        | Some p -> p.Write(Encoding.UTF8.GetBytes text)
        | None -> ()

    member _.Resize(c: int, r: int) =
        match pty with
        | Some p -> p.Resize(c, r)
        | None -> ()

    /// Ends the shell and everything it started.
    member _.Stop() =
        stopped <- true

        match pty with
        | Some p ->
            p.Dispose()
            pty <- None
        | None -> ()

module TermBackends =
    /// Whether the person turned the pseudo-terminal off (`FSH_TERMINAL=pipe`), for a computer where it misbehaves.
    let private pipeRequested () =
        String.Equals(Environment.GetEnvironmentVariable "FSH_TERMINAL", "pipe", StringComparison.OrdinalIgnoreCase)

    /// Pipe fallback: the line-oriented session, dressed up as a backend. Output has its escapes removed and gets
    /// terminal line ends; typing is echoed by a `LineEditor`.
    let private pipeBackend
        (shell: ShellChoice)
        (cwd: string)
        (onOutput: string -> unit)
        (onExit: int -> unit)
        : Result<TermBackend, string> =
        let session =
            TermSession(shell, cwd, (fun s -> onOutput (s.Replace("\n", "\r\n"))), onExit)

        match session.Start() with
        | Error e -> Error e
        | Ok() ->
            let editor = LineEditor(onOutput, session.Send)

            Ok
                { Shell = shell
                  IsPty = false
                  IsRunning = fun () -> session.IsRunning
                  Write = editor.Feed
                  Resize = ignore
                  Stop = session.Stop }

    /// Opens a shell in `cwd`: on a pseudo-terminal when the system can make one, otherwise (or when the person asked
    /// for it) on pipes. The second part is a line to show the person when it fell back, naming the reason.
    let openShell
        (usePty: bool)
        (ptyShell: ShellChoice)
        (pipeShell: ShellChoice)
        (cwd: string)
        (cols: int)
        (rows: int)
        (onOutput: string -> unit)
        (onExit: int -> unit)
        : Result<TermBackend * string option, string> =
        let viaPipe (note: string option) =
            match pipeBackend pipeShell cwd onOutput onExit with
            | Ok b -> Ok(b, note)
            | Error e -> Error e

        if not usePty || pipeRequested () then
            viaPipe (Some "pseudo-terminal off: line mode (no full-screen programs, colours or Ctrl+C)")
        else
            let session = PtySession(ptyShell, cwd, cols, rows, onOutput, onExit)

            match session.Start() with
            | Ok() ->
                Ok(
                    { Shell = ptyShell
                      IsPty = true
                      IsRunning = fun () -> session.IsRunning
                      Write = session.Write
                      Resize = session.Resize
                      Stop = session.Stop },
                    None
                )
            | Error why ->
                viaPipe (
                    Some(
                        sprintf
                            "no pseudo-terminal (%s): line mode, without full-screen programs, colours or Ctrl+C"
                            why
                    )
                )
