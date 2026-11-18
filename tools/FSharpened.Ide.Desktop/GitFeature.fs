/// Git in the editor: the diff gutter, blame, the current branch and switching it, and the Source Control view (stage,
/// unstage, commit). Everything runs the `git` program in the opened folder, and only when the folder is trusted.
namespace FSharpened.Ide.Desktop

open System
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open FSharpened.Ide
open FSharpened.Ide.Host

type GitFeature(ui: Ui, statusBar: StatusBar, editorTabs: EditorTabs) =
    let editor = ui.Editor
    let say = ui.Say
    let env = ui.Env
    let refreshGit = statusBar.RefreshGit

    // ---- state ------------------------------------------------------------------------------------
    let mutable marks: Map<int, LineMark> = Map.empty
    let mutable blameOn = false
    let mutable blameLines: Map<int, BlameLine> = Map.empty
    let mutable generation = 0
    /// The committed text of each file, read once per git refresh.
    let committed = Collections.Generic.Dictionary<string, string option>()

    let diffMargin = DiffMargin(fun () -> marks)

    let blameMargin =
        BlameMargin(
            (fun line ->
                match blameLines.TryFind line with
                | Some b -> GitOps.blameLabel DateTimeOffset.Now b
                | None -> ""),
            (fun line ->
                match blameLines.TryFind line with
                | Some b -> GitOps.blameDetail DateTimeOffset.Now b
                | None -> "")
        )

    let trusted () =
        match ui.Root with
        | Some r -> env.Trust.IsTrusted r
        | None -> false

    /// A git action needs a folder that is open and trusted; says what is missing otherwise.
    let allowed () : string option =
        match ui.Root with
        | None -> None
        | Some r when env.Trust.IsTrusted r -> Some r
        | Some _ ->
            say
                "Git is off in a folder you have not trusted: its configuration and hooks can run programs. Choose Run › Trust This Folder first"

            None

    // ---- the diff gutter and blame ----------------------------------------------------------------
    let relIsNew (rel: string) =
        match ui.GitChanges.TryFind rel with
        | Some Untracked
        | Some Added -> true
        | _ -> false

    /// Works out the gutter marks (and blame) of a file from its committed text and the editor's text.
    let updateGutter (d: OpenDoc) =
        match ui.Root with
        | Some r when trusted () && ui.GitBranch.IsSome ->
            generation <- generation + 1
            let mine = generation
            let rel = d.Rel
            let text = d.Text
            let isNew = relIsNew rel
            let wantBlame = blameOn

            task {
                let! result =
                    Task.Run(fun () ->
                        let basis =
                            if isNew then
                                None
                            else
                                lock committed (fun () ->
                                    match committed.TryGetValue rel with
                                    | true, c -> c
                                    | _ ->
                                        let c = Git.head r rel
                                        committed[rel] <- c
                                        c)

                        let m = GitDiff.marks basis text

                        let b =
                            if not wantBlame then
                                []
                            elif isNew || basis.IsNone then
                                // not in any commit: every line is "not committed yet"
                                [ for i in 1 .. (GitDiff.lines text).Length ->
                                      { Line = i
                                        Commit = ""
                                        Author = ""
                                        Time = None
                                        Summary = ""
                                        Committed = false } ]
                            else
                                match GitOps.blame r rel text with
                                | Ok l -> l
                                | Error _ -> []

                        m, b)

                if mine = generation && ui.Active = Some d then
                    marks <- result |> fst |> Map.ofList
                    blameLines <- result |> snd |> List.map (fun b -> b.Line, b) |> Map.ofList
                    diffMargin.InvalidateVisual()
                    blameMargin.InvalidateVisual()
            }
            |> ignore
        | _ ->
            marks <- Map.empty
            blameLines <- Map.empty
            diffMargin.InvalidateVisual()
            blameMargin.InvalidateVisual()

    let setBlame (on: bool) =
        blameOn <- on
        blameMargin.IsVisible <- on

        if not on then
            blameLines <- Map.empty

        match ui.Active with
        | Some d -> updateGutter d
        | None -> ()

        say (if on then "Blame is on" else "Blame is off")

    // ---- actions ----------------------------------------------------------------------------------
    /// Runs a git operation off the UI thread, reports its outcome and reads git's state again.
    let act (what: string) (work: string -> Result<string, string>) =
        match allowed () with
        | None -> ()
        | Some r ->
            task {
                let! res = Task.Run(fun () -> work r)

                match res with
                | Ok m -> say (if m = "" then what else m)
                | Error e -> say (sprintf "Git: %s" (e.Split('\n')[0]))

                refreshGit ()
            }
            |> ignore

    let stage (paths: string list) =
        act "Staged" (fun r ->
            GitOps.stage r paths
            |> Result.map (fun () -> sprintf "Staged %d file(s)" paths.Length))

    let unstage (paths: string list) =
        act "Unstaged" (fun r ->
            GitOps.unstage r paths
            |> Result.map (fun () -> sprintf "Unstaged %d file(s)" paths.Length))

    let switchTo (name: string) =
        act "Switched" (fun r -> GitOps.switchTo r name |> Result.map (fun () -> sprintf "Switched to %s" name))

    let createBranch (name: string) =
        act "Created" (fun r ->
            GitOps.createBranch r name
            |> Result.map (fun n -> sprintf "Created and switched to %s" n))

    /// The commit message box of the Source Control view.
    let message = ui.TextBox "Commit message"

    let commit () =
        let text = message.Text |> Option.ofObj |> Option.defaultValue ""

        match allowed () with
        | None -> ()
        | Some r ->
            task {
                let! res = Task.Run(fun () -> GitOps.commit r text)

                match res with
                | Ok first ->
                    message.Text <- ""
                    say ("Committed: " + first)
                | Error e -> say ("Git: " + e.Split('\n')[0])

                refreshGit ()
            }
            |> ignore

    let askNewBranch () =
        match allowed () with
        | None -> ()
        | Some r ->
            task {
                let! name =
                    Dialogs.ask
                        ui.Window
                        ui.Roles
                        "New branch"
                        "Name of the new branch (it starts from the current commit):"
                        ""
                        (fun n ->
                            if n.Trim() = "" then
                                ""
                            else
                                match GitOps.checkBranchName r n with
                                | Ok _ -> ""
                                | Error m -> m)

                match name with
                | Some n when n.Trim() <> "" -> createBranch n
                | _ -> ()
            }
            |> ignore

    /// The menu of local branches under the anchor: pick one to switch, or create a new one.
    let showBranchMenu (anchor: Control) =
        match allowed () with
        | None -> ()
        | Some r ->
            task {
                let! list = Task.Run(fun () -> GitOps.branches r)
                let menu = ContextMenu(Placement = Avalonia.Controls.PlacementMode.Top)
                Access.describe "Branches" "Choose a branch to switch to." menu |> ignore

                for b in list do
                    let current = ui.GitBranch = Some b

                    let item =
                        MenuItem(Header = (if current then "✓ " + b else "   " + b), IsEnabled = not current)

                    Access.nameOnly (if current then b + ", current branch" else "Switch to " + b) "" item
                    |> ignore

                    item.Click.Add(fun _ -> switchTo b)
                    menu.Items.Add item |> ignore

                if not list.IsEmpty then
                    menu.Items.Add(Separator()) |> ignore

                let create = MenuItem(Header = "Create new branch…")
                Access.nameOnly "Create new branch" "" create |> ignore
                create.Click.Add(fun _ -> askNewBranch ())
                menu.Items.Add create |> ignore
                menu.Open anchor
            }
            |> ignore

    // ---- the Source Control view ------------------------------------------------------------------
    let viewHost = StackPanel(Spacing = 6.0, Margin = Thickness(10.0, 0.0))
    let listHost = StackPanel(Spacing = 2.0)
    let summary = ui.Muted ""

    let commitButton =
        Button(Content = "Commit", HorizontalAlignment = HorizontalAlignment.Stretch)

    let statusLetter (c: GitChange) =
        match c with
        | Modified -> "M"
        | Added -> "A"
        | Deleted -> "D"
        | Renamed -> "R"
        | Untracked -> "U"
        | Conflicted -> "!"

    let row (e: GitEntry) (change: GitChange) (staged: bool) =
        let grid =
            Grid(ColumnDefinitions = ColumnDefinitions("Auto,*,Auto"), Margin = Thickness(0.0, 1.0))

        let letter =
            TextBlock(
                Text = statusLetter change,
                Width = 16.0,
                Foreground = ui.Roles.Accent,
                VerticalAlignment = VerticalAlignment.Center
            )

        let name =
            Button(
                Content = e.Path,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = Thickness 0.0,
                Padding = Thickness(4.0, 2.0),
                Foreground = ui.Roles.Fg
            )

        Access.describe (sprintf "Open %s" e.Path) "Opens the file in the editor." name
        |> ignore

        name.Click.Add(fun _ -> ui.OpenFileFn e.Path 0 0)

        let toggle =
            ui.Flat
                (if staged then "−" else "+")
                (if staged then
                     sprintf "Unstage %s" e.Path
                 else
                     sprintf "Stage %s" e.Path)
                (fun () -> if staged then unstage [ e.Path ] else stage [ e.Path ])

        Grid.SetColumn(name, 1)
        Grid.SetColumn(toggle, 2)
        grid.Children.Add letter
        grid.Children.Add name
        grid.Children.Add toggle
        grid :> Control

    let section (title: string) (rows: Control list) (bulkLabel: string) (bulkTip: string) (bulk: unit -> unit) =
        let header = DockPanel(Margin = Thickness(0.0, 8.0, 0.0, 2.0))
        let all = ui.Flat bulkLabel bulkTip bulk
        DockPanel.SetDock(all, Dock.Right)
        header.Children.Add all

        header.Children.Add(
            TextBlock(
                Text = title,
                FontSize = 11.0,
                FontWeight = FontWeight.SemiBold,
                Foreground = ui.Roles.Muted,
                VerticalAlignment = VerticalAlignment.Center
            )
        )

        listHost.Children.Add header

        for r in rows do
            listHost.Children.Add r

    let rebuildView () =
        listHost.Children.Clear()

        let staged =
            ui.GitEntries |> List.choose (fun e -> e.Staged |> Option.map (fun c -> e, c))

        let unstaged =
            ui.GitEntries |> List.choose (fun e -> e.Unstaged |> Option.map (fun c -> e, c))

        match ui.Root with
        | None -> summary.Text <- "Open a folder to see its changes."
        | Some _ when not (trusted ()) ->
            summary.Text <-
                "Git is off in this folder because it is not trusted: a repository's own configuration and hooks can run programs. Choose Run › Trust This Folder to turn it on."
        | Some _ when ui.GitBranch.IsNone -> summary.Text <- "This folder is not a git repository."
        | Some _ ->
            summary.Text <-
                sprintf
                    "On %s · %s"
                    ui.GitBranch.Value
                    (if staged.IsEmpty && unstaged.IsEmpty then
                         "no changes"
                     else
                         sprintf "%d staged, %d not staged" staged.Length unstaged.Length)

            if not staged.IsEmpty then
                section
                    (sprintf "STAGED CHANGES (%d)" staged.Length)
                    [ for (e, c) in staged -> row e c true ]
                    "−"
                    "Unstage all changes"
                    (fun () -> unstage [ for (e, _) in staged -> e.Path ])

            if not unstaged.IsEmpty then
                section
                    (sprintf "CHANGES (%d)" unstaged.Length)
                    [ for (e, c) in unstaged -> row e c false ]
                    "+"
                    "Stage all changes"
                    (fun () -> stage [ for (e, _) in unstaged -> e.Path ])

        commitButton.IsEnabled <- trusted () && ui.GitEntries |> List.exists (fun e -> e.Staged.IsSome)

    let buildView () : Control =
        let box = message
        box.AcceptsReturn <- true
        box.TextWrapping <- TextWrapping.Wrap
        box.MinHeight <- 64.0

        box.KeyDown.Add(fun e ->
            if e.Key = Key.Enter && e.KeyModifiers.HasFlag KeyModifiers.Control then
                e.Handled <- true
                commit ())

        Access.describe
            "Commit"
            "Commits the staged changes with the message above. Shortcut Ctrl+Enter in the message box."
            commitButton
        |> ignore

        ui.Themed(fun r ->
            commitButton.Background <- r.Brand
            commitButton.Foreground <- r.BrandFg)

        commitButton.Click.Add(fun _ -> commit ())
        viewHost.Children.Clear()
        viewHost.Children.Add summary
        viewHost.Children.Add box
        viewHost.Children.Add commitButton
        viewHost.Children.Add listHost
        rebuildView ()
        ScrollViewer(Content = viewHost) :> Control

    let view = lazy (buildView ())

    // ---- trust ------------------------------------------------------------------------------------
    let trustFolder () =
        match ui.Root with
        | None -> say "Open a folder first"
        | Some r when env.Trust.IsTrusted r -> say "This folder is already trusted"
        | Some r ->
            task {
                let! yes =
                    Dialogs.confirm
                        ui.Window
                        ui.Roles
                        "Trust this folder?"
                        (sprintf
                            "Trusting lets the IDE run programs in this folder: git (whose configuration and hooks come from the repository), language servers, formatters and your code:\n\n%s\n\nOnly trust folders whose code you would run anyway. You can revoke this from the command palette."
                            r)
                        "Trust"

                if yes then
                    env.Trust.Trust r
                    say "Folder is trusted"
                    refreshGit ()

                    match ui.Active with
                    | Some d -> editorTabs.Activate(Some d)
                    | None -> ()
            }
            |> ignore

    // ---- wiring -----------------------------------------------------------------------------------
    do
        blameMargin.IsVisible <- false
        editor.TextArea.LeftMargins.Insert(0, blameMargin)
        editor.TextArea.LeftMargins.Add diffMargin

        ui.Themed(fun r ->
            diffMargin.AddedBrush <- r.Ok
            diffMargin.ModifiedBrush <- r.Accent
            diffMargin.RemovedBrush <- r.Bad
            blameMargin.Ink <- r.Muted
            diffMargin.InvalidateVisual())

        ui.ExtraViews["git"] <- ("SOURCE CONTROL", (fun () -> view.Value))

        ui.OnDocShown.Add updateGutter
        ui.OnSettled.Add updateGutter

        ui.OnGitChanged.Add(fun () ->
            lock committed (fun () -> committed.Clear())

            match ui.Active with
            | Some d -> updateGutter d
            | None ->
                marks <- Map.empty
                diffMargin.InvalidateVisual()

            if view.IsValueCreated then
                rebuildView ())

        // a click on the branch in the status bar chooses another
        ui.StatusGit.PointerPressed.Add(fun _ -> showBranchMenu ui.StatusGit)
        ui.StatusGit.Cursor <- new Cursor(StandardCursorType.Hand)

    member _.StageFile =
        fun () ->
            match ui.Active with
            | Some d -> stage [ d.Rel ]
            | None -> say "Open a file first"

    member _.UnstageFile =
        fun () ->
            match ui.Active with
            | Some d -> unstage [ d.Rel ]
            | None -> say "Open a file first"

    member _.Commit = commit
    member _.ToggleBlame = fun () -> setBlame (not blameOn)
    member _.SwitchBranch = fun () -> showBranchMenu ui.StatusGit
    member _.NewBranch = askNewBranch
    member _.Refresh = refreshGit
    member _.TrustFolder = trustFolder

    // for the tests and the screenshot switches
    member _.Marks = marks |> Map.toList

    member _.BlameLabels =
        blameLines
        |> Map.map (fun _ b -> GitOps.blameLabel DateTimeOffset.Now b)
        |> Map.toList

    member _.BlameOn = blameOn
    member _.SetCommitMessage(text: string) = message.Text <- text
    member _.SwitchTo = switchTo
    member _.CreateBranch = createBranch
    member _.StagePaths = stage
    member _.UnstagePaths = unstage
