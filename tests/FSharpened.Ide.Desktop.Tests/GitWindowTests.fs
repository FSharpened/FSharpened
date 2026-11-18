/// Git in the real window (headless), on temporary repositories: the branch in the status bar, the diff gutter, stage
/// and commit through the Source Control view, switching branches, blame, and nothing at all in an untrusted folder.
/// Needs the `git` program: without it these tests do nothing.
module GitWindowTests

open System
open System.IO
open Avalonia.Automation
open Avalonia.Controls
open Xunit
open FSharpened.Ide.Desktop
open FSharpened.Ide.Host
open HeadlessHarness

let private click (b: Button) =
    b.RaiseEvent(Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent))

/// git on a pool thread. `core.fsmonitor=false` because Git for Windows may start a background daemon that keeps the
/// output pipe open, and `Proc.run` then gives up on the output after three seconds.
let private git (dir: string) (args: string list) =
    System.Threading.Tasks.Task
        .Run(fun () -> Proc.run ("git" :: "-c" :: "core.fsmonitor=false" :: args) dir None (TimeSpan.FromSeconds 30.0))
        .GetAwaiter()
        .GetResult()

let private hasGit =
    lazy
        (match Proc.run [ "git"; "--version" ] (Path.GetTempPath()) None (TimeSpan.FromSeconds 10.0) with
         | Ok r -> r.ExitCode = Some 0
         | _ -> false)

let private files = [ "a.txt", "one\ntwo\nthree\n"; "src/b.txt", "bee\n" ]

/// A window on a repository with everything committed on `main`; does nothing without git.
let private withRepoWindow (trusted: bool) (body: Session -> unit) =
    if hasGit.Value then
        onUi (fun () ->
            use ws = new Workspace(files, trusted)
            git ws.Root [ "init"; "-q"; "-b"; "main" ] |> ignore
            git ws.Root [ "config"; "user.email"; "t@example.invalid" ] |> ignore
            git ws.Root [ "config"; "user.name"; "Tess Tester" ] |> ignore
            git ws.Root [ "config"; "commit.gpgsign"; "false" ] |> ignore
            git ws.Root [ "add"; "." ] |> ignore
            git ws.Root [ "commit"; "-q"; "-m"; "first commit" ] |> ignore
            use s = new Session(ws)
            s.OpenFolder()
            body s)

let private statusGit (s: Session) =
    (find<TextBlock> s.Window "git-branch-and-changes").Text

/// The exit code of a git command: the answer that does not depend on reading its output.
let private exitOf (s: Session) (args: string list) =
    match git s.Workspace.Root args with
    | Ok r -> r.ExitCode
    | Error e -> failwith e

let private status (s: Session) =
    (find<TextBlock> s.Window "status-message").Text

let private editorOf (s: Session) =
    find<AvaloniaEdit.TextEditor> s.Window "code-editor"

let private log (s: Session) =
    match git s.Workspace.Root [ "log"; "--format=%s" ] with
    | Ok r when r.ExitCode = Some 0 -> r.Stdout
    | Ok r -> failwithf "git log exited with %A: %s %s" r.ExitCode r.Stdout r.Stderr
    | Error e -> failwith e

[<Fact>]
let ``the status bar shows the branch, and the number of changes once there are some`` () =
    withRepoWindow true (fun s ->
        until "the branch" 10000 (fun () -> (statusGit s).Contains "main")
        Assert.Equal("⎇ main", statusGit s)
        File.WriteAllText(s.Workspace.Path "a.txt", "changed\n")
        s.Shell.Git.Refresh()
        until "the change count" 10000 (fun () -> (statusGit s).Contains "● 1"))

