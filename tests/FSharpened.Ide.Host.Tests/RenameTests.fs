module RenameTests

open System.IO
open System.Text.Json
open Xunit
open FSharpened.Ide.Host

let private pos l c = { Line = l; Character = c }

let private edit (l1, c1) (l2, c2) text : TextEdit =
    { Range = { Start = pos l1 c1; End = pos l2 c2 }
      NewText = text }

let private ok (r: Result<string, string>) =
    match r with
    | Ok s -> s
    | Error e -> failwithf "expected success, got: %s" e

let private ok' (r: Result<'a, string>) =
    match r with
    | Ok s -> s
    | Error e -> failwithf "expected success, got: %s" e

let private fails (r: Result<string, string>) =
    match r with
    | Error e -> e
    | Ok s -> failwithf "expected a refusal, got: %s" s

[<Fact>]
let ``positions are found by line and UTF-16 character, whatever the line endings`` () =
    let text = "ab\r\ncd\nef\rgh"
    let starts = Edits.lineStarts text
    Assert.Equal<int[]>([| 0; 4; 7; 10 |], starts)
    Assert.Equal(Ok 5, Edits.offsetOf text starts (pos 1 1))
    Assert.Equal(Ok 10, Edits.offsetOf text starts (pos 3 0))
    // a character past the end of its line is the end of the line, before the line break
    Assert.Equal(Ok 2, Edits.offsetOf text starts (pos 0 99))
    Assert.Equal(Ok 6, Edits.offsetOf text starts (pos 1 99))
    // a line that does not exist is an error; a negative position too
    Assert.True(Result.isError (Edits.offsetOf text starts (pos 4 0)))
    Assert.True(Result.isError (Edits.offsetOf text starts (pos -1 0)))

[<Fact>]
let ``the line after a final newline is the end of the text`` () =
    let text = "a\n"
    let starts = Edits.lineStarts text
    Assert.Equal(Ok 2, Edits.offsetOf text starts (pos 1 0))

[<Fact>]
let ``edits are applied whatever order they come in, and offsets do not drift`` () =
    let text = "let foo = 1\nlet bar = foo + foo\n"

    let edits =
        [ edit (1, 16) (1, 19) "answer" // the second foo of line 2
          edit (0, 4) (0, 7) "answer"
          edit (1, 10) (1, 13) "answer" ]

    Assert.Equal("let answer = 1\nlet bar = answer + answer\n", ok (Edits.apply text edits))

[<Fact>]
let ``an edit can span lines, insert, and delete`` () =
    let text = "one\ntwo\nthree"
    Assert.Equal("one\nTWO and\nthree", ok (Edits.apply text [ edit (1, 0) (1, 3) "TWO and" ]))
    Assert.Equal("onetwo\nthree", ok (Edits.apply text [ edit (0, 3) (1, 0) "" ]))
    Assert.Equal(">one\ntwo\nthree<", ok (Edits.apply text [ edit (0, 0) (0, 0) ">"; edit (2, 5) (2, 5) "<" ]))
    Assert.Equal("one\n2\nthree", ok (Edits.apply text [ edit (1, 0) (1, 3) "2" ]))

[<Fact>]
let ``insertions at one position keep the order they were given in`` () =
    let text = "x"

    let edits =
        [ edit (0, 1) (0, 1) "1"; edit (0, 1) (0, 1) "2"; edit (0, 0) (0, 0) "0" ]

    Assert.Equal("0x12", ok (Edits.apply text edits))

[<Fact>]
let ``touching edits are fine and overlapping edits refuse the whole change`` () =
    let text = "abcdef"
    Assert.Equal("XYef", ok (Edits.apply text [ edit (0, 0) (0, 2) "X"; edit (0, 2) (0, 4) "Y" ]))
    let e = fails (Edits.apply text [ edit (0, 0) (0, 3) "X"; edit (0, 2) (0, 4) "Y" ])
    Assert.Contains("overlap", e)
    // an insertion inside a replaced range overlaps it too
    Assert.True(Result.isError (Edits.apply text [ edit (0, 0) (0, 4) "X"; edit (0, 2) (0, 2) "Y" ]))

[<Fact>]
let ``a range that runs backwards or leaves the file is refused`` () =
    let text = "abc\ndef"
    Assert.Contains("before it starts", fails (Edits.apply text [ edit (0, 2) (0, 1) "x" ]))
    Assert.Contains("beyond the end", fails (Edits.apply text [ edit (5, 0) (5, 1) "x" ]))

