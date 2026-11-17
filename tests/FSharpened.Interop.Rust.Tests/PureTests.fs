module FSharpened.Interop.Rust.Tests.PureTests

// Contract tests that need neither Rust nor a disk: they pin the mapping tables, the search plan, the ABI
// conventions, the command lines and the parsers. InteropTests.fs then proves the same conventions against a
// real cargo build.

open System
open System.IO
open System.Runtime.InteropServices
open Xunit
open FSharpened.Platform
open FSharpened.Interop.Rust

let private identity rid os arch : RuntimeIdentity =
    { Rid = rid
      Os = os
      Arch = arch
      IsAot = false
      IsContainer = false
      IsWsl = false }

// ---- target mapping -------------------------------------------------------------------------------

[<Fact>]
let ``the six matrix RIDs all have a target`` () =
    let rids = Targets.matrix |> List.map (fun t -> t.Rid) |> List.sort

    Assert.Equal<string list>([ "linux-arm64"; "linux-x64"; "osx-arm64"; "osx-x64"; "win-arm64"; "win-x64" ], rids)

[<Fact>]
let ``rids and triples are unique and round-trip`` () =
    Assert.Equal(Targets.all.Length, (Targets.all |> List.map (fun t -> t.Rid) |> List.distinct).Length)
    Assert.Equal(Targets.all.Length, (Targets.all |> List.map (fun t -> t.Triple) |> List.distinct).Length)

    for t in Targets.all do
        Assert.Equal(Some t, Targets.tryOfRid t.Rid)
        Assert.Equal(Some t, Targets.tryOfTriple t.Triple)

[<Fact>]
let ``every triple agrees with its rid on cpu and operating system`` () =
    for t in Targets.all do
        let cpu =
            match t.Arch with
            | X64 -> "x86_64"
            | Arm64 -> "aarch64"
            | OtherArch _ -> failwith "no other architectures are mapped"

        let osMarker, ridPrefix =
            match t.Os with
            | Windows -> "windows", "win-"
            | Linux -> "linux", "linux-"
            | MacOs -> "apple-darwin", "osx-"
            | _ -> failwith "no other operating systems are mapped"

        Assert.StartsWith(cpu + "-", t.Triple)
        Assert.Contains(osMarker, t.Triple)
        Assert.StartsWith(ridPrefix, t.Rid)

        Assert.EndsWith(
            (match t.Arch with
             | X64 -> "x64"
             | _ -> "arm64"),
            t.Rid
        )

[<Fact>]
let ``musl is a different target from glibc`` () =
    Assert.Equal(Some "x86_64-unknown-linux-musl", Targets.tryOfRid "linux-musl-x64" |> Option.map (fun t -> t.Triple))
    Assert.Equal(Some "x86_64-unknown-linux-gnu", Targets.tryOfRid "linux-x64" |> Option.map (fun t -> t.Triple))
    Assert.False((Targets.tryOfRid "linux-musl-arm64").Value.InMatrix)

[<Fact>]
let ``unknown rids and triples are None, never guessed`` () =
    Assert.Equal(None, Targets.tryOfRid "freebsd-x64")
    Assert.Equal(None, Targets.tryOfTriple "wasm32-unknown-unknown")
    Assert.Equal(None, Targets.tryOfIdentity (identity "unknown-x64" UnknownOs X64))

[<Fact>]
let ``library file names follow each operating system's convention`` () =
    let name os kind =
        Targets.libraryFileName os kind "fsh_demo"

    Assert.Equal(Some "fsh_demo.dll", name Windows Dynamic)
    Assert.Equal(Some "fsh_demo.lib", name Windows Static)
    Assert.Equal(Some "libfsh_demo.so", name Linux Dynamic)
    Assert.Equal(Some "libfsh_demo.dylib", name MacOs Dynamic)
    Assert.Equal(Some "libfsh_demo.a", name MacOs Static)
    Assert.Equal(Some "libfsh_demo.so", name Android Dynamic)
    // iOS forbids loading a dynamic library from an app bundle path this pillar can promise; a static archive only
    Assert.Equal(None, name Ios Dynamic)
    Assert.Equal(Some "libfsh_demo.a", name Ios Static)
    Assert.Equal(None, name UnknownOs Dynamic)

