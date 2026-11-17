namespace FSharpened.Interop.Rust

open System

type RustToolchain =
    {
        CargoVersion: string
        RustcVersion: string
        /// The triple rustc reports as its own, for example x86_64-pc-windows-msvc.
        HostTriple: string
        /// The pillar's target for that triple, when it has one.
        Host: RustTarget option
    }

/// Reports what Rust is installed. It never installs, updates or selects a toolchain: that stays with rustup.
module Toolchain =
    let private lines (text: string) =
        text.Replace("\r\n", "\n").Split('\n') |> Array.toList

    /// Reads "1.99.0" or "1.99.0-nightly" as (1, 99, 0). Pre-release and build suffixes are ignored.
    let tryParseVersion (text: string) : (int * int * int) option =
        let core = text.Trim().Split('-', '+').[0]

        match core.Split('.') |> Array.map Int32.TryParse |> Array.toList with
        | [ (true, major); (true, minor); (true, patch) ] -> Some(major, minor, patch)
        | _ -> None

    /// The oldest rustc that accepts the edition, or None for an edition that does not exist.
    let minimumRustcFor (edition: string) : (int * int * int) option =
        match edition with
        | "2015" -> Some(1, 0, 0)
        | "2018" -> Some(1, 31, 0)
        | "2021" -> Some(1, 56, 0)
        | "2024" -> Some(1, 85, 0)
        | _ -> None

    let supportsEdition (rustcVersion: string) (edition: string) : bool =
        match tryParseVersion rustcVersion, minimumRustcFor edition with
        | Some have, Some need -> have >= need
        | _ -> false

    /// "cargo 1.99.0 (5f94df478 2026-08-27)" gives "1.99.0".
    let parseCargoVersion (output: string) : string option =
        lines output
        |> List.tryPick (fun line ->
            match line.Trim().Split(' ') with
            | [| "cargo"; version |]
            | [| "cargo"; version; _; _ |] when (tryParseVersion version).IsSome -> Some version
            | _ -> None)

    /// Reads the release and host lines of "rustc -vV".
    let parseRustcVerbose (output: string) : (string * string) option =
        let field name =
            lines output
            |> List.tryPick (fun line ->
                let prefix = name + ":"

                if line.StartsWith(prefix, StringComparison.Ordinal) then
                    Some(line.Substring(prefix.Length).Trim())
                else
                    None)

        match field "release", field "host" with
        | Some release, Some host when release <> "" && host <> "" -> Some(release, host)
        | _ -> None

    let missingMessage =
        "Rust is not installed or not on PATH. Install it with rustup (https://rustup.rs); FSharpened never installs toolchains."

    /// Runs "cargo --version" and "rustc -vV" through the supplied runner.
    let probe (run: CommandPlan -> RunResult) : Result<RustToolchain, string> =
        let cargo = run (Command.make "cargo" [ "--version" ])
        let rustc = run (Command.make "rustc" [ "-vV" ])

        match parseCargoVersion cargo.Stdout, parseRustcVerbose rustc.Stdout with
        | Some cargoVersion, Some(release, host) when cargo.ExitCode = 0 && rustc.ExitCode = 0 ->
            Ok
                { CargoVersion = cargoVersion
                  RustcVersion = release
                  HostTriple = host
                  Host = Targets.tryOfTriple host }
        | _ when cargo.ExitCode <> 0 || rustc.ExitCode <> 0 -> Error missingMessage
        | _ -> Error "cargo or rustc ran but printed output this version of FSharpened does not understand."
