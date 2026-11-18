module LspFeatureTests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpened.Ide.Host

let private pos l c = { Line = l; Character = c }

let private json (text: string) = JsonDocument.Parse(text).RootElement

let private file (name: string) =
    LspWire.toUri (Path.Combine(Path.GetTempPath(), name))

// ---- reading what servers send ---------------------------------------------------------------------------

[<Fact>]
let ``locations are read from a Location, a list of them and a list of links, and only files count`` () =
    let one =
        sprintf
            """{"uri":"%s","range":{"start":{"line":1,"character":2},"end":{"line":1,"character":5}}}"""
            (file "a.rs")

    let list =
        "["
        + one
        + """,{"uri":"untitled:Untitled-1","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}]"""

    let link =
        sprintf
            """[{"targetUri":"%s","targetRange":{"start":{"line":9,"character":0},"end":{"line":12,"character":1}},"targetSelectionRange":{"start":{"line":9,"character":4},"end":{"line":9,"character":8}}}]"""
            (file "b.rs")

    let a = LspFeatureParse.locations (json one)
    Assert.Equal(1, a.Length)
    Assert.Equal(pos 1 2, a.Head.Range.Start)
    Assert.Equal(1, (LspFeatureParse.locations (json list)).Length)
    let l = LspFeatureParse.locations (json link)
    Assert.Equal(pos 9 4, l.Head.Range.Start)
    Assert.Empty(LspFeatureParse.locations (json "null"))

[<Fact>]
let ``a hierarchical outline keeps its children and a flat one keeps its container`` () =
    let tree =
        """[{"name":"Shape","kind":5,"range":{"start":{"line":0,"character":0},"end":{"line":9,"character":1}},
              "selectionRange":{"start":{"line":0,"character":6},"end":{"line":0,"character":11}},
              "children":[{"name":"area","kind":6,"range":{"start":{"line":2,"character":4},"end":{"line":4,"character":5}},
                           "selectionRange":{"start":{"line":2,"character":7},"end":{"line":2,"character":11}}}]}]"""

    let symbols = LspFeatureParse.documentSymbols (json tree)
    Assert.Equal(1, symbols.Length)
    Assert.Equal("area", symbols.Head.Children.Head.Name)
    Assert.Equal("Shape", symbols.Head.Children.Head.Container)

    let flat =
        sprintf
            """[{"name":"run","kind":12,"containerName":"Main","location":{"uri":"%s","range":{"start":{"line":3,"character":0},"end":{"line":5,"character":1}}}}]"""
            (file "m.rs")

    let f = LspFeatureParse.documentSymbols (json flat)
    Assert.Equal("Main", f.Head.Container)
    Assert.Equal(12, f.Head.Kind)

[<Fact>]
let ``the server's outline becomes the IDE's outline with 1-based lines, depth and kinds`` () =
    let tree =
        """[{"name":"Shape","kind":5,"range":{"start":{"line":0,"character":0},"end":{"line":9,"character":1}},
              "selectionRange":{"start":{"line":0,"character":6},"end":{"line":0,"character":11}},
              "children":[{"name":"new","kind":9,"range":{"start":{"line":2,"character":4},"end":{"line":4,"character":5}},
                           "selectionRange":{"start":{"line":2,"character":7},"end":{"line":2,"character":10}}}]},
             {"name":"main","kind":12,"range":{"start":{"line":11,"character":0},"end":{"line":13,"character":1}},
              "selectionRange":{"start":{"line":11,"character":3},"end":{"line":11,"character":7}}}]"""

    let outline = LspOutline.toOutline (LspFeatureParse.documentSymbols (json tree))
    Assert.Equal<string list>([ "Shape"; "new"; "main" ], outline |> List.map (fun s -> s.Name))
    Assert.Equal<int list>([ 0; 1; 0 ], outline |> List.map (fun s -> s.Depth))
    Assert.Equal<string list>([ "class"; "constructor"; "function" ], outline |> List.map (fun s -> s.Kind))
    Assert.Equal(1, outline[0].Line)
    Assert.Equal(7, outline[0].Column)
    Assert.Equal(10, outline[0].EndLine)
    Assert.Equal("Shape", outline[1].Container)

[<Fact>]
let ``signature help reads offsets and strings for parameters and picks the active one`` () =
    let h =
        json
            """{"signatures":[{"label":"add(a: int, b: int)","documentation":{"kind":"markdown","value":"Adds."},
                  "parameters":[{"label":[4,10]},{"label":"b: int","documentation":"the second"}]}],
                "activeSignature":0,"activeParameter":1}"""

    match LspFeatureParse.signatureHelp h with
    | None -> failwith "expected signature help"
    | Some s ->
        Assert.Equal("Adds.", s.Signatures.Head.Doc)
        Assert.Equal<string list>([ "a: int"; "b: int" ], s.Signatures.Head.Parameters |> List.map (fun p -> p.Label))
        Assert.Equal(Some "b: int", LspFeatureParse.activeParameterLabel s)

    // no signatures, or a parameter index past the end, is no emphasis rather than a failure
    Assert.True((LspFeatureParse.signatureHelp (json """{"signatures":[]}""")).IsNone)

    match LspFeatureParse.signatureHelp (json """{"signatures":[{"label":"f()"}],"activeParameter":3}""") with
    | Some s -> Assert.Equal(None, LspFeatureParse.activeParameterLabel s)
    | None -> failwith "expected signature help"