[<Fact>]
let ``cargo turns hyphens in crate names into underscores`` () =
    Assert.Equal("fsh_interop_example", Targets.libraryStem "fsh-interop-example")
    Assert.Equal("plain", Targets.libraryStem "plain")

[<Fact>]
let ``native assets live under runtimes rid native with forward slashes`` () =
    Assert.Equal("runtimes/linux-arm64/native", Targets.nativeDirectory "linux-arm64")
    Assert.Equal("runtimes/win-x64/native/x.dll", Targets.nativeRelativePath "win-x64" "x.dll")

// ---- resolver -------------------------------------------------------------------------------------

[<Fact>]
let ``the search plan tries the rid folder before the root, root by root`` () =
    let plan = NativeResolver.searchPlan "linux-x64" [ "a"; "b" ] "libx.so"

    Assert.Equal<string list>(
        [ Path.Combine("a", "runtimes", "linux-x64", "native", "libx.so")
          Path.Combine("a", "libx.so")
          Path.Combine("b", "runtimes", "linux-x64", "native", "libx.so")
          Path.Combine("b", "libx.so") ],
        plan
    )

[<Fact>]
let ``the search plan never contains the current directory or PATH`` () =
    let plan = NativeResolver.searchPlan "win-x64" [ "app" ] "x.dll"
    Assert.All(plan, fun p -> Assert.StartsWith("app", p))
    Assert.Empty(NativeResolver.searchPlan "win-x64" [] "x.dll")

[<Fact>]
let ``a plan for the identity uses its rid and file name, or is None when unmapped`` () =
    let plan =
        NativeResolver.planFor (identity "osx-arm64" MacOs Arm64) [ "root" ] "fsh_demo"

    Assert.Equal(
        Some
            [ Path.Combine("root", "runtimes", "osx-arm64", "native", "libfsh_demo.dylib")
              Path.Combine("root", "libfsh_demo.dylib") ],
        plan
    )

    Assert.Equal(None, NativeResolver.planFor (identity "unknown-x64" UnknownOs X64) [ "root" ] "fsh_demo")

[<Fact>]
let ``resolve picks the first existing path and reports every path tried on a miss`` () =
    let plan = [ "one"; "two"; "three" ]

    match NativeResolver.resolve (fun p -> p = "two" || p = "three") plan with
    | Found p -> Assert.Equal("two", p)
    | NotFound _ -> failwith "expected a hit"

    match NativeResolver.resolve (fun _ -> false) plan with
    | NotFound tried ->
        Assert.Equal<string list>(plan, tried)
        let message = NativeResolver.describeMiss "demo" tried
        Assert.Contains("'demo'", message)
        Assert.Contains("one; two; three", message)
    | Found _ -> failwith "expected a miss"

// ---- ABI conventions ------------------------------------------------------------------------------

[<Fact>]
let ``status codes round-trip and unknown codes are kept`` () =
    for code in 0..5 do
        Assert.Equal(code, AbiStatus.code (AbiStatus.ofCode code))

    Assert.Equal(Success, AbiStatus.ofCode 0)
    Assert.Equal(Panicked, AbiStatus.ofCode 4)
    Assert.Equal(Unknown 99, AbiStatus.ofCode 99)
    Assert.Equal(99, AbiStatus.code (Unknown 99))
    Assert.Equal("unknown status 99", AbiStatus.name (Unknown 99))

[<Fact>]
let ``a successful call never reads the last error`` () =
    let result = Abi.check (fun () -> failwith "must not be called") 0
    Assert.Equal(Ok(), result)

