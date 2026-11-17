module PackTests

open System
open System.IO
open Xunit
open FSharpened.Ide.Host

let repoRoot =
    let rec up (d: DirectoryInfo) =
        if isNull d then
            failwith "repository root not found"
        elif File.Exists(Path.Combine(d.FullName, "FSharpened.slnx")) then
            d.FullName
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let shipped () =
    Languages.loadDirectory (Path.Combine(repoRoot, "languages"))

let minimal extra =
    sprintf
        """{ "format": "fsharpened.language/1", "id": "demo", "name": "Demo", "extensions": [".demo"],
             "tools": [ { "id": "demo", "candidates": [["demo"]], "version": ["--version"] } ] %s }"""
        extra

let refused (json: string) =
    match LanguagePack.parse "test" json with
    | Error m -> m
    | Ok _ -> failwith "expected the pack to be refused"

[<Fact>]
let ``every shipped pack is valid, with a unique id and no extension claimed twice`` () =
    let packs, problems = shipped ()
    Assert.Empty problems
    Assert.True(packs.Length >= 30, sprintf "only %d packs" packs.Length)
    Assert.Equal(packs.Length, (packs |> List.map (fun p -> p.Id) |> List.distinct).Length)

    let claims =
        packs |> List.collect (fun p -> p.Extensions |> List.map (fun e -> e, p.Id))

    let dup = claims |> List.groupBy fst |> List.filter (fun (_, l) -> l.Length > 1)
    Assert.True(dup.IsEmpty, sprintf "extensions claimed twice: %A" dup)

[<Fact>]
let ``the shipped set covers the website's eighteen languages and Go with a way to run it`` () =
    let reg = Languages.merge [ shipped () ]

    for id in
        [ "fsharp"
          "csharp"
          "vb"
          "javascript"
          "typescript"
          "python"
          "rust"
          "dart"
          "c"
          "cpp"
          "go"
          "java"
          "ruby"
          "lua"
          "php"
          "r"
          "haskell"
          "ocaml" ] do
        let p = Languages.byId reg id
        Assert.True(p.IsSome, id)
        Assert.False(p.Value.Run.IsEmpty, id + " has no run plan")
        Assert.NotEqual<string>("", p.Value.Scope)

    let go = (Languages.byId reg "go").Value
    Assert.Equal<string list>([ ".go" ], go.Extensions)
    Assert.Equal("gopls", go.Server.Value.Command)
    Assert.True(go.UseTabs)

[<Fact>]
let ``a file finds its language by exact name, then by the longest extension`` () =
    let reg = Languages.merge [ shipped () ]

    let id (p: string) =
        Languages.forFile reg p |> Option.map (fun x -> x.Id)

    Assert.Equal(Some "go", id "cmd/server/main.go")
    Assert.Equal(Some "dockerfile", id "build/Dockerfile")
    Assert.Equal(Some "makefile", id "Makefile")
    Assert.Equal(Some "toml", id "Cargo.lock")
    Assert.Equal(Some "typescript", id "src\\app.test.ts")
    Assert.Equal(Some "fsharp", id "Program.FS")
    Assert.Equal(None, id "photo.png")
    Assert.Equal(None, id ".go") // an extension alone is not a file of that language

