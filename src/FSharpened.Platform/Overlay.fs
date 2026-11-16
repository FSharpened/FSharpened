namespace FSharpened.Platform

open System.Collections.Generic

// Portable by construction: no OS, IO or reflection APIs, so this file also compiles to JavaScript through Fable
// (the web IDE reuses it unchanged; Invariant 5: no private checkpointing).
// ---- Overlay store: in-memory, content-addressed; filesystem CoW is only an optimisation
type IOverlayStore =
    abstract Get: key: string -> byte[] option
    abstract Put: key: string * value: byte[] -> unit
    /// Freezes the current layer and returns a new writable child. O(layer) copy, not O(world).
    abstract Fork: unit -> IOverlayStore

type InMemoryOverlay(parent: IOverlayStore option, seed: seq<KeyValuePair<string, byte[]>>) =
    let layer = Dictionary<string, byte[]>()

    do
        for kv in seed do
            layer.[kv.Key] <- kv.Value

    new() = InMemoryOverlay(None, Seq.empty)

    interface IOverlayStore with
        member _.Get key =
            match layer.TryGetValue key with
            | true, v -> Some v
            | _ ->
                match parent with
                | Some p -> p.Get key
                | None -> None

        member _.Put(key, value) = layer.[key] <- value

        member _.Fork() =
            let frozen = InMemoryOverlay(parent, layer) :> IOverlayStore
            InMemoryOverlay(Some frozen, Seq.empty) :> IOverlayStore
