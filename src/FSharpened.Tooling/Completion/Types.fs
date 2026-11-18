namespace FSharpened.Tooling.Completion

open System.Threading

/// What a suggestion is: text a model wrote, a language keyword, or a snippet.
type CandidateKind =
    | Inline
    | Keyword
    | Snippet

/// One suggestion: the text to insert at the cursor, what it is, and how much to trust it (0.0 to 1.0, a rough guide only).
type Candidate =
    { Text: string
      Kind: CandidateKind
      Confidence: float }

/// A request for a completion at the cursor of the file the person has open and is editing.
/// There is deliberately no field for any other file: a provider is never given text from a file the person did not open.
type CompletionRequest =
    {
        /// Path of the open file, relative to the project. Used for the language and the sensitive-file check.
        Path: string
        /// The IDE's language id (`fsharp`, `csharp`, `cpp`, ...).
        Language: string
        /// Text of the open file before the cursor.
        Prefix: string
        /// Text of the open file after the cursor.
        Suffix: string
        /// Offset of the cursor in the whole file (the length of the prefix before trimming).
        Cursor: int
        /// Most characters of prefix and suffix together a provider may be given.
        MaxContext: int
    }

/// Why a provider gave no answer. The reasons the Workers AI module reports are `Disabled`, `Budget`, `Error`, `Timeout` and `Empty`.
type Unavailable =
    | Disabled
    | Budget of retryAfterSeconds: int
    | Failed of string
    | TimedOut
    | Empty
    | Cancelled

/// What one provider returns: candidates, or the reason it could not.
type ProviderOutcome =
    | Answer of Candidate list
    | Unavailable of Unavailable

/// A completion provider. Providers know nothing about each other; the chain decides the order.
type ICompletionProvider =
    /// A short stable name, shown to the person as the source of a suggestion (`workers-ai`, `static`).
    abstract Name: string
    /// True when the provider sends the request off this machine. Remote providers are never given a withheld request.
    abstract IsRemote: bool
    abstract Complete: request: CompletionRequest * ct: CancellationToken -> Async<ProviderOutcome>

/// Why a request was not sent to remote providers.
type Withheld =
    | LooksLikeSecret of pattern: string
    | SensitiveFile of path: string
    | NothingToComplete

/// The final answer of a chain: the candidates, who produced them, and why earlier providers did not.
type CompletionResult =
    {
        Candidates: Candidate list
        /// The name of the provider that answered, or "none".
        Source: string
        /// (provider, reason) for every provider that was tried and gave no answer.
        Skipped: (string * Unavailable) list
        /// Set when remote providers were not asked at all.
        Withheld: Withheld option
    }
