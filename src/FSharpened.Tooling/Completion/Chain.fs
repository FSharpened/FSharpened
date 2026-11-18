namespace FSharpened.Tooling.Completion

open System.Threading

/// Runs providers in order and returns the first answer. A provider that fails, times out, is switched off, is over
/// budget or has nothing to say is skipped, and the reason is kept. Remote providers are never asked when the request
/// was withheld (it looks like a secret, or the file is a sensitive one); local providers still are. Cancellation stops the chain.
module Chain =
    let private nothing (withheld: Withheld option) (skipped: (string * Unavailable) list) =
        { Candidates = []
          Source = "none"
          Skipped = List.rev skipped
          Withheld = withheld }

    /// Asks the providers one after another. The request is windowed and checked once, here, so no provider sees more
    /// than the others would. A provider that throws is treated as failed: the chain never throws.
    let run
        (providers: ICompletionProvider list)
        (request: CompletionRequest)
        (ct: CancellationToken)
        : Async<CompletionResult> =
        async {
            let withheld, remoteRequest, localRequest =
                match Context.prepare request with
                | Ok r -> None, Some r, r
                | Error w -> Some w, None, Context.window request

            let rec go (rest: ICompletionProvider list) skipped =
                async {
                    match rest with
                    | [] -> return nothing withheld skipped
                    | p :: tail ->
                        if ct.IsCancellationRequested then
                            return nothing withheld ((p.Name, Cancelled) :: skipped)
                        else
                            let req = if p.IsRemote then remoteRequest else Some localRequest

                            match req with
                            | None -> return! go tail ((p.Name, Failed "withheld") :: skipped)
                            | Some r ->
                                let! outcome =
                                    async {
                                        try
                                            return! p.Complete(r, ct)
                                        with e ->
                                            return Unavailable(Failed e.Message)
                                    }

                                match outcome with
                                | Answer cs when not cs.IsEmpty ->
                                    return
                                        { Candidates = cs
                                          Source = p.Name
                                          Skipped = List.rev skipped
                                          Withheld = withheld }
                                | Answer _ -> return! go tail ((p.Name, Empty) :: skipped)
                                | Unavailable Cancelled -> return nothing withheld ((p.Name, Cancelled) :: skipped)
                                | Unavailable u -> return! go tail ((p.Name, u) :: skipped)
                }

            return! go providers []
        }
