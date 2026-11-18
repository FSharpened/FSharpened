module GitTests

open System
open System.IO
open Xunit
open FSharpened.Ide.Host

// ---- the line diff behind the gutter ------------------------------------------------------------------------

let private marks (a: string) (b: string) = GitDiff.marks (Some a) b

[<Fact>]
let ``identical texts have no marks and a text without a committed version is all added`` () =
    Assert.Empty(marks "a\nb\nc\n" "a\nb\nc\n")
    Assert.Equal<(int * LineMark) list>([ 1, AddedLine; 2, AddedLine ], GitDiff.marks None "x\ny\n")
    Assert.Empty(GitDiff.marks None "")
    // line endings do not count as changes
    Assert.Empty(marks "a\r\nb\r\n" "a\nb\n")

[<Fact>]
let ``a changed line is modified, a new line is added and extra new lines beyond a replaced one are added`` () =
    Assert.Equal<(int * LineMark) list>([ 2, ModifiedLine ], marks "a\nb\nc\n" "a\nB\nc\n")
    Assert.Equal<(int * LineMark) list>([ 3, AddedLine ], marks "a\nb\nc\n" "a\nb\nNEW\nc\n")
    Assert.Equal<(int * LineMark) list>([ 4, AddedLine ], marks "a\nb\nc\n" "a\nb\nc\nlast\n")
    // one line replaced by three: the first reads as modified, the other two as added
    Assert.Equal<(int * LineMark) list>(
        [ 2, ModifiedLine; 3, AddedLine; 4, AddedLine ],
        marks "a\nb\nc\n" "a\nx\ny\nz\nc\n"
    )

[<Fact>]
let ``removed lines mark the line after the gap, and at the end the last line`` () =
    Assert.Equal<(int * LineMark) list>([ 2, RemovedAbove ], marks "a\nb\nc\n" "a\nc\n")
    Assert.Equal<(int * LineMark) list>([ 2, RemovedAbove ], marks "a\nb\nc\n" "a\nb\n")
    Assert.Equal<(int * LineMark) list>([ 1, RemovedAbove ], marks "a\nb\n" "")
    // three lines replaced by one: that line is modified, no removal mark on top of it
    Assert.Equal<(int * LineMark) list>([ 2, ModifiedLine ], marks "a\nb\nc\nd\ne\n" "a\nZ\ne\n")

[<Fact>]
let ``several separate changes are marked each in its place`` () =
    let a = "1\n2\n3\n4\n5\n6\n7\n8\n"
    let b = "1\nTWO\n3\n4\n5\n7\n8\nnine\n"

    Assert.Equal<(int * LineMark) list>([ 2, ModifiedLine; 6, RemovedAbove; 8, AddedLine ], marks a b)

[<Fact>]
let ``a huge change does not take forever: it is marked as one block`` () =
    let a = String.Join("\n", [ for i in 1..3000 -> "old " + string i ])
    let b = String.Join("\n", [ for i in 1..3000 -> "new " + string i ])
    let m = marks a b
    Assert.Equal(3000, m.Length)
    Assert.True(m |> List.forall (fun (_, k) -> k = ModifiedLine))

// ---- status entries and blame text ----------------------------------------------------------------------------

[<Fact>]
let ``status entries keep the staged and the unstaged column apart`` () =
    let out =
        "M  a.fs\000 M b.fs\000MM c.fs\000?? d.fs\000A  e.fs\000 D f.fs\000UU g.fs\000R  new.go\000old.go\000"

    let e = GitOps.parseEntries out |> List.map (fun x -> x.Path, x.Staged, x.Unstaged)

    Assert.Equal<(string * GitChange option * GitChange option) list>(
        [ "a.fs", Some Modified, None
          "b.fs", None, Some Modified
          "c.fs", Some Modified, Some Modified
          "d.fs", None, Some Untracked
          "e.fs", Some Added, None
          "f.fs", None, Some Deleted
          "g.fs", None, Some Conflicted
          "new.go", Some Renamed, None ],
        e
    )

[<Fact>]
let ``blame output is read line by line and the not-yet-committed lines are told apart`` () =
    let sha1 = String('a', 40)
    let zero = String('0', 40)

    let out =
        String.concat
            "\n"
            [ sprintf "%s 1 1 1" sha1
              "author Ada Lovelace"
              "author-mail <ada@example.invalid>"
              "author-time 1700000000"
              "author-tz +0000"
              "summary First line"
              "filename a.txt"
              "\tone"
              sprintf "%s 2 2 1" zero
              "author Not Committed Yet"
              "author-time 1700000100"
              "summary Version of a.txt from a.txt"
              "filename a.txt"
              "\ttwo"
              "" ]

    match GitOps.parseBlame out with
    | [ a; b ] ->
        Assert.Equal(1, a.Line)
        Assert.Equal("Ada Lovelace", a.Author)
        Assert.True a.Committed
        Assert.Equal("First line", a.Summary)
        Assert.Equal(2, b.Line)
        Assert.False b.Committed

        let now = DateTimeOffset.FromUnixTimeSeconds 1700000000L + TimeSpan.FromDays 3.0
        Assert.Equal("Ada Lovelace, 3 days ago", GitOps.blameLabel now a)
        Assert.Equal("Not committed yet", GitOps.blameLabel now b)
        Assert.Contains("First line", GitOps.blameDetail now a)
        Assert.StartsWith("aaaaaaaa", GitOps.blameDetail now a)
    | other -> failwithf "expected two lines, got %d" other.Length