[<Fact>]
let ``code actions come as actions or bare commands, with their edits, kinds and reasons`` () =
    let uri = file "q.rs"

    let actions =
        json (
            sprintf
                """[{"title":"Remove it","kind":"quickfix","isPreferred":true,
                     "edit":{"changes":{"%s":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":""}]}}},
                    {"title":"Later","kind":"refactor","data":{"x":1}},
                    {"title":"Server command","command":"do.it"},
                    {"title":"Nope","disabled":{"reason":"not in a function"}},
                    {"kind":"quickfix"}]"""
                uri
        )
        |> LspFeatureParse.codeActions

    Assert.Equal<string list>(
        [ "Remove it"; "Later"; "Server command"; "Nope" ],
        actions |> List.map (fun a -> a.Title)
    )

    Assert.True actions[0].IsPreferred
    Assert.Equal(1, actions[0].Edit.Value.Files.Length)
    Assert.True actions[1].Edit.IsNone
    Assert.False actions[1].HasCommand
    Assert.True actions[2].HasCommand
    Assert.Equal(Some "not in a function", actions[3].Disabled)

[<Fact>]
let ``formatting edits are read, and a broken edit is an error not a guess`` () =
    let edits =
        LspFeatureParse.textEdits (
            json """[{"range":{"start":{"line":0,"character":3},"end":{"line":0,"character":5}},"newText":""}]"""
        )

    Assert.Equal(1, edits.Length)

    Assert.ThrowsAny<exn>(fun () -> LspFeatureParse.textEdits (json """[{"newText":"x"}]""") |> ignore)
    |> ignore

// ---- against a scripted server process --------------------------------------------------------------------

let private python =
    lazy
        ([ "python"; "python3" ]
         |> List.tryFind (fun p ->
             match Proc.run [ p; "-c"; "print(1)" ] (Path.GetTempPath()) None (TimeSpan.FromSeconds 10.0) with
             | Ok r -> r.ExitCode = Some 0
             | _ -> false))

/// Starts the scripted server on a folder with these files and runs the body; nothing happens without Python.
let private withServer (extraArgs: string list) (files: (string * string) list) (body: string -> LspClient -> unit) =
    match python.Value with
    | None -> ()
    | Some py ->
        let root =
            Path.Combine(Path.GetTempPath(), "fsh-lspf-" + Guid.NewGuid().ToString "N")

        Directory.CreateDirectory root |> ignore

        for (name, text) in files do
            File.WriteAllText(Path.Combine(root, name), text)

        let spec: ServerSpec =
            { Command = py
              Args = Path.Combine(AppContext.BaseDirectory, "fake_lsp.py") :: extraArgs
              Install = "" }

        match Lsp.start spec (fun _ _ -> ()) with
        | Error e -> failwith e
        | Ok session ->
            try
                Assert.True((session.Client.Initialize root).Result)
                body root session.Client
            finally
                session.Stop()

                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

let private source =
    "class Shape\n    fn area(w, h)\nfn main()\n    area(2, 3)   \nfn helper(x: int, y: int)\nhelper(1, 2)"

[<Fact>]
let ``a server that supports the features says so, and one that does not is not asked`` () =
    withServer [] [ "a.rs", source ] (fun _ c ->
        Assert.True c.SupportsReferences
        Assert.True c.SupportsDocumentSymbols
        Assert.True c.SupportsSignatureHelp
        Assert.True c.SupportsCodeActions
        Assert.True c.SupportsFormatting
        Assert.True c.CodeActionsResolve
        Assert.Equal<string list>([ "("; "," ], c.SignatureTriggers))

    withServer [ "--basic" ] [ "a.rs", source ] (fun root c ->
        let a = Path.Combine(root, "a.rs")
        Assert.False c.SupportsReferences
        Assert.False c.SupportsDocumentSymbols
        Assert.False c.SupportsSignatureHelp
        Assert.False c.SupportsCodeActions
        Assert.False c.SupportsFormatting
        Assert.Empty c.SignatureTriggers

        match c.References(a, pos 0 7).Result with
        | Error m -> Assert.Contains("does not support", m)
        | Ok _ -> failwith "references should be refused"

        Assert.True(Result.isError (c.DocumentSymbols(a).Result))
        Assert.True((c.SignatureHelp(a, pos 3 9).Result).IsNone)
        Assert.Equal(Ok [], c.CodeActions(a, { Start = pos 0 0; End = pos 0 0 }, []).Result)
        Assert.True(Result.isError (c.FormatDocument(a, { TabSize = 4; InsertSpaces = true }).Result)))