[<Fact>]
let ``edited lines are marked in the gutter: added, modified and removed`` () =
    withRepoWindow true (fun s ->
        until "the branch" 10000 (fun () -> (statusGit s).Contains "main")
        s.Shell.OpenFile("a.txt", 1, 1)
        let doc = (editorOf s).Document
        // line 2 changed, a line added at the end, and line 1 removed
        doc.Text <- "TWO\nthree\nfour\n"
        s.Shell.Git.Refresh()

        until "the gutter marks" 10000 (fun () -> not s.Shell.Git.Marks.IsEmpty)
        let marks = s.Shell.Git.Marks |> Map.ofList
        Assert.Equal(Some ModifiedLine, marks.TryFind 1)
        Assert.Equal(Some AddedLine, marks.TryFind 3)

        // back to the committed text: no marks
        doc.Text <- "one\ntwo\nthree\n"
        s.Shell.Git.Refresh()
        until "the marks to go" 10000 (fun () -> s.Shell.Git.Marks.IsEmpty))

[<Fact>]
let ``a new file is all added and a file in no repository has no marks`` () =
    withRepoWindow true (fun s ->
        until "the branch" 10000 (fun () -> (statusGit s).Contains "main")
        File.WriteAllText(s.Workspace.Path "fresh.txt", "x\ny\n")
        s.Shell.Git.Refresh()
        until "the new file in the status" 10000 (fun () -> (statusGit s).Contains "● 1")
        s.Shell.OpenFile("fresh.txt", 1, 1)
        s.Shell.Git.Refresh()
        until "all lines added" 10000 (fun () -> s.Shell.Git.Marks.Length = 2)
        Assert.True(s.Shell.Git.Marks |> List.forall (fun (_, m) -> m = AddedLine)))

[<Fact>]
let ``staging in the Source Control view moves a file to the staged list and commit takes it`` () =
    withRepoWindow true (fun s ->
        until "the branch" 10000 (fun () -> (statusGit s).Contains "main")
        File.WriteAllText(s.Workspace.Path "a.txt", "edited\n")
        s.Shell.Run "git-view"
        s.Shell.Git.Refresh()
        // the file is listed as not staged, with a button to stage it
        until "the stage button" 10000 (fun () -> (tryFind<Button> s.Window "stage-a-txt").IsSome)
        click (find<Button> s.Window "stage-a-txt")
        until "the unstage button" 10000 (fun () -> (tryFind<Button> s.Window "unstage-a-txt").IsSome)
        Assert.True((find<Button> s.Window "commit").IsEnabled)

        // an empty message is refused
        s.Shell.Run "git-commit"
        until "the refusal" 10000 (fun () -> (status s).Contains "commit message")
        Assert.DoesNotContain("edited", log s)

        s.Shell.Git.SetCommitMessage "Edit a.txt from the IDE"
        click (find<Button> s.Window "commit")

        try
            until "the commit" 40000 (fun () -> (log s).Contains "Edit a.txt from the IDE")
        with e ->
            failwithf "%s | status: %s | log: %s" e.Message (status s) (log s)
        // nothing is left to commit: the lists are empty again
        until "the lists to empty" 30000 (fun () -> (tryFind<Button> s.Window "unstage-a-txt").IsNone)

        Assert.Equal(
            "",
            (find<TextBox> s.Window "commit-message").Text
            |> Option.ofObj
            |> Option.defaultValue ""
        )

        Assert.Equal("⎇ main", statusGit s))

[<Fact>]
let ``unstaging puts the file back, and the buttons of the view are named`` () =
    withRepoWindow true (fun s ->
        until "the branch" 10000 (fun () -> (statusGit s).Contains "main")
        File.WriteAllText(s.Workspace.Path "a.txt", "edited\n")
        File.WriteAllText(s.Workspace.Path "src/b.txt", "bee2\n")
        s.Shell.Run "git-view"
        s.Shell.Git.StagePaths [ "a.txt" ]
        until "a staged file" 10000 (fun () -> (tryFind<Button> s.Window "unstage-a-txt").IsSome)
        until "a file not staged" 10000 (fun () -> (tryFind<Button> s.Window "stage-src-b-txt").IsSome)

        // every control of the view has a name a screen reader can say
        let host = find<ContentControl> s.Window "sidebar"

        let bad =
            [ for c in descendants host do
                  match c with
                  | :? Button
                  | :? TextBox ->
                      if not (Access.isMeaningful (AutomationProperties.GetName c)) then
                          yield c.GetType().Name
                  | _ -> () ]

        Assert.Empty bad
        s.Shell.Git.UnstagePaths [ "a.txt" ]
        until "unstaged again" 10000 (fun () -> (tryFind<Button> s.Window "stage-a-txt").IsSome))