[<Fact>]
let ``a failed call carries the library's message, or the status name when there is none`` () =
    match Abi.check (fun () -> Some "values must not be null") 1 with
    | Error e ->
        Assert.Equal(NullPointer, e.Status)
        Assert.Equal("values must not be null", e.Message)
    | Ok _ -> failwith "expected an error"

    match Abi.check (fun () -> None) 4 with
    | Error e -> Assert.Equal("panic", e.Message)
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``borrowed strings arrive as NUL-terminated UTF-8`` () =
    let text = "Ünïcode 🦀 日本語"

    let back =
        Utf8.withBorrowed text (fun p ->
            Assert.Equal(0uy, Marshal.ReadByte(p, Text.Encoding.UTF8.GetByteCount text))
            Marshal.PtrToStringUTF8 p)

    Assert.Equal(text, back)

[<Fact>]
let ``taking an owned string copies it and frees it exactly once`` () =
    let mutable freed = 0
    let p = Marshal.StringToCoTaskMemUTF8 "from rust"

    let taken =
        Utf8.takeOwned
            (fun q ->
                freed <- freed + 1
                Marshal.FreeCoTaskMem q)
            p

    Assert.Equal(Some "from rust", taken)
    Assert.Equal(1, freed)

[<Fact>]
let ``a null owned string is None and is not freed`` () =
    let result = Utf8.takeOwned (fun _ -> failwith "must not free null") 0n
    Assert.Equal(None, result)

// ---- command lines and parsers --------------------------------------------------------------------

[<Fact>]
let ``commands render as one readable line with quoting only where needed`` () =
    let plan =
        Command.make "cargo" [ "build"; "--manifest-path"; "C:\\my dir\\Cargo.toml" ]

    Assert.Equal("cargo build --manifest-path \"C:\\my dir\\Cargo.toml\"", Command.render plan)

[<Fact>]
let ``a command that cannot start is a result, not an exception`` () =
    let r = Command.run 5000 (Command.make "fsh-no-such-program-for-tests" [])
    Assert.Equal(-1, r.ExitCode)
    Assert.Contains("could not start", r.Stderr)
    Assert.False r.TimedOut

[<Fact>]
let ``versions parse with or without a pre-release suffix`` () =
    Assert.Equal(Some(1, 99, 0), Toolchain.tryParseVersion "1.99.0")
    Assert.Equal(Some(1, 100, 0), Toolchain.tryParseVersion "1.100.0-nightly")
    Assert.Equal(Some(1, 90, 2), Toolchain.tryParseVersion " 1.90.2+build.5 ")
    Assert.Equal(None, Toolchain.tryParseVersion "nightly")
    Assert.Equal(None, Toolchain.tryParseVersion "1.99")

[<Fact>]
let ``edition support follows the rustc that introduced it`` () =
    Assert.True(Toolchain.supportsEdition "1.85.0" "2024")
    Assert.False(Toolchain.supportsEdition "1.84.1" "2024")
    Assert.True(Toolchain.supportsEdition "1.56.0" "2021")
    Assert.False(Toolchain.supportsEdition "1.99.0" "2030")
    Assert.False(Toolchain.supportsEdition "garbage" "2021")

let private cargoOutput = "cargo 1.99.0 (5f94df478 2026-08-27)\n"

let private rustcOutput =
    "rustc 1.99.0 (b940084d7 2026-09-28)\nbinary: rustc\ncommit-hash: b940084d7eb6a299eb4bfeb8e34901bc051e7ac4\ncommit-date: 2026-09-28\nhost: x86_64-pc-windows-msvc\nrelease: 1.99.0\n"

