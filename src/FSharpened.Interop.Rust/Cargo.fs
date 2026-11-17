namespace FSharpened.Interop.Rust

open System
open System.IO
open System.Text.Json

type BuildProfile =
    | Debug
    | Release

type CargoTarget =
    { TargetName: string
      Kinds: string list
      CrateTypes: string list }

type CargoPackage =
    { PackageName: string
      Version: string
      ManifestPath: string
      Edition: string
      RustVersion: string option
      Targets: CargoTarget list }

type CargoMetadata =
    { WorkspaceRoot: string
      TargetDirectory: string
      Packages: CargoPackage list }

type BuildOptions =
    {
        Manifest: string
        Rid: string
        Profile: BuildProfile
        /// Pass --locked, so a build never edits Cargo.lock and a stale lock file fails loudly.
        Locked: bool
        /// Pass --target-dir. When None, cargo's own target directory (reported by cargo metadata) is used.
        TargetDir: string option
    }

type BuiltLibrary =
    { Package: string
      Stem: string
      Triple: string
      Profile: BuildProfile
      Path: string }

/// Wraps cargo (ADR-0006): this module builds command lines and reads cargo's own JSON. It resolves nothing itself.
module Cargo =
    let profileDirectory (profile: BuildProfile) : string =
        match profile with
        | Debug -> "debug"
        | Release -> "release"

    /// "cargo metadata" without dependencies. It reads manifests only: it runs no build script and no proc macro.
    let metadataPlan (manifest: string) : CommandPlan =
        Command.make
            "cargo"
            [ "metadata"
              "--format-version"
              "1"
              "--no-deps"
              "--manifest-path"
              manifest ]

    let buildPlan (options: BuildOptions) : Result<CommandPlan, string> =
        match Targets.tryOfRid options.Rid with
        | None ->
            let known = Targets.all |> List.map (fun t -> t.Rid) |> String.concat ", "
            Error("No Rust target is known for RID '" + options.Rid + "'. Known RIDs: " + known)
        | Some target ->
            let args =
                [ "build"
                  "--manifest-path"
                  options.Manifest
                  "--target"
                  target.Triple
                  "--color"
                  "never" ]
                @ (if options.Profile = Release then [ "--release" ] else [])
                @ (if options.Locked then [ "--locked" ] else [])
                @ (match options.TargetDir with
                   | Some dir -> [ "--target-dir"; dir ]
                   | None -> [])

            Ok(Command.make "cargo" args)

    let private stringProperty (element: JsonElement) (name: string) : string option =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None

    let private stringList (element: JsonElement) (name: string) : string list =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Array ->
            [ for item in value.EnumerateArray() do
                  if item.ValueKind = JsonValueKind.String then
                      item.GetString() ]
        | _ -> []

    let private objects (element: JsonElement) (name: string) : JsonElement list =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Array -> [ for item in value.EnumerateArray() -> item ]
        | _ -> []

    let private parseTarget (element: JsonElement) : CargoTarget =
        { TargetName = stringProperty element "name" |> Option.defaultValue ""
          Kinds = stringList element "kind"
          CrateTypes = stringList element "crate_types" }

    let private parsePackage (element: JsonElement) : CargoPackage =
        { PackageName = stringProperty element "name" |> Option.defaultValue ""
          Version = stringProperty element "version" |> Option.defaultValue ""
          ManifestPath = stringProperty element "manifest_path" |> Option.defaultValue ""
          Edition = stringProperty element "edition" |> Option.defaultValue ""
          RustVersion = stringProperty element "rust_version"
          Targets = objects element "targets" |> List.map parseTarget }

    /// Reads the JSON that "cargo metadata --format-version 1" prints. System.Text.Json's document reader is used
    /// on purpose: no reflection, so it is safe under NativeAOT (ADR-0014).
    let parseMetadata (json: string) : Result<CargoMetadata, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            match stringProperty root "target_directory", stringProperty root "workspace_root" with
            | Some targetDirectory, Some workspaceRoot ->
                Ok
                    { WorkspaceRoot = workspaceRoot
                      TargetDirectory = targetDirectory
                      Packages = objects root "packages" |> List.map parsePackage }
            | _ -> Error "cargo metadata output has no target_directory or workspace_root."
        with :? JsonException as e ->
            Error("cargo metadata output is not valid JSON: " + e.Message)

    /// Every library target that builds a shared library, with the package that owns it.
    let cdylibTargets (metadata: CargoMetadata) : (CargoPackage * CargoTarget) list =
        [ for package in metadata.Packages do
              for target in package.Targets do
                  if List.contains "cdylib" target.CrateTypes then
                      package, target ]

    /// Where cargo writes the shared library for the target and profile, below the given target directory.
    let libraryPath (targetDir: string) (target: RustTarget) (profile: BuildProfile) (stem: string) : string option =
        Targets.libraryFileName target.Os Dynamic stem
        |> Option.map (fun file -> Path.Combine(targetDir, target.Triple, profileDirectory profile, file))

    let private tailOf (text: string) =
        let all =
            text.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.Trim() <> "")

        String.Join("\n", all |> Array.skip (max 0 (all.Length - 15)))

    let private chooseTarget (wanted: string option) (candidates: (CargoPackage * CargoTarget) list) =
        let matching =
            match wanted with
            | Some name ->
                candidates
                |> List.filter (fun (p, t) -> p.PackageName = name || t.TargetName = name)
            | None -> candidates

        match matching with
        | [ single ] -> Ok single
        | [] when candidates.IsEmpty ->
            Error "No cdylib target found. Add: [lib] crate-type = [\"cdylib\"] to the manifest."
        | [] -> Error "No cdylib target matches the requested name."
        | many ->
            let names = many |> List.map (fun (p, _) -> p.PackageName) |> String.concat ", "
            Error("More than one cdylib target; name one of: " + names)

    /// Reads the manifest with cargo metadata, builds the single cdylib for the RID, and returns the library path.
    /// The runner and the existence check are parameters so the whole flow is testable without cargo or a disk.
    let build
        (run: CommandPlan -> RunResult)
        (exists: string -> bool)
        (options: BuildOptions)
        (wanted: string option)
        : Result<BuiltLibrary, string> =
        let describe (plan: CommandPlan) (r: RunResult) =
            Command.render plan
            + " failed (exit "
            + string r.ExitCode
            + "): "
            + tailOf (if r.Stderr <> "" then r.Stderr else r.Stdout)

        let metadataCommand = metadataPlan options.Manifest
        let meta = run metadataCommand

        if meta.ExitCode <> 0 then
            Error(describe metadataCommand meta)
        else
            parseMetadata meta.Stdout
            |> Result.bind (fun metadata ->
                chooseTarget wanted (cdylibTargets metadata)
                |> Result.bind (fun (package, target) ->
                    buildPlan options
                    |> Result.bind (fun buildCommand ->
                        let built = run buildCommand

                        if built.ExitCode <> 0 then
                            Error(describe buildCommand built)
                        else
                            let rust = (Targets.tryOfRid options.Rid).Value
                            let targetDir = options.TargetDir |> Option.defaultValue metadata.TargetDirectory

                            match libraryPath targetDir rust options.Profile target.TargetName with
                            | None -> Error("RID " + options.Rid + " has no shared library file name.")
                            | Some path when not (exists path) ->
                                Error("cargo reported success but the library is missing: " + path)
                            | Some path ->
                                Ok
                                    { Package = package.PackageName
                                      Stem = target.TargetName
                                      Triple = rust.Triple
                                      Profile = options.Profile
                                      Path = path })))
