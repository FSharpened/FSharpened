module ToolingTests

open System
open System.IO
open System.IO.Pipes
open System.Text.Json
open System.Threading
open Xunit
open FSharpened.Ide.Host

[<Fact>]
let ``messages are framed with their byte length and read back whole`` () =
    use ms = new MemoryStream()
    let body = """{"jsonrpc":"2.0","method":"x","params":{"s":"héllo ✓"}}"""
    let bytes = LspWire.frame body
    ms.Write(bytes, 0, bytes.Length)
    ms.Write(bytes, 0, bytes.Length)
    ms.Position <- 0L
    Assert.Equal(Some body, LspWire.read ms)
    Assert.Equal(Some body, LspWire.read ms)
    Assert.Equal(None, LspWire.read ms)

[<Fact>]
let ``broken framing ends the connection instead of reading garbage`` () =
    let read (s: string) =
        LspWire.read (new MemoryStream(Text.Encoding.ASCII.GetBytes s))

    Assert.Equal(None, read "Content-Length: abc\r\n\r\n{}")
    Assert.Equal(None, read "Content-Length: 99999999999\r\n\r\n")
    Assert.Equal(None, read "Content-Length: 10\r\n\r\n{}")
    Assert.Equal(None, read "")

[<Fact>]
let ``file URIs and paths convert both ways and other schemes are not paths`` () =
    let p = Path.Combine(Path.GetTempPath(), "a b", "x.go")
    Assert.Equal(Path.GetFullPath p, LspWire.ofUri (LspWire.toUri p))
    Assert.Equal("", LspWire.ofUri "https://example.com/x")
    Assert.Equal("", LspWire.ofUri "not a uri")

/// A scripted language server on the far end of two pipes.
let private withServer
    (script: JsonElement -> (string -> unit) -> unit)
    (f: LspClient -> (string * LspDiagnostic list) list ref -> unit)
    =
    use toServer = new AnonymousPipeServerStream(PipeDirection.Out)

    use serverIn =
        new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle)

    use fromServer = new AnonymousPipeServerStream(PipeDirection.Out)

    use clientIn =
        new AnonymousPipeClientStream(PipeDirection.In, fromServer.ClientSafePipeHandle)

    let reply (json: string) =
        let b = LspWire.frame json
        fromServer.Write(b, 0, b.Length)
        fromServer.Flush()

    let thread =
        Thread(
            (fun () ->
                let mutable go = true

                while go do
                    match
                        (try
                            LspWire.read serverIn
                         with _ ->
                             None)
                    with
                    | Some m ->
                        use d = JsonDocument.Parse m
                        script d.RootElement reply
                    | None -> go <- false),
            IsBackground = true
        )

    thread.Start()
    let seen = ref []

    let client =
        LspClient(clientIn, toServer, (fun p d -> seen.Value <- (p, d) :: seen.Value))

    f client seen

let private idOf (m: JsonElement) = m.GetProperty("id").GetRawText()

let private methodOf (m: JsonElement) =
    match m.TryGetProperty "method" with
    | true, v -> v.GetString()
    | _ -> ""

