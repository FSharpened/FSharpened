module FSharpened.Ide.Tests.ProjectTests

open Xunit
open FSharpened.Ide

let private ok (r: Result<'a, string>) =
    match r with
    | Ok v -> v
    | Error e -> failwith ("expected Ok, got: " + e)

let private err (r: Result<'a, string>) =
    match r with
    | Ok _ -> failwith "expected an error"
    | Error e -> e

let private sample =
    Project.create "Demo"
    |> Project.addFile "src/engine.rs" "fn main() {}"
    |> ok
    |> Project.addFile "src/ui/board.js" "console.log(1)"
    |> ok
    |> Project.addFile "main.py" "print(1)"
    |> ok
    |> Project.addFile "README.md" "# demo"
    |> ok

[<Theory>]
[<InlineData("src/a.rs", "src/a.rs")>]
[<InlineData("./src//a.rs", "src/a.rs")>]
[<InlineData("src\\a.rs", "src/a.rs")>]
[<InlineData("  a.py  ", "a.py")>]
let ``paths are normalised to forward slashes without empty parts`` (raw: string, expected: string) =
    Assert.Equal(Ok expected, ProjectPath.normalize raw)

[<Theory>]
[<InlineData("")>]
[<InlineData("/etc/passwd")>]
[<InlineData("../escape.py")>]
[<InlineData("a/../b.py")>]
[<InlineData("C:\\temp\\a.py")>]
[<InlineData("a/b?.py")>]
[<InlineData("con.py")>]
[<InlineData("src/NUL")>]
[<InlineData("trailing.")>]
[<InlineData("a/b/c/d/e/f/g/h/i.py")>]
let ``paths that could escape or fail on another operating system are refused`` (raw: string) =
    Assert.True(Result.isError (ProjectPath.normalize raw), raw)

[<Fact>]
let ``a path longer than the limit is refused`` () =
    Assert.True(Result.isError (ProjectPath.normalize (System.String('a', 201) + ".py")))
    Assert.True(Result.isError (ProjectPath.normalize (System.String('a', 101) + ".py")))

[<Fact>]
let ``extensions map to the IDE's languages and text files have none`` () =
    let l (p: string) = ProjectPath.languageOf p
    Assert.Equal(Some Rust, l "src/engine.rs")
    Assert.Equal(Some Cpp, l "a.CC")
    Assert.Equal(Some R, l "stats.R")
    Assert.Equal(Some R, l "stats.r")
    Assert.Equal(Some FSharp, l "Program.fs")
    Assert.Equal(Some OCaml, l "x.ml")
    Assert.Equal(None, l "README.md")
    Assert.Equal(None, l "Makefile")
    Assert.Equal(None, l "data.json")

    // every language's own default file name maps back to that language
    for i in Language.infos do
        Assert.Equal(Some i.Lang, l i.File)

[<Fact>]
let ``adding keeps files sorted and refuses duplicates ignoring case`` () =
    Assert.Equal<string list>([ "main.py"; "README.md"; "src/engine.rs"; "src/ui/board.js" ], Project.paths sample)
    Assert.Contains("already exists", err (Project.addFile "MAIN.PY" "x" sample))

[<Fact>]
let ``a path cannot be both a file and a folder`` () =
    Assert.Contains("both a file and a folder", err (Project.addFile "src" "x" sample))
    Assert.Contains("both a file and a folder", err (Project.addFile "main.py/inner.py" "x" sample))

[<Fact>]
let ``limits on files, file size and total size hold for every operation`` () =
    let big = System.String('x', Project.maxFileChars + 1)
    Assert.Contains("larger than", err (Project.addFile "big.py" big sample))
    Assert.Contains("larger than", err (Project.setText "main.py" big sample))

    let mutable p = Project.create "many"

    for i in 1 .. Project.maxFiles do
        p <- p |> Project.addFile ("f" + string i + ".py") "" |> ok

    Assert.Contains("at most", err (Project.addFile "one-more.py" "" p))

    let mutable q = Project.create "heavy"
    let chunk = System.String('y', Project.maxFileChars)

    for i in 1..30 do
        q <- q |> Project.addFile ("h" + string i + ".py") chunk |> ok

    Assert.Contains("2 million", err (Project.addFile "h31.py" chunk q))

[<Fact>]
let ``setText replaces one file and nothing else`` () =
    let p = sample |> Project.setText "main.py" "print(2)" |> ok
    Assert.Equal("print(2)", (Project.tryFind "main.py" p).Value.Text)
    Assert.Equal("fn main() {}", (Project.tryFind "src/engine.rs" p).Value.Text)
    Assert.Contains("not in the project", err (Project.setText "nope.py" "" sample))

[<Fact>]
let ``removing a file or a folder drops it from the entry and the pipeline`` () =
    let p =
        sample
        |> Project.setEntry "main.py"
        |> ok
        |> Project.setPipeline [ "src/engine.rs"; "src/ui/board.js" ]
        |> ok

    let noFile = p |> Project.removeFile "main.py" |> ok
    Assert.Equal(None, noFile.Entry)
    Assert.Equal<string list>([ "src/engine.rs"; "src/ui/board.js" ], noFile.Pipeline)

    let noFolder = p |> Project.removeFolder "src" |> ok
    Assert.Equal<string list>([ "main.py"; "README.md" ], Project.paths noFolder)
    Assert.Empty noFolder.Pipeline
    Assert.Equal(Some "main.py", noFolder.Entry)
    Assert.Contains("not in the project", err (Project.removeFolder "ghost" p))

[<Fact>]
let ``renaming a file keeps the entry and the pipeline pointing at it`` () =
    let p =
        sample
        |> Project.setEntry "src/engine.rs"
        |> ok
        |> Project.setPipeline [ "src/engine.rs" ]
        |> ok

    let r = p |> Project.rename "src/engine.rs" "search.rs" |> ok
    Assert.Contains("src/search.rs", Project.paths r)
    Assert.DoesNotContain("src/engine.rs", Project.paths r)
    Assert.Equal(Some "src/search.rs", r.Entry)
    Assert.Equal<string list>([ "src/search.rs" ], r.Pipeline)

[<Fact>]
let ``moving a folder carries every file and every reference`` () =
    let p = sample |> Project.setPipeline [ "src/ui/board.js" ] |> ok
    let r = p |> Project.move "src" "lib/core" |> ok

    Assert.Equal<string list>(
        [ "lib/core/engine.rs"; "lib/core/ui/board.js"; "main.py"; "README.md" ]
        |> List.sortBy (fun s -> s.ToLowerInvariant()),
        Project.paths r
    )

    Assert.Equal<string list>([ "lib/core/ui/board.js" ], r.Pipeline)

[<Fact>]
let ``a move that would collide, escape or loop is refused and changes nothing`` () =
    Assert.Contains("same name", err (Project.move "main.py" "README.md" sample))
    Assert.True(Result.isError (Project.move "main.py" "../out.py" sample))
    Assert.Contains("into itself", err (Project.move "src" "src/ui/deeper" sample))
    Assert.Contains("not in the project", err (Project.move "ghost.py" "x.py" sample))

[<Fact>]
let ``a case-only rename is allowed`` () =
    let r = sample |> Project.rename "main.py" "Main.py" |> ok
    Assert.Contains("Main.py", Project.paths r)

[<Fact>]
let ``the entry must be a runnable source file`` () =
    Assert.Contains("not a source file", err (Project.setEntry "README.md" sample))
    Assert.Contains("not in the project", err (Project.setEntry "ghost.py" sample))
    Assert.Equal(Some "main.py", (sample |> Project.setEntry "MAIN.PY" |> ok).Entry)

[<Fact>]
let ``a pipeline lists existing source files, in order, up to the cap`` () =
    let p =
        sample
        |> Project.setPipeline [ "src/ui/board.js"; "main.py"; "src/engine.rs" ]
        |> ok

    Assert.Equal<string list>([ "src/ui/board.js"; "main.py"; "src/engine.rs" ], p.Pipeline)
    Assert.Contains("not in the project", err (Project.setPipeline [ "ghost.py" ] sample))
    Assert.Contains("not a source file", err (Project.setPipeline [ "README.md" ] sample))
    Assert.Contains("at most", err (Project.setPipeline (List.replicate 25 "main.py") sample))
    Assert.Empty(Project.issues p)

[<Fact>]
let ``the entry file falls back to a conventional name and then to the first source`` () =
    Assert.Equal(Some "main.py", Project.entryFile sample |> Option.map (fun f -> f.Path))
    let chosen = sample |> Project.setEntry "src/engine.rs" |> ok
    Assert.Equal(Some "src/engine.rs", Project.entryFile chosen |> Option.map (fun f -> f.Path))

    let onlyNested =
        Project.create "n"
        |> Project.addFile "a/z.go" ""
        |> ok
        |> Project.addFile "notes.txt" ""
        |> ok

    Assert.Equal(Some "a/z.go", Project.entryFile onlyNested |> Option.map (fun f -> f.Path))
    Assert.Equal(None, Project.entryFile (Project.create "empty"))

[<Fact>]
let ``the listing shows folders first, then files, each sorted ignoring case`` () =
    let rows =
        Project.listing sample
        |> List.map (fun r -> (if r.IsFolder then "d " else "f ") + string r.Depth + " " + r.Path)

    Assert.Equal<string list>(
        [ "d 0 src"
          "d 1 src/ui"
          "f 2 src/ui/board.js"
          "f 1 src/engine.rs"
          "f 0 main.py"
          "f 0 README.md" ],
        rows
    )

    let board =
        Project.listing sample |> List.find (fun r -> r.Path = "src/ui/board.js")

    Assert.Equal(Some JavaScript, board.Language)
    Assert.Equal("board.js", board.Name)

[<Fact>]
let ``the languages of a project come back in tab order`` () =
    Assert.Equal<Language list>(
        [ Python; JavaScript; Rust ]
        |> List.sortBy (fun l -> List.findIndex ((=) l) Language.all),
        Project.languages sample
    )

[<Fact>]
let ``buffers from before projects existed become one file per language, nothing lost`` () =
    let p =
        Project.ofBuffers "Mine" Python [ Python, "print('kept')"; Rust, "fn main() {}" ]

    Assert.Equal<string list>([ "main.py"; "main.rs" ], Project.paths p)
    Assert.Equal(Some "main.py", p.Entry)
    Assert.Equal("print('kept')", (Project.tryFind "main.py" p).Value.Text)

[<Fact>]
let ``an import keeps good files, skips bad ones with a reason, and ignores the manifest`` () =
    let r =
        Project.ofEntries
            "Imported"
            [ "main.py", "print(1)"
              "../evil.py", "x"
              "src\\lib.rs", "fn f() {}"
              "fsharpened.project.json", "{}"
              "CON.txt", "x"
              "main.py", "duplicate" ]
            None
            []

    Assert.Equal<string list>([ "main.py"; "src/lib.rs" ], Project.paths r.Project)
    Assert.Equal("print(1)", (Project.tryFind "main.py" r.Project).Value.Text)
    let skipped = r.Skipped |> List.map fst
    Assert.Contains("../evil.py", skipped)
    Assert.Contains("CON.txt", skipped)
    Assert.Contains("main.py", skipped)
    Assert.DoesNotContain("fsharpened.project.json", skipped)

[<Fact>]
let ``an archive wrapped in one top-level folder is unwrapped, a single loose file is not`` () =
    let wrapped =
        Project.ofEntries "w" [ "demo/main.py", "a"; "demo/src/x.rs", "b" ] None []

    Assert.Equal<string list>([ "main.py"; "src/x.rs" ], Project.paths wrapped.Project)

    let withManifest =
        Project.ofEntries "w" [ "demo/main.py", "a"; "demo/fsharpened.project.json", "{}" ] None []

    Assert.Equal<string list>([ "main.py" ], Project.paths withManifest.Project)

    let loose = Project.ofEntries "l" [ "main.py", "a" ] None []
    Assert.Equal<string list>([ "main.py" ], Project.paths loose.Project)

    let mixed = Project.ofEntries "m" [ "demo/main.py", "a"; "other/x.rs", "b" ] None []
    Assert.Equal<string list>([ "demo/main.py"; "other/x.rs" ], Project.paths mixed.Project)

[<Fact>]
let ``an import applies the manifest's entry and pipeline and reports what does not resolve`` () =
    let files = [ "a.py", "1"; "b.rs", "2"; "notes.txt", "3" ]
    let good = Project.ofEntries "g" files (Some "b.rs") [ "a.py"; "b.rs" ]
    Assert.Equal(Some "b.rs", good.Project.Entry)
    Assert.Equal<string list>([ "a.py"; "b.rs" ], good.Project.Pipeline)
    Assert.Empty good.Skipped

    let bad = Project.ofEntries "b" files (Some "ghost.py") [ "a.py"; "ghost.py" ]
    Assert.Equal(None, bad.Project.Entry)
    Assert.Empty bad.Project.Pipeline
    Assert.Contains(bad.Skipped, fun (path, why) -> path = ProjectPath.manifestName && why.Contains "ghost.py")

[<Fact>]
let ``an import stops at the file limit without failing the whole import`` () =
    let many = [ for i in 1 .. Project.maxFiles + 5 -> "f" + string i + ".py", "" ]
    let r = Project.ofEntries "big" many None []
    Assert.Equal(Project.maxFiles, r.Project.Files.Length)
    Assert.Equal(5, r.Skipped.Length)

[<Fact>]
let ``project names are cleaned`` () =
    Assert.Equal("Untitled", Project.cleanName "   ")
    Assert.Equal("ab", Project.cleanName "a/b\\")
    Assert.Equal(80, (Project.cleanName (System.String('n', 200))).Length)

// ---- whole-project history ----------------------------------------------------------------------------------------

let private state (p: Project) active : ProjectState =
    { Project = p
      Active = active
      Stdin = "" }

let private built () =
    Project.create "Chess"
    |> Project.addFile "main.py" "print(1)"
    |> ok
    |> Project.addFile "src/engine.rs" "fn main() {}"
    |> ok
    |> Project.setEntry "src/engine.rs"
    |> ok
    |> Project.setPipeline [ "main.py"; "src/engine.rs" ]
    |> ok

[<Fact>]
let ``a project state survives a write and a read unchanged`` () =
    let store =
        FSharpened.Platform.InMemoryOverlay() :> FSharpened.Platform.IOverlayStore

    let s =
        { (state (built ()) "main.py") with
            Stdin = "e2e4\n" }

    ProjectState.write store s
    Assert.Equal(s, ProjectState.read store)

[<Fact>]
let ``a project without an entry or a pipeline reads back without inventing them`` () =
    let store =
        FSharpened.Platform.InMemoryOverlay() :> FSharpened.Platform.IOverlayStore

    let s = state (Project.create "Empty") ""
    ProjectState.write store s
    let back = ProjectState.read store
    Assert.Equal(None, back.Project.Entry)
    Assert.Empty back.Project.Pipeline
    Assert.Empty back.Project.Files

[<Fact>]
let ``restoring a snapshot brings back removed files, edits and renames, and drops later additions`` () =
    let t0 = built ()
    let tl = ProjectTimeline(state t0 "main.py")
    let first = tl.HeadId

    // edit one file, rename another, add a third: all in a later statepoint
    let t1 =
        t0
        |> Project.setText "main.py" "print(2)"
        |> ok
        |> Project.move "src/engine.rs" "src/core.rs"
        |> ok
        |> Project.addFile "notes.md" "hi"
        |> ok

    let second = tl.Snapshot("later", state t1 "notes.md")
    Assert.Equal<string list>([ "main.py"; "notes.md"; "src/core.rs" ], Project.paths (tl.Read second).Value.Project)

    let back = (tl.Restore first).Value
    Assert.Equal<string list>([ "main.py"; "src/engine.rs" ], Project.paths back.Project)
    Assert.Equal("print(1)", (Project.tryFind "main.py" back.Project).Value.Text)
    Assert.Equal(Some "src/engine.rs", back.Project.Entry)
    Assert.Equal<string list>([ "main.py"; "src/engine.rs" ], back.Project.Pipeline)
    Assert.Equal("main.py", back.Active)

[<Fact>]
let ``a file deleted after a snapshot does not reappear in the next one`` () =
    let t0 = built ()
    let tl = ProjectTimeline(state t0 "main.py")

    let without =
        t0 |> Project.removeFile "main.py" |> ok |> Project.setPipeline [] |> ok

    let id = tl.Snapshot("deleted", state without "src/engine.rs")
    Assert.Equal<string list>([ "src/engine.rs" ], Project.paths (tl.Read id).Value.Project)

[<Fact>]
let ``a fork of a project statepoint is isolated from its source`` () =
    let tl = ProjectTimeline(state (built ()) "main.py")
    let start = tl.HeadId
    let edited = built () |> Project.setText "main.py" "changed" |> ok
    tl.Snapshot("edit", state edited "main.py") |> ignore
    let branch = (tl.Fork(start, "try")).Value
    Assert.Equal("print(1)", (Project.tryFind "main.py" (tl.Read branch).Value.Project).Value.Text)
    Assert.Contains(tl.Entries, fun e -> e.Id = branch && e.Branch = "try")

[<Fact>]
let ``an unchanged file is stored once across snapshots`` () =
    let store =
        FSharpened.Platform.InMemoryOverlay() :> FSharpened.Platform.IOverlayStore

    ProjectState.write store (state (built ()) "main.py")
    let before = (store.Get "file/main.py").Value
    let child = store.Fork()
    ProjectState.write child (state (built ()) "main.py")
    // the same bytes, found in the parent layer: nothing was written to the child for it
    Assert.True(obj.ReferenceEquals(before, (child.Get "file/main.py").Value))
