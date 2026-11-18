/// Debugging through the Debug Adapter Protocol: breakpoints, the DEBUG panel and the session.
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

type DebugPanel
    (
        ui: Ui,
        appearance: Appearance,
        languageServers: LanguageServers,
        editorTabs: EditorTabs,
        renameFeature: RenameFeature
    ) =
    let dbgButtons = ui.DbgButtons
    let dbgConsole = ui.DbgConsole
    let dbgStack = ui.DbgStack
    let dbgStatus = ui.DbgStatus
    let dbgVars = ui.DbgVars
    let docs = ui.Docs
    let editor = ui.Editor
    let env = ui.Env
    let flagsBox = ui.FlagsBox
    let fullPathOf = renameFeature.FullPathOf
    let label = ui.Label
    let muted = ui.Muted
    let openFile = editorTabs.OpenFile
    let saveAll = editorTabs.SaveAll
    let say = ui.Say
    let showPanel = appearance.ShowPanel
    let syncLsp = languageServers.SyncLsp
    let themed = ui.Themed
    let window = ui.Window

    // ---- debugging (Debug Adapter Protocol) --------------------------------------------------------
    let mutable dbg: DapSession option = None
    let mutable dbgGen = 0
    let mutable dbgPhase = DebugView.Idle
    let mutable dbgThread = 0
    let mutable dbgOutDir = ""
    let mutable dbgFrame: DapFrame option = None
    let mutable dbgStop: (string * int) option = None
    let closedBreakpoints = Dictionary<string, int list>()
    let execLine = ExecutionLine()

    let bpLinesOf (d: OpenDoc) : int list =
        d.Breakpoints
        |> Seq.filter (fun a -> not a.IsDeleted)
        |> Seq.map (fun a -> a.Line)
        |> Seq.distinct
        |> Seq.sort
        |> List.ofSeq

    let breakpointLines (rel: string) : int list =
        match docs |> Seq.tryFind (fun d -> d.Rel = rel) with
        | Some d -> bpLinesOf d
        | None ->
            match closedBreakpoints.TryGetValue rel with
            | true, l -> l
            | _ -> []

    /// Every file that has breakpoints, open or not, with its lines.
    let allBreakpoints () : (string * int list) list =
        [ for d in docs do
              let l = bpLinesOf d

              if not l.IsEmpty then
                  yield d.Rel, l
          for kv in closedBreakpoints do
              if not (docs |> Seq.exists (fun d -> d.Rel = kv.Key)) && not kv.Value.IsEmpty then
                  yield kv.Key, kv.Value ]

    let mutable toggleBreakpointFn: int -> unit = ignore

    let bpMargin =
        BreakpointMargin(
            (fun () ->
                match ui.Active with
                | Some d -> bpLinesOf d
                | None -> []),
            (fun line -> toggleBreakpointFn line)
        )

    let sessionBreakpoints (rel: string) =
        match ui.Root, dbg with
        | Some r, Some s when dbgPhase <> DebugView.Idle ->
            s.Client.SetBreakpoints(fullPathOf r rel, breakpointLines rel) |> ignore
        | _ -> ()

    let toggleBreakpointAt (d: OpenDoc) (line: int) =
        if line >= 1 && line <= d.Doc.LineCount then
            let before = bpLinesOf d

            if List.contains line (Breakpoints.toggle before line) then
                let a = d.Doc.CreateAnchor(d.Doc.GetLineByNumber(line).Offset)
                a.MovementType <- AnchorMovementType.AfterInsertion
                d.Breakpoints.Add a
            else
                d.Breakpoints.RemoveAll(fun a -> a.IsDeleted || a.Line = line) |> ignore

            bpMargin.InvalidateVisual()
            sessionBreakpoints d.Rel

    let toggleBreakpoint () =
        match ui.Active with
        | Some d -> toggleBreakpointAt d editor.TextArea.Caret.Line
        | None -> say "Open a file to set a breakpoint"

    do
        toggleBreakpointFn <-
            (fun line ->
                match ui.Active with
                | Some d -> toggleBreakpointAt d line
                | None -> ())

        editor.TextArea.LeftMargins.Insert(0, bpMargin)
        editor.TextArea.TextView.BackgroundRenderers.Add execLine

        themed (fun r ->
            bpMargin.DotBrush <- r.Bad
            bpMargin.ArrowBrush <- r.Warn
            execLine.Brush <- SolidColorBrush(Color.FromArgb(70uy, 255uy, 214uy, 10uy)))

        ui.SaveBreakpointsFn <-
            (fun d ->
                let l = bpLinesOf d

                if l.IsEmpty then
                    closedBreakpoints.Remove d.Rel |> ignore
                else
                    closedBreakpoints[d.Rel] <- l)

        ui.RestoreBreakpointsFn <-
            (fun d ->
                match closedBreakpoints.TryGetValue d.Rel with
                | true, lines ->
                    closedBreakpoints.Remove d.Rel |> ignore

                    for line in lines do
                        if line >= 1 && line <= d.Doc.LineCount then
                            let a = d.Doc.CreateAnchor(d.Doc.GetLineByNumber(line).Offset)
                            a.MovementType <- AnchorMovementType.AfterInsertion
                            d.Breakpoints.Add a
                | _ -> ())

    /// The band and arrow on the line the program stopped at, only while that file is the one shown.
    let refreshExec () =
        let line =
            match dbgStop, ui.Active with
            | Some(rel, l), Some d when d.Rel = rel -> l
            | _ -> 0

        execLine.Line <- line
        bpMargin.StoppedLine <- line
        editor.TextArea.TextView.InvalidateLayer KnownLayer.Background

    do ui.RefreshExecFn <- refreshExec

    let appendConsole (text: string) =
        Dispatcher.UIThread.Post(fun () ->
            dbgConsole.Text <-
                DebugView.capConsole 200000 (dbgConsole.Text |> Option.ofObj |> Option.defaultValue "") text

            dbgConsole.CaretIndex <- dbgConsole.Text.Length)

    let setPhase (p: DebugView.Phase) (detail: string) =
        dbgPhase <- p
        dbgStatus.Text <- DebugView.statusText p detail

        let enable (id: string) (v: bool) =
            match dbgButtons.TryGetValue id with
            | true, b -> b.IsEnabled <- v
            | _ -> ()

        enable "continue" (DebugView.canStep p)
        enable "over" (DebugView.canStep p)
        enable "into" (DebugView.canStep p)
        enable "out" (DebugView.canStep p)
        enable "pause" (DebugView.canPause p)
        enable "stop" (DebugView.canStop p)
        enable "start" (p = DebugView.Idle)

        if p <> DebugView.Idle then
            say (DebugView.statusText p detail)

    /// Children of a scope or a structured value, fetched when its row is opened.
    let rec variableRow (v: DapVariable) : TreeViewItem =
        let item = TreeViewItem(Header = label (DebugView.variableLabel v))

        if v.VarRef > 0 then
            item.Items.Push(TreeViewItem(Header = muted "…"))
            let loaded = ref false

            item.PropertyChanged.Add(fun a ->
                if
                    a.Property = TreeViewItem.IsExpandedProperty
                    && item.IsExpanded
                    && not loaded.Value
                then
                    loaded.Value <- true
                    loadChildren item v.VarRef)

        item

    and loadChildren (item: TreeViewItem) (reference: int) =
        match dbg with
        | Some s ->
            task {
                let! r = s.Client.Variables reference
                item.Items.Clear()

                match r with
                | Ok vs ->
                    for v in vs do
                        item.Items.Push(variableRow v)
                | Error m -> item.Items.Push(TreeViewItem(Header = muted m))
            }
            |> ignore
        | None -> ()

    let showVariables (f: DapFrame) =
        match dbg with
        | Some s ->
            let gen = dbgGen

            task {
                let! scopes = s.Client.Scopes f.FrameId

                if gen = dbgGen then
                    dbgVars.Items.Clear()

                    match scopes with
                    | Ok list ->
                        for sc in list do
                            let row = TreeViewItem(Header = label sc.ScopeName)

                            if sc.ScopeRef > 0 then
                                row.Items.Push(TreeViewItem(Header = muted "…"))
                                let loaded = ref false

                                let load () =
                                    if not loaded.Value then
                                        loaded.Value <- true
                                        loadChildren row sc.ScopeRef

                                row.PropertyChanged.Add(fun a ->
                                    if a.Property = TreeViewItem.IsExpandedProperty && row.IsExpanded then
                                        load ())

                                // the first scope (usually the locals) is open; a slow one waits to be asked for
                                if not sc.Expensive && dbgVars.Items.Count = 0 then
                                    row.IsExpanded <- true

                            dbgVars.Items.Push row
                    | Error m -> dbgVars.Items.Push(TreeViewItem(Header = muted m))
            }
            |> ignore
        | None -> ()

    let selectFrame (f: DapFrame) =
        dbgFrame <- Some f

        match ui.Root with
        | Some r ->
            match DebugView.relOfFrame r f with
            | Some rel when f.FrameLine > 0 ->
                dbgStop <- Some(rel, f.FrameLine)
                openFile rel f.FrameLine 1
                refreshExec ()
            | _ ->
                dbgStop <- None
                refreshExec ()
        | None -> ()

        showVariables f

    let showStack (threadId: int) =
        match ui.Root, dbg with
        | Some r, Some s ->
            let gen = dbgGen

            task {
                let! frames = s.Client.StackTrace threadId

                if gen = dbgGen then
                    dbgStack.Items.Clear()

                    match frames with
                    | Ok list ->
                        let rows =
                            list
                            |> List.map (fun f ->
                                let t = TextBlock(Text = DebugView.frameLabel r f, Tag = f)

                                themed (fun ro -> t.Foreground <- if f.FramePath = "" then ro.Muted else ro.Fg)

                                t)

                        for t in rows do
                            dbgStack.Items.Push t

                        // the top frame that is in the opened folder, else the first
                        let pick =
                            list
                            |> List.tryFindIndex (fun f -> (DebugView.relOfFrame r f).IsSome && f.FrameLine > 0)
                            |> Option.defaultValue 0

                        if not rows.IsEmpty then
                            dbgStack.SelectedIndex <- pick
                    | Error m -> appendConsole (sprintf "Could not read the call stack: %s\n" m)
            }
            |> ignore
        | _ -> ()

    let clearStop () =
        dbgStop <- None
        dbgFrame <- None
        dbgStack.Items.Clear()
        dbgVars.Items.Clear()
        refreshExec ()

    /// Ends the session: asks the adapter to stop the program, kills it if it does not, removes the build output.
    let endSession (note: string) =
        let sess = dbg
        let outDir = dbgOutDir
        dbg <- None
        dbgOutDir <- ""
        dbgGen <- dbgGen + 1

        match sess with
        | Some s ->
            Task.Run(fun () ->
                (try
                    s.Stop().Wait(5000) |> ignore
                 with _ ->
                     ())

                if outDir <> "" then
                    try
                        Directory.Delete(outDir, true)
                    with _ ->
                        ())
            |> ignore
        | None ->
            if outDir <> "" then
                try
                    Directory.Delete(outDir, true)
                with _ ->
                    ()

        clearStop ()
        setPhase DebugView.Idle ""

        if note <> "" then
            appendConsole (note + "\n")
            say note

    do
        ui.StopDebugFn <-
            (fun note ->
                if dbgPhase <> DebugView.Idle then
                    endSession note)

    let onDapEvent (gen: int) (e: DapEvent) =
        Dispatcher.UIThread.Post(fun () ->
            if gen = dbgGen then
                match e with
                | Stopped(thread, reason, text) ->
                    dbgThread <- thread
                    setPhase DebugView.Paused (DebugView.stopDetail reason text)
                    showStack thread
                | Continued _ ->
                    if dbgPhase = DebugView.Paused then
                        setPhase DebugView.Running ""
                        clearStop ()
                | Output(cat, text) ->
                    if DebugView.showsOutput cat then
                        appendConsole text
                | Exited code -> appendConsole (sprintf "[the program exited with code %d]\n" code)
                | Terminated -> endSession "Debug session ended"
                | Closed -> endSession "The debug adapter closed"
                | ThreadChanged _
                | BreakpointChanged _
                | Initialized -> ())

    let installHint (spec: DebugSpec) =
        sprintf
            "The debug adapter “%s” is not installed or not on the PATH.%s"
            spec.Adapter
            (if spec.Install <> "" then
                 "\n\nGet it: " + spec.Install
             else
                 "")

    let startDebug () =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            match d.Pack |> Option.bind (fun p -> p.Debug |> Option.map (fun s -> p, s)) with
            | None ->
                Dialogs.info
                    window
                    ui.Roles
                    "Debugging is not set up"
                    (sprintf
                        "%s has no debug adapter configured. A language pack adds one with a “debug” block (see docs/operations/desktop.md)."
                        (match d.Pack with
                         | Some p -> p.Name
                         | None -> "This file"))
                |> ignore
            | Some(pack, spec) ->
                if not (Dap.onPath spec.Adapter) then
                    showPanel "debug"
                    appendConsole (installHint spec + "\n")

                    Dialogs.info window ui.Roles "Debug adapter not found" (installHint spec)
                    |> ignore
                else
                    task {
                        saveAll ()

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
                                                "Debugging starts programs on this computer, with your permissions: the debug adapter and your code, from:\n\n%s\n\nOnly trust folders whose code you would run anyway. You can revoke this from the command palette."
                                                r)
                                            "Trust and debug"

                                    if yes then
                                        env.Trust.Trust r
                                        syncLsp d

                                    return yes
                            }

                        if allowed then
                            dbgGen <- dbgGen + 1
                            let gen = dbgGen
                            dbgConsole.Text <- ""
                            showPanel "debug"
                            setPhase DebugView.Starting ""

                            let outDir =
                                Path.Combine(Path.GetTempPath(), "fsharpened-debug-" + Guid.NewGuid().ToString "N")

                            Directory.CreateDirectory outDir |> ignore
                            dbgOutDir <- outDir

                            let fail (title: string) (msg: string) =
                                appendConsole (msg + "\n")
                                endSession ""
                                Dialogs.info window ui.Roles title msg |> ignore

                            match Plans.parseFlags (flagsBox.Text |> Option.ofObj |> Option.defaultValue "") with
                            | Error e -> fail "Debug" e
                            | Ok flags ->
                                let ctx: PlanContext =
                                    { Root = r
                                      Entry = d.Rel
                                      OutDir = outDir
                                      Flags = flags
                                      ExeSuffix = Runner.exeSuffix () }

                                // 1. build, when the language needs it
                                let! built =
                                    task {
                                        match spec.Build with
                                        | None -> return Ok()
                                        | Some step ->
                                            match env.Box.Get step.Tool, DebugPlan.expandArgs ctx step.Args with
                                            | _, Error e -> return Error e
                                            | Some t, Ok args when t.Found ->
                                                appendConsole (sprintf "Building with %s…\n" t.Label)

                                                let! res =
                                                    Task.Run(fun () ->
                                                        Proc.run (t.Exe @ args) r None (TimeSpan.FromSeconds 180.0))

                                                match res with
                                                | Ok p when p.ExitCode = Some 0 -> return Ok()
                                                | Ok p ->
                                                    return
                                                        Error(
                                                            sprintf
                                                                "The build failed, so nothing was started:\n%s%s"
                                                                p.Stdout
                                                                p.Stderr
                                                        )
                                                | Error e -> return Error e
                                            | Some t, _ ->
                                                return
                                                    Error(
                                                        sprintf
                                                            "%s is not installed%s"
                                                            t.Label
                                                            (if t.Install <> "" then " (" + t.Install + ")" else "")
                                                    )
                                            | None, _ -> return Error(sprintf "the pack has no tool “%s”" step.Tool)
                                    }

                                match built with
                                | Error e ->
                                    if gen = dbgGen then
                                        fail "Debug" e
                                | Ok() when gen <> dbgGen -> ()
                                | Ok() ->
                                    match DebugPlan.expandConfiguration ctx spec.Configuration with
                                    | Error e -> fail "Debug" e
                                    | Ok config ->
                                        // 2. start the adapter
                                        appendConsole (sprintf "Starting %s…\n" spec.Adapter)

                                        let! started = Task.Run(fun () -> Dap.start spec (onDapEvent gen))

                                        match started with
                                        | Error e -> fail "Debug adapter" e
                                        | Ok sess ->
                                            if gen <> dbgGen then
                                                sess.Stop() |> ignore
                                            else
                                                dbg <- Some sess

                                                let bps =
                                                    allBreakpoints () |> List.map (fun (rel, l) -> fullPathOf r rel, l)

                                                // 3. initialize, launch, breakpoints, configurationDone
                                                let! begun = sess.Begin(config, bps)

                                                if gen = dbgGen then
                                                    match begun with
                                                    | Error e -> fail "Debugging could not start" e
                                                    | Ok() ->
                                                        if dbgPhase = DebugView.Starting then
                                                            setPhase DebugView.Running ""
                    }
                    |> ignore
        | _ -> say "Open a file in a folder to debug it"

    let debugStep (what: string) (act: DapClient -> int -> Task<Result<unit, string>>) =
        match dbg with
        | Some s when dbgPhase = DebugView.Paused ->
            let thread = dbgThread
            setPhase DebugView.Running ""
            clearStop ()

            task {
                let! r = act s.Client thread

                match r with
                | Ok() -> ()
                | Error m -> appendConsole (sprintf "%s failed: %s\n" what m)
            }
            |> ignore
        | Some _ -> say "The program is not paused"
        | None -> say "No debug session is running"

    let debugContinue () =
        debugStep "Continue" (fun c t -> c.Continue t)

    let debugPause () =
        match dbg with
        | Some s when dbgPhase = DebugView.Running ->
            task {
                let! r = s.Client.Pause(max 1 dbgThread)

                match r with
                | Ok() -> ()
                | Error m -> appendConsole (sprintf "Pause failed: %s\n" m)
            }
            |> ignore
        | _ -> say "The program is not running"

    let debugStop () =
        if dbgPhase <> DebugView.Idle then
            endSession "Debug session stopped"
        else
            say "No debug session is running"

    let evaluateInput (text: string) =
        match dbg with
        | Some s when text.Trim() <> "" ->
            let frame =
                match dbgFrame with
                | Some f -> f.FrameId
                | None -> 0

            appendConsole ("> " + text + "\n")

            task {
                let! r = s.Client.Evaluate(text, frame, "repl")

                match r with
                | Ok(v, _) -> appendConsole (v + "\n")
                | Error m -> appendConsole (sprintf "error: %s\n" m)
            }
            |> ignore
        | Some _ -> ()
        | None -> appendConsole "There is no debug session. Start one with F5.\n"

    /// F5: start debugging, or continue when the program is paused.
    let debugStartOrContinue () =
        match dbgPhase with
        | DebugView.Idle -> startDebug ()
        | DebugView.Paused -> debugContinue ()
        | DebugView.Starting -> say "The debugger is still starting"
        | DebugView.Running -> say "The program is running; use Pause or Stop"


    member _.AllBreakpoints = allBreakpoints
    member _.BpLinesOf = bpLinesOf
    member _.BreakpointLines = breakpointLines
    member _.ClosedBreakpoints = closedBreakpoints
    member _.DebugContinue = debugContinue
    member _.DebugPause = debugPause
    member _.DebugStartOrContinue = debugStartOrContinue
    member _.DebugStep = debugStep
    member _.DebugStop = debugStop
    member _.EvaluateInput = evaluateInput
    member _.SelectFrame = selectFrame
    member _.SetPhase = setPhase
    member _.ToggleBreakpoint = toggleBreakpoint
    member _.ToggleBreakpointAt = toggleBreakpointAt
