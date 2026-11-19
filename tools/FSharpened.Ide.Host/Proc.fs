namespace FSharpened.Ide.Host

open System
open System.Diagnostics
open System.Text
open System.Threading.Tasks

/// What a finished (or killed) program left behind.
type ProcResult =
    {
        /// `None` when the program was killed because it ran out of time.
        ExitCode: int option
        TimedOut: bool
        Stdout: string
        Stderr: string
        /// Output beyond the cap was read and discarded.
        Truncated: bool
        Millis: int64
    }

/// How to start a program given as a name or a path and an argument list: found on the PATH (with the PATHEXT
/// extensions on Windows), and on Windows a `.cmd` or `.bat` script, which only `cmd.exe` can run, started through
/// `cmd.exe /d /v:off /s /c` with every part quoted for it. Arguments are data (they come from language packs, folder
/// settings and file names), so one that `cmd.exe` could read as more than a value is refused rather than escaped.
type LaunchPlan =
    /// Start `File` itself with these arguments, one by one.
    | Direct of file: string * args: string list
    /// Start `cmd.exe` (`File`) with this exact command line.
    | ViaCmd of file: string * commandLine: string

/// What the plan needs to know about the machine, so it can be tested for another system.
type LaunchEnv =
    {
        IsWindows: bool
        /// The PATH variable (`;` or `:` separated).
        Path: string
        /// PATHEXT, only read on Windows.
        PathExt: string
        Exists: string -> bool
        /// The command interpreter to use for scripts.
        Cmd: string
    }

module Launch =
    let private sep (isWindows: bool) = if isWindows then ';' else ':'

    let private hasDir (name: string) = name.IndexOfAny [| '/'; '\\' |] >= 0

    let private extensions (env: LaunchEnv) : string list =
        let raw =
            if String.IsNullOrWhiteSpace env.PathExt then
                ".COM;.EXE;.BAT;.CMD"
            else
                env.PathExt

        raw.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.filter (fun e -> e.StartsWith ".")
        |> Array.toList

    /// The file a name stands for: a path as given, or the first match along the PATH. On Windows a name is tried
    /// with each PATHEXT extension (the name as it is only when it already ends in one: an extensionless `npm` next to
    /// `npm.cmd` is a script for a Unix shell). The current folder is never searched.
    let resolve (env: LaunchEnv) (name: string) : string option =
        if String.IsNullOrWhiteSpace name then
            None
        else
            let exts = if env.IsWindows then extensions env else []

            let endsInKnown (n: string) =
                exts |> List.exists (fun e -> n.EndsWith(e, StringComparison.OrdinalIgnoreCase))

            let candidates (stem: string) =
                if not env.IsWindows then
                    [ stem ]
                else
                    [ if endsInKnown stem then
                          yield stem
                      for e in exts do
                          yield stem + e.ToLowerInvariant() ]

            let dirs =
                if hasDir name then
                    [ "" ]
                else
                    env.Path.Split(
                        sep env.IsWindows,
                        StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries
                    )
                    |> Array.map (fun d -> d.Trim '"')
                    |> Array.filter (fun d -> d <> "")
                    |> Array.toList

            dirs
            |> List.tryPick (fun d ->
                candidates (
                    if d = "" then
                        name
                    else
                        d.TrimEnd('/', '\\') + (if env.IsWindows then "\\" else "/") + name
                )
                |> List.tryFind env.Exists)

    /// A script `cmd.exe` has to run.
    let isScript (file: string) =
        file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)

    /// One value as `cmd.exe` and the program behind the script will read it: in double quotes, with the backslashes
    /// before the closing quote doubled. Inside the quotes `& | ^ < > ( )` and spaces are plain characters. What
    /// quotes cannot hold is refused: `"` (it ends the quoted part), `%` (`%NAME%` is expanded even inside quotes),
    /// a line break or NUL (they end the command).
    let quoteForCmd (what: string) (value: string) : Result<string, string> =
        let bad =
            value
            |> Seq.tryFind (fun c -> c = '"' || c = '%' || c = '\r' || c = '\n' || c = '\000')

        match bad with
        | Some c ->
            let shown =
                match c with
                | '\r'
                | '\n' -> "a line break"
                | '\000' -> "a NUL character"
                | c -> sprintf "“%c”" c

            Error(
                sprintf
                    "%s cannot be passed to a .cmd or .bat script because it contains %s, which cmd.exe would read as part of the command"
                    what
                    shown
            )
        | None ->
            let trailing = value.Length - value.TrimEnd('\\').Length
            Ok("\"" + value + String('\\', trailing) + "\"")

    /// The command line after `/c`: the script and its arguments, each quoted, all of it in one more pair of quotes
    /// (which `/s` removes).
    let cmdLine (script: string) (args: string list) : Result<string, string> =
        let parts =
            quoteForCmd "the script's path" script
            :: (args |> List.mapi (fun i a -> quoteForCmd (sprintf "argument %d" (i + 1)) a))

        let errors =
            parts
            |> List.choose (function
                | Error e -> Some e
                | Ok _ -> None)

        match errors with
        | e :: _ -> Error e
        | [] ->
            let quoted =
                parts
                |> List.choose (function
                    | Ok v -> Some v
                    | Error _ -> None)

            Ok("\"" + String.Join(" ", quoted) + "\"")

    /// What to start for `exe` and `args` on this machine.
    let plan (env: LaunchEnv) (exe: string) (args: string list) : Result<LaunchPlan, string> =
        let file = defaultArg (resolve env exe) exe

        if env.IsWindows && isScript file then
            match cmdLine file args with
            | Ok line -> Ok(ViaCmd(env.Cmd, "/d /v:off /s /c " + line))
            | Error e -> Error e
        else
            Ok(Direct(file, args))

    /// The machine this process runs on.
    let realEnv () : LaunchEnv =
        let win = OperatingSystem.IsWindows()

        let cmd =
            let root = Environment.GetEnvironmentVariable "SystemRoot"

            if win && not (String.IsNullOrEmpty root) then
                let c = IO.Path.Combine(root, "System32", "cmd.exe")
                if IO.File.Exists c then c else "cmd.exe"
            else
                "cmd.exe"

        { IsWindows = win
          Path = defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "PATH")) ""
          PathExt = defaultArg (Option.ofObj (Environment.GetEnvironmentVariable "PATHEXT")) ""
          Exists = IO.File.Exists
          Cmd = cmd }

    /// A start description for the program and arguments (no shell unless the program is a Windows script), or why it
    /// cannot be started safely. The caller sets the redirections.
    let startInfo (argv: string list) : Result<ProcessStartInfo, string> =
        match argv with
        | [] -> Error "there is no program to run"
        | exe :: args ->
            match plan (realEnv ()) exe args with
            | Error e -> Error e
            | Ok(Direct(file, a)) ->
                let psi = ProcessStartInfo(file)
                a |> List.iter psi.ArgumentList.Add
                Ok psi
            | Ok(ViaCmd(file, line)) ->
                let psi = ProcessStartInfo(file)
                psi.Arguments <- line
                Ok psi