[<Fact>]
let ``cargo and rustc version output is parsed, with CRLF too`` () =
    Assert.Equal(Some "1.99.0", Toolchain.parseCargoVersion cargoOutput)
    // the pre-release suffix is kept: it is information, and tryParseVersion ignores it when comparing
    Assert.Equal(Some "1.99.0-nightly", Toolchain.parseCargoVersion "cargo 1.99.0-nightly (abc 2026-09-01)\r\n")
    Assert.Equal(Some "1.99.0", Toolchain.parseCargoVersion "cargo 1.99.0\n")
    Assert.Equal(None, Toolchain.parseCargoVersion "error: no such command")

    Assert.Equal(
        Some("1.99.0", "x86_64-pc-windows-msvc"),
        Toolchain.parseRustcVerbose (rustcOutput.Replace("\n", "\r\n"))
    )

    Assert.Equal(None, Toolchain.parseRustcVerbose "rustc 1.99.0\n")

let private runner (cargo: RunResult) (rustc: RunResult) (plan: CommandPlan) =
    if plan.Program = "cargo" then cargo else rustc

let private ran stdout =
    { ExitCode = 0
      Stdout = stdout
      Stderr = ""
      TimedOut = false }

let private missing =
    { ExitCode = -1
      Stdout = ""
      Stderr = "could not start 'cargo': not found"
      TimedOut = false }

[<Fact>]
let ``probe reports versions and maps the host triple to a target`` () =
    match Toolchain.probe (runner (ran cargoOutput) (ran rustcOutput)) with
    | Ok tc ->
        Assert.Equal("1.99.0", tc.CargoVersion)
        Assert.Equal("x86_64-pc-windows-msvc", tc.HostTriple)
        Assert.Equal(Some "win-x64", tc.Host |> Option.map (fun t -> t.Rid))
    | Error e -> failwith e

[<Fact>]
let ``probe keeps an unmapped host triple but has no target for it`` () =
    let weird =
        rustcOutput.Replace("x86_64-pc-windows-msvc", "riscv64gc-unknown-linux-gnu")

    match Toolchain.probe (runner (ran cargoOutput) (ran weird)) with
    | Ok tc ->
        Assert.Equal("riscv64gc-unknown-linux-gnu", tc.HostTriple)
        Assert.Equal(None, tc.Host)
    | Error e -> failwith e

[<Fact>]
let ``probe says Rust is missing instead of throwing`` () =
    match Toolchain.probe (runner missing missing) with
    | Error e -> Assert.Contains("rustup", e)
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``probe rejects output it does not understand`` () =
    match Toolchain.probe (runner (ran "hello") (ran "world")) with
    | Error e -> Assert.Contains("does not understand", e)
    | Ok _ -> failwith "expected an error"

// ---- cargo ----------------------------------------------------------------------------------------

let private options =
    { Manifest = "native/Cargo.toml"
      Rid = "linux-arm64"
      Profile = Release
      Locked = true
      TargetDir = Some "out" }

[<Fact>]
let ``the build plan names the manifest, the triple, the profile and the lock policy`` () =
    match Cargo.buildPlan options with
    | Ok plan ->
        Assert.Equal("cargo", plan.Program)

        Assert.Equal<string list>(
            [ "build"
              "--manifest-path"
              "native/Cargo.toml"
              "--target"
              "aarch64-unknown-linux-gnu"
              "--color"
              "never"
              "--release"
              "--locked"
              "--target-dir"
              "out" ],
            plan.Args
        )
    | Error e -> failwith e

[<Fact>]
let ``a debug build without a lock or target dir omits those flags`` () =
    match
        Cargo.buildPlan
            { options with
                Profile = Debug
                Locked = false
                TargetDir = None }
    with
    | Ok plan ->
        Assert.DoesNotContain("--release", plan.Args)
        Assert.DoesNotContain("--locked", plan.Args)
        Assert.DoesNotContain("--target-dir", plan.Args)
    | Error e -> failwith e

