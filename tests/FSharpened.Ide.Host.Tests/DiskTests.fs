module DiskTests

open System
open System.IO
open Xunit
open FSharpened.Ide.Host

let withFolder (f: string -> unit) =
    let dir =
        Path.Combine(Path.GetTempPath(), "fsh-disk-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

[<Theory>]
[<InlineData("../x")>]
[<InlineData("a/../b")>]
[<InlineData("/etc/passwd")>]
[<InlineData("C:/Windows")>]
[<InlineData("a\\b")>]
[<InlineData("con.txt")>]
[<InlineData("NUL")>]
[<InlineData("a/b ")>]
[<InlineData("a//b")>]
[<InlineData("a|b")>]
let ``a path that could leave the folder or fail on another system is refused`` (rel: string) =
    withFolder (fun root -> Assert.True((DiskWorkspace.resolve root rel).IsError, rel))

[<Fact>]
let ``a path inside the folder resolves, including one that does not exist yet`` () =
    withFolder (fun root ->
        let p = DiskWorkspace.resolve root "src/new/File.fs" |> Result.defaultValue ""
        Assert.StartsWith(Path.GetFullPath root, p)
        Assert.True((DiskWorkspace.resolve root "").IsOk))

[<Fact>]
let ``a link inside the folder is never followed`` () =
    withFolder (fun root ->
        withFolder (fun outside ->
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret")
            let link = Path.Combine(root, "link")

            let made =
                try
                    Directory.CreateSymbolicLink(link, outside) |> ignore
                    true
                with _ ->
                    false // creating one needs privileges on some systems

            if made then
                Assert.True((DiskWorkspace.readText root "link/secret.txt").IsError)
                Assert.True((DiskWorkspace.writeText root "link/new.txt" "x").IsError)
                Assert.False(File.Exists(Path.Combine(outside, "new.txt")))

                let names =
                    DiskWorkspace.list root ""
                    |> Result.defaultValue []
                    |> List.map (fun e -> e.Name)

                Assert.DoesNotContain("link", names)))

[<Fact>]
let ``text is written whole, read back, and a byte-order mark is dropped`` () =
    withFolder (fun root ->
        Assert.True((DiskWorkspace.writeText root "a/b/hello.go" "package main\r\n").IsOk)
        Assert.Equal(Ok "package main\r\n", DiskWorkspace.readText root "a/b/hello.go")
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "a", "b"), "*.fsh-tmp"))
        File.WriteAllBytes(Path.Combine(root, "bom.txt"), [| 0xEFuy; 0xBBuy; 0xBFuy; byte 'h'; byte 'i' |])
        Assert.Equal(Ok "hi", DiskWorkspace.readText root "bom.txt"))

[<Fact>]
let ``binary, non-UTF-8 and oversized files are refused with a reason`` () =
    withFolder (fun root ->
        File.WriteAllBytes(Path.Combine(root, "b.bin"), [| 1uy; 0uy; 2uy |])
        File.WriteAllBytes(Path.Combine(root, "latin.txt"), [| 0xE9uy; 0x41uy |])
        Assert.Equal(Error "binary file", DiskWorkspace.readText root "b.bin")
        Assert.Equal(Error "not UTF-8 text", DiskWorkspace.readText root "latin.txt")
        Assert.True((DiskWorkspace.readText root "missing.txt").IsError))

[<Fact>]
let ``listing puts folders first, hides dependency folders, and quick open walks the rest`` () =
    withFolder (fun root ->
        for d in [ "src"; ".git"; "node_modules"; "target"; "bin"; "docs" ] do
            Directory.CreateDirectory(Path.Combine(root, d)) |> ignore

        File.WriteAllText(Path.Combine(root, "zeta.txt"), "z")
        File.WriteAllText(Path.Combine(root, "Alpha.txt"), "a")
        File.WriteAllText(Path.Combine(root, "src", "main.rs"), "fn main() {}")
        File.WriteAllText(Path.Combine(root, "node_modules", "x.js"), "x")

        let names =
            DiskWorkspace.list root ""
            |> Result.defaultValue []
            |> List.map (fun e -> e.Name)

        Assert.Equal<string list>([ "docs"; "src"; "Alpha.txt"; "zeta.txt" ], names)
        let all, cut = DiskWorkspace.allFiles root 100
        Assert.False cut
        Assert.Equal<string list>([ "Alpha.txt"; "zeta.txt"; "src/main.rs" ], all |> List.map (fun e -> e.Rel))
        let some, cut2 = DiskWorkspace.allFiles root 1
        Assert.True cut2
        Assert.Single some |> ignore)

[<Fact>]
let ``files and folders can be created, moved and deleted, and nothing is overwritten by accident`` () =
    withFolder (fun root ->
        Assert.True((DiskWorkspace.createFile root "notes/todo.md").IsOk)
        Assert.True((DiskWorkspace.createFile root "notes/todo.md").IsError)
        Assert.True((DiskWorkspace.createFolder root "empty/deeper").IsOk)
        DiskWorkspace.writeText root "notes/a.txt" "A" |> ignore
        Assert.True((DiskWorkspace.move root "notes/a.txt" "notes/todo.md").IsError)
        Assert.True((DiskWorkspace.move root "notes/a.txt" "archive/a.txt").IsOk)
        Assert.Equal(Ok "A", DiskWorkspace.readText root "archive/a.txt")
        Assert.True((DiskWorkspace.move root "notes" "docs").IsOk)
        Assert.True(File.Exists(Path.Combine(root, "docs", "todo.md")))
        Assert.True((DiskWorkspace.delete root "docs").IsOk)
        Assert.False(Directory.Exists(Path.Combine(root, "docs")))
        Assert.True((DiskWorkspace.delete root "").IsError)
        Assert.True((DiskWorkspace.delete root "../outside").IsError))
