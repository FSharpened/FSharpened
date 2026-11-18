/// AI completion for the page (ADR-0026): the provider chain of FSharpened.Tooling.Completion as plain objects.
/// The page owns the network. It hands over `send(url, body, timeoutMs)`, which returns a promise of
/// `{ status, body }`, `{ timedOut: true }` or `{ error }`. Nothing here runs unless the person turned AI completion on.
module FSharpened.Ide.Web.CompletionApi

open System.Threading
open Fable.Core
open Fable.Core.JsInterop
open FSharpened.Tooling.Completion

let private str (v: obj) : string =
    if jsTypeof v = "string" then unbox<string> v else ""

let private strings (v: obj) : string list =
    if isNull v || not (JS.Constructors.Array.isArray v) then
        []
    else
        unbox<obj[]> v
        |> Array.filter (fun x -> jsTypeof x = "string")
        |> Array.map unbox<string>
        |> Array.toList

/// True for an endpoint the page may post to: https, or http on localhost.
let endpointOk (url: string) : bool = WorkersAi.isAllowedEndpoint url

let private reasonText (u: Unavailable) : string =
    match u with
    | Disabled -> "off"
    | Budget n ->
        if n > 0 then
            "over budget, retry in " + string n + " s"
        else
            "over budget"
    | Failed m -> m
    | TimedOut -> "timed out"
    | Empty -> "nothing to suggest"
    | Cancelled -> "cancelled"

let private withheldText (w: Withheld) : string =
    match w with
    | LooksLikeSecret p -> "the text near the cursor looks like a secret (" + p + ")"
    | SensitiveFile p -> p + " is a file that usually holds secrets"
    | NothingToComplete -> "there is nothing before the cursor to complete"

let private kindText (k: CandidateKind) =
    match k with
    | Inline -> "inline"
    | Keyword -> "keyword"
    | Snippet -> "snippet"

let private snippetEntries (v: obj) : StaticProvider.SnippetEntry list =
    if isNull v || not (JS.Constructors.Array.isArray v) then
        []
    else
        unbox<obj[]> v
        |> Array.choose (fun o ->
            let label = str o?label
            let body = str o?body

            if label = "" || body = "" then
                None
            else
                let entry: StaticProvider.SnippetEntry =
                    { Label = label
                      Body = body
                      Languages = strings o?languages }

                Some entry)
        |> Array.toList

let private transportOf (send: System.Func<string, string, int, JS.Promise<obj>>) : Transport =
    fun url body timeoutMs _ ->
        async {
            try
                let! r = send.Invoke(url, body, timeoutMs) |> Async.AwaitPromise

                if jsTypeof r?timedOut = "boolean" && unbox<bool> r?timedOut then
                    return TransportTimedOut
                elif jsTypeof r?status = "number" then
                    return Reply(int (unbox<float> r?status), str r?body)
                else
                    return NetworkError(let e = str r?error in if e = "" then "request failed" else e)
            with e ->
                return NetworkError e.Message
        }

/// Asks the chain (Workers AI first when an endpoint is given, then the static provider) for a completion at `cursor` of
/// the open file `text`. Resolves to `{ source, candidates: [{ text, kind, confidence }], skipped: [string], withheld }`.
/// It never rejects: every failure is a reason in `skipped`.
let run
    (path: string)
    (language: string)
    (text: string)
    (cursor: int)
    (maxContext: int)
    (endpoint: string)
    (timeoutMs: int)
    (snippets: obj)
    (send: System.Func<string, string, int, JS.Promise<obj>>)
    : JS.Promise<obj> =
    let request = Context.ofBuffer path language text cursor maxContext
    let staticProvider = StaticProvider.create (snippetEntries snippets)

    let providers =
        if endpoint = "" then
            [ staticProvider ]
        else
            let settings =
                { WorkersAi.defaultSettings endpoint with
                    TimeoutMs = timeoutMs }

            [ WorkersAi.create settings (transportOf send); staticProvider ]

    async {
        let! r = Chain.run providers request CancellationToken.None

        return
            createObj
                [ "source" ==> r.Source
                  "candidates"
                  ==> (r.Candidates
                       |> List.map (fun c ->
                           createObj [ "text" ==> c.Text; "kind" ==> kindText c.Kind; "confidence" ==> c.Confidence ])
                       |> List.toArray)
                  "skipped"
                  ==> (r.Skipped |> List.map (fun (p, u) -> p + ": " + reasonText u) |> List.toArray)
                  "withheld" ==> (r.Withheld |> Option.map withheldText |> Option.defaultValue "") ]
    }
    |> Async.StartAsPromise