[<Fact>]
let ``a build for an unknown rid lists the known ones`` () =
    match Cargo.buildPlan { options with Rid = "plan9-x64" } with
    | Error e ->
        Assert.Contains("plan9-x64", e)
        Assert.Contains("win-x64", e)
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``the metadata plan reads manifests only`` () =
    let plan = Cargo.metadataPlan "m/Cargo.toml"
    Assert.Contains("--no-deps", plan.Args)
    Assert.Equal("m/Cargo.toml", plan.Args.[plan.Args.Length - 1])

/// A trimmed "cargo metadata" document for a two-package workspace: one cdylib, one plain library.
let private metadataJson =
    """{
  "packages": [
    { "name": "fsh-interop-example", "version": "0.1.0", "edition": "2024", "rust_version": "1.85",
      "manifest_path": "/w/fsh-interop-example/Cargo.toml",
      "targets": [ { "kind": ["cdylib"], "crate_types": ["cdylib"], "name": "fsh_interop_example" } ] },
    { "name": "helper", "version": "2.0.0", "edition": "2021", "rust_version": null,
      "manifest_path": "/w/helper/Cargo.toml",
      "targets": [ { "kind": ["lib"], "crate_types": ["lib"], "name": "helper" },
                   { "kind": ["bin"], "crate_types": ["bin"], "name": "helper-cli" } ] }
  ],
  "workspace_members": [],
  "target_directory": "/w/target",
  "workspace_root": "/w",
  "version": 1
}"""

[<Fact>]
let ``cargo metadata is read into packages and targets`` () =
    match Cargo.parseMetadata metadataJson with
    | Ok m ->
        Assert.Equal("/w/target", m.TargetDirectory)
        Assert.Equal("/w", m.WorkspaceRoot)
        Assert.Equal(2, m.Packages.Length)
        let first = m.Packages.[0]
        Assert.Equal("fsh-interop-example", first.PackageName)
        Assert.Equal("2024", first.Edition)
        Assert.Equal(Some "1.85", first.RustVersion)
        Assert.Equal(None, m.Packages.[1].RustVersion)
        Assert.Equal<string list>([ "lib" ], m.Packages.[1].Targets.[0].CrateTypes)
    | Error e -> failwith e

[<Fact>]
let ``only cdylib targets are offered as bindable libraries`` () =
    let m = (Cargo.parseMetadata metadataJson) |> Result.defaultWith failwith

    match Cargo.cdylibTargets m with
    | [ (package, target) ] ->
        Assert.Equal("fsh-interop-example", package.PackageName)
        Assert.Equal("fsh_interop_example", target.TargetName)
    | other -> failwith ("expected one cdylib, got " + string other.Length)

[<Fact>]
let ``malformed or incomplete metadata is an error, not an exception`` () =
    Assert.True((Cargo.parseMetadata "not json").IsError)
    Assert.True((Cargo.parseMetadata "{}").IsError)
    Assert.True((Cargo.parseMetadata "").IsError)

[<Fact>]
let ``the library path is target dir, triple, profile, file name`` () =
    let win = (Targets.tryOfRid "win-x64").Value
    let mac = (Targets.tryOfRid "osx-arm64").Value

    Assert.Equal(
        Some(Path.Combine("t", "x86_64-pc-windows-msvc", "release", "fsh_demo.dll")),
        Cargo.libraryPath "t" win Release "fsh_demo"
    )

    Assert.Equal(
        Some(Path.Combine("t", "aarch64-apple-darwin", "debug", "libfsh_demo.dylib")),
        Cargo.libraryPath "t" mac Debug "fsh_demo"
    )

// ---- the whole build flow, with a fake runner and a fake disk -------------------------------------

let private ok stdout =
    { ExitCode = 0
      Stdout = stdout
      Stderr = ""
      TimedOut = false }

let private fail stderr =
    { ExitCode = 101
      Stdout = ""
      Stderr = stderr
      TimedOut = false }

let private fakeCargo (build: RunResult) (plan: CommandPlan) =
    if List.contains "metadata" plan.Args then
        ok metadataJson
    else
        build