[<Fact>]
let ``a client starts a server, sends the file, and receives its problems, completions and definitions`` () =
    let file = Path.Combine(Path.GetTempPath(), "fsh-lsp", "main.go")
    let uri = LspWire.toUri file

    let script (m: JsonElement) (reply: string -> unit) =
        match methodOf m with
        | "initialize" ->
            reply (sprintf """{"jsonrpc":"2.0","id":%s,"result":{"capabilities":{"completionProvider":{}}}}""" (idOf m))
        | "textDocument/didOpen" ->
            // a server may ask the client something before it reports; the client must answer
            reply """{"jsonrpc":"2.0","id":"cfg-1","method":"workspace/configuration","params":{"items":[]}}"""

            reply (
                sprintf
                    """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"%s","diagnostics":[{"range":{"start":{"line":5,"character":13},"end":{"line":5,"character":24}},"severity":1,"message":"undefined: undefinedName","source":"compiler","code":"UndeclaredName"}]}}"""
                    uri
            )
        | "textDocument/completion" ->
            reply (
                sprintf
                    """{"jsonrpc":"2.0","id":%s,"result":{"isIncomplete":false,"items":[{"label":"Println","detail":"func(a ...any)","textEdit":{"newText":"Println","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}}}},{"label":"Printf"}]}}"""
                    (idOf m)
            )
        | "textDocument/definition" ->
            reply (
                sprintf
                    """{"jsonrpc":"2.0","id":%s,"result":[{"uri":"%s","range":{"start":{"line":9,"character":5},"end":{"line":9,"character":9}}}]}"""
                    (idOf m)
                    uri
            )
        | "textDocument/hover" ->
            reply (
                sprintf
                    """{"jsonrpc":"2.0","id":%s,"result":{"contents":{"kind":"markdown","value":"func main()"}}}"""
                    (idOf m)
            )
        | "shutdown" -> reply (sprintf """{"jsonrpc":"2.0","id":%s,"result":null}""" (idOf m))
        | _ -> ()

    withServer script (fun client seen ->
        Assert.True((client.Initialize(Path.GetDirectoryName file)).Result)
        client.DidOpen(file, "go", "package main\n")
        let deadline = DateTime.UtcNow.AddSeconds 10.0

        while seen.Value.IsEmpty && DateTime.UtcNow < deadline do
            Thread.Sleep 20

        let path, diags = seen.Value.Head
        Assert.Equal(Path.GetFullPath file, path)
        Assert.Equal(1, diags.Length)
        Assert.Equal(1, diags.Head.Severity)
        Assert.Equal(5, diags.Head.Range.Start.Line)
        Assert.Equal("UndeclaredName", diags.Head.Code)
        let items = client.Completion(file, { Line = 0; Character = 0 }).Result
        Assert.Equal<string list>([ "Println"; "Printf" ], items |> List.map (fun i -> i.Label))
        Assert.Equal("Println", items.Head.Insert)
        Assert.Equal("Printf", items[1].Insert)
        let defs = client.Definition(file, { Line = 5; Character = 3 }).Result
        Assert.Equal(Path.GetFullPath file, defs.Head.Path)
        Assert.Equal(9, defs.Head.Range.Start.Line)
        Assert.Equal(Some "func main()", client.Hover(file, { Line = 0; Character = 0 }).Result)
        client.Shutdown().Wait())

[<Fact>]
let ``a server that goes away makes requests return nothing rather than hang`` () =
    withServer (fun _ _ -> ()) (fun client _ ->
        // the script never answers; closing the far end must release the caller
        let t = client.Completion("x.go", { Line = 0; Character = 0 })
        Assert.False(t.Wait(TimeSpan.FromMilliseconds 300.0)))

[<Fact>]
let ``a server that is not installed is an error that says how to get it`` () =
    let spec: ServerSpec =
        { Command = "fsh-no-such-server-" + Guid.NewGuid().ToString "N"
          Args = []
          Install = "https://example.invalid/ls" }

    match Lsp.start spec (fun _ _ -> ()) with
    | Ok _ -> failwith "should not start"
    | Error e -> Assert.Contains("https://example.invalid/ls", e)

[<Fact>]
let ``git status entries are read, including renames and names with spaces`` () =
    let out =
        "M  src/a.fs\000?? new file.txt\000R  new.go\000old.go\000 D gone.rs\000UU conflict.c\000A  added.md\000"

    let parsed = Git.parseStatus out

    Assert.Equal<(string * GitChange) list>(
        [ "src/a.fs", Modified
          "new file.txt", Untracked
          "new.go", Renamed
          "gone.rs", Deleted
          "conflict.c", Conflicted
          "added.md", Added ],
        parsed
    )

[<Fact>]
let ``a real repository reports its branch and changes, and a plain folder reports none`` () =
    DiskTests.withFolder (fun dir ->
        Assert.Equal(None, Git.branch dir)
        Assert.Empty(Git.status dir)

        let git args =
            Proc.run ("git" :: args) dir None (TimeSpan.FromSeconds 20.0)

        match git [ "init"; "-q"; "-b"; "trunk" ] with
        | Error _ -> () // git is not installed here: nothing to check
        | Ok _ ->
            git [ "config"; "user.email"; "t@example.invalid" ] |> ignore
            git [ "config"; "user.name"; "t" ] |> ignore
            File.WriteAllText(Path.Combine(dir, "a.txt"), "one")
            git [ "add"; "." ] |> ignore
            git [ "commit"; "-q"; "-m"; "first" ] |> ignore
            Assert.Equal(Some "trunk", Git.branch dir)
            File.WriteAllText(Path.Combine(dir, "a.txt"), "two")
            File.WriteAllText(Path.Combine(dir, "b b.txt"), "new")
            let changes = Git.status dir |> Map.ofList
            Assert.Equal(Modified, changes["a.txt"])
            Assert.Equal(Untracked, changes["b b.txt"])
            Assert.Equal(Some "one", Git.head dir "a.txt")
            Assert.Equal(None, Git.head dir "b b.txt"))
