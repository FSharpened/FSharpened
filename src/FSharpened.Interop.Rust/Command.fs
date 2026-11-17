namespace FSharpened.Interop.Rust

open System
open System.ComponentModel
open System.Diagnostics

/// An external command as data: a program and an argument vector. Never a shell string, so there is nothing to
/// quote and nothing for a path or a crate name to inject into.
type CommandPlan =
    { Program: string
      Args: string list
      WorkingDirectory: string option
      Env: (string * string) list }

type RunResult =
    { ExitCode: int
      Stdout: string
      Stderr: string
      TimedOut: bool }

module Command =
    let make (program: string) (args: string list) : CommandPlan =
        { Program = program
          Args = args
          WorkingDirectory = None
          Env = [] }

    /// One line for logs and error messages.
    let render (plan: CommandPlan) : string =
        let quote (a: string) =
            if a.Contains ' ' then "\"" + a + "\"" else a

        String.Join(" ", plan.Program :: (plan.Args |> List.map quote))

    let private failedToStart (plan: CommandPlan) (reason: string) =
        { ExitCode = -1
          Stdout = ""
          Stderr = "could not start '" + plan.Program + "': " + reason
          TimedOut = false }

    /// Runs the command and waits at most the timeout. A command that cannot start (for example cargo is not
    /// installed) is a result with exit code -1, not an exception: callers decide whether that is an error.
    let run (timeoutMs: int) (plan: CommandPlan) : RunResult =
        let info = ProcessStartInfo(plan.Program)

        for arg in plan.Args do
            info.ArgumentList.Add arg

        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false
        info.CreateNoWindow <- true

        plan.WorkingDirectory |> Option.iter (fun dir -> info.WorkingDirectory <- dir)

        for (key, value) in plan.Env do
            info.Environment.[key] <- value

        try
            use proc = Process.Start info

            if isNull proc then
                failedToStart plan "the operating system returned no process"
            else
                let stdout = proc.StandardOutput.ReadToEndAsync()
                let stderr = proc.StandardError.ReadToEndAsync()
                let exited = proc.WaitForExit timeoutMs

                if not exited then
                    proc.Kill true
                    proc.WaitForExit()

                { ExitCode = if exited then proc.ExitCode else -1
                  Stdout = stdout.Result
                  Stderr = stderr.Result
                  TimedOut = not exited }
        with
        | :? Win32Exception as e -> failedToStart plan e.Message
        | :? InvalidOperationException as e -> failedToStart plan e.Message
