module FSharpened.Ide.Tests.CompletionTests

open System
open System.Threading
open Xunit
open FSharpened.Tooling.Completion

let private run (a: Async<'a>) = Async.RunSynchronously a

let private request prefix suffix : CompletionRequest =
    { Path = "src/a.fs"
      Language = "fsharp"
      Prefix = prefix
      Suffix = suffix
      Cursor = String.length prefix
      MaxContext = 6000 }

// Secret-shaped strings are assembled at run time so this file never contains one (the CI secrets check scans it).
let private githubToken = "gh" + "p_" + String('a', 36)
let private awsKey = "AK" + "IA" + String('A', 16)
let private pemHeader = "-----BEGIN " + "RSA PRIVATE KEY-----"
let private slackToken = "xo" + "xb-" + "1234567890-abcdef"

let private fixedProvider name remote (outcome: ProviderOutcome) (calls: int ref) : ICompletionProvider =
    { new ICompletionProvider with
        member _.Name = name
        member _.IsRemote = remote

        member _.Complete(_, _) =
            async {
                calls.Value <- calls.Value + 1
                return outcome
            } }

let private cand t =
    { Text = t
      Kind = Inline
      Confidence = 0.5 }

// ---- JSON ---------------------------------------------------------------------------------------------

[<Fact>]
let ``MiniJson round-trips strings with escapes and unicode`` () =
    let v =
        JObj
            [ "a", JStr "line1\nline2\t\"q\" \\ \u0001 \u00e9 \ud83d\ude00"
              "n", JNum 3.0
              "b", JBool true
              "z", JNull
              "l", JArr [ JNum 1.5; JStr "x" ] ]

    Assert.Equal(Ok v, MiniJson.parse (MiniJson.render v))

[<Fact>]
let ``MiniJson reads escapes and numbers`` () =
    Assert.Equal(Ok(JStr "a/b\u00e9"), MiniJson.parse "\"a\\/b\\u00e9\"")
    Assert.Equal(Ok(JNum -12.5e1), MiniJson.parse " -12.5e1 ")
    Assert.Equal(Ok(JObj []), MiniJson.parse "{ }")
    Assert.Equal(Ok(JArr []), MiniJson.parse "[]")

[<Theory>]
[<InlineData("")>]
[<InlineData("{")>]
[<InlineData("{\"a\":}")>]
[<InlineData("[1,]")>]
[<InlineData("\"abc")>]
[<InlineData("\"\\x\"")>]
[<InlineData("\"\\u12G4\"")>]
[<InlineData("tru")>]
[<InlineData("1 2")>]
[<InlineData("{\"a\":1} x")>]
[<InlineData("\"a\nb\"")>]
let ``MiniJson rejects malformed input without throwing`` (text: string) =
    Assert.True(Result.isError (MiniJson.parse text))

[<Fact>]
let ``MiniJson refuses deep nesting and oversized input`` () =
    Assert.True(Result.isError (MiniJson.parse (String('[', 40) + String(']', 40))))
    Assert.True(Result.isOk (MiniJson.parse (String('[', 10) + String(']', 10))))
    Assert.True(Result.isError (MiniJson.parse ("\"" + String('a', MiniJson.MaxChars) + "\"")))

// ---- context trimming -----------------------------------------------------------------------------------

[<Fact>]
let ``ofBuffer splits the open file at the cursor and clamps the cursor`` () =
    let r = Context.ofBuffer "a.fs" "fsharp" "let x = 1" 4 100
    Assert.Equal("let ", r.Prefix)
    Assert.Equal("x = 1", r.Suffix)
    Assert.Equal(4, r.Cursor)
    Assert.Equal("let x = 1", (Context.ofBuffer "a.fs" "fsharp" "let x = 1" 99 100).Prefix)
    Assert.Equal("", (Context.ofBuffer "a.fs" "fsharp" "let x = 1" -5 100).Prefix)

[<Fact>]
let ``a window keeps the text next to the cursor and respects the cap`` () =
    let prefix = String.replicate 500 "0123456789\n"
    let suffix = String.replicate 500 "abcdefghi\n"

    let w =
        Context.window
            { request prefix suffix with
                MaxContext = 400 }

    Assert.True(w.Prefix.Length + w.Suffix.Length <= 400)
    Assert.True(prefix.EndsWith w.Prefix)
    Assert.True(suffix.StartsWith w.Suffix)
    Assert.True(w.Prefix.Length > w.Suffix.Length) // three quarters before, a quarter after

[<Fact>]
let ``a window cuts at line boundaries where it can`` () =
    let w =
        Context.window
            { request (String.replicate 100 "abcdefghi\n") (String.replicate 100 "abcdefghi\n") with
                MaxContext = 200 }

    Assert.StartsWith("abcdefghi\n", w.Prefix) // starts at a line start, not mid-line
    Assert.EndsWith("abcdefghi", w.Suffix) // ends with a whole line, not part of one

[<Fact>]
let ``a window never exceeds what the module accepts`` () =
    let w =
        Context.window
            { request (String('a', 50000)) (String('b', 50000)) with
                MaxContext = 1000000 }

    Assert.True(w.Prefix.Length <= Context.WorkerPrefixLimit)
    Assert.True(w.Suffix.Length <= Context.WorkerSuffixLimit)

[<Fact>]
let ``a window does not split a surrogate pair`` () =
    let text = String.replicate 50 "\ud83d\ude00" // 100 chars
    let w = Context.window { request text "" with MaxContext = 41 }
    Assert.True(Char.IsHighSurrogate w.Prefix.[0] || not (Char.IsLowSurrogate w.Prefix.[0]))

[<Fact>]
let ``a zero cap leaves nothing`` () =
    let w =
        Context.window
            { request "abc" "def" with
                MaxContext = 0 }

    Assert.Equal("", w.Prefix)
    Assert.Equal("", w.Suffix)

[<Fact>]
let ``text that fits is left alone`` () =
    let w = Context.window (request "let x =" "\n")
    Assert.Equal("let x =", w.Prefix)
    Assert.Equal("\n", w.Suffix)

[<Fact>]
let ``secret-looking content is found`` () =
    for text in
        [ githubToken
          awsKey
          pemHeader
          slackToken
          "password = \"hunter2hunter2\""
          "const apiKey: \"abcdefgh12345678\""
          "sk-" + String('a', 24)
          "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkw.abcdefghijklmnop" ] do
        Assert.True((Context.findSecret text).IsSome, text)

[<Fact>]
let ``ordinary code is not mistaken for a secret`` () =
    for text in
        [ "let password = readPassword ()"
          "let token = lexer.Next()"
          "printfn \"hello\""
          "let key = \"a\"" ] do
        Assert.True((Context.findSecret text).IsNone, text)

[<Theory>]
[<InlineData(".env")>]
[<InlineData("config/.env.local")>]
[<InlineData("keys/server.pem")>]
[<InlineData("C:\\u\\.ssh\\id_rsa")>]
[<InlineData("ops/credentials.json")>]
[<InlineData(".npmrc")>]
[<InlineData("store.PFX")>]
let ``sensitive files are recognised`` (path: string) =
    Assert.True(Context.isSensitivePath path)

[<Theory>]
[<InlineData("src/main.fs")>]
[<InlineData("docs/environment.md")>]
[<InlineData("src/secretary.fs")>]
let ``ordinary files are not sensitive`` (path: string) =
    Assert.False(Context.isSensitivePath path)

[<Fact>]
let ``prepare withholds a request with a secret in the window, a sensitive file, or nothing before the cursor`` () =
    Assert.Equal(
        Error(LooksLikeSecret "GitHub token"),
        Context.prepare (request ("let t = \"" + githubToken + "\"\nlet x = ") "")
    )

    Assert.Equal(
        Error(SensitiveFile ".env"),
        Context.prepare
            { request "A=1\nB" "" with
                Path = ".env" }
    )

    Assert.Equal(Error NothingToComplete, Context.prepare (request "   \n " ""))
    Assert.True(Result.isOk (Context.prepare (request "let x =" "")))

[<Fact>]
let ``a secret far from the cursor, outside the window, is not sent and does not block`` () =
    let far = githubToken + "\n" + String.replicate 400 "let a = 1\n"

    let r =
        Context.prepare
            { request (far + "let x = ") "" with
                MaxContext = 300 }

    match r with
    | Ok w -> Assert.DoesNotContain(githubToken, w.Prefix)
    | Error e -> failwithf "unexpected %A" e

[<Fact>]
let ``a secret in the suffix is also caught`` () =
    Assert.True(Result.isError (Context.prepare (request "let x = " ("\nlet k = \"" + awsKey + "\""))))

// ---- the static provider ---------------------------------------------------------------------------------

[<Fact>]
let ``the word at the cursor is found`` () =
    Assert.Equal("pri", StaticProvider.currentWord "let a = 1\n  pri")
    Assert.Equal("", StaticProvider.currentWord "let x = ")
    Assert.Equal("#inc", StaticProvider.currentWord "  #inc")
    Assert.Equal("std::vec", StaticProvider.currentWord "  std::vec")
    Assert.Equal("x.", StaticProvider.currentWord "x.") // a dotted name is one word: List.m continues to List.map
    Assert.Equal("", StaticProvider.currentWord "f(")

[<Fact>]
let ``static keywords continue the word, shortest first, and are deterministic`` () =
    let a = StaticProvider.candidates [] "fsharp" "le" 5
    Assert.Equal<string list>([ "t"; "t rec" ], a |> List.map (fun c -> c.Text))
    Assert.All(a, fun c -> Assert.Equal(Keyword, c.Kind))
    Assert.Equal<Candidate list>(a, StaticProvider.candidates [] "fsharp" "le" 5)

[<Fact>]
let ``static completion knows snippets the host passes and limits by language and count`` () =
    let sn: StaticProvider.SnippetEntry list =
        [ { Label = "prop"
            Body = "member val X = 0 with get, set"
            Languages = [ "fsharp" ] }
          { Label = "proc"
            Body = "never offered"
            Languages = [ "python" ] } ]

    let c = StaticProvider.candidates sn "fsharp" "pro" 5
    Assert.Contains(c, fun x -> x.Kind = Snippet && x.Text = "member val X = 0 with get, set")
    Assert.DoesNotContain(c, fun x -> x.Text = "never offered")
    Assert.True((StaticProvider.candidates sn "fsharp" "p" 1).Length <= 1)
    Assert.Empty(StaticProvider.candidates [] "brainfuck" "pri" 5)
    Assert.Empty(StaticProvider.candidates [] "fsharp" "let x = " 5)

[<Fact>]
let ``the static provider answers, reports empty, and honours cancellation`` () =
    let p = StaticProvider.create []
    Assert.False p.IsRemote
    Assert.Equal("static", p.Name)

    Assert.True(
        match run (p.Complete(request "  pri" "", CancellationToken.None)) with
        | Answer(_ :: _) -> true
        | _ -> false
    )

    Assert.Equal(Unavailable Empty, run (p.Complete(request "let x = " "", CancellationToken.None)))
    use cts = new CancellationTokenSource()
    cts.Cancel()
    Assert.Equal(Unavailable Cancelled, run (p.Complete(request "pri" "", cts.Token)))

// ---- the Workers AI provider ------------------------------------------------------------------------------

let private okBody (text: string) =
    MiniJson.render (
        JObj
            [ "ok", JBool true
              "source", JStr "workers-ai"
              "degraded", JNull
              "model", JStr "m"
              "cached", JBool false
              "candidates", JArr [ JObj [ "text", JStr text; "kind", JStr "inline"; "confidence", JNum 0.5 ] ] ]
    )

let private degradedBody (reason: string) =
    MiniJson.render (
        JObj
            [ "ok", JBool true
              "source", JStr "static"
              "degraded", JStr reason
              "retryAfterSeconds", JNum 42.0
              "candidates", JArr [ JObj [ "text", JStr "ntfn"; "kind", JStr "keyword"; "confidence", JNum 0.3 ] ] ]
    )

[<Fact>]
let ``the endpoint must be https, or http on localhost`` () =
    Assert.True(WorkersAi.isAllowedEndpoint "https://ai.example/api/ai/complete")
    Assert.True(WorkersAi.isAllowedEndpoint "http://localhost:8791/api/ai/complete")
    Assert.True(WorkersAi.isAllowedEndpoint "http://127.0.0.1:8791/api/ai/complete")

    for bad in
        [ ""
          "ai.example"
          "http://ai.example/x"
          "ftp://ai.example/x"
          "javascript:alert(1)"
          "https://user:pw@ai.example/x"
          "file:///etc/passwd" ] do
        Assert.False(WorkersAi.isAllowedEndpoint bad, bad)

[<Fact>]
let ``the request carries the open file's window and nothing else`` () =
    let body = WorkersAi.encodeRequest (Some 64) (request "let x =" "\n")

    match MiniJson.parse body with
    | Ok(JObj fields) ->
        Assert.Equal<string list>([ "language"; "prefix"; "suffix"; "path"; "maxTokens" ], fields |> List.map fst)
    | other -> failwithf "unexpected %A" other

    Assert.DoesNotContain("maxTokens", WorkersAi.encodeRequest None (request "a" ""))

[<Fact>]
let ``a good answer becomes candidates`` () =
    match WorkersAi.decodeResponse (okBody "ntfn \"hi\"") with
    | Answer [ c ] ->
        Assert.Equal("ntfn \"hi\"", c.Text)
        Assert.Equal(Inline, c.Kind)
        Assert.Equal(0.5, c.Confidence)
    | other -> failwithf "unexpected %A" other

[<Theory>]
[<InlineData("disabled")>]
[<InlineData("unavailable")>]
[<InlineData("budget")>]
[<InlineData("timeout")>]
[<InlineData("empty")>]
[<InlineData("error")>]
let ``a degraded answer is unavailable so the local static provider answers instead`` (reason: string) =
    let expected =
        match reason with
        | "disabled"
        | "unavailable" -> Disabled
        | "budget" -> Budget 42
        | "timeout" -> TimedOut
        | "empty" -> Empty
        | other -> Failed other

    Assert.Equal(Unavailable expected, WorkersAi.decodeResponse (degradedBody reason))

[<Fact>]
let ``a bad answer is a failure, never an exception`` () =
    for body in
        [ ""
          "not json"
          "[]"
          "{}"
          "{\"ok\":true}"
          "{\"ok\":true,\"source\":\"workers-ai\",\"degraded\":null}"
          "{\"ok\":false}" ] do
        let isFailure =
            match WorkersAi.decodeResponse body with
            | Unavailable(Failed _) -> true
            | _ -> false

        Assert.True(isFailure, body)

[<Fact>]
let ``candidates are validated: unknown kinds and blank text dropped, count and length capped, control characters removed``
    ()
    =
    let item kind (text: string) =
        JObj [ "text", JStr text; "kind", JStr kind; "confidence", JNum 9.0 ]

    let body =
        MiniJson.render (
            JObj
                [ "ok", JBool true
                  "source", JStr "workers-ai"
                  "degraded", JNull
                  "candidates",
                  JArr(
                      [ item "run" "rm -rf /"
                        item "inline" "   "
                        item "inline" "a\u0000b\u202ec\n\td" ]
                      @ [ for i in 1..10 -> item "inline" (string i) ]
                      @ [ item "inline" (String('x', 5000)) ]
                  ) ]
        )

    match WorkersAi.decodeResponse body with
    | Answer cs ->
        Assert.Equal(WorkersAi.MaxCandidates, cs.Length)
        Assert.Equal("abc\n\td", cs.Head.Text)
        Assert.All(cs, fun c -> Assert.InRange(c.Confidence, 0.0, 1.0))
        Assert.DoesNotContain(cs, fun c -> c.Text.Contains "rm -rf")
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``HTTP outcomes map to reasons`` () =
    Assert.Equal(Unavailable TimedOut, WorkersAi.interpret TransportTimedOut)
    Assert.Equal(Unavailable(Failed "offline"), WorkersAi.interpret (NetworkError "offline"))
    Assert.Equal(Unavailable(Budget 0), WorkersAi.interpret (Reply(429, "")))
    Assert.Equal(Unavailable(Failed "HTTP 503"), WorkersAi.interpret (Reply(503, "x")))
    Assert.Equal(Unavailable(Failed "HTTP 413"), WorkersAi.interpret (Reply(413, "{}")))

let private transport (outcome: TransportOutcome) (seen: (string * string * int) list ref) : Transport =
    fun url body timeout _ ->
        async {
            seen.Value <- (url, body, timeout) :: seen.Value
            return outcome
        }

[<Fact>]
let ``the provider posts to the endpoint with the timeout and decodes the reply`` () =
    let seen = ref []

    let s =
        { WorkersAi.defaultSettings "https://ai.example/api/ai/complete" with
            TimeoutMs = 1234 }

    let p = WorkersAi.create s (transport (Reply(200, okBody "ntfn")) seen)
    Assert.True p.IsRemote
    Assert.Equal("workers-ai", p.Name)

    Assert.True(
        match run (p.Complete(request "  pri" "", CancellationToken.None)) with
        | Answer [ c ] -> c.Text = "ntfn"
        | _ -> false
    )

    Assert.Equal<(string * string * int) list>(
        [ ("https://ai.example/api/ai/complete", WorkersAi.encodeRequest None (request "  pri" ""), 1234) ],
        seen.Value
    )

[<Fact>]
let ``the provider does not call a transport for a bad endpoint or a cancelled request`` () =
    let seen = ref []

    let bad =
        WorkersAi.create (WorkersAi.defaultSettings "http://ai.example/x") (transport (Reply(200, okBody "x")) seen)

    Assert.True(
        match run (bad.Complete(request "a" "", CancellationToken.None)) with
        | Unavailable(Failed _) -> true
        | _ -> false
    )

    let good =
        WorkersAi.create (WorkersAi.defaultSettings "https://ai.example/x") (transport (Reply(200, okBody "x")) seen)

    use cts = new CancellationTokenSource()
    cts.Cancel()
    Assert.Equal(Unavailable Cancelled, run (good.Complete(request "a" "", cts.Token)))
    Assert.Empty seen.Value

// ---- the chain --------------------------------------------------------------------------------------------

let private runChain providers req =
    run (Chain.run providers req CancellationToken.None)

[<Fact>]
let ``the first provider with an answer wins and later ones are not asked`` () =
    let a, b = ref 0, ref 0

    let r =
        runChain
            [ fixedProvider "one" true (Answer [ cand "x" ]) a
              fixedProvider "two" false (Answer [ cand "y" ]) b ]
            (request "pri" "")

    Assert.Equal("one", r.Source)
    Assert.Equal(1, a.Value)
    Assert.Equal(0, b.Value)
    Assert.Empty r.Skipped

[<Theory>]
[<InlineData("budget")>]
[<InlineData("error")>]
[<InlineData("timeout")>]
[<InlineData("disabled")>]
[<InlineData("empty")>]
let ``when the remote provider cannot answer the static provider does, and the reason is kept`` (kind: string) =
    let reason =
        match kind with
        | "budget" -> Budget 30
        | "error" -> Failed "boom"
        | "timeout" -> TimedOut
        | "disabled" -> Disabled
        | _ -> Empty

    let seen = ref 0

    let r =
        runChain
            [ fixedProvider "workers-ai" true (Unavailable reason) seen
              StaticProvider.create [] ]
            (request "  pri" "")

    Assert.Equal("static", r.Source)
    Assert.NotEmpty r.Candidates
    Assert.Equal<(string * Unavailable) list>([ ("workers-ai", reason) ], r.Skipped)

[<Fact>]
let ``a provider that returns an empty answer or throws is skipped`` () =
    let thrower =
        { new ICompletionProvider with
            member _.Name = "thrower"
            member _.IsRemote = true
            member _.Complete(_, _) = async { return failwith "kaboom" } }

    let n = ref 0

    let r =
        runChain [ thrower; fixedProvider "blank" false (Answer []) n; StaticProvider.create [] ] (request "  pri" "")

    Assert.Equal("static", r.Source)
    Assert.Equal<(string * Unavailable) list>([ ("thrower", Failed "kaboom"); ("blank", Empty) ], r.Skipped)

[<Fact>]
let ``a withheld request is never given to a remote provider, but the local one still answers`` () =
    let remote = ref 0
    let req = request ("let t = \"" + githubToken + "\"\n  pri") ""

    let r =
        runChain
            [ fixedProvider "workers-ai" true (Answer [ cand "SENT" ]) remote
              StaticProvider.create [] ]
            req

    Assert.Equal(0, remote.Value)
    Assert.Equal("static", r.Source)
    Assert.Equal(Some(LooksLikeSecret "GitHub token"), r.Withheld)

[<Fact>]
let ``a sensitive file is not sent either`` () =
    let remote = ref 0

    let r =
        runChain
            [ fixedProvider "workers-ai" true (Answer [ cand "SENT" ]) remote ]
            { request "A=1\nB" "" with
                Path = "config/.env" }

    Assert.Equal(0, remote.Value)
    Assert.Equal("none", r.Source)
    Assert.Equal(Some(SensitiveFile "config/.env"), r.Withheld)

[<Fact>]
let ``providers see the windowed request, not the whole file`` () =
    let mutable seenLength = 0

    let spy =
        { new ICompletionProvider with
            member _.Name = "spy"
            member _.IsRemote = true

            member _.Complete(r, _) =
                async {
                    seenLength <- r.Prefix.Length + r.Suffix.Length
                    return Answer [ cand "x" ]
                } }

    runChain
        [ spy ]
        { request (String('a', 20000)) (String('b', 20000)) with
            MaxContext = 500 }
    |> ignore

    Assert.True(seenLength <= 500 && seenLength > 0)

[<Fact>]
let ``no providers, or only failing ones, give no candidates`` () =
    Assert.Equal("none", (runChain [] (request "pri" "")).Source)
    let n = ref 0

    let r =
        runChain [ fixedProvider "a" true (Unavailable(Failed "x")) n ] (request "pri" "")

    Assert.Empty r.Candidates
    Assert.Equal("none", r.Source)

[<Fact>]
let ``cancellation stops the chain before the next provider`` () =
    use cts = new CancellationTokenSource()
    let second = ref 0

    let canceller =
        { new ICompletionProvider with
            member _.Name = "first"
            member _.IsRemote = true

            member _.Complete(_, _) =
                async {
                    cts.Cancel()
                    return Unavailable(Failed "slow")
                } }

    let r =
        run (
            Chain.run
                [ canceller; fixedProvider "second" false (Answer [ cand "x" ]) second ]
                (request "pri" "")
                cts.Token
        )

    Assert.Equal(0, second.Value)
    Assert.Empty r.Candidates
    Assert.Contains(r.Skipped, fun (n, u) -> n = "second" && u = Cancelled)

[<Fact>]
let ``the full chain degrades from Workers AI to static on a real timeout outcome`` () =
    let seen = ref []

    let ai =
        WorkersAi.create
            (WorkersAi.defaultSettings "https://ai.example/api/ai/complete")
            (transport TransportTimedOut seen)

    let r = runChain [ ai; StaticProvider.create [] ] (request "  pri" "")
    Assert.Equal("static", r.Source)
    Assert.Equal<(string * Unavailable) list>([ ("workers-ai", TimedOut) ], r.Skipped)
