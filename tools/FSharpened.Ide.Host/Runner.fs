namespace FSharpened.Ide.Host

open System
open System.IO
open FSharpened.Ide
open FSharpened.Platform

/// What to run: the plan chosen from a pack, in an opened folder.
type RunRequest =
    {
        Root: string
        /// Relative to the root, forward slashes.
        Entry: string
        Pack: LanguagePack
        Plan: Plan
        Stdin: string
        Flags: string list
        /// Run-step time limit, 1 to 120 seconds.
        TimeoutSec: int
    }

module Runner =
    let defaultTimeoutSec = 30
    let maxTimeoutSec = 120
    let private buildTimeout = TimeSpan.FromSeconds 180.0

    let private exeSuffix () =
        match (RuntimeIdentity.detect ()).Os with
        | Windows -> ".exe"
        | _ -> ""

    /// The tools a plan needs that are not installed, with how to get them.
    let missing (box: ToolBox) (plan: Plan) : ToolInfo list =
        plan.Steps
        |> List.map (fun s -> s.Tool)
        |> List.filter (fun t -> t <> "built")
        |> List.distinct
        |> List.choose box.Get
        |> List.filter (fun t -> not t.Found)

    let private join (r: ProcResult) =
        (r.Stdout
         + (if r.Stderr <> "" && r.Stdout <> "" && not (r.Stdout.EndsWith "\n") then
                "\n"
            else
                "")
         + r.Stderr)
            .TrimEnd()

    /// Run a plan, step by step, in the opened folder. The caller has already checked trust. A failed compile step
    /// stops the plan and is a build failure; the last other step's output is the program's output. The result has the
    /// shape the website's gateway returns, so the same interpreter reads both.
    let run (box: ToolBox) (req: RunRequest) : Result<RawResult, string> =
        let timeout = TimeSpan.FromSeconds(float (max 1 (min maxTimeoutSec req.TimeoutSec)))

        match missing box req.Plan with
        | m :: _ ->
            Error(
                sprintf
                    "%s is not installed or not on the PATH%s."
                    m.Label
                    (if m.Install <> "" then " (" + m.Install + ")" else "")
            )
        | [] ->
            let outDir =
                Path.Combine(Path.GetTempPath(), "fsharpened-run-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory outDir |> ignore

            let ctx =
                { Root = Path.GetFullPath req.Root
                  Entry = req.Entry
                  OutDir = outDir
                  Flags = req.Flags
                  ExeSuffix = exeSuffix () }

            let engineOf (s: FSharpened.Ide.Host.Step) =
                match box.Get s.Tool with
                | Some t -> sprintf "local · %s" (if t.Version <> "" then t.Version else t.Label)
                | None -> "local"

            try
                let sw = Diagnostics.Stopwatch.StartNew()
                let mutable engine = "local"
                let mutable last: ProcResult option = None
                let mutable buildFailure: (ProcResult * string) option = None

                for s in req.Plan.Steps do
                    if buildFailure.IsNone then
                        let args = Plans.expandArgs ctx s.Args

                        let argv =
                            if s.Tool = "built" then
                                Plans.outPath ctx :: args
                            else
                                (box.Get s.Tool |> Option.map (fun t -> t.Exe) |> Option.defaultValue [ s.Tool ])
                                @ args

                        if s.Tool <> "built" then
                            engine <- engineOf s

                        match
                            Proc.run
                                argv
                                ctx.Root
                                (if s.Phase = Compile then None else Some req.Stdin)
                                (if s.Phase = Compile then buildTimeout else timeout)
                        with
                        | Error e ->
                            buildFailure <-
                                Some(
                                    { ExitCode = Some 127
                                      TimedOut = false
                                      Stdout = ""
                                      Stderr = e
                                      Truncated = false
                                      Millis = 0L },
                                    engine
                                )
                        | Ok r when s.Phase = Compile && r.ExitCode <> Some 0 -> buildFailure <- Some(r, engine)
                        | Ok r ->
                            if s.Phase <> Compile then
                                last <- Some r

                match buildFailure, last with
                | Some(r, eng), _ ->
                    Ok
                        { Engine = eng
                          BuildFailed = true
                          BuildOutput = join r
                          Stdout = ""
                          Stderr = ""
                          ExitCode = defaultArg r.ExitCode 1
                          TimedOut = r.TimedOut
                          DurationMs = int sw.ElapsedMilliseconds
                          Cached = false }
                | None, Some r ->
                    Ok
                        { Engine = engine
                          BuildFailed = false
                          BuildOutput = ""
                          Stdout = r.Stdout + (if r.Truncated then "\n[output cut at 1 MB]" else "")
                          Stderr = r.Stderr
                          ExitCode = defaultArg r.ExitCode 1
                          TimedOut = r.TimedOut
                          DurationMs = int sw.ElapsedMilliseconds
                          Cached = false }
                | None, None -> Error "the plan has no step that runs the program"
            finally
                try
                    Directory.Delete(outDir, true)
                with _ ->
                    ()
