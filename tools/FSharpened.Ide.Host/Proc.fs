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
        match argv with
        | [] -> Error "there is no program to run"
        | exe :: args ->
            let psi = ProcessStartInfo(exe)
            args |> List.iter psi.ArgumentList.Add
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
