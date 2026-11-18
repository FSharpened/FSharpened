/// `--selftest`: what a packaged copy of the app checks about itself without opening a window, so a build machine with
/// no display can still prove the package is whole. It loads the shipped language packs and grammars, opens and lists
/// a temporary folder through the host, loads every native library shipped for this platform, prints one JSON line and
/// exits 0 (all well) or 1.
module FSharpened.Ide.Desktop.Selftest

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json.Nodes
open FSharpened.Ide.Host
open FSharpened.Platform

let run () : int =
    let failures = ResizeArray<string>()
    let nativeErrors = ResizeArray<string>()
    let baseDir = AppContext.BaseDirectory
    let rid = RuntimeIdentity.detect().Rid

    let temp =
        Path.Combine(Path.GetTempPath(), "fsh-selftest-" + Guid.NewGuid().ToString("N"))

    try
        Directory.CreateDirectory temp |> ignore
        File.WriteAllText(Path.Combine(temp, "hello.fsx"), "printfn \"hi\"")
        // a data folder of its own, so the check never reads or writes the user's settings
        let data = Path.Combine(temp, "data")
        let env = HostEnv(data, Path.Combine(baseDir, "languages"))
        env.ReloadLanguages()
        let packs = env.Registry.Packs.Length
        let problems = env.Registry.Problems.Length

        if packs = 0 then
            failures.Add "no language packs were found next to the program"

        let grammars = Grammars.index [ Path.Combine(baseDir, "grammars") ] |> Map.count

        if grammars = 0 then
            failures.Add "no shipped grammars were found next to the program"

        let entries =
            match DiskWorkspace.list temp "" with
            | Ok es -> es |> List.filter (fun e -> not e.IsDir) |> List.length
            | Error e ->
                failures.Add("could not list the temporary folder: " + e)
                0

        let nativeDir = Path.Combine(baseDir, "runtimes", rid, "native")

        let nativeFiles =
            if Directory.Exists nativeDir then
                Directory.GetFiles nativeDir
                // symbol files sit beside the libraries; only the libraries themselves are loaded
                |> Array.filter (fun f ->
                    let n = Path.GetFileName f
                    n.EndsWith ".dll" || n.EndsWith ".dylib" || n.Contains ".so")
                |> Array.sort
            else
                [||]

        for f in nativeFiles do
            match NativeLibrary.TryLoad f with
            | true, h -> NativeLibrary.Free h
            | _ -> nativeErrors.Add(Path.GetFileName f)

        let ok = failures.Count = 0 && nativeErrors.Count = 0

        let report = JsonObject()
        report["ok"] <- JsonValue.Create ok
        report["rid"] <- JsonValue.Create rid
        report["packs"] <- JsonValue.Create packs
        report["packProblems"] <- JsonValue.Create problems
        report["grammars"] <- JsonValue.Create grammars
        report["workspaceEntries"] <- JsonValue.Create entries
        report["nativeLibraries"] <- JsonValue.Create nativeFiles.Length
        report["nativeFailed"] <- JsonValue.Create nativeErrors.Count
        report["nativeErrors"] <- JsonValue.Create(String.Join(", ", nativeErrors))
        report["failures"] <- JsonValue.Create(String.Join("; ", failures))
        Console.Out.WriteLine(report.ToJsonString())
        if ok then 0 else 1
    with e ->
        Console.Out.WriteLine("{\"ok\":false,\"failures\":" + JsonValue.Create(e.Message).ToJsonString() + "}")
        1
    |> fun code ->
        try
            Directory.Delete(temp, true)
        with _ ->
            ()

        code
