namespace FSharpened.Tooling.Completion

open System
open System.Text.RegularExpressions
open System.Threading

/// What a host's HTTP call returns. The host (browser `fetch` with an `AbortController`, or `HttpClient`) enforces the
/// timeout it is given and reports `TransportTimedOut`; this library does no I/O of its own.
type TransportOutcome =
    | Reply of status: int * body: string
    | TransportTimedOut
    | NetworkError of string

/// `endpoint -> json body -> timeout in milliseconds -> cancellation -> outcome`.
type Transport = string -> string -> int -> CancellationToken -> Async<TransportOutcome>

type WorkersAiSettings =
    {
        /// Full URL of `POST /api/ai/complete` of the Workers AI module (ai/). https, or http on localhost for development.
        Endpoint: string
        TimeoutMs: int
        MaxTokens: int option
    }

/// The first remote provider: asks the Workers AI module (ADR-0026) for a completion and reports honestly why it could not.
module WorkersAi =
    [<Literal>]
    let Name = "workers-ai"

    let defaultSettings endpoint =
        { Endpoint = endpoint
          TimeoutMs = 9000
          MaxTokens = None }

    let private authority =
        Regex("^(?:[A-Za-z0-9][A-Za-z0-9.-]*|\\[::1\\])(?::[0-9]{1,5})?$")

    let private isLocalHost (a: string) =
        let host =
            if a.StartsWith "[" then a.Substring(0, a.IndexOf ']' + 1)
            elif a.Contains ":" then a.Substring(0, a.IndexOf ':')
            else a

        host = "localhost" || host = "127.0.0.1" || host = "[::1]"

    /// https anywhere, plain http only for localhost; nothing else (no credentials in the URL, no other schemes,
    /// no spaces or control characters). Checked by hand because `System.Uri` is only partly available under Fable.
    let isAllowedEndpoint (url: string) : bool =
        let schemeEnd = url.IndexOf "://"

        if
            url.Length > 300
            || schemeEnd < 0
            || url |> Seq.exists (fun c -> c <= ' ' || c = '\\')
        then
            false
        else
            let scheme = url.Substring(0, schemeEnd)
            let rest = url.Substring(schemeEnd + 3)
            let cut = rest.IndexOfAny [| '/'; '?'; '#' |]
            let a = if cut < 0 then rest else rest.Substring(0, cut)

            authority.IsMatch a && (scheme = "https" || (scheme = "http" && isLocalHost a))

    let encodeRequest (maxTokens: int option) (r: CompletionRequest) : string =
        JObj
            [ yield "language", JStr r.Language
              yield "prefix", JStr r.Prefix
              yield "suffix", JStr r.Suffix
              yield "path", JStr r.Path
              match maxTokens with
              | Some n -> yield "maxTokens", JNum(float n)
              | None -> () ]
        |> MiniJson.render

    /// Text from a model is data. Control characters (other than newline, tab and carriage return) and bidirectional
    /// override characters are dropped here again, in case a server did not.
    let cleanText (s: string) : string =
        let keep (c: char) =
            (c >= ' '
             && c <> '\u007f'
             && not (c >= '\u202a' && c <= '\u202e')
             && not (c >= '\u2066' && c <= '\u2069')
             && c <> '\u2028'
             && c <> '\u2029')
            || c = '\n'
            || c = '\t'

        String(s.ToCharArray() |> Array.filter keep)

    [<Literal>]
    let MaxCandidates = 5

    [<Literal>]
    let MaxCandidateChars = 2000

    let private kindOf (k: string) =
        match k with
        | "inline" -> Some Inline
        | "keyword" -> Some Keyword
        | "snippet" -> Some Snippet
        | _ -> None

    let private candidate (v: JsonValue) : Candidate option =
        match MiniJson.tryField "text" v, MiniJson.tryField "kind" v with
        | Some(JStr t), Some(JStr k) ->
            match kindOf k, cleanText t with
            | Some kind, text when text.Trim().Length > 0 ->
                let conf =
                    match MiniJson.tryField "confidence" v with
                    | Some(JNum c) -> max 0.0 (min 1.0 c)
                    | _ -> 0.0

                Some
                    { Text =
                        (if text.Length > MaxCandidateChars then
                             text.Substring(0, MaxCandidateChars)
                         else
                             text)
                      Kind = kind
                      Confidence = conf }
            | _ -> None
        | _ -> None

    let private unavailable (reason: string) (retry: int) : Unavailable =
        match reason with
        | "disabled"
        | "unavailable" -> Disabled
        | "budget" -> Budget retry
        | "timeout" -> TimedOut
        | "empty" -> Empty
        | other -> Failed other

    /// Reads the module's answer. A static (degraded) answer from the server is reported as unavailable, so the chain's
    /// own static provider, which also knows the IDE's snippets, answers instead.
    let decodeResponse (body: string) : ProviderOutcome =
        match MiniJson.parse body with
        | Error e -> Unavailable(Failed("bad response: " + e))
        | Ok v ->
            let degraded =
                match MiniJson.tryField "degraded" v with
                | Some(JStr s) -> Some s
                | _ -> None

            let retry =
                match MiniJson.tryField "retryAfterSeconds" v with
                | Some(JNum n) when n >= 0.0 && n < 1e6 -> int n
                | _ -> 0

            match MiniJson.tryField "ok" v, MiniJson.tryField "source" v, degraded with
            | Some(JBool true), _, Some d -> Unavailable(unavailable d retry)
            | Some(JBool true), Some(JStr "workers-ai"), None ->
                match MiniJson.tryField "candidates" v with
                | Some(JArr items) ->
                    match items |> List.choose candidate |> List.truncate MaxCandidates with
                    | [] -> Unavailable Empty
                    | cs -> Answer cs
                | _ -> Unavailable(Failed "bad response: no candidates")
            | _ -> Unavailable(Failed "bad response: unexpected shape")

    /// Maps what the host's HTTP call returned.
    let interpret (outcome: TransportOutcome) : ProviderOutcome =
        match outcome with
        | TransportTimedOut -> Unavailable TimedOut
        | NetworkError m -> Unavailable(Failed m)
        | Reply(200, body) -> decodeResponse body
        | Reply(429, _) -> Unavailable(Budget 0)
        | Reply(status, _) -> Unavailable(Failed("HTTP " + string status))

    let create (settings: WorkersAiSettings) (transport: Transport) : ICompletionProvider =
        { new ICompletionProvider with
            member _.Name = Name
            member _.IsRemote = true

            member _.Complete(request, ct) =
                async {
                    if ct.IsCancellationRequested then
                        return Unavailable Cancelled
                    elif not (isAllowedEndpoint settings.Endpoint) then
                        return Unavailable(Failed "endpoint must be https (or http on localhost)")
                    else
                        let body = encodeRequest settings.MaxTokens request
                        let! outcome = transport settings.Endpoint body settings.TimeoutMs ct

                        if ct.IsCancellationRequested then
                            return Unavailable Cancelled
                        else
                            return interpret outcome
                } }