module Proc =
    /// Characters kept per stream; a program that prints more is not stopped, its extra output is dropped.
    let maxOutput = 1024 * 1024

    let private drain (reader: IO.StreamReader) (cap: int) : Task<string * bool> =
        task {
            let sb = StringBuilder()
            let buf = Array.zeroCreate<char> 4096
            let mutable truncated = false
            let mutable fin = false

            while not fin do
                let! n = reader.ReadAsync(buf, 0, buf.Length)

                if n = 0 then
                    fin <- true
                elif sb.Length < cap then
                    let take = min n (cap - sb.Length)
                    sb.Append(buf, 0, take) |> ignore

                    if take < n then
                        truncated <- true
                else
                    truncated <- true

            return sb.ToString(), truncated
        }

    /// Start a program (an argument vector: no shell, no quoting), feed it `stdin`, collect its output, and kill it and
    /// everything it started when `timeout` passes.
    let run (argv: string list) (cwd: string) (stdin: string option) (timeout: TimeSpan) : Result<ProcResult, string> =
        match argv, Launch.startInfo argv with
        | [], _ -> Error "there is no program to run"
        | _, Error e -> Error e
        | exe :: _, Ok psi ->
            psi.WorkingDirectory <- cwd
            // a git hook (the pre-push check) points GIT_DIR at its own repository; a program started here, `git status`
            // of a folder the person opened included, must look at its own folder, not at that one
            for v in [ "GIT_DIR"; "GIT_WORK_TREE"; "GIT_INDEX_FILE"; "GIT_PREFIX"; "GIT_COMMON_DIR" ] do
                psi.Environment.Remove v |> ignore

            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.StandardInputEncoding <- UTF8Encoding(false)
            psi.StandardOutputEncoding <- UTF8Encoding(false)
            psi.StandardErrorEncoding <- UTF8Encoding(false)

            try
                let clock = Stopwatch.StartNew()
                use p = new Process(StartInfo = psi)
                p.Start() |> ignore
                let o = drain p.StandardOutput maxOutput
                let e = drain p.StandardError maxOutput

                let feed =
                    Task.Run(fun () ->
                        try
                            match stdin with
                            | Some s when s <> "" -> p.StandardInput.Write s
                            | _ -> ()

                            p.StandardInput.Close()
                        with _ ->
                            ())

                let finished = p.WaitForExit(int timeout.TotalMilliseconds)

                if not finished then
                    (try
                        p.Kill true
                     with _ ->
                         ())

                    p.WaitForExit 2000 |> ignore

                // A grandchild may keep a pipe open; do not wait for it forever.
                Task.WaitAll([| o :> Task; e :> Task; feed |], 3000) |> ignore

                let read (t: Task<string * bool>) =
                    if t.IsCompletedSuccessfully then t.Result else ("", false)

                let so, t1 = read o
                let se, t2 = read e

                Ok
                    { ExitCode = if finished then Some p.ExitCode else None
                      TimedOut = not finished
                      Stdout = so
                      Stderr = se
                      Truncated = t1 || t2
                      Millis = clock.ElapsedMilliseconds }
            with
            | :? ComponentModel.Win32Exception as ex -> Error(sprintf "could not start “%s”: %s" exe ex.Message)
            | :? InvalidOperationException as ex -> Error(sprintf "could not start “%s”: %s" exe ex.Message)
