module FSharpened.Cli.Program

open System
open FSharpened.Platform
open FSharpened.Interop.Rust

// printf-family functions use reflection that NativeAOT cannot compile (see gap K, ADR-0014).
// Use Console + string concatenation in anything that must run under PublishAot.
let private out (s: string) = Console.Out.WriteLine s
let private err (s: string) = Console.Error.WriteLine s

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
    out "fsh (FSharpened) - Sharpen your existing F#."
    out ""

    for (name, text) in commands do
        out ("  " + name.PadRight 13 + " " + text)

/// Rust is optional: only the Interop.Rust pillar needs it, so a missing toolchain is information, not a failure.
let private rustLine () =
    match Toolchain.probe (Command.run 10000) with
    | Ok tc ->
        let target =
            match tc.Host with
            | Some t -> t.Rid
            | None -> "no FSharpened target"

        "cargo "
        + tc.CargoVersion
        + ", rustc "
        + tc.RustcVersion
        + ", host "
        + tc.HostTriple
        + " ("
        + target
        + ")"
    | Error _ -> "not found (optional; only the Interop.Rust pillar needs it)"

let doctor () =
    let id = RuntimeIdentity.detect ()
    out ("rid:        " + id.Rid)
    out ("aot:        " + string id.IsAot)
    out ("container:  " + string id.IsContainer)
    out ("wsl:        " + string id.IsWsl)
    out ("rust:       " + rustLine ())
    0

[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | []
    | [ "--help" ]
    | [ "-h" ] ->
        printHelp ()
        0
    | "doctor" :: _ -> doctor ()
    | cmd :: _ when commands |> List.exists (fun (n, _) -> n = cmd) ->
        err ("fsh " + cmd + ": not implemented yet (skeleton). See docs/packages/.")
        2
    | cmd :: _ ->
        err ("fsh: unknown command '" + cmd + "'")
        1