[<Fact>]
let ``ages read as a person would say them`` () =
    let now = DateTimeOffset.FromUnixTimeSeconds 1_800_000_000L

    let ago (s: float) =
        GitOps.ago now (now - TimeSpan.FromSeconds s)

    Assert.Equal("just now", ago 5.0)
    Assert.Equal("1 minute ago", ago 90.0)
    Assert.Equal("5 hours ago", ago 18000.0)
    Assert.Equal("1 day ago", ago 90000.0)
    Assert.Equal("2 months ago", ago (86400.0 * 65.0))
    Assert.Equal("2 years ago", ago (86400.0 * 800.0))

// ---- against real repositories ------------------------------------------------------------------------------------

let private git (dir: string) (args: string list) =
    // `core.fsmonitor=false`: Git for Windows may start a daemon that keeps the pipe open and costs the output
    Proc.run ("git" :: "-c" :: "core.fsmonitor=false" :: args) dir None (TimeSpan.FromSeconds 30.0)

let private hasGit =
    lazy
        (match Proc.run [ "git"; "--version" ] (Path.GetTempPath()) None (TimeSpan.FromSeconds 10.0) with
         | Ok r -> r.ExitCode = Some 0
         | _ -> false)

/// A repository with one commit ("a.txt" = "one\ntwo\nthree\n") on branch `main`; does nothing without git.
let private withRepo (body: string -> unit) =
    if hasGit.Value then
        DiskTests.withFolder (fun dir ->
            git dir [ "init"; "-q"; "-b"; "main" ] |> ignore
            git dir [ "config"; "user.email"; "t@example.invalid" ] |> ignore
            git dir [ "config"; "user.name"; "Tess Tester" ] |> ignore
            git dir [ "config"; "commit.gpgsign"; "false" ] |> ignore
            File.WriteAllText(Path.Combine(dir, "a.txt"), "one\ntwo\nthree\n")
            git dir [ "add"; "." ] |> ignore
            git dir [ "commit"; "-q"; "-m"; "first commit" ] |> ignore
            body dir)

