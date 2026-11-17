namespace FSharpened.Interop.Rust

open FSharpened.Platform

/// A .NET runtime identifier paired with the Rust target triple that produces native code for it.
/// The two vocabularies do not map one-to-one (a RID names an OS and CPU, a triple also names a vendor and an ABI),
/// so the pairing is data, reviewed here, rather than string surgery at call sites.
type RustTarget =
    {
        Rid: string
        Triple: string
        Os: Os
        Arch: Arch
        /// True for the six RIDs of the CI matrix (ADR-0014). The others are supported but not gated.
        InMatrix: bool
    }

type LibraryKind =
    /// A shared library a .NET process loads with DllImport or NativeLibrary (cargo crate-type cdylib).
    | Dynamic
    /// A static archive for linking into a NativeAOT binary (cargo crate-type staticlib).
    | Static

module Targets =
    let private target rid triple os arch inMatrix =
        { Rid = rid
          Triple = triple
          Os = os
          Arch = arch
          InMatrix = inMatrix }

    /// Every RID this pillar knows how to build for. Platform behaviour is data here, never an #if (Invariant 1).
    let all: RustTarget list =
        [ target "win-x64" "x86_64-pc-windows-msvc" Windows X64 true
          target "win-arm64" "aarch64-pc-windows-msvc" Windows Arm64 true
          target "linux-x64" "x86_64-unknown-linux-gnu" Linux X64 true
          target "linux-arm64" "aarch64-unknown-linux-gnu" Linux Arm64 true
          target "osx-x64" "x86_64-apple-darwin" MacOs X64 true
          target "osx-arm64" "aarch64-apple-darwin" MacOs Arm64 true
          // musl libc: a different ABI from glibc, so it is a different RID and a different triple.
          target "linux-musl-x64" "x86_64-unknown-linux-musl" Linux X64 false
          target "linux-musl-arm64" "aarch64-unknown-linux-musl" Linux Arm64 false ]

    /// The six RIDs the CI matrix runs.
    let matrix: RustTarget list = all |> List.filter (fun t -> t.InMatrix)

    let tryOfRid (rid: string) : RustTarget option =
        all |> List.tryFind (fun t -> t.Rid = rid)

    let tryOfTriple (triple: string) : RustTarget option =
        all |> List.tryFind (fun t -> t.Triple = triple)

    /// The target for the machine this process runs on.
    let tryOfIdentity (identity: RuntimeIdentity) : RustTarget option = tryOfRid identity.Rid

    /// Cargo derives a library file name from the crate name with every '-' replaced by '_'.
    let libraryStem (crateName: string) : string = crateName.Replace('-', '_')

    /// The file cargo writes for a library of the given kind on the given OS, or None when that OS has no such file.
    let libraryFileName (os: Os) (kind: LibraryKind) (stem: string) : string option =
        match os, kind with
        | Windows, Dynamic -> Some(stem + ".dll")
        | Windows, Static -> Some(stem + ".lib")
        | MacOs, Dynamic -> Some("lib" + stem + ".dylib")
        | (Linux | Android), Dynamic -> Some("lib" + stem + ".so")
        | (MacOs | Linux | Android | Ios), Static -> Some("lib" + stem + ".a")
        | _ -> None

    /// The NuGet and dotnet publish convention for native assets, always with forward slashes.
    let nativeDirectory (rid: string) : string = "runtimes/" + rid + "/native"

    let nativeRelativePath (rid: string) (fileName: string) : string = nativeDirectory rid + "/" + fileName
