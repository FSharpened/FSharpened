module FSharpened.Cli.Program

open FSharpened.Platform

let commands =
    [ "init", "Scaffold a project with analyzers, telemetry, Fantomas and a CI stub"
      "watch", "Warm compiler plus statepoint-backed hot reload"
      "doctor", "Structured toolchain report (add --wsl for the WSL interop check)"
      "analyze", "Run the curated analyzer set (--ci makes it a gate)"
      "quality", "F#-aware complexity, coverage, CRAP and mutation gates"
      "explain", "Explain an F# compiler error code"
      "refactor", "Type-directed refactorings"
      "mutate", "AST-level mutation testing"
      "build", "Build with the per-file compiler cache (--cached)"
      "sign", "Sigstore-sign NuGet and model artifacts"
      "architecture", "Architecture fitness checks (check)"
      "new", "Templates (desktop, solution)" ]

let printHelp () =
    printfn "fsh (FSharpened) - Sharpen your existing F#."
    printfn ""

    for (name, text) in commands do
        printfn "  %-13s %s" name text

let doctor () =
    let id = RuntimeIdentity.detect ()
    printfn "rid:        %s" id.Rid
    printfn "aot:        %b" id.IsAot
    printfn "container:  %b" id.IsContainer
    printfn "wsl:        %b" id.IsWsl
    0

[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | [] | [ "--help" ] | [ "-h" ] ->
        printHelp ()
        0
    | "doctor" :: _ -> doctor ()
    | cmd :: _ when commands |> List.exists (fun (n, _) -> n = cmd) ->
        eprintfn "fsh %s: not implemented yet (skeleton). See docs/packages/." cmd
        2
    | cmd :: _ ->
        eprintfn "fsh: unknown command '%s'" cmd
        1
