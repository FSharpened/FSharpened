namespace FSharpened.Interop.Rust

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open FSharpened.Platform

type ResolveOutcome =
    | Found of path: string
    /// Every path that was tried, in order, so the error can say where the library was expected.
    | NotFound of tried: string list

/// Finds a native library without #if and without trusting the current directory or PATH.
/// The plan is pure data; only the last step touches the disk, and it takes the existence check as a parameter.
module NativeResolver =
    /// Candidate paths for one library file, most specific first: the RID folder of each root, then the root itself.
    let searchPlan (rid: string) (roots: string list) (fileName: string) : string list =
        [ for root in roots do
              Path.Combine(root, "runtimes", rid, "native", fileName)
              Path.Combine(root, fileName) ]
        |> List.distinct

    /// The plan for a Rust crate on the machine described by the identity. None when the pillar has no mapping for
    /// the identity's RID, so an unsupported platform is reported rather than guessed at.
    let planFor (identity: RuntimeIdentity) (roots: string list) (stem: string) : string list option =
        Targets.tryOfIdentity identity
        |> Option.bind (fun t -> Targets.libraryFileName t.Os Dynamic stem |> Option.map (searchPlan t.Rid roots))

    let resolve (exists: string -> bool) (plan: string list) : ResolveOutcome =
        match List.tryFind exists plan with
        | Some path -> Found path
        | None -> NotFound plan

    let describeMiss (name: string) (tried: string list) : string =
        "Native library '"
        + name
        + "' was not found. Tried: "
        + String.Join("; ", tried)

    /// Installs a DllImport resolver for the assembly. Names in the map are looked up in their plan and loaded by
    /// full path. With strict set, a miss throws DllNotFoundException naming every path tried, instead of letting
    /// the runtime fall back to its default probing (which includes directories we do not control).
    /// The runtime allows one resolver per assembly: call this once, with every library the assembly binds.
    let install (assembly: Assembly) (libraries: Map<string, string list>) (strict: bool) : unit =
        let resolver =
            DllImportResolver(fun name _ _ ->
                match Map.tryFind name libraries with
                | None -> 0n
                | Some plan ->
                    match resolve File.Exists plan with
                    | Found path -> NativeLibrary.Load path
                    | NotFound tried when strict -> raise (DllNotFoundException(describeMiss name tried))
                    | NotFound _ -> 0n)

        NativeLibrary.SetDllImportResolver(assembly, resolver)
