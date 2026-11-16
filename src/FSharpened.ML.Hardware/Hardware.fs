namespace FSharpened.ML.Hardware

open FSharpened.Platform

type BackendClass =
    | TrainingBackend
    | InferenceBackend
    | KernelBackend

type Backend =
    { Id: string
      Class: BackendClass
      /// Empty list means every RID.
      Rids: string list
      Capabilities: Set<string> }

type TaskRequest =
    { Class: BackendClass
      Needs: Set<string> }

/// Invariant 8: every training, inference and kernel run routes through this resolver.
module Resolver =
    let private supportsRid rid (b: Backend) = List.isEmpty b.Rids || List.contains rid b.Rids

    /// Walks the fixed priority chain, keeping backends that match class, RID, needed
    /// capabilities and the availability probe. Deterministic: same inputs, same order.
    let resolve (identity: RuntimeIdentity) (chain: Backend list) (isAvailable: Backend -> bool) (task: TaskRequest) =
        chain
        |> List.filter (fun b ->
            b.Class = task.Class
            && supportsRid identity.Rid b
            && Set.isSubset task.Needs b.Capabilities
            && isAvailable b)

    /// Default chain, highest priority first. CPU entries are the guaranteed last resort.
    let defaultInferenceChain =
        [ { Id = "onnx-cuda"; Class = InferenceBackend; Rids = [ "win-x64"; "linux-x64" ]; Capabilities = set [ "fp16" ] }
          { Id = "onnx-directml"; Class = InferenceBackend; Rids = [ "win-x64"; "win-arm64" ]; Capabilities = set [ "fp16" ] }
          { Id = "onnx-rocm"; Class = InferenceBackend; Rids = [ "linux-x64" ]; Capabilities = set [ "fp16" ] }
          { Id = "onnx-openvino"; Class = InferenceBackend; Rids = [ "win-x64"; "linux-x64" ]; Capabilities = Set.empty }
          { Id = "onnx-coreml"; Class = InferenceBackend; Rids = [ "osx-arm64"; "osx-x64"; "ios-arm64" ]; Capabilities = Set.empty }
          { Id = "onnx-cpu"; Class = InferenceBackend; Rids = []; Capabilities = Set.empty } ]

module Pillar =
    [<Literal>]
    let Id = "ML.Hardware"

    [<Literal>]
    let Stability = "Preview"

    [<Literal>]
    let Status = "Skeleton"
