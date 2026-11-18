namespace FSharpened.Tooling.Completion

open System
open System.Text.RegularExpressions

/// Decides what a provider may be shown: a bounded window around the cursor of the open file, and nothing that looks
/// like a secret. The patterns are the same idea as the `secrets` check of `tools/ci/fsh_ci.py`, plus assignments of
/// quoted values to names like `password` and `api_key`. A pattern list is best effort: it can miss a secret and it can
/// withhold harmless code, so it is a second line of defence behind "only the open file" and "off by default".
module Context =
    /// The most the Workers AI module accepts (ai/src/policy.ts LIMITS); a request is trimmed to fit before it is sent.
    [<Literal>]
    let WorkerPrefixLimit = 6000

    [<Literal>]
    let WorkerSuffixLimit = 2000

    [<Literal>]
    let DefaultMaxContext = 6000

    let private secretPatterns: (string * Regex) list =
        let rx (p: string) = Regex(p)
        let rxi (p: string) = Regex(p, RegexOptions.IgnoreCase)

        [ "private key", rx "-----BEGIN [A-Z ]*PRIVATE KEY-----"
          "GitHub token", rx "gh[pousr]_[A-Za-z0-9]{36,}"
          "AWS access key", rx "AKIA[0-9A-Z]{16}"
          "Slack token", rx "xox[baprs]-[A-Za-z0-9-]{10,}"
          "JSON web token", rx "eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}"
          "API key", rx "sk-[A-Za-z0-9]{20,}"
          "credential assignment",
          rxi
              "(password|passwd|pwd|secret|api[_-]?key|access[_-]?key|auth[_-]?token|token)[\"']?\\s*[:=]\\s*[\"'][^\"'\\s]{8,}[\"']" ]

    /// The name of the first secret pattern that matches the text, if any.
    let findSecret (text: string) : string option =
        secretPatterns
        |> List.tryPick (fun (name, rx) -> if rx.IsMatch text then Some name else None)

    let private sensitiveNames =
        [ ".env"
          ".npmrc"
          ".pypirc"
          ".netrc"
          "id_rsa"
          "id_ed25519"
          "credentials"
          "secrets"
          "secret.json"
          "appsettings.production.json" ]

    let private sensitiveExtensions =
        [ ".pem"; ".key"; ".pfx"; ".p12"; ".keystore"; ".jks"; ".kdbx" ]

    /// True for files that usually hold secrets (`.env`, key and certificate stores, credentials files).
    let isSensitivePath (path: string) : bool =
        let p = path.Replace('\\', '/').ToLowerInvariant()
        let name = p.Substring(p.LastIndexOf '/' + 1)

        sensitiveNames |> List.exists (fun n -> name = n || name.StartsWith(n + "."))
        || sensitiveExtensions |> List.exists (fun e -> name.EndsWith e)

    let private isLowSurrogate (c: char) = int c >= 0xDC00 && int c <= 0xDFFF
    let private isHighSurrogate (c: char) = int c >= 0xD800 && int c <= 0xDBFF

    /// The last `n` characters of the text, starting at a line start when one is close enough (at most half is dropped to get there).
    let takeTail (n: int) (text: string) : string =
        if n <= 0 then
            ""
        elif text.Length <= n then
            text
        else
            let mutable start = text.Length - n
            let nl = text.IndexOf('\n', start)

            if nl >= 0 && nl + 1 - start <= n / 2 && nl + 1 < text.Length then
                start <- nl + 1

            if start < text.Length && isLowSurrogate text.[start] then
                start <- start + 1

            text.Substring start

    /// The first `n` characters of the text, ending at a line end when one is close enough (at most half is dropped to get there).
    let takeHead (n: int) (text: string) : string =
        if n <= 0 then
            ""
        elif text.Length <= n then
            text
        else
            let mutable stop = n
            let nl = text.LastIndexOf('\n', n - 1)

            if nl >= n / 2 then
                stop <- nl

            if stop > 0 && stop < text.Length && isHighSurrogate text.[stop - 1] then
                stop <- stop - 1

            text.Substring(0, stop)

    /// The window around the cursor: three quarters of the cap before it, a quarter after, never more than the module accepts.
    let window (request: CompletionRequest) : CompletionRequest =
        let cap = max 0 (min request.MaxContext (WorkerPrefixLimit + WorkerSuffixLimit))
        let suffixShare = min WorkerSuffixLimit (cap / 4)
        let suffix = takeHead suffixShare request.Suffix
        let prefixShare = min WorkerPrefixLimit (cap - suffix.Length)

        { request with
            Prefix = takeTail prefixShare request.Prefix
            Suffix = suffix }

    /// Builds a request from the open file's whole text and the cursor offset.
    let ofBuffer (path: string) (language: string) (text: string) (cursor: int) (maxContext: int) : CompletionRequest =
        let at = max 0 (min cursor text.Length)

        { Path = path
          Language = language
          Prefix = text.Substring(0, at)
          Suffix = text.Substring at
          Cursor = at
          MaxContext = maxContext }

    /// The request a remote provider may see, or why it may not see one. Local providers get the same request without the secret check.
    let prepare (request: CompletionRequest) : Result<CompletionRequest, Withheld> =
        let w = window request

        if isSensitivePath w.Path then
            Error(SensitiveFile w.Path)
        elif w.Prefix.Trim().Length = 0 then
            Error NothingToComplete
        else
            match findSecret (w.Prefix + "\n" + w.Suffix) with
            | Some name -> Error(LooksLikeSecret name)
            | None -> Ok w
