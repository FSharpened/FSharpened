module ReplaceTests

open System.IO
open Xunit
open FSharpened.Ide
open FSharpened.Ide.Host

let private opts (q: string) : SearchOptions =
    { Query = q
      CaseSensitive = true
      WholeWord = false
      Regex = false }

let private find (root: string) (o: SearchOptions) (opened: (string * string) list) =
    let r = Search.run o "" "" (Replace.gather root opened)
    Assert.Equal("", r.Error)
    r

[<Fact>]
let ``files on disk are rewritten, and others are left alone`` () =
    DiskTests.withFolder (fun root ->
        File.WriteAllText(Path.Combine(root, "a.txt"), "old one\nold two\n")
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "b.txt"), "no match here\nold\n")
        File.WriteAllText(Path.Combine(root, "c.txt"), "nothing\n")

        let o = opts "old"
        let found = find root o []
        let outcome = Replace.apply root o "new" found.Files []

        Assert.Equal(2, outcome.Files)
        Assert.Equal(3, outcome.Matches)
        Assert.Empty outcome.Failed
        Assert.Empty outcome.Editor
        Assert.Equal("new one\nnew two\n", File.ReadAllText(Path.Combine(root, "a.txt")))
        Assert.Equal("no match here\nnew\n", File.ReadAllText(Path.Combine(root, "src", "b.txt")))
        Assert.Equal("nothing\n", File.ReadAllText(Path.Combine(root, "c.txt"))))

[<Fact>]
let ``a file open in the editor is searched as it is there and handed back, not written`` () =
    DiskTests.withFolder (fun root ->
        // on disk the word is absent; in the editor it was just typed, unsaved
        File.WriteAllText(Path.Combine(root, "a.txt"), "saved text\n")
        let opened = [ "a.txt", "typed old text\n" ]

        let o = opts "old"
        let found = find root o opened
        Assert.Equal(1, found.Total)

        let outcome = Replace.apply root o "new" found.Files opened
        Assert.Equal(1, outcome.Matches)
        Assert.Equal<(string * string) list>([ "a.txt", "typed new text\n" ], outcome.Editor)
        Assert.Equal("saved text\n", File.ReadAllText(Path.Combine(root, "a.txt"))))

[<Fact>]
let ``line endings, regex groups and a bad pattern are handled`` () =
    DiskTests.withFolder (fun root ->
        File.WriteAllText(Path.Combine(root, "a.txt"), "x1 y\r\nx22 y\r\n")

        let o = { opts "x(\d+)" with Regex = true }

        let found = find root o []
        let outcome = Replace.apply root o "n$1" found.Files []
        Assert.Equal(2, outcome.Matches)
        Assert.Equal("n1 y\r\nn22 y\r\n", File.ReadAllText(Path.Combine(root, "a.txt")))

        let bad = { opts "(" with Regex = true }
        let r = Search.run bad "" "" (Replace.gather root [])
        Assert.False(r.Error = ""))

[<Fact>]
let ``a file that disappeared or cannot be read is reported and the rest still change`` () =
    DiskTests.withFolder (fun root ->
        File.WriteAllText(Path.Combine(root, "a.txt"), "old\n")
        File.WriteAllText(Path.Combine(root, "b.txt"), "old\n")
        let o = opts "old"
        let found = find root o []
        File.Delete(Path.Combine(root, "a.txt"))

        let outcome = Replace.apply root o "new" found.Files []
        Assert.Equal(1, outcome.Files)
        Assert.Equal(1, outcome.Failed.Length)
        Assert.Contains("a.txt", outcome.Failed.Head)
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(root, "b.txt"))))
