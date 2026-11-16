module FSharpened.Skeleton.Tests.SkeletonTests

open System
open Xunit
open FSharpened.Platform
open FSharpened.Statepoints
open FSharpened.Effects
open FSharpened.Models
open FSharpened.ML.Hardware

let pillarIds =
    [ FSharpened.Platform.Pillar.Id
      FSharpened.Statepoints.Pillar.Id
      FSharpened.Semantic.Pillar.Id
      FSharpened.CompilerBridge.Pillar.Id
      FSharpened.Diagnostics.Pillar.Id
      FSharpened.Telemetry.Pillar.Id
      FSharpened.Analyzers.Pillar.Id
      FSharpened.Tooling.Pillar.Id
      FSharpened.Build.Pillar.Id
      FSharpened.LanguageExtensions.Pillar.Id
      FSharpened.Effects.Pillar.Id
      FSharpened.Serialization.Pillar.Id
      FSharpened.Quality.Pillar.Id
      FSharpened.Desktop.Pillar.Id
      FSharpened.Debugging.Pillar.Id
      FSharpened.Refactoring.Pillar.Id
      FSharpened.Testing.Pillar.Id
      FSharpened.Data.Pillar.Id
      FSharpened.Security.Pillar.Id
      FSharpened.Architecture.Pillar.Id
      FSharpened.Models.Pillar.Id
      FSharpened.ML.Portability.Pillar.Id
      FSharpened.ML.Hardware.Pillar.Id
      FSharpened.ML.Authoring.Pillar.Id
      FSharpened.ML.Data.Pillar.Id
      FSharpened.ML.Experiments.Pillar.Id
      FSharpened.ML.Registry.Pillar.Id
      FSharpened.ML.Serving.Pillar.Id ]

[<Fact>]
let ``every pillar has a unique non-empty id`` () =
    Assert.All(pillarIds, fun id -> Assert.False(String.IsNullOrWhiteSpace id))
    Assert.Equal(pillarIds.Length, (List.distinct pillarIds).Length)

[<Fact>]
let ``runtime identity reports a known rid`` () =
    let id = RuntimeIdentity.detect ()
    Assert.False(String.IsNullOrWhiteSpace id.Rid)
    Assert.Contains("-", id.Rid)

[<Fact>]
let ``overlay fork isolates writes from the parent`` () =
    let parent = InMemoryOverlay() :> IOverlayStore
    parent.Put("k", [| 1uy |])
    let child = parent.Fork()
    child.Put("k", [| 2uy |])
    Assert.Equal<byte[]>([| 1uy |], (parent.Get "k").Value)
    Assert.Equal<byte[]>([| 2uy |], (child.Get "k").Value)

[<Fact>]
let ``statepoint fork never mutates its source`` () =
    let g = StatepointGraph()
    let root = InMemoryOverlay() :> IOverlayStore
    root.Put("a", [| 1uy |])
    let s1 = g.Snapshot("base", root)
    let f = g.Fork(s1.Id, "experiment")
    f.Overlay.Put("a", [| 9uy |])
    Assert.Equal<byte[]>([| 1uy |], (s1.Overlay.Get "a").Value)
    Assert.Equal(2, g.Count)

[<Fact>]
let ``rollback moves head to the target`` () =
    let g = StatepointGraph()
    let ov = InMemoryOverlay() :> IOverlayStore
    let s1 = g.Snapshot("one", ov)
    let _ = g.Snapshot("two", ov)
    g.Rollback(s1.Id) |> ignore
    Assert.Equal(Some s1.Id, g.Head)

[<Fact>]
let ``effect map and bind short-circuit on failure`` () =
    let e: Effect<int, string> = Effect.fail "boom"
    let r = e |> Effect.map ((+) 1) |> Effect.bind (fun x -> Effect.succeed (x * 2))

    match r with
    | Failure m -> Assert.Equal("boom", m)
    | _ -> failwith "expected failure"

[<Fact>]
let ``fallback stack resolves at the first layer that succeeds`` () =
    let outcome =
        Fallback.run
            "demo"
            [ SpecialisedModel, (fun () -> Error "model crashed")
              SiblingModel, (fun () -> failwith "sibling threw")
              SymbolicRules, (fun () -> Ok 42) ]

    match outcome with
    | Resolved(layer, v) ->
        Assert.Equal(SymbolicRules, layer)
        Assert.Equal(42, v)
    | Deferred _ -> failwith "expected resolution"

[<Fact>]
let ``fallback stack defers with a resume token instead of failing`` () =
    let outcome = Fallback.run "demo" [ SpecialisedModel, (fun () -> Error "x") ]

    match outcome with
    | Deferred r -> Assert.False(String.IsNullOrEmpty r.ResumeToken)
    | Resolved _ -> failwith "expected deferral"

[<Fact>]
let ``resolver always ends in a cpu backend`` () =
    let id = RuntimeIdentity.detect ()

    let task =
        { Class = InferenceBackend
          Needs = Set.empty }
    // Probe says only the CPU backend is actually available on this machine.
    let resolved =
        Resolver.resolve id Resolver.defaultInferenceChain (fun b -> b.Id = "onnx-cpu") task

    Assert.Equal<string list>([ "onnx-cpu" ], resolved |> List.map (fun b -> b.Id))