[<Fact>]
let ``characters outside the basic plane count as two`` () =
    // "😀" is two UTF-16 units; the server counts them that way
    let text = "a😀b = 1"
    Assert.Equal("a😀c = 1", ok (Edits.apply text [ edit (0, 3) (0, 4) "c" ]))

let private reader (files: (string * string) list) =
    let m = dict files

    fun (p: string) ->
        match m.TryGetValue p with
        | true, t -> Ok t
        | _ -> Error "not found"

[<Fact>]
let ``a workspace edit is planned for every file before anything is written`` () =
    let a = Path.Combine(Path.GetTempPath(), "a.go")
    let b = Path.Combine(Path.GetTempPath(), "b.go")
    let text = "foo foo"

    let we =
        { Files =
            [ a, [ edit (0, 0) (0, 3) "bar"; edit (0, 4) (0, 7) "bar" ]
              b, [ edit (0, 0) (0, 3) "bar" ] ]
          Unsupported = [] }

    match Edits.plan (reader [ a, text; b, text ]) we with
    | Ok [ x; y ] ->
        Assert.Equal("bar bar", x.After)
        Assert.Equal(2, x.Count)
        Assert.Equal("foo foo", x.Before)
        Assert.Equal("bar foo", y.After)
        Assert.Equal("3 places in 2 files", Edits.summary [ x; y ])
        Assert.Equal("1 place in 1 file", Edits.summary [ y ])
    | other -> failwithf "unexpected %A" other

    // one bad file refuses all of them
    let bad =
        { we with
            Files =
                we.Files
                @ [ Path.Combine(Path.GetTempPath(), "c.go"), [ edit (0, 0) (0, 1) "x" ] ] }

    Assert.True(Result.isError (Edits.plan (reader [ a, text; b, text ]) bad))

    let overlapping =
        { we with
            Files = [ a, [ edit (0, 0) (0, 5) "q"; edit (0, 3) (0, 6) "r" ] ] }

    Assert.True(Result.isError (Edits.plan (reader [ a, text ]) overlapping))

[<Fact>]
let ``file operations in a workspace edit are refused, not half done`` () =
    let we =
        { Files = []
          Unsupported = [ "rename"; "delete" ] }

    match Edits.plan (fun _ -> Ok "") we with
    | Error e -> Assert.Contains("rename, delete", e)
    | Ok _ -> failwith "should be refused"

[<Fact>]
let ``names are checked before they are sent`` () =
    Assert.Equal("", Edits.checkName "newName")
    Assert.True(Edits.checkName "" <> "")
    Assert.True(Edits.checkName "  " <> "")
    Assert.True(Edits.checkName "a\nb" <> "")
    Assert.True(Edits.checkName " a" <> "")

let private parse (json: string) =
    use d = JsonDocument.Parse json
    LspParse.workspaceEdit d.RootElement

[<Fact>]
let ``both shapes of a workspace edit are read, and file operations are named`` () =
    let a = LspWire.toUri (Path.Combine(Path.GetTempPath(), "x", "a.go"))
    let b = LspWire.toUri (Path.Combine(Path.GetTempPath(), "x", "b.go"))

    let changes =
        parse (
            sprintf
                """{"changes":{"%s":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"newText":"bar"}],"%s":[]}}"""
                a
                b
        )

    Assert.Equal(2, changes.Files.Length)
    Assert.Equal("bar", (snd changes.Files.Head).Head.NewText)

    let docChanges =
        parse (
            sprintf
                """{"documentChanges":[{"textDocument":{"uri":"%s","version":3},"edits":[{"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":4}},"newText":"z"}]},{"kind":"rename","oldUri":"%s","newUri":"%s"},{"textDocument":{"uri":"%s","version":null},"edits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"q"}]}]}"""
                a
                a
                b
                a
        )

    Assert.Equal<string list>([ "rename" ], docChanges.Unsupported)
    // two pieces for the same file are joined
    Assert.Equal(1, docChanges.Files.Length)
    Assert.Equal(2, (snd docChanges.Files.Head).Length)

