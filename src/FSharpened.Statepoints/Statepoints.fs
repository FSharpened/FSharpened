namespace FSharpened.Statepoints

open System
open System.Collections.Generic
open FSharpened.Platform

[<Struct>]
type StatepointId = StatepointId of Guid

type Statepoint =
    { Id: StatepointId
      Parents: StatepointId list
      Label: string
      Created: DateTimeOffset
      Overlay: IOverlayStore }

/// Graph (not a linear list) so forks, merges and rollbacks keep provenance.
/// Hot reload, debugging, model canaries, experiments and chaos tests all consume this (Invariant 5).
type StatepointGraph() =
    let nodes = Dictionary<StatepointId, Statepoint>()
    let mutable head: StatepointId option = None

    let create parents label overlay =
        { Id = StatepointId(Guid.NewGuid())
          Parents = parents
          Label = label
          Created = DateTimeOffset.UtcNow
          Overlay = overlay }

    member _.Head = head
    member _.Count = nodes.Count

    member _.Get(id: StatepointId) : Statepoint option =
        match nodes.TryGetValue id with
        | true, sp -> Some sp
        | _ -> None

    member _.Snapshot(label: string, overlay: IOverlayStore) : Statepoint =
        let parents =
            match head with
            | Some h -> [ h ]
            | None -> []

        let sp = create parents label overlay
        nodes.[sp.Id] <- sp
        head <- Some sp.Id
        sp

    /// Isolated branch from a statepoint. The source is never mutated.
    member _.Fork(source: StatepointId, label: string) : Statepoint =
        let src = nodes.[source]
        let sp = create [ source ] label (src.Overlay.Fork())
        nodes.[sp.Id] <- sp
        sp

    /// Moves head back. Compensating actions for remote state are the caller's journal, not ours.
    member _.Rollback(target: StatepointId) : Statepoint =
        let sp = nodes.[target]
        head <- Some target
        sp

    member _.Merge(parents: StatepointId list, label: string, merged: IOverlayStore) : Statepoint =
        let sp = create parents label merged
        nodes.[sp.Id] <- sp
        head <- Some sp.Id
        sp

    /// Root-first ancestry along first parents.
    member this.Lineage(id: StatepointId) : Statepoint list =
        let rec walk current acc =
            match this.Get current with
            | None -> acc
            | Some sp ->
                match sp.Parents with
                | p :: _ -> walk p (sp :: acc)
                | [] -> sp :: acc

        walk id []

module Pillar =
    [<Literal>]
    let Id = "Statepoints"

    [<Literal>]
    let Stability = "Stable"

    [<Literal>]
    let Status = "Skeleton"
