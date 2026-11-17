module GrammarTests

open System
open System.IO
open Xunit
open TextMateSharp.Grammars
open TextMateSharp.Registry
open FSharpened.Ide
open FSharpened.Ide.Desktop
open FSharpened.Ide.Host

let private shipped = Path.Combine(DesktopTests.repoRoot, "grammars")

let private registry (extra: string list) =
    let files = Grammars.index (shipped :: extra)
    let packs = Grammars.PackRegistry(RegistryOptions(ThemeName.DarkPlus), files)
    TextMateSharp.Registry.Registry(packs :> IRegistryOptions)

/// The scopes of the token that covers the first character of `needle` in `line`.
let private scopesAt (reg: TextMateSharp.Registry.Registry) (scope: string) (line: string) (needle: string) : string =
    let g = reg.LoadGrammar scope
    Assert.NotNull g
    let at = line.IndexOf(needle, StringComparison.Ordinal)
    Assert.True(at >= 0, sprintf "%s is not in %s" needle line)

    let tokens = (g.TokenizeLine line).Tokens

    match tokens |> Array.tryFind (fun t -> t.StartIndex <= at && at < t.EndIndex) with
    | Some t -> String.Join(" ", t.Scopes)
    | None -> ""

/// (grammar scope, line, the text to look at, a fragment its scope must contain)
let private cases =
    [ "source.haskell", "-- a comment", "comment", "comment.line"
      "source.haskell", "{- block -}", "block", "comment.block"
      "source.haskell", "data Shape = Circle Double", "data", "keyword.control"
      "source.haskell", "data Shape = Circle Double", "Shape", "entity.name.type"
      "source.haskell", "main = putStrLn \"hi\"", "\"hi\"", "string.quoted"
      "source.haskell", "x = 42", "42", "constant.numeric"
      "source.ocaml", "(* note *)", "note", "comment.block"
      "source.ocaml", "let x = 42 in x", "let", "keyword.control"
      "source.ocaml", "let x = 42 in x", "42", "constant.numeric"
      "source.ocaml", "let s = \"hi\"", "\"hi\"", "string.quoted"
      "source.ocaml", "type 'a t = 'a list", "'a", "entity.name.type"
      "source.elixir", "# note", "note", "comment.line"
      "source.elixir", "defmodule Foo do", "defmodule", "keyword.control"
      "source.elixir", "defmodule Foo do", "Foo", "entity.name.type.module"
      "source.elixir", "{:ok, value}", ":ok", "constant.other.symbol"
      "source.elixir", "IO.puts \"hello\"", "\"hello\"", "string.quoted"
      "source.kotlin", "// note", "note", "comment.line"
      "source.kotlin", "fun main() {", "fun", "keyword.control"
      "source.kotlin", "val s = \"hi\"", "\"hi\"", "string.quoted"
      "source.kotlin", "@Test fun x() {}", "@Test", "storage.type.annotation"
      "source.kotlin", "val n = null", "null", "constant.language"
      "source.scala", "// note", "note", "comment.line"
      "source.scala", "case class Point(x: Int)", "case", "keyword.control"
      "source.scala", "case class Point(x: Int)", "Point", "entity.name.type"
      "source.scala", "val s = \"hi\"", "\"hi\"", "string.quoted"
      "source.zig", "// note", "note", "comment.line"
      "source.zig", "const std = @import(\"std\");", "const", "keyword.control"
      "source.zig", "const std = @import(\"std\");", "@import", "support.function.builtin"
      "source.zig", "pub fn main() void {", "fn", "keyword.control"
      "source.zig", "pub fn main() void {", "void", "storage.type"
      "source.toml", "# note", "note", "comment.line"
      "source.toml", "[server]", "server", "entity.name.section"
      "source.toml", "name = \"fsh\"", "name", "variable.other.key"
      "source.toml", "name = \"fsh\"", "\"fsh\"", "string.quoted"
      "source.toml", "port = 8080", "8080", "constant.numeric"
      "source.toml", "on = true", "true", "constant.language.boolean" ]

[<Fact>]
let ``each shipped grammar colours comments, keywords, strings, numbers and types`` () =
    let reg = registry []

    for scope, line, needle, expected in cases do
        let got = scopesAt reg scope line needle

        Assert.True(
            got.Contains expected,
            sprintf "%s: “%s” in `%s` got [%s], wanted %s" scope needle line got expected
        )

[<Fact>]
let ``every language that had no grammar now has one, and every pack's scope resolves`` () =
    let reg = registry []

    for scope in
        [ "source.haskell"
          "source.ocaml"
          "source.elixir"
          "source.kotlin"
          "source.scala"
          "source.zig"
          "source.toml" ] do
        Assert.NotNull(reg.LoadGrammar scope)

    let packs =
        Languages.merge [ Languages.loadDirectory (Path.Combine(DesktopTests.repoRoot, "languages")) ]

    let missing =
        packs.Packs
        |> List.filter (fun p -> p.Scope <> "" && isNull (reg.LoadGrammar p.Scope))
        |> List.map (fun p -> p.Id)

    Assert.True(missing.IsEmpty, "packs with no grammar: " + String.Join(", ", missing))

[<Fact>]
let ``a grammar file dropped in the user's folder adds a language or replaces a shipped one`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fsh-grammars-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    try
        // a new language: "#" starts a comment in a made-up language
        File.WriteAllText(
            Path.Combine(dir, "tiny.json"),
            """{ "scopeName": "source.tiny", "patterns": [ { "name": "comment.line.tiny", "match": "#.*$" } ] }"""
        )

        // a replacement for a shipped one: in this TOML, everything is a string
        File.WriteAllText(
            Path.Combine(dir, "toml.json"),
            """{ "scopeName": "source.toml", "patterns": [ { "name": "string.mine", "match": ".+" } ] }"""
        )

        // ignored: starts with an underscore, is not JSON, or names no scope
        File.WriteAllText(Path.Combine(dir, "_off.json"), """{ "scopeName": "source.off", "patterns": [] }""")
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json")
        File.WriteAllText(Path.Combine(dir, "noscope.json"), """{ "patterns": [] }""")

        let files = Grammars.index [ shipped; dir ]
        Assert.True(files.ContainsKey "source.tiny")
        Assert.False(files.ContainsKey "source.off")
        Assert.Equal(7, files |> Map.filter (fun k _ -> k <> "source.tiny") |> Map.count)

        let reg = registry [ dir ]
        Assert.Contains("comment.line.tiny", scopesAt reg "source.tiny" "# hi" "hi")
        Assert.Contains("string.mine", scopesAt reg "source.toml" "name = 1" "name")
    finally
        Directory.Delete(dir, true)
