/// Syntax grammars that come with the app or from the user, in front of the ones TextMateSharp bundles (ADR-0023).
/// A grammar is a TextMate `.tmLanguage.json` file; its `scopeName` is what a language pack's `scope` names. Dropping a
/// file into `FSharpened/grammars` adds a language or replaces a shipped grammar, with no code change.
module FSharpened.Ide.Desktop.Grammars

open System
open System.IO
open System.Text.Json
open TextMateSharp.Grammars
open TextMateSharp.Internal.Grammars.Reader
open TextMateSharp.Internal.Types
open TextMateSharp.Registry
open TextMateSharp.Themes

/// The `scopeName` of a grammar file, or None when the file is not JSON or names no scope.
let scopeOf (path: string) : string option =
    try
        use doc = JsonDocument.Parse(File.ReadAllText path)

        match doc.RootElement.TryGetProperty "scopeName" with
        | true, v when v.ValueKind = JsonValueKind.String && v.GetString() <> "" -> Some(v.GetString())
        | _ -> None
    with _ ->
        None

/// scope name -> file, from the folders given in order; a later folder wins over an earlier one for the same scope.
let index (dirs: string list) : Map<string, string> =
    dirs
    |> List.fold
        (fun (acc: Map<string, string>) dir ->
            if Directory.Exists dir then
                Directory.GetFiles(dir, "*.json")
                |> Array.sort
                |> Array.fold
                    (fun (m: Map<string, string>) file ->
                        match scopeOf file with
                        | Some scope when not (Path.GetFileName(file).StartsWith "_") -> m.Add(scope, file)
                        | _ -> m)
                    acc
            else
                acc)
        Map.empty

/// TextMateSharp's own registry with extra grammar files in front of it.
type PackRegistry(inner: RegistryOptions, files: Map<string, string>) =
    member _.Inner = inner
    member _.Files = files
    member _.Has(scope: string) = files.ContainsKey scope

    interface IRegistryOptions with
        member _.GetInjections(scope: string) = inner.GetInjections scope
        member _.GetTheme(scope: string) = inner.GetTheme scope
        member _.GetDefaultTheme() = inner.GetDefaultTheme()

        member _.GetGrammar(scope: string) : IRawGrammar =
            match files.TryFind scope with
            | Some path ->
                try
                    use reader = new StreamReader(path)
                    GrammarReader.ReadGrammarSync reader
                with _ ->
                    inner.GetGrammar scope
            | None -> inner.GetGrammar scope