[<Fact>]
let ``a prepareRename answer is read in all three shapes`` () =
    let prep (json: string) =
        use d = JsonDocument.Parse json
        LspParse.prepareRename d.RootElement

    let r = prep """{"start":{"line":1,"character":2},"end":{"line":1,"character":5}}"""
    Assert.Equal(Some { Start = pos 1 2; End = pos 1 5 }, r.Range)

    let r2 =
        prep """{"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":5}},"placeholder":"foo"}"""

    Assert.Equal(Some "foo", r2.Placeholder)
    let r3 = prep """{"defaultBehavior":true}"""
    Assert.Equal(None, r3.Range)

// ---- the client against a scripted server ------------------------------------------------------------

let private serverWith (capabilities: string) (rename: string -> string) =
    fun (m: JsonElement) (reply: string -> unit) ->
        match ToolingTests.methodOf m with
        | "initialize" ->
            reply (
                sprintf """{"jsonrpc":"2.0","id":%s,"result":{"capabilities":%s}}""" (ToolingTests.idOf m) capabilities
            )
        | "textDocument/prepareRename" ->
            reply (
                sprintf
                    """{"jsonrpc":"2.0","id":%s,"result":{"range":{"start":{"line":0,"character":4},"end":{"line":0,"character":7}},"placeholder":"foo"}}"""
                    (ToolingTests.idOf m)
            )
        | "textDocument/rename" ->
            let newName = m.GetProperty("params").GetProperty("newName").GetString()
            reply (rename (ToolingTests.idOf m) |> fun s -> s.Replace("NEWNAME", newName))
        | _ -> ()

[<Fact>]
let ``rename asks the server first, then for the edits, and reports the server's own reason when it refuses`` () =
    let file = Path.Combine(Path.GetTempPath(), "fsh-rename", "main.go")
    let uri = LspWire.toUri file

    let rename id =
        if id = "4" then
            sprintf """{"jsonrpc":"2.0","id":%s,"error":{"code":-32602,"message":"cannot rename a keyword"}}""" id
        else
            sprintf
                """{"jsonrpc":"2.0","id":%s,"result":{"changes":{"%s":[{"range":{"start":{"line":0,"character":4},"end":{"line":0,"character":7}},"newText":"NEWNAME"}]}}}"""
                id
                uri

    ToolingTests.withServer (serverWith """{"renameProvider":{"prepareProvider":true}}""" rename) (fun client _ ->
        Assert.True(client.Initialize(Path.GetTempPath()).Result)
        Assert.Equal(RenameWithPrepare, client.RenameSupport)

        match client.PrepareRename(file, pos 0 5).Result with
        | Ok p ->
            Assert.Equal(Some "foo", p.Placeholder)
            Assert.Equal(Some { Start = pos 0 4; End = pos 0 7 }, p.Range)
        | Error e -> failwith e

        match client.Rename(file, pos 0 5, "answer").Result with
        | Ok we ->
            Assert.Equal(1, we.Files.Length)
            Assert.Equal("answer", (snd we.Files.Head).Head.NewText)
        | Error e -> failwith e

        match client.Rename(file, pos 0 5, "if").Result with
        | Error e -> Assert.Equal("cannot rename a keyword", e)
        | Ok _ -> failwith "the server's error should come through")

[<Fact>]
let ``a server without rename is told so before it is asked`` () =
    let file = Path.Combine(Path.GetTempPath(), "fsh-rename", "main.go")

    ToolingTests.withServer
        (serverWith """{"hoverProvider":true}""" (fun _ -> failwith "must not be asked"))
        (fun client _ ->
            Assert.True(client.Initialize(Path.GetTempPath()).Result)
            Assert.Equal(NoRename, client.RenameSupport)

            match client.Rename(file, pos 0 0, "x").Result with
            | Error e -> Assert.Contains("does not support rename", e)
            | Ok _ -> failwith "should be refused")

[<Fact>]
let ``a server with rename but no prepareRename is not asked to prepare`` () =
    let file = Path.Combine(Path.GetTempPath(), "fsh-rename", "main.go")

    ToolingTests.withServer (serverWith """{"renameProvider":true}""" (fun _ -> "")) (fun client _ ->
        Assert.True(client.Initialize(Path.GetTempPath()).Result)
        Assert.Equal(RenameOnly, client.RenameSupport)

        match client.PrepareRename(file, pos 0 0).Result with
        | Ok p ->
            Assert.Equal(None, p.Range)
            Assert.Equal(None, p.Placeholder)
        | Error e -> failwith e)

