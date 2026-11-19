/// Run and format: the trust question, the runner, tool hints, the report in the output and problems panels.
namespace FSharpened.Ide.Desktop

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Styling
open Avalonia.Threading
open AvaloniaEdit.CodeCompletion
open AvaloniaEdit
open AvaloniaEdit.Document
open AvaloniaEdit.Rendering
open AvaloniaEdit.Search
open AvaloniaEdit.TextMate
open TextMateSharp.Grammars
open FSharpened.Ide
open FSharpened.Ide.Host

type RunPanel
    (ui: Ui, appearance: Appearance, problems: Problems, languageServers: LanguageServers, editorTabs: EditorTabs) =
    let env = ui.Env
    let flagsBox = ui.FlagsBox
    let inputBox = ui.InputBox
    let refreshProblems = problems.RefreshProblems
    let saveAll = editorTabs.SaveAll
    let say = ui.Say
    let setOutput = ui.SetOutput
    let showPanel = appearance.ShowPanel
    let syncLsp = languageServers.SyncLsp
    let window = ui.Window

    let mutable running = false

    // ---- run, format -------------------------------------------------------------------------------
    let explainMissing (missing: ToolInfo list) =
        let lines =
            missing
            |> List.map (fun t ->
                sprintf
                    "  • %s is not installed%s"
                    t.Label
                    (if t.Install <> "" then
                         " — get it from " + t.Install
                     else
                         ""))

        "This file needs a program that was not found on this computer:\n"
        + String.Join("\n", lines)
        + "\n\nInstall it, then choose Tools ▸ Refresh Tools."

    let showReport (pack: LanguagePack) (raw: RawResult) =
        let report = Interpret.report pack.Id raw
        let sb = Text.StringBuilder()

        sb.AppendLine(
            sprintf
                "%s · %d ms · exit %d%s"
                raw.Engine
                raw.DurationMs
                raw.ExitCode
                (if raw.TimedOut then " · TIMED OUT" else "")
        )
        |> ignore

        if raw.BuildFailed then
            sb.AppendLine("\nBuild failed:").AppendLine(raw.BuildOutput) |> ignore
        else
            if raw.BuildOutput.Trim() <> "" then
                sb.AppendLine("\nBuild output:").AppendLine(raw.BuildOutput) |> ignore

            sb.AppendLine().Append(raw.Stdout) |> ignore

            if raw.Stderr.Trim() <> "" then
                sb.AppendLine("\n— error output —").AppendLine(raw.Stderr) |> ignore

        setOutput (sb.ToString())
        ui.RunDiagnostics <- report.Diagnostics
        refreshProblems ()

        showPanel (
            if report.Diagnostics.IsEmpty || report.Ok then
                "output"
            else
                "problems"
        )

        say (
            if report.Ok then
                "✔ " + report.Summary
            else
                "✖ " + report.Summary
        )

    let runCurrent (timeoutSec: int) =
        match ui.Root, ui.Active with
        | Some r, Some d when not running ->
            task {
                saveAll ()

                match d.Pack with
                | None ->
                    setOutput (
                        sprintf
                            "There is no language pack for %s, so FSharpened does not know how to run it.\nAdd one in %s (see docs/operations/desktop.md)."
                            d.Rel
                            env.UserPacks
                    )

                    showPanel "output"
                | Some pack ->
                    match Plans.choose r d.Rel pack.Run with
                    | None ->
                        setOutput (
                            sprintf "%s has no way to run %s here (no run plan applies in this folder)." pack.Name d.Rel
                        )

                        showPanel "output"
                    | Some plan ->
                        let missing = Runner.missing env.Box plan

                        if not missing.IsEmpty then
                            setOutput (explainMissing missing)
                            showPanel "output"
                        else
                            let! allowed =
                                task {
                                    if env.Trust.IsTrusted r then
                                        return true
                                    else
                                        let! yes =
                                            Dialogs.confirm
                                                window
                                                ui.Roles
                                                "Trust this folder?"
                                                (sprintf
                                                    "Running code starts programs on this computer, with your permissions, from:\n\n%s\n\nOnly trust folders whose code you would run anyway. You can revoke this from the command palette."
                                                    r)
                                                "Trust and run"

                                        if yes then
                                            env.Trust.Trust r
                                            syncLsp d

                                        return yes
                                }

                            if allowed then
                                match Plans.parseFlags (flagsBox.Text |> Option.ofObj |> Option.defaultValue "") with
                                | Error e ->
                                    setOutput e
                                    showPanel "output"
                                | Ok flags ->
                                    running <- true
                                    say (sprintf "Running %s…" d.Rel)

                                    let req: RunRequest =
                                        { Root = r
                                          Entry = d.Rel
                                          Pack = pack
                                          Plan = plan
                                          Stdin = inputBox.Text |> Option.ofObj |> Option.defaultValue ""
                                          Flags = flags
                                          TimeoutSec = timeoutSec }

                                    let! result = Task.Run(fun () -> Runner.run env.Box req)
                                    running <- false

                                    match result with
                                    | Ok raw -> showReport pack raw
                                    | Error e ->
                                        setOutput e
                                        showPanel "output"
                                        say "✖ could not run"
            }
            |> ignore
        | _ -> ()

    let formatCurrent () =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            match d.Pack |> Option.bind (fun p -> p.Format |> Option.map (fun s -> p, s)) with
            | None -> say "No formatter is set up for this language"
            | Some(pack, step) ->
                task {
                    saveAll ()

                    match env.Box.Get step.Tool with
                    | Some info when info.Found ->
                        let ctx: PlanContext =
                            { Root = r
                              Entry = d.Rel
                              OutDir = Path.GetTempPath()
                              Flags = []
                              ExeSuffix = "" }

                        let argv = info.Exe @ Plans.expandArgs ctx step.Args
                        say (sprintf "Formatting with %s…" info.Label)
                        let! res = Task.Run(fun () -> Proc.run argv r None (TimeSpan.FromSeconds 30.0))

                        match res with
                        | Ok p when p.ExitCode = Some 0 ->
                            match DiskWorkspace.readText r d.Rel with
                            | Ok text when text <> d.Text ->
                                d.Reload text
                                say "Formatted"
                            | _ -> say "Already formatted"
                        | Ok p ->
                            setOutput (sprintf "%s could not format %s:\n%s%s" info.Label d.Rel p.Stdout p.Stderr)
                            showPanel "output"
                        | Error e -> say e
                    | _ -> say (sprintf "%s is not installed" pack.Name)
                }
                |> ignore
        | _ -> ()


    member _.FormatCurrent = formatCurrent
    member _.RunCurrent = runCurrent
