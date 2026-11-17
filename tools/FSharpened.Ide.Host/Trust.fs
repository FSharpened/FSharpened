namespace FSharpened.Ide.Host

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open FSharpened.Platform

/// Which folders may run programs and start language servers (ADR-0023). Nothing runs in a folder that is not here,
/// and a folder is added only by the person, through a prompt the app draws itself.
type TrustStore(file: string) =
    let gate = obj ()

    let comparer =
        match (RuntimeIdentity.detect ()).Os with
        | Windows
        | MacOs -> StringComparer.OrdinalIgnoreCase
        | _ -> StringComparer.Ordinal

    let trusted = HashSet<string>(comparer)

    do
        try
            if File.Exists file then
                JsonSerializer.Deserialize<string[]>(File.ReadAllText file)
                |> Array.iter (trusted.Add >> ignore)
        with _ ->
            () // a damaged file means "nothing is trusted yet"

    let save () =
        try
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath file))
            |> ignore

            File.WriteAllText(file, JsonSerializer.Serialize(trusted |> Seq.sort |> Seq.toArray))
        with _ ->
            () // trust then lasts for this session only

    /// The form in which a folder is remembered: absolute, without a trailing separator.
    static member KeyOf(folder: string) =
        let full = Path.GetFullPath folder
        if full.Length > 3 then full.TrimEnd('\\', '/') else full

    member _.IsTrusted(folder: string) =
        lock gate (fun () -> trusted.Contains(TrustStore.KeyOf folder))

    member _.Trust(folder: string) =
        lock gate (fun () ->
            trusted.Add(TrustStore.KeyOf folder) |> ignore
            save ())

    member _.Revoke(folder: string) =
        lock gate (fun () ->
            trusted.Remove(TrustStore.KeyOf folder) |> ignore
            save ())

    member _.All = lock gate (fun () -> trusted |> Seq.sort |> Seq.toList)