[<Fact>]
let ``spans are the checked edits in order, and the preview lists the lines that changed`` () =
    let text = "a foo\nb\nc foo foo\n"

    let sp =
        Edits.spans text [ edit (2, 6) (2, 9) "x"; edit (0, 2) (0, 5) "x"; edit (2, 2) (2, 5) "x" ]

    Assert.Equal<(int * int * string) list>([ 2, 5, "x"; 10, 13, "x"; 14, 17, "x" ], ok' sp)

    let after =
        ok (Edits.apply text [ edit (2, 6) (2, 9) "x"; edit (0, 2) (0, 5) "x"; edit (2, 2) (2, 5) "x" ])

    Assert.Equal("a x\nb\nc x x\n", after)

    Assert.Equal<(int * string * string) list>(
        [ 1, "a foo", "a x"; 3, "c foo foo", "c x x" ],
        Edits.changedLines text after
    )
    // when the line count changes the preview does not guess
    Assert.Empty(Edits.changedLines "a\nb" "a\nb\nc")

// ---- the whole thing, through a started server process and files on disk ----------------------------------

[<Fact>]
let ``a rename goes from the caret to new text on disk, through a real server process`` () =
    let py =
        [ "python"; "python3" ]
        |> List.tryFind (fun p ->
            match Proc.run [ p; "-c"; "print(1)" ] (Path.GetTempPath()) None (System.TimeSpan.FromSeconds 10.0) with
            | Ok r -> r.ExitCode = Some 0
            | _ -> false)

    match py with
    | None -> () // no Python here, so the scripted server cannot run
    | Some python ->
        let root =
            Path.Combine(Path.GetTempPath(), "fsh-ren-" + System.Guid.NewGuid().ToString "N")

        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        let main = Path.Combine(root, "src", "main.rs")
        let util = Path.Combine(root, "src", "util.rs")

        let mainText =
            "mod util;\r\nfn main() {\r\n    let a = util::compute(3, 4);\r\n}\r\n"

        let utilText = "pub fn compute(x: i32, y: i32) -> i32 {\n    x + y // compute\n}\n"
        File.WriteAllText(main, mainText)
        File.WriteAllText(util, utilText)

        let spec: ServerSpec =
            { Command = python
              Args = [ Path.Combine(System.AppContext.BaseDirectory, "fake_lsp.py") ]
              Install = "" }

        match Lsp.start spec (fun _ _ -> ()) with
        | Error e -> failwith e
        | Ok session ->
            try
                let c = session.Client
                Assert.True((c.Initialize root).Result)
                Assert.Equal(RenameWithPrepare, c.RenameSupport)
                // the editor's text, not the disk's, is what the server sees: the file is open and changed
                let edited = mainText.Replace("(3, 4)", "(30, 40)")
                c.DidOpen(main, "rust", edited)
                let at = pos 2 20

                match c.PrepareRename(main, at).Result with
                | Ok p -> Assert.Equal(Some "compute", p.Placeholder)
                | Error e -> failwith e

                // a keyword is refused with the server's own words
                match c.PrepareRename(main, pos 1 0).Result with
                | Error e -> Assert.Contains("keyword", e)
                | Ok _ -> failwith "a keyword should be refused"

                match c.Rename(main, at, "total").Result with
                | Error e -> failwith e
                | Ok we ->
                    let read (p: string) =
                        if p = main then Ok edited else Ok(File.ReadAllText p)

                    match Edits.plan read we with
                    | Error e -> failwith e
                    | Ok changes ->
                        Assert.Equal(2, changes.Length)
                        Assert.Equal("3 places in 2 files", Edits.summary changes)
                        let m = changes |> List.find (fun x -> x.Path = main)
                        let u = changes |> List.find (fun x -> x.Path = util)
                        Assert.Equal(edited.Replace("compute", "total"), m.After)
                        Assert.Equal(utilText.Replace("compute", "total"), u.After)
                        // CRLF survives in the file that had it
                        Assert.Contains("\r\n", m.After)
                        // nothing was written by planning
                        Assert.Equal(utilText, File.ReadAllText util)

                        for ch in changes do
                            File.WriteAllText(ch.Path, ch.After)

                        Assert.Equal(utilText.Replace("compute", "total"), File.ReadAllText util)
            finally
                session.Stop()

                try
                    Directory.Delete(root, true)
                with _ ->
                    ()