[<Fact>]
let ``adding a language is adding a file: a user pack replaces a shipped one and a bad one is only reported`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fsh-packs-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    File.WriteAllText(
        Path.Combine(dir, "nim.json"),
        """{ "format": "fsharpened.language/1", "id": "nim", "name": "Nim", "extensions": [".nim"], "scope": "source.nim",
        "tools": [ { "id": "nim", "candidates": [["nim"]], "version": ["--version"] } ],
        "run": [ { "steps": [ { "phase": "build", "tool": "nim", "args": ["c", "-o:{out}", "{entry}"] }, { "phase": "run", "tool": "built" } ] } ] }"""
    )

    File.WriteAllText(
        Path.Combine(dir, "go.json"),
        """{ "format": "fsharpened.language/1", "id": "go", "name": "Go (mine)", "extensions": [".go"] }"""
    )

    File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json")
    File.WriteAllText(Path.Combine(dir, "_notes.json"), "ignored: starts with an underscore")

    try
        let reg = Languages.merge [ shipped (); Languages.loadDirectory dir ]
        Assert.Equal("Go (mine)", (Languages.byId reg "go").Value.Name)
        Assert.Equal(Some "nim", Languages.forFile reg "x.nim" |> Option.map (fun p -> p.Id))
        Assert.Single reg.Problems |> ignore
        Assert.Contains("broken.json", reg.Problems.Head.Origin)
    finally
        Directory.Delete(dir, true)

[<Theory>]
[<InlineData("""{ "format": "other/1", "id": "x", "name": "X", "extensions": [".x"] }""", "format")>]
[<InlineData("""{ "format": "fsharpened.language/1", "id": "X Y", "name": "X", "extensions": [".x"] }""", "id")>]
[<InlineData("""{ "format": "fsharpened.language/1", "id": "x", "name": "X", "extensions": ["x"] }""", "extension")>]
[<InlineData("""{ "format": "fsharpened.language/1", "id": "x", "name": "X" }""", "extension")>]
[<InlineData("""{ "format": "fsharpened.language/1", "id": "x", "name": "X", "extensions": [".x"], "grammar": "../../etc/passwd" }""",
             "grammar")>]
[<InlineData("""{ "format": "fsharpened.language/1", "id": "x", "name": "X", "extensions": [".x"], "server": { "command": "C:/tools/ls.exe" } }""",
             "bare program name")>]
[<InlineData("""[1,2,3]""", "object")>]
let ``a pack that is malformed or reaches outside itself is refused with a reason`` (json: string, word: string) =
    Assert.Contains(word, refused json)

[<Fact>]
let ``steps may only use declared tools, known placeholders and a built output that exists`` () =
    let step tool args =
        sprintf """, "run": [ { "steps": [ { "phase": "run", "tool": "%s", "args": [%s] } ] } ]""" tool args

    Assert.Contains("does not declare", refused (minimal (step "powershell" "")))
    Assert.Contains("{secret}", refused (minimal (step "demo" "\"{secret}\"")))
    Assert.Contains("built", refused (minimal (step "built" "")))

    Assert.Contains(
        "bare program name",
        refused (
            """{ "format": "fsharpened.language/1", "id": "x", "name": "X", "extensions": [".x"], "tools": [ { "id": "t", "candidates": [["..\\evil"]], "version": [] } ] }"""
        )
    )

    Assert.True(
        (LanguagePack.parse
            "t"
            (minimal (step "demo" "\"{entry}\", \"{flags}\", \"{out}\", \"{dir}\", \"{root}\", \"{name}\"")))
            .IsOk
    )

[<Fact>]
let ``plans are chosen by the files in the folder and by the entry's extension`` () =
    let reg = Languages.merge [ shipped () ]
    let fsharp = (Languages.byId reg "fsharp").Value

    let dir =
        Path.Combine(Path.GetTempPath(), "fsh-plan-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    try
        let pick entry =
            Plans.choose dir entry fsharp.Run
            |> Option.map (fun p -> p.Steps |> List.map (fun s -> s.Args))

        Assert.Equal(Some [ [ "fsi"; "{entry}" ] ], pick "script.fsx")
        Assert.Equal(None, pick "Program.fs") // no project file: nothing honest to run
        File.WriteAllText(Path.Combine(dir, "App.fsproj"), "<Project/>")
        Assert.Equal(Some [ [ "run" ] ], pick "Program.fs")
        Assert.Equal(Some [ [ "fsi"; "{entry}" ] ], pick "script.fsx") // a script still runs as a script
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``arguments expand placeholders and flags, and flags are checked`` () =
    let ctx: PlanContext =
        { Root = "/work"
          Entry = "src/main.rs"
          OutDir = "/tmp/out"
          Flags = [ "-O"; "--edition=2024" ]
          ExeSuffix = ".exe" }

    let args = Plans.expandArgs ctx [ "{entry}"; "-o"; "{out}"; "{flags}"; "{name}" ]

    Assert.Equal<string list>(
        [ "src/main.rs"
          "-o"
          Path.Combine("/tmp/out", "fsh-out.exe")
          "-O"
          "--edition=2024"
          "main" ],
        args
    )

    Assert.Equal<string list>([ "-O2"; "-std=c++20" ], Plans.parseFlags " -O2   -std=c++20 " |> Result.defaultValue [])
    Assert.True((Plans.parseFlags "-O2; rm -rf /").IsError)
    Assert.True((Plans.parseFlags "$(whoami)").IsError)

[<Fact>]
let ``trust is per folder, remembered across restarts, and a damaged file trusts nothing`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fsh-trust-" + Guid.NewGuid().ToString "N")

    let file = Path.Combine(dir, "trust.json")
    let folder = Path.Combine(dir, "project")
    Directory.CreateDirectory folder |> ignore

    try
        let store = TrustStore file
        Assert.False(store.IsTrusted folder)
        store.Trust folder
        Assert.True(store.IsTrusted folder)
        Assert.True(store.IsTrusted(folder + Path.DirectorySeparatorChar.ToString())) // a trailing separator is the same folder
        Assert.False(store.IsTrusted(Path.Combine(dir, "other")))
        Assert.True((TrustStore file).IsTrusted folder)
        store.Revoke folder
        Assert.False((TrustStore file).IsTrusted folder)
        File.WriteAllText(file, "{ damaged")
        Assert.Empty (TrustStore file).All
    finally
        Directory.Delete(dir, true)