[<Fact>]
let ``branches are created and switched from the window`` () =
    withRepoWindow true (fun s ->
        until "the branch" 10000 (fun () -> (statusGit s).Contains "main")
        s.Shell.Git.CreateBranch "feature/gutter"
        until "the new branch" 10000 (fun () -> (statusGit s).Contains "feature/gutter")
        s.Shell.Git.SwitchTo "main"
        until "main again" 10000 (fun () -> (statusGit s) = "⎇ main")
        // a branch that does not exist is refused in words and nothing moves
        s.Shell.Git.SwitchTo "nope"
        until "the refusal" 10000 (fun () -> (status s).Contains "no branch named")
        s.Shell.Git.CreateBranch "bad name"
        until "the name refusal" 10000 (fun () -> (status s).Contains "not a valid branch name")
        Assert.Equal("⎇ main", statusGit s))

[<Fact>]
let ``blame shows who changed each line, and the editor's own edits as not committed yet`` () =
    withRepoWindow true (fun s ->
        until "the branch" 10000 (fun () -> (statusGit s).Contains "main")
        s.Shell.OpenFile("a.txt", 1, 1)
        s.Shell.Run "git-blame"
        Assert.True s.Shell.Git.BlameOn
        until "the blame" 10000 (fun () -> s.Shell.Git.BlameLabels.Length = 3)
        Assert.All(s.Shell.Git.BlameLabels, fun (_, l) -> Assert.StartsWith("Tess Tester", l))
        (editorOf s).Document.Insert(0, "brand new\n")
        s.Shell.Git.Refresh()

        until "the new line to read as not committed" 10000 (fun () ->
            s.Shell.Git.BlameLabels
            |> List.exists (fun (l, text) -> l = 1 && text = "Not committed yet"))

        s.Shell.Run "git-blame"
        Assert.False s.Shell.Git.BlameOn
        until "the blame to clear" 10000 (fun () -> s.Shell.Git.BlameLabels.IsEmpty))

[<Fact>]
let ``in an untrusted folder git is not run at all: no branch, no marks, and actions refuse`` () =
    withRepoWindow false (fun s ->
        pump 400
        Assert.Equal("", statusGit s)
        s.Shell.OpenFile("a.txt", 1, 1)
        (editorOf s).Document.Text <- "something else\n"
        s.Shell.Git.Refresh()
        pump 400
        Assert.Empty s.Shell.Git.Marks
        File.WriteAllText(s.Workspace.Path "a.txt", "edited on disk\n")
        s.Shell.Git.StagePaths [ "a.txt" ]
        // said at once, before a change-on-disk message can replace it
        Assert.Contains("not trusted", status s)
        pump 400
        // nothing was staged (git's exit codes say it: 0 when there is no difference, 1 when there is)
        Assert.Equal(Some 0, exitOf s [ "diff"; "--cached"; "--quiet" ])
        Assert.Equal(Some 1, exitOf s [ "diff"; "--quiet" ])
        s.Shell.Git.CreateBranch "x"
        Assert.Contains("not trusted", status s)
        pump 300
        // no branch was made
        Assert.Equal(Some 1, exitOf s [ "rev-parse"; "--verify"; "-q"; "refs/heads/x" ])

        // the view says why, and trusting the folder turns git on
        s.Shell.Run "git-view"
        s.Workspace.Env.Trust.Trust s.Workspace.Root
        s.Shell.Git.Refresh()
        until "the branch after trust" 10000 (fun () -> (statusGit s).Contains "main"))
