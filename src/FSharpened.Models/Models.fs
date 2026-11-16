namespace FSharpened.Models

/// The five-layer deterministic fallback stack. Layer 3 is pure F# with no native dependency,
/// so it is always available on every platform.
type FallbackLayer =
    | SpecialisedModel
    | SiblingModel
    | SymbolicRules
    | WarmCheckpoint
    | StructuredEscalation

type FailureRecord =
    { Task: string
      Attempts: (FallbackLayer * string) list
      /// Deterministic resume token handed to the Orchestrator.
      ResumeToken: string }

type Outcome<'T> =
    | Resolved of layer: FallbackLayer * value: 'T
    /// Never "failed": deferred with a recovery plan.
    | Deferred of FailureRecord

module Fallback =
    /// Runs the layers in order. Exceptions are failures, never escapes.
    let run (task: string) (layers: (FallbackLayer * (unit -> Result<'T, string>)) list) : Outcome<'T> =
        let rec go remaining attempts =
            match remaining with
            | [] ->
                Deferred
                    { Task = task
                      Attempts = List.rev attempts
                      ResumeToken = sprintf "%s#%d" task (List.length attempts) }
            | (layer, f) :: rest ->
                let result =
                    try
                        f ()
                    with ex ->
                        Error ex.Message

                match result with
                | Ok v -> Resolved(layer, v)
                | Error msg -> go rest ((layer, msg) :: attempts)

        go layers []

module Pillar =
    [<Literal>]
    let Id = "Models"

    [<Literal>]
    let Stability = "Experimental"

    [<Literal>]
    let Status = "Skeleton"