[<Fact>]
let ``find references lists every use across the open buffer and the files of the folder`` () =
    withServer [] [ "a.rs", "fn total()\ntotal()\n"; "b.rs", "x = total()\n" ] (fun root c ->
        let a = Path.Combine(root, "a.rs")
        // the buffer differs from the disk: the server must see the editor's text
        c.DidOpen(a, "rust", "fn total()\ntotal()\ntotal()\n")

        match c.References(a, pos 0 4).Result with
        | Error m -> failwith m
        | Ok locs ->
            Assert.Equal(4, locs.Length)
            Assert.Equal(3, locs |> List.filter (fun l -> Path.GetFileName l.Path = "a.rs") |> List.length)
            Assert.Equal(1, locs |> List.filter (fun l -> Path.GetFileName l.Path = "b.rs") |> List.length)

        // nothing under the caret is an empty answer, not an error
        match c.References(a, pos 0 3).Result with
        | Ok _ -> ()
        | Error m -> failwith m)

[<Fact>]
let ``the outline comes back as a tree and flattens into the IDE's symbols`` () =
    withServer [] [ "a.rs", source ] (fun root c ->
        let a = Path.Combine(root, "a.rs")
        c.DidOpen(a, "rust", source)

        match c.DocumentSymbols(a).Result with
        | Error m -> failwith m
        | Ok symbols ->
            Assert.Equal<string list>([ "Shape"; "main"; "helper" ], symbols |> List.map (fun s -> s.Name))
            Assert.Equal<string list>([ "area" ], symbols.Head.Children |> List.map (fun s -> s.Name))
            let outline = LspOutline.toOutline symbols
            Assert.Equal<string list>([ "Shape"; "area"; "main"; "helper" ], outline |> List.map (fun s -> s.Name))
            Assert.Equal(2, outline[1].Line))

[<Fact>]
let ``signature help follows the comma to the next parameter`` () =
    withServer [] [ "a.rs", source ] (fun root c ->
        let a = Path.Combine(root, "a.rs")
        c.DidOpen(a, "rust", source)

        // inside "helper(1, 2)" on the last line, after "helper(" and after "helper(1, "
        match c.SignatureHelp(a, pos 5 7).Result with
        | None -> failwith "expected a signature"
        | Some h ->
            Assert.Equal("helper(x: int, y: int)", h.Signatures.Head.Label)
            Assert.Equal(Some "x: int", LspFeatureParse.activeParameterLabel h)

        match c.SignatureHelp(a, pos 5 10).Result with
        | None -> failwith "expected a signature"
        | Some h -> Assert.Equal(Some "y: int", LspFeatureParse.activeParameterLabel h)

        // outside any call there is none
        Assert.True((c.SignatureHelp(a, pos 0 2).Result).IsNone))

[<Fact>]
let ``code actions carry an edit, resolve later, or are a command this client cannot run`` () =
    withServer [] [ "a.rs", source ] (fun root c ->
        let a = Path.Combine(root, "a.rs")
        c.DidOpen(a, "rust", source)

        // line 3 ("    area(2, 3)   ") has trailing spaces
        match c.CodeActions(a, { Start = pos 3 5; End = pos 3 5 }, []).Result with
        | Error m -> failwith m
        | Ok actions ->
            let fix = actions |> List.find (fun x -> x.Title = "Remove trailing whitespace")
            Assert.Equal("quickfix", fix.Kind)
            Assert.True fix.IsPreferred
            Assert.True fix.Edit.IsSome

            let upper = actions |> List.find (fun x -> x.Title.StartsWith "Convert")
            Assert.True upper.Edit.IsNone
            let resolved = c.ResolveCodeAction(upper).Result
            Assert.True resolved.Edit.IsSome

            // applying the resolved edit gives the upper-case word
            let text = source
            let edits = resolved.Edit.Value.Files |> List.collect snd

            match Edits.apply text edits with
            | Ok t -> Assert.Contains("    AREA(2, 3)", t)
            | Error e -> failwith e

            let cmd = actions |> List.find (fun x -> x.Title = "Run server command")
            Assert.True cmd.HasCommand
            Assert.True cmd.Edit.IsNone)

[<Fact>]
let ``formatting through the server gives edits that, applied, trim lines and end the text with a newline`` () =
    withServer [] [ "a.rs", "x = 1   \ny = 2" ] (fun root c ->
        let a = Path.Combine(root, "a.rs")
        let text = "x = 1   \ny = 2   \nz"
        c.DidOpen(a, "rust", text)

        match c.FormatDocument(a, { TabSize = 4; InsertSpaces = true }).Result with
        | Error m -> failwith m
        | Ok edits ->
            match Edits.apply text edits with
            | Ok t -> Assert.Equal("x = 1\ny = 2\nz\n", t)
            | Error e -> failwith e

        // already formatted: no edits
        c.DidChange(a, "x = 1\n")
        Assert.Equal(Ok [], c.FormatDocument(a, { TabSize = 4; InsertSpaces = true }).Result))
