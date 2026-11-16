namespace FSharpened.Ide

open FSharpened.Platform
open FSharpened.Statepoints

type TimelineEntry =
    { Id: string
      Label: string
      Parent: string option
      Branch: string
      CreatedMs: float
      IsHead: bool }

/// Editor history on top of the shared statepoint graph: snapshots on run/save, restore = rollback, branch = fork.
/// No private checkpointing (Invariant 5): everything here is FSharpened.Statepoints.
type Timeline(start: Workspace) =
    let graph = StatepointGraph()
    let branches = System.Collections.Generic.Dictionary<string, string>()
    let order = ResizeArray<StatepointId>()
    let root = InMemoryOverlay() :> IOverlayStore

    let idText (StatepointId g) = string g

    let find (id: string) =
        order |> Seq.tryFind (fun sid -> idText sid = id)

    let record (sp: Statepoint) (branch: string) =
        order.Add sp.Id
        branches.[idText sp.Id] <- branch
        sp

    do
        Workspace.write root start
        record (graph.Snapshot("Start", root)) "main" |> ignore

    member _.Count = graph.Count

    member _.HeadId =
        match graph.Head with
        | Some h -> idText h
        | None -> ""

    /// New statepoint on the current branch holding `w`.
    member _.Snapshot(label: string, w: Workspace) : string =
        let parentBranch =
            match graph.Head with
            | Some h -> branches.[idText h]
            | None -> "main"

        let overlay =
            match graph.Head with
            | Some h -> (graph.Get h).Value.Overlay.Fork()
            | None -> InMemoryOverlay() :> IOverlayStore

        Workspace.write overlay w
        idText (record (graph.Snapshot(label, overlay)) parentBranch).Id

    /// Moves head back to a statepoint and returns the workspace it holds.
    member _.Restore(id: string) : Workspace option =
        match find id with
        | None -> None
        | Some sid ->
            let sp = graph.Rollback sid
            Some(Workspace.read sp.Overlay)

    /// Isolated branch from a statepoint (source is never mutated).
    member _.Fork(id: string, branchName: string) : string option =
        match find id with
        | None -> None
        | Some sid ->
            let name =
                if branchName.Trim() = "" then
                    "branch"
                else
                    branchName.Trim()

            Some(idText (record (graph.Fork(sid, "Fork: " + name)) name).Id)

    member _.Read(id: string) : Workspace option =
        find id
        |> Option.bind graph.Get
        |> Option.map (fun sp -> Workspace.read sp.Overlay)

    member _.Entries: TimelineEntry list =
        let head = graph.Head

        [ for sid in order do
              match graph.Get sid with
              | Some sp ->
                  yield
                      { Id = idText sid
                        Label = sp.Label
                        Parent = sp.Parents |> List.tryHead |> Option.map idText
                        Branch = branches.[idText sid]
                        CreatedMs = float (sp.Created.ToUnixTimeMilliseconds())
                        IsHead = (head = Some sid) }
              | None -> () ]
