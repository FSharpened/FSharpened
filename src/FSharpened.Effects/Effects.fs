namespace FSharpened.Effects

/// Struct-based unification of success / typed error / cancellation. No monad transformers.
[<Struct>]
type Effect<'T, 'E> =
    | Success of value: 'T
    | Failure of error: 'E
    | Cancelled

module Effect =
    let succeed (v: 'T) : Effect<'T, 'E> = Success v
    let fail (e: 'E) : Effect<'T, 'E> = Failure e

    let map (f: 'T -> 'U) (e: Effect<'T, 'E>) : Effect<'U, 'E> =
        match e with
        | Success v -> Success(f v)
        | Failure err -> Failure err
        | Cancelled -> Cancelled

    let bind (f: 'T -> Effect<'U, 'E>) (e: Effect<'T, 'E>) : Effect<'U, 'E> =
        match e with
        | Success v -> f v
        | Failure err -> Failure err
        | Cancelled -> Cancelled

    let ofResult (r: Result<'T, 'E>) : Effect<'T, 'E> =
        match r with
        | Ok v -> Success v
        | Error e -> Failure e

    let toResult (e: Effect<'T, 'E>) (onCancel: 'E) : Result<'T, 'E> =
        match e with
        | Success v -> Ok v
        | Failure err -> Error err
        | Cancelled -> Error onCancel

module Pillar =
    [<Literal>]
    let Id = "Effects"

    [<Literal>]
    let Stability = "Preview"

    [<Literal>]
    let Status = "Skeleton"
