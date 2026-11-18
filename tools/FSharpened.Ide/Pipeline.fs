namespace FSharpened.Ide

/// One step of a pipeline: a file of the project, run on its own with the files it needs bundled in.
type Stage =
    { Index: int
      Path: string
      Language: Language
      Bundle: Bundle }

/// What a finished step left behind (the part of its report the next step needs).
type StepSummary =
    { Path: string
      Ok: bool
      Stdout: string
      Summary: string }

type Outcome = { Ok: bool; Summary: string }

/// What to do next in a run.
type Next =
    | RunStage of index: int * stdin: string
    | Stop of Outcome

/// The project pipeline (ADR-0021, ADR-0024): ordered files, the standard output of each one the standard input of the
/// next. A project with no pipeline runs as one step, its entry file. The pure part is here; the page runs the steps.
module Pipeline =
    /// What the gateway accepts as input (gateway/src/policy.ts, LIMITS.stdin).
    let maxStdin = 4096

    /// The paths that run, in order.
    let steps (project: Project) : string list =
        match project.Pipeline with
        | [] ->
            match Project.entryFile project with
            | Some f -> [ f.Path ]
            | None -> []
        | stages -> stages

    /// Every step bundled, or every reason one cannot be.
    let plan (project: Project) : Result<Stage list, BundleProblem list> =
        let results =
            steps project
            |> List.mapi (fun i path ->
                match Bundler.make project path with
                | Ok b ->
                    Ok
                        { Index = i
                          Path = path
                          Language = b.Language
                          Bundle = b }
                | Error ps -> Error ps)

        let problems =
            results
            |> List.collect (fun r ->
                match r with
                | Error ps -> ps
                | Ok _ -> [])

        if not problems.IsEmpty then
            Error problems
        elif results.IsEmpty then
            Error [ BundleKit.problem "" 0 "the project has no source file to run" ]
        else
            Ok(
                results
                |> List.choose (fun r ->
                    match r with
                    | Ok s -> Some s
                    | Error _ -> None)
            )

    let private plural (n: int) (one: string) (many: string) =
        string n + " " + (if n = 1 then one else many)

    /// After the steps in `finished` (in order), what comes next. `stdin` is the project's input, for the first step.
    let next (paths: string list) (finished: StepSummary list) (stdin: string) : Next =
        let n = finished.Length
        let last = List.tryLast finished

        match last with
        | Some l when not l.Ok ->
            Stop
                { Ok = false
                  Summary =
                    (if paths.Length > 1 then
                         "Step " + string n + " of " + string paths.Length + " (" + l.Path + ") failed: "
                     else
                         "")
                    + l.Summary
                    + (if n < paths.Length then
                           "; " + plural (paths.Length - n) "step" "steps" + " not run"
                       else
                           "") }
        | _ when n >= paths.Length ->
            Stop
                { Ok = true
                  Summary =
                    if paths.Length > 1 then
                        "All " + string paths.Length + " steps finished"
                    else
                        match last with
                        | Some l -> l.Summary
                        | None -> "Nothing to run" }
        | _ ->
            let input =
                match last with
                | None -> stdin
                | Some l -> l.Stdout

            if input.Length > maxStdin then
                Stop
                    { Ok = false
                      Summary =
                        "Step "
                        + string (n + 1)
                        + " ("
                        + paths.[n]
                        + ") cannot start: the output of "
                        + (match last with
                           | Some l -> l.Path
                           | None -> "the project")
                        + " is "
                        + string input.Length
                        + " characters and the gateway takes at most "
                        + string maxStdin
                        + " as input. Print less, or run the steps one at a time." }
            else
                RunStage(n, input)
