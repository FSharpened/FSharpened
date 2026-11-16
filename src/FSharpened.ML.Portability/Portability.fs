namespace FSharpened.ML.Portability

/// Invariant 10: no ML component may touch a fragile framework except through a contract.
type StabilityTier =
    | StableTier
    | PreviewTier
    | ExperimentalTier
    | Unavailable

type FrameworkContract =
    {
        Framework: string
        /// RID -> tier. Absent RID means Unavailable.
        Rids: Map<string, StabilityTier>
        SupportsTraining: bool
        SupportsInference: bool
        /// Exact version used for training; recorded in every artifact.
        PinnedVersion: string
    }

module FrameworkContract =
    let tierFor (rid: string) (c: FrameworkContract) =
        match Map.tryFind rid c.Rids with
        | Some t -> t
        | None -> Unavailable

    let usable (rid: string) (c: FrameworkContract) =
        match tierFor rid c with
        | Unavailable -> false
        | _ -> true

module Builtin =
    let torchSharp =
        { Framework = "TorchSharp"
          Rids =
            Map.ofList
                [ "win-x64", PreviewTier
                  "linux-x64", PreviewTier
                  "osx-arm64", PreviewTier
                  "linux-arm64", ExperimentalTier ]
          SupportsTraining = true
          SupportsInference = true
          PinnedVersion = "unpinned" }

    let onnxRuntime =
        { Framework = "ONNX Runtime"
          Rids =
            Map.ofList
                [ "win-x64", StableTier
                  "win-arm64", StableTier
                  "linux-x64", StableTier
                  "linux-arm64", StableTier
                  "osx-x64", StableTier
                  "osx-arm64", StableTier ]
          SupportsTraining = false
          SupportsInference = true
          PinnedVersion = "unpinned" }

    let all = [ torchSharp; onnxRuntime ]

module Pillar =
    [<Literal>]
    let Id = "ML.Portability"

    [<Literal>]
    let Stability = "Preview"

    [<Literal>]
    let Status = "Skeleton"