let private ok (r: Result<'a, string>) =
    match r with
    | Ok v -> v
    | Error e -> failwithf "expected success, got: %s" e

let private err (r: Result<'a, string>) =
    match r with
    | Error e -> e
    | Ok _ -> failwith "expected a refusal"

[<Fact>]
let ``staging and unstaging move a file between the two lists, with names that have spaces`` () =
    withRepo (fun dir ->
        File.WriteAllText(Path.Combine(dir, "a.txt"), "ONE\ntwo\nthree\n")
        File.WriteAllText(Path.Combine(dir, "new file.txt"), "n")

        let find p =
            ok (GitOps.entries dir) |> List.find (fun e -> e.Path = p)

        Assert.Equal(None, (find "a.txt").Staged)
        Assert.Equal(Some Untracked, (find "new file.txt").Unstaged)
        ok (GitOps.stage dir [ "a.txt"; "new file.txt" ])
        Assert.Equal(Some Modified, (find "a.txt").Staged)
        Assert.Equal(None, (find "a.txt").Unstaged)
        Assert.Equal(Some Added, (find "new file.txt").Staged)
        ok (GitOps.unstage dir [ "a.txt" ])
        Assert.Equal(None, (find "a.txt").Staged)
        Assert.Equal(Some Modified, (find "a.txt").Unstaged)
        // the work tree was not touched by unstaging
        Assert.Equal("ONE\ntwo\nthree\n", File.ReadAllText(Path.Combine(dir, "a.txt"))))

[<Fact>]
let ``a deleted file's removal can be staged`` () =
    withRepo (fun dir ->
        File.Delete(Path.Combine(dir, "a.txt"))
        ok (GitOps.stage dir [ "a.txt" ])
        let e = ok (GitOps.entries dir) |> List.find (fun e -> e.Path = "a.txt")
        Assert.Equal(Some Deleted, e.Staged))

[<Fact>]
let ``unstaging works in a repository with no commit yet`` () =
    if hasGit.Value then
        DiskTests.withFolder (fun dir ->
            git dir [ "init"; "-q"; "-b"; "main" ] |> ignore
            File.WriteAllText(Path.Combine(dir, "x.txt"), "x")
            ok (GitOps.stage dir [ "x.txt" ])
            Assert.Equal(Some Added, (ok (GitOps.entries dir)).Head.Staged)
            ok (GitOps.unstage dir [ "x.txt" ])
            Assert.Equal(Some Untracked, (ok (GitOps.entries dir)).Head.Unstaged))

[<Fact>]
let ``a commit takes what is staged, refuses an empty message or nothing staged, and keeps a dash-first message as text``
    ()
    =
    withRepo (fun dir ->
        Assert.Contains("message", err (GitOps.commit dir "   "))
        Assert.Contains("Nothing is staged", err (GitOps.commit dir "no changes"))
        File.WriteAllText(Path.Combine(dir, "a.txt"), "ONE\ntwo\nthree\n")
        File.WriteAllText(Path.Combine(dir, "other.txt"), "left unstaged")
        ok (GitOps.stage dir [ "a.txt" ])
        // a message that looks like an option is still only the message
        let said = ok (GitOps.commit dir "--amend this is not a flag; \"quotes\" & $(no)")
        Assert.Contains("main", said)

        let log =
            match git dir [ "log"; "--format=%s" ] with
            | Ok r -> r.Stdout
            | Error e -> failwith e

        Assert.StartsWith("--amend this is not a flag; \"quotes\" & $(no)", log)
        Assert.Contains("first commit", log)
        // the unstaged file is still there, still unstaged
        let e = ok (GitOps.entries dir)
        Assert.Equal(1, e.Length)
        Assert.Equal("other.txt", e.Head.Path)
        Assert.Equal(Some "ONE\ntwo\nthree\n", Git.head dir "a.txt"))

[<Fact>]
let ``branches are listed, created, switched to and refused by name`` () =
    withRepo (fun dir ->
        Assert.Equal<string list>([ "main" ], GitOps.branches dir)
        Assert.Equal("feature/x", ok (GitOps.createBranch dir "feature/x"))
        Assert.Equal(Some "feature/x", Git.branch dir)
        Assert.Equal<string list>([ "feature/x"; "main" ], GitOps.branches dir)
        ok (GitOps.switchTo dir "main")
        Assert.Equal(Some "main", Git.branch dir)
        // refused: bad names, a name that exists, a branch that does not exist
        for bad in [ ""; "-x"; "a b"; "a..b"; "x~1"; "--detach"; "a:b" ] do
            Assert.True(Result.isError (GitOps.createBranch dir bad), bad)

        Assert.Contains("already exists", err (GitOps.createBranch dir "feature/x"))
        Assert.Contains("no branch named", err (GitOps.switchTo dir "nope"))
        Assert.Contains("no branch named", err (GitOps.switchTo dir "--force"))
        Assert.Equal(Some "main", Git.branch dir))

[<Fact>]
let ``switching with a conflicting change in the work tree is refused with git's reason and changes nothing`` () =
    withRepo (fun dir ->
        ok (GitOps.createBranch dir "other") |> ignore
        File.WriteAllText(Path.Combine(dir, "a.txt"), "other\n")
        git dir [ "commit"; "-q"; "-am"; "on other" ] |> ignore
        ok (GitOps.switchTo dir "main")
        File.WriteAllText(Path.Combine(dir, "a.txt"), "uncommitted on main\n")
        let why = err (GitOps.switchTo dir "other")
        Assert.Contains("a.txt", why)
        Assert.Equal(Some "main", Git.branch dir)
        Assert.Equal("uncommitted on main\n", File.ReadAllText(Path.Combine(dir, "a.txt"))))

[<Fact>]
let ``blame names the author of each line and sees the editor's unsaved text`` () =
    withRepo (fun dir ->
        let editorText = "one\nTWO changed in the editor\nthree\nnew last line\n"

        match GitOps.blame dir "a.txt" editorText with
        | Error e -> failwith e
        | Ok lines ->
            Assert.Equal(4, lines.Length)
            Assert.Equal("Tess Tester", lines[0].Author)
            Assert.Equal("first commit", lines[0].Summary)
            Assert.True lines[0].Committed
            Assert.False lines[1].Committed
            Assert.True lines[2].Committed
            Assert.False lines[3].Committed

        // a file git does not know has no blame
        Assert.True(Result.isError (GitOps.blame dir "nope.txt" "x")))

[<Fact>]
let ``the diff against the last commit is what the editor's gutter shows`` () =
    withRepo (fun dir ->
        let committed = Git.head dir "a.txt"
        Assert.Equal(Some "one\ntwo\nthree\n", committed)

        Assert.Equal<(int * LineMark) list>(
            [ 2, ModifiedLine; 4, AddedLine ],
            GitDiff.marks committed "one\nTWO\nthree\nfour\n"
        ))

[<Fact>]
let ``git is read without optional locks and the repository's fsmonitor hook is not run`` () =
    withRepo (fun dir ->
        let marker = Path.Combine(dir, "hook-ran")
        // a hook that would leave a file behind if the repository's own configuration could start programs
        let hook = Path.Combine(dir, "hook.sh")
        File.WriteAllText(hook, "#!/bin/sh\ntouch hook-ran\n")
        git dir [ "config"; "core.fsmonitor"; hook.Replace('\\', '/') ] |> ignore
        Git.status dir |> ignore
        Git.branch dir |> ignore
        Assert.False(File.Exists marker))
