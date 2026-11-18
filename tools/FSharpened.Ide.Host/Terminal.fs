namespace FSharpened.Ide.Host

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open FSharpened.Platform

/// The shell a terminal panel starts.
type ShellChoice = { Name: string; Argv: string list }

/// Terminal helpers. The line-oriented `TermSession` below is the fallback (ADR-0023): the user's own shell with its
/// input and output on pipes. It is **not** a pseudo-terminal: full-screen programs (vim, top), colours, and Ctrl+C do
/// not work; each line typed is sent to the shell, and what it prints comes back with the escape sequences removed.
/// The real thing is `PtySession` in TermBackend.fs, which uses this module only to choose the shell.
module Terminal =
    // a verbatim string of regex escapes: \x1b is the escape character and \x07 the bell
    let private sequence =
        Regex(
            @"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[()][0-9A-Za-z]|\x1b[=>78]",
            RegexOptions.Compiled
        )

    /// Removes terminal escape sequences (colours, cursor moves, window titles) and carriage returns, which a plain text
    /// view cannot show. Returns the clean text and any unfinished sequence at the end, to be put in front of the next
    /// chunk.
    let strip (chunk: string) : string * string =
        let cut = chunk.LastIndexOf '\u001b'

        let body, held =
            if
                cut >= 0
                && not (sequence.IsMatch(chunk.Substring cut))
                && chunk.Length - cut < 64
            then
                chunk.Substring(0, cut), chunk.Substring cut
            else
                chunk, ""

        sequence.Replace(body, "").Replace("\r\n", "\n").Replace("\r", ""), held

    /// Which shell to start. `exists` says whether a program is on the PATH (or a file exists, for an absolute path);
    /// `envShell` is the user's `$SHELL`. Nothing here starts anything, so it is the same on every system.
    let chooseShell (os: Os) (exists: string -> bool) (envShell: string option) : ShellChoice =
        match os with
        | Windows ->
            if exists "pwsh.exe" then
                { Name = "PowerShell"
                  Argv = [ "pwsh.exe"; "-NoLogo"; "-NoProfile"; "-Command"; "-" ] }
            elif exists "powershell.exe" then
                { Name = "Windows PowerShell"
                  Argv = [ "powershell.exe"; "-NoLogo"; "-NoProfile"; "-Command"; "-" ] }
            else
                { Name = "Command Prompt"
                  Argv = [ "cmd.exe"; "/Q"; "/D" ] }
        | _ ->
            match envShell with
            | Some s when s <> "" && exists s ->
                { Name = Path.GetFileName s
                  Argv = [ s ] }
            | _ when exists "bash" ->
                { Name = "bash"
                  Argv = [ "bash"; "--noprofile"; "--norc" ] }
            | _ -> { Name = "sh"; Argv = [ "sh" ] }

    /// The shell to start on a pseudo-terminal. A real terminal needs no flag to make the shell read commands from
    /// standard input (the pipe session's `-Command -`); a shell on a terminal is interactive by itself, so the person's
    /// own profile and prompt load.
    let chooseInteractive (os: Os) (exists: string -> bool) (envShell: string option) : ShellChoice =
        match os with
        | Windows ->
            if exists "pwsh.exe" then
                { Name = "PowerShell"
                  Argv = [ "pwsh.exe"; "-NoLogo" ] }
            elif exists "powershell.exe" then
                { Name = "Windows PowerShell"
                  Argv = [ "powershell.exe"; "-NoLogo" ] }
            else
                { Name = "Command Prompt"
                  Argv = [ "cmd.exe" ] }
        | _ -> chooseShell os exists envShell

    /// Whether a program name resolves on the PATH, or an absolute path is a file.
    let onPath (name: string) : bool =
        if Path.IsPathRooted name then
            File.Exists name
        else
            let dirs =
                (Environment.GetEnvironmentVariable "PATH"
                 |> Option.ofObj
                 |> Option.defaultValue "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)

            dirs
            |> Array.exists (fun d ->
                try
                    File.Exists(Path.Combine(d.Trim '"', name))
                with _ ->
                    false)

    /// The shell for this computer.
    let defaultShell () : ShellChoice =
        chooseShell (RuntimeIdentity.detect ()).Os onPath (Environment.GetEnvironmentVariable "SHELL" |> Option.ofObj)

    /// The shell for this computer, for a pseudo-terminal.
    let defaultInteractiveShell () : ShellChoice =
        chooseInteractive
            (RuntimeIdentity.detect ()).Os
            onPath
            (Environment.GetEnvironmentVariable "SHELL" |> Option.ofObj)

/// One running shell on pipes. Output arrives on a pool thread through `onOutput`; `onExit` is called once with the exit code.
type TermSession(shell: ShellChoice, cwd: string, onOutput: string -> unit, onExit: int -> unit) =
    let mutable proc: Process = null
    let mutable stopped = false

    let pump (reader: StreamReader) =
        Task.Run(fun () ->
            let buf = Array.zeroCreate<char> 4096
            let mutable held = ""
            let mutable fin = false

            while not fin do
                let n =
                    try
                        reader.Read(buf, 0, buf.Length)
                    with _ ->
                        0

                if n = 0 then
                    fin <- true
                else
                    let clean, rest = Terminal.strip (held + String(buf, 0, n))
                    held <- rest

                    if clean <> "" then
                        onOutput clean)

    member _.Shell = shell
    member _.IsRunning = not (isNull proc) && not proc.HasExited

    /// Starts the shell in `cwd`. The folder must already be trusted: this runs whatever is typed into it.
    member this.Start() : Result<unit, string> =
        match shell.Argv with
        | [] -> Error "there is no shell to start"
        | exe :: args ->
            let psi = ProcessStartInfo(exe)
            args |> List.iter psi.ArgumentList.Add
            psi.WorkingDirectory <- cwd
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.StandardInputEncoding <- UTF8Encoding(false)
            psi.StandardOutputEncoding <- UTF8Encoding(false)
            psi.StandardErrorEncoding <- UTF8Encoding(false)
            psi.Environment["TERM"] <- "dumb"
            psi.Environment["NO_COLOR"] <- "1"

            try
                let p = new Process(StartInfo = psi, EnableRaisingEvents = true)
                p.Start() |> ignore
                proc <- p
                let o = pump p.StandardOutput
                let e = pump p.StandardError

                Task.Run(fun () ->
                    try
                        p.WaitForExit()
                        Task.WaitAll([| o; e |], 2000) |> ignore

                        if not stopped then
                            onExit p.ExitCode
                    with _ ->
                        ())
                |> ignore

                Ok()
            with
            | :? ComponentModel.Win32Exception as ex -> Error(sprintf "could not start “%s”: %s" exe ex.Message)
            | :? InvalidOperationException as ex -> Error(sprintf "could not start “%s”: %s" exe ex.Message)

    /// Sends one line to the shell, as if typed and followed by Enter.
    member _.Send(line: string) =
        if not (isNull proc) && not proc.HasExited then
            try
                proc.StandardInput.WriteLine line
                proc.StandardInput.Flush()
            with _ ->
                ()

    /// Ends the shell and everything it started.
    member _.Stop() =
        stopped <- true

        if not (isNull proc) then
            try
                if not proc.HasExited then
                    proc.Kill true
            with _ ->
                ()