let private buildOptions =
    { options with
        Rid = "win-x64"
        TargetDir = None }

[<Fact>]
let ``build finds the cdylib, runs cargo and returns the library path`` () =
    let expected =
        Path.Combine("/w/target", "x86_64-pc-windows-msvc", "release", "fsh_interop_example.dll")

    match Cargo.build (fakeCargo (ok "")) (fun p -> p = expected) buildOptions None with
    | Ok lib ->
        Assert.Equal(expected, lib.Path)
        Assert.Equal("fsh-interop-example", lib.Package)
        Assert.Equal("fsh_interop_example", lib.Stem)
        Assert.Equal("x86_64-pc-windows-msvc", lib.Triple)
    | Error e -> failwith e

[<Fact>]
let ``build uses the requested target dir instead of cargo's`` () =
    let expected =
        Path.Combine("mine", "x86_64-pc-windows-msvc", "release", "fsh_interop_example.dll")

    match
        Cargo.build
            (fakeCargo (ok ""))
            (fun p -> p = expected)
            { buildOptions with
                TargetDir = Some "mine" }
            None
    with
    | Ok lib -> Assert.Equal(expected, lib.Path)
    | Error e -> failwith e

[<Fact>]
let ``build reports a compile failure with the tail of cargo's output`` () =
    let stderr = String.Join("\n", [ for i in 1..40 -> "line " + string i ])

    match Cargo.build (fakeCargo (fail stderr)) (fun _ -> true) buildOptions None with
    | Error e ->
        Assert.Contains("exit 101", e)
        Assert.Contains("line 40", e)
        Assert.DoesNotContain("line 5\n", e)
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``build reports success with a missing library instead of returning a dead path`` () =
    match Cargo.build (fakeCargo (ok "")) (fun _ -> false) buildOptions None with
    | Error e -> Assert.Contains("library is missing", e)
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``build stops at a failing cargo metadata`` () =
    let run (_: CommandPlan) = fail "error: could not find Cargo.toml"

    match Cargo.build run (fun _ -> true) buildOptions None with
    | Error e -> Assert.Contains("could not find Cargo.toml", e)
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``build says what to add when the manifest has no cdylib`` () =
    let noCdylib =
        metadataJson.Replace("\"crate_types\": [\"cdylib\"]", "\"crate_types\": [\"lib\"]")

    let run (_: CommandPlan) = ok noCdylib

    match Cargo.build run (fun _ -> true) buildOptions None with
    | Error e -> Assert.Contains("crate-type", e)
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``build refuses to guess between two cdylibs and accepts a name`` () =
    let two =
        metadataJson.Replace("\"crate_types\": [\"lib\"]", "\"crate_types\": [\"cdylib\"]")

    let run (plan: CommandPlan) =
        if List.contains "metadata" plan.Args then ok two else ok ""

    match Cargo.build run (fun _ -> true) buildOptions None with
    | Error e ->
        Assert.Contains("More than one", e)
        Assert.Contains("helper", e)
    | Ok _ -> failwith "expected an error"

    match Cargo.build run (fun _ -> true) buildOptions (Some "helper") with
    | Ok lib -> Assert.Equal("helper", lib.Stem)
    | Error e -> failwith e

    match Cargo.build run (fun _ -> true) buildOptions (Some "nope") with
    | Error e -> Assert.Contains("No cdylib target matches", e)
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``build for an unknown rid fails before running anything that builds`` () =
    let mutable builds = 0

    let run (plan: CommandPlan) =
        if not (List.contains "metadata" plan.Args) then
            builds <- builds + 1

        ok metadataJson

    match Cargo.build run (fun _ -> true) { buildOptions with Rid = "plan9-x64" } None with
    | Error e -> Assert.Contains("plan9-x64", e)
    | Ok _ -> failwith "expected an error"

    Assert.Equal(0, builds)
