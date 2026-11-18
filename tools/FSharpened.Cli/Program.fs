module FSharpened.Cli.Program

open System
open FSharpened.Platform
open FSharpened.Interop.Rust
open FSharpened.Analyzers

// printf-family functions use reflection that NativeAOT cannot compile (see gap K, ADR-0014).
// Use Console + string concatenation in anything that must run under PublishAot.
let private out (s: string) = Console.Out.WriteLine s
let private err (s: string) = Console.Error.WriteLine s

let commands =
    [ "init", "Scaffold a project with analyzers, telemetry, Fantomas and a CI stub"
      "watch", "Warm compiler plus statepoint-backed hot reload"
      "doctor", "Structured toolchain report (add --wsl for the WSL interop check)"
      "analyze", "Policy checks with structured diagnostics (--format json, --ci makes it a gate)"
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


let private toolName =
    "fsh "
    + typeof<FSharpened.Platform.RuntimeIdentity>.Assembly.GetName().Version.ToString()

let private writeUtf8 (s: string) =
    // Bytes, not Console.Out: the report must be UTF-8 without a BOM whatever the console code page is.
    use stdout = Console.OpenStandardOutput()
    let bytes = Text.UTF8Encoding(false).GetBytes s
    stdout.Write(bytes, 0, bytes.Length)

let private sourceFiles (paths: string list) =
    paths
    |> List.collect (fun p ->
        if IO.Directory.Exists p then
            IO.Directory.EnumerateFiles(p, "*.*", IO.SearchOption.AllDirectories)
            |> Seq.filter (fun f ->
                (f.EndsWith ".fs" || f.EndsWith ".fsi" || f.EndsWith ".fsx")
                && (let n = f.Replace('\\', '/') in not (n.Contains "/obj/" || n.Contains "/bin/")))
            |> Seq.sort
            |> List.ofSeq
        else
            [ p ])

/// `fsh analyze [--format json|text] [--ci] <file-or-folder>...`. Exit codes: 0 clean (or findings without --ci),
/// 1 findings with --ci, 2 bad usage or an unreadable path.
let analyze (args: string list) =
    let mutable format = "text"
    let mutable ci = false
    let mutable paths = []
    let mutable bad = None
    let mutable rest = args

    while not rest.IsEmpty do
        match rest with
        | "--format" :: f :: tail when f = "json" || f = "text" ->
            format <- f
            rest <- tail
        | "--format" :: _ ->
            bad <- Some "--format takes json or text"
            rest <- []
        | "--ci" :: tail ->
            ci <- true
            rest <- tail
        | p :: tail when not (p.StartsWith "--") ->
            paths <- paths @ [ p ]
            rest <- tail
        | other :: _ ->
            bad <- Some("unknown option " + other)
            rest <- []
        | [] -> ()

    match bad with
    | Some m ->
        err ("fsh analyze: " + m)
        2
    | None when paths.IsEmpty ->
        err "usage: fsh analyze [--format json|text] [--ci] <file-or-folder>..."
        2
    | None ->
        match paths |> List.tryFind (fun p -> not (IO.File.Exists p || IO.Directory.Exists p)) with
        | Some missing ->
            err ("fsh analyze: not found: " + missing)
            2
        | None ->
            let findings =
                sourceFiles paths
                |> List.collect (fun f -> PolicyRules.check f (IO.File.ReadAllText f))

            if format = "json" then
                writeUtf8 (FSharpened.Diagnostics.DiagnosticReport.toJson toolName findings)
            else
                out (FSharpened.Diagnostics.DiagnosticReport.toText findings)

            let failing =
                findings
                |> List.exists (fun f ->
                    f.Diagnostic.Severity = FSharpened.Diagnostics.Warning
                    || f.Diagnostic.Severity = FSharpened.Diagnostics.Severity.Error)

            if ci && failing then 1 else 0

[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | []
    | [ "--help" ]
    | [ "-h" ] ->
        printHelp ()
        0
    | "doctor" :: _ -> doctor ()
    | "analyze" :: rest -> analyze rest
    | cmd :: _ when commands |> List.exists (fun (n, _) -> n = cmd) ->
        err ("fsh " + cmd + ": not implemented yet (skeleton). See docs/packages/.")
        2
    | cmd :: _ ->
        err ("fsh: unknown command '" + cmd + "'")
        1
