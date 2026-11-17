module DesktopTests

open System
open System.IO
open Xunit
open Avalonia.Media
open TextMateSharp.Grammars
open FSharpened.Ide
open FSharpened.Ide.Desktop
open FSharpened.Ide.Host

let repoRoot =
    let rec up (d: DirectoryInfo) =
        if isNull d then
            failwith "repository root not found"
        elif File.Exists(Path.Combine(d.FullName, "FSharpened.slnx")) then
            d.FullName
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

[<Fact>]
let ``colours come out as the page has them, with alpha last, and nonsense is visible rather than fatal`` () =
    Assert.Equal(Color.FromRgb(0x43uy, 0x38uy, 0xcauy), Palette.parseColor "#4338ca")
    Assert.Equal(Color.FromArgb(0x80uy, 0x43uy, 0x38uy, 0xcauy), Palette.parseColor "#4338ca80")
    Assert.Equal(Colors.Magenta, Palette.parseColor "red")
    Assert.Equal(Colors.Magenta, Palette.parseColor null)
    Assert.Equal(Colors.Magenta, Palette.parseColor "#zzzzzz")

[<Fact>]
let ``every built-in theme of the website gives the window a complete set of roles`` () =
    Assert.True(Palette.choices.Length >= 8)

    for t in Palette.choices do
        let r = Palette.ofTheme t
        Assert.Equal(t.Scheme = "dark", r.Dark)

        for b in
            [ r.Bg
              r.Fg
              r.Muted
              r.Line
              r.Card
              r.Brand
              r.BrandFg
              r.Accent
              r.Code
              r.EditorBg
              r.Selection
              r.Bad
              r.Warn
              r.Ok ] do
            let c = (b :?> ISolidColorBrush).Color
            Assert.NotEqual(Colors.Magenta, c)

    Assert.Equal("dark", (Palette.find "no-such-theme").Id)
    Assert.Equal("dark", (Palette.find "auto").Id)

[<Fact>]
let ``preferences survive a round trip and a damaged file gives the defaults`` () =
    let p =
        { Prefs.defaults with
            Editor =
                { Prefs.defaults.Editor with
                    Theme = "monokai"
                    FontSize = 18
                    WordWrap = true }
            Recent = [ "C:\work\a"; "/home/me/b" ] }

    let back = Prefs.parse (Prefs.render p)
    Assert.Equal(p, back)
    Assert.Equal(Prefs.defaults, Prefs.parse "{ not json")
    Assert.Equal(Prefs.defaults, Prefs.parse "[]")

[<Fact>]
let ``a hand-edited preferences file is brought into range`` () =
    let p =
        Prefs.parse
            """{ "theme": "Bad Theme!", "fontSize": 400, "tabSize": 0, "wordWrap": "yes", "recent": ["a", 3, "b"] }"""

    Assert.Equal(Settings.defaults.Theme, p.Editor.Theme)
    Assert.Equal(Settings.fontMax, p.Editor.FontSize)
    Assert.Equal(Settings.tabMin, p.Editor.TabSize)
    Assert.Equal<string list>([ "a"; "b" ], p.Recent)

[<Fact>]
let ``recent folders put the newest first, once, and keep eight`` () =
    let mutable p = Prefs.defaults

    for i in 1..10 do
        p <- Prefs.remember (sprintf "/f/%d" i) p

    p <- Prefs.remember "/F/5" p
    Assert.Equal(8, p.Recent.Length)
    Assert.Equal("/F/5", p.Recent.Head)
    Assert.Equal(1, p.Recent |> List.filter (fun r -> r.ToLowerInvariant() = "/f/5") |> List.length)

[<Fact>]
let ``the environment loads the shipped packs, lets a user pack win, and keeps trust in its own folder`` () =
    let data =
        Path.Combine(Path.GetTempPath(), "fsh-desk-" + Guid.NewGuid().ToString "N")

    try
        let env = HostEnv(data, Path.Combine(repoRoot, "languages"))
        env.ReloadLanguages()
        Assert.True(env.Registry.Packs.Length >= 30)
        Assert.NotNull(box (Languages.byId env.Registry "go"))
        Directory.CreateDirectory env.UserPacks |> ignore

        File.WriteAllText(
            Path.Combine(env.UserPacks, "go.json"),
            """{ "format": "fsharpened.language/1", "id": "go", "name": "Go (mine)", "extensions": [".go"] }"""
        )

        env.ReloadLanguages()
        Assert.Equal("Go (mine)", (Languages.byId env.Registry "go").Value.Name)
        env.Trust.Trust data
        Assert.True((HostEnv(data, Path.Combine(repoRoot, "languages"))).Trust.IsTrusted data)
    finally
        try
            Directory.Delete(data, true)
        with _ ->
            ()

[<Fact>]
let ``the highlighter has a grammar for every language of the website and for most packs`` () =
    let reg =
        Languages.merge [ Languages.loadDirectory (Path.Combine(repoRoot, "languages")) ]

    let options = RegistryOptions(ThemeName.DarkPlus)

    let missing =
        reg.Packs
        |> List.filter (fun p -> p.Scope <> "" && isNull (options.GetGrammar p.Scope))
        |> List.map (fun p -> p.Id)

    // the languages the website already supports must all highlight
    for id in
        [ "fsharp"
          "csharp"
          "vb"
          "javascript"
          "typescript"
          "python"
          "rust"
          "c"
          "cpp"
          "go"
          "java"
          "ruby"
          "lua"
          "php"
          "r" ] do
        Assert.DoesNotContain(id, missing)

    // known gaps in the bundled grammars (docs/operations/desktop.md lists them); a pack may bring its own grammar later
    let known = set [ "haskell"; "ocaml"; "elixir"; "kotlin"; "scala"; "zig"; "toml" ]

    Assert.True(
        missing |> List.forall known.Contains,
        "unexpected packs without a grammar: "
        + String.Join(", ", missing |> List.filter (known.Contains >> not))
    )

    Console.WriteLine("packs without a bundled grammar: " + String.Join(", ", missing))

    // a language with no grammar still opens and runs, just without colour; say which ones, so the docs can too
    Assert.True(
        missing.Length < reg.Packs.Length / 3,
        "too many packs without a grammar: " + String.Join(", ", missing)
    )
