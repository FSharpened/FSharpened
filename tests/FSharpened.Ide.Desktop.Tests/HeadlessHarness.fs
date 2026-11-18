/// Runs the real window without a display: Avalonia's headless platform draws nothing but builds the whole control tree,
/// runs layout, focus, key and pointer handling and the dispatcher, so a test can open a folder in the actual `Shell` and
/// look at what a person would see. All tests that use a window run on one UI thread, one at a time.
module HeadlessHarness

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.LogicalTree
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open FSharpened.Ide.Desktop
open FSharpened.Ide.Host

// every test of this project shares one UI thread, so they must not run side by side
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

/// How the headless platform builds the application: the app's own `App` class, no display.
type TestAppBuilder =
    static member BuildAvaloniaApp() : AppBuilder =
        AppBuilder.Configure<Program.App>().UseHeadless(AvaloniaHeadlessPlatformOptions())

let private session =
    lazy (HeadlessUnitTestSession.StartNew(typeof<TestAppBuilder>))

/// Runs `f` on the UI thread and waits for it.
let onUi (f: unit -> 'T) : 'T =
    session.Value.Dispatch(Func<'T>(f), CancellationToken.None).GetAwaiter().GetResult()

/// Lets the dispatcher run what is queued (posted continuations, timers that are due) and the layout settle.
let pump (ms: int) =
    let sw = Stopwatch.StartNew()

    while sw.ElapsedMilliseconds < int64 ms do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep 5

/// Pumps until the condition holds; fails with `what` after the timeout. Call on the UI thread.
let until (what: string) (timeoutMs: int) (cond: unit -> bool) =
    let sw = Stopwatch.StartNew()
    let mutable ok = cond ()

    while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep 10
        ok <- cond ()

    if not ok then
        failwithf "Timed out after %d ms waiting for: %s" timeoutMs what

/// Every control below `root`, in the visual tree and in the logical tree (menu items and tree rows that are not
/// drawn yet are only in the logical one).
let descendants (root: Control) : Control list =
    let seen = HashSet<Control>(HashIdentity.Reference)
    let out = List<Control>()

    let add (c: Control) =
        if seen.Add c then
            out.Add c

    for v in root.GetVisualDescendants() do
        match v with
        | :? Control as c -> add c
        | _ -> ()

    for l in root.GetLogicalDescendants() do
        match l with
        | :? Control as c -> add c
        | _ -> ()

    List.ofSeq out

/// The control with this automation id (the slug of its accessible name), or a failure listing the ids that exist.
let find<'T when 'T :> Control> (root: Control) (id: string) : 'T =
    let all = descendants root

    match
        all
        |> List.tryFind (fun c -> AutomationProperties.GetAutomationId c = id && (c :? 'T))
    with
    | Some c -> c :?> 'T
    | None ->
        let ids =
            all
            |> List.map AutomationProperties.GetAutomationId
            |> List.filter (fun s -> not (String.IsNullOrEmpty s))
            |> List.distinct

        failwithf "No %s with automation id '%s'. Ids in the window: %s" typeof<'T>.Name id (String.Join(", ", ids))

let tryFind<'T when 'T :> Control> (root: Control) (id: string) : 'T option =
    descendants root
    |> List.tryFind (fun c -> AutomationProperties.GetAutomationId c = id && (c :? 'T))
    |> Option.map (fun c -> c :?> 'T)

/// Raises a key press on a control, as the keyboard would (tunnel, then bubble).
let press (target: InputElement) (key: Key) (mods: KeyModifiers) =
    let args =
        KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods)

    args.Source <- target
    target.RaiseEvent args

/// The temporary folder a test works in, its data folder, and the settings the window runs with.
type Workspace(files: (string * string) list, trusted: bool) =
    let root =
        let d =
            Path.Combine(Path.GetTempPath(), "fsh-headless-" + Guid.NewGuid().ToString("N").Substring(0, 8))

        Directory.CreateDirectory d |> ignore
        d

    let data =
        let d =
            Path.Combine(Path.GetTempPath(), "fsh-headless-data-" + Guid.NewGuid().ToString("N").Substring(0, 8))

        Directory.CreateDirectory d |> ignore
        d

    do
        for (rel, text) in files do
            let full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
            Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
            File.WriteAllText(full, text)

    let env =
        let e = HostEnv(data, Path.Combine(AppContext.BaseDirectory, "languages"))
        e.ReloadLanguages()

        if trusted then
            e.Trust.Trust root

        e

    member _.Root = root
    member _.Env = env

    member _.Path(rel: string) =
        Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))

    member w.Read(rel: string) = File.ReadAllText(w.Path rel)

    interface IDisposable with
        member _.Dispose() =
            for d in [ root; data ] do
                try
                    Directory.Delete(d, true)
                with _ ->
                    ()

/// The files most tests use.
let sampleFiles =
    [ "src/app.fs", "let main () = printfn \"hello\"\nlet answer = 42\n"
      "src/util.go", "package util\n\nfunc Hello() string { return \"hello\" }\n"
      "notes.txt", "hello world\nanswer hello\n"
      "README.md", "# Sample\n" ]

/// A window on a workspace, shown, and closed (with its watchers and terminal) when the test ends.
type Session(ws: Workspace) =
    let shell = Shell(ws.Env)

    do
        shell.Window.Width <- 1200.0
        shell.Window.Height <- 760.0
        shell.Window.Show()
        pump 50

    member _.Shell = shell
    member _.Window = shell.Window
    member _.Workspace = ws

    /// Opens the workspace folder in the window and waits for the tree.
    member _.OpenFolder() =
        shell.OpenFolder ws.Root
        until "the folder tree to fill" 5000 (fun () -> (find<TreeView> shell.Window "folder-tree").ItemCount > 0)

    interface IDisposable with
        member _.Dispose() =
            try
                shell.ForceQuit()
            with _ ->
                ()

            pump 20

/// Runs a test body on the UI thread with a window on a fresh workspace.
let withWindow (trusted: bool) (files: (string * string) list) (body: Session -> unit) =
    onUi (fun () ->
        use ws = new Workspace(files, trusted)
        use s = new Session(ws)
        body s)

/// Like `withWindow`, with a step that runs before the window is made (to put files in the data folder, say).
let withPreparedWindow
    (trusted: bool)
    (files: (string * string) list)
    (prepare: Workspace -> unit)
    (body: Session -> unit)
    =
    onUi (fun () ->
        use ws = new Workspace(files, trusted)
        prepare ws
        use s = new Session(ws)
        body s)

/// Types text into a control, as the keyboard's text input would.
let typeText (target: InputElement) (text: string) =
    let args =
        TextInputEventArgs(RoutedEvent = InputElement.TextInputEvent, Text = text)

    args.Source <- target
    target.RaiseEvent args

/// The text of a tree row's header.
let rowText (item: obj) =
    match item with
    | :? TreeViewItem as t ->
        match t.Header with
        | :? TextBlock as b -> b.Text
        | h -> string h
    | _ -> string item

/// The names the explorer shows at its top level.
let topLevelNames (tree: TreeView) = [ for i in tree.Items -> rowText i ]
