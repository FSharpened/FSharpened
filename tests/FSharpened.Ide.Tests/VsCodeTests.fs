module FSharpened.Ide.Tests.VsCodeTests

open Xunit
open FSharpened.Ide

let private ctx =
    { FileName = "main.rs"
      FilePath = "src/main.rs"
      Folder = "src"
      Selected = "picked"
      ProjectName = "Demo"
      Year = 2026
      Month = 3
      Day = 7
      LineComment = "//" }

let private tpl body = Snippets.template body ctx

// ---- snippets ------------------------------------------------------------------------------------------

[<Fact>]
let ``tab stops and the final position become editor fields`` () =
    Assert.Equal("for (${1}; ${2:x}; ${})", (tpl "for ($1; ${2:x}; $0)").Template)

[<Fact>]
let ``a linked copy shows the first default`` () =
    Assert.Equal("${1:name} and ${1:name}", (tpl "${1:name} and $1").Template)

[<Fact>]
let ``a choice becomes its first option and says so`` () =
    let r = tpl "${1|a,b,c|}"
    Assert.Equal("${1:a}", r.Template)
    Assert.Single r.Warnings |> ignore

[<Fact>]
let ``a transform is dropped but its field stays`` () =
    Assert.Equal("${1}", (tpl "${1/(.*)/${1:/upcase}/}").Template)

[<Fact>]
let ``a nested placeholder is flattened`` () =
    Assert.Equal("${1:a b}", (tpl "${1:a ${2:b}}").Template)

[<Fact>]
let ``known variables are filled in`` () =
    Assert.Equal(
        "main.rs main src/main.rs Demo picked 2026-03-07 //",
        (tpl
            "$TM_FILENAME ${TM_FILENAME_BASE} $RELATIVE_FILEPATH $WORKSPACE_NAME $TM_SELECTED_TEXT $CURRENT_YEAR-$CURRENT_MONTH-$CURRENT_DATE $LINE_COMMENT")
            .Template
    )

[<Fact>]
let ``an unknown variable falls back to its default or its name`` () =
    let r = tpl "${FOO:fallback} $BAR"
    Assert.Equal("fallback BAR", r.Template)
    Assert.Equal(2, r.Warnings.Length)

[<Fact>]
let ``an escaped dollar is a plain character`` () =
    Assert.Equal("cost $1 and }", (tpl "cost \\$1 and \\}").Template)

[<Fact>]
let ``a literal field marker cannot be written and is reported`` () =
    let r = tpl "a \\${b}"
    Assert.Equal("a {b}", r.Template)
    Assert.Single r.Warnings |> ignore

[<Fact>]
let ``malformed placeholders do not throw`` () =
    for body in [ "${1:abc"; "${"; "$"; "${1|a"; "${1/x"; "$1000000"; "${a b}"; "}}" ] do
        let r = tpl body
        Assert.NotNull r.Template

[<Fact>]
let ``every built-in snippet converts cleanly`` () =
    for s in Snippets.builtin do
        let r = tpl s.Body
        Assert.True(r.Warnings.IsEmpty, s.Label + ": " + String.concat "; " r.Warnings)
        Assert.NotEqual<string>("", s.Label)

[<Fact>]
let ``every language has built-in snippets`` () =
    for i in Language.infos do
        let count = (Snippets.forLanguage i.Id []).Length
        Assert.True(count >= 3, i.Id + " has " + string count)

[<Fact>]
let ``imported snippets are read per prefix and scoped to languages`` () =
    let defs =
        [ { Name = "Loop"
            Prefixes = [ "lp"; "loop" ]
            Body = "for (;;) {}"
            Description = "a loop"
            Scope = [ "javascriptreact"; "c++" ] }
          { Name = "Nothing"
            Prefixes = [ "" ]
            Body = "x"
            Description = ""
            Scope = [] }
          { Name = "Huge"
            Prefixes = [ "huge" ]
            Body = String.replicate 5000 "x"
            Description = ""
            Scope = [] }
          { Name = "Shell"
            Prefixes = [ "sh" ]
            Body = "echo"
            Description = ""
            Scope = [ "shellscript" ] }
          { Name = "All"
            Prefixes = [ "all" ]
            Body = "x"
            Description = ""
            Scope = [] } ]

    let got = Snippets.ofDefs defs
    Assert.Equal<string list>([ "lp"; "loop"; "sh"; "all" ], got |> List.map (fun s -> s.Label))
    Assert.Equal<string list>([ "javascript"; "cpp" ], got.Head.Languages)
    Assert.Equal<string list>([ "unsupported" ], (got |> List.find (fun s -> s.Label = "sh")).Languages)

    let rust = Snippets.forLanguage "rust" got |> List.map (fun s -> s.Label)
    Assert.Contains("all", rust)
    Assert.DoesNotContain("lp", rust)
    Assert.DoesNotContain("sh", rust)
    Assert.Contains("lp", Snippets.forLanguage "cpp" got |> List.map (fun s -> s.Label))

// ---- themes --------------------------------------------------------------------------------------------

[<Theory>]
[<InlineData("#FFF", "#ffffff")>]
[<InlineData("#abcd", "#aabbcc")>]
[<InlineData("#112233ff", "#112233")>]
[<InlineData("  #A1B2C3  ", "#a1b2c3")>]
let ``colours are normalised`` (raw: string, expected: string) =
    Assert.Equal(Some expected, Themes.normalizeColor raw)

[<Theory>]
[<InlineData("red")>]
[<InlineData("#12")>]
[<InlineData("#12345")>]
[<InlineData("#ggg")>]
[<InlineData("url(javascript:alert(1))")>]
[<InlineData("#fff; background: url(x)")>]
[<InlineData("")>]
let ``anything that is not hex is refused`` (raw: string) =
    Assert.Equal(None, Themes.normalizeColor raw)

[<Fact>]
let ``contrast follows the WCAG formula`` () =
    Assert.Equal(21.0, Themes.contrast "#000000" "#ffffff", 3)
    Assert.Equal(1.0, Themes.contrast "#777777" "#777777", 3)

[<Fact>]
let ``built-in themes are unique, start with auto and carry only hex colours`` () =
    let ids = Themes.builtin |> List.map (fun t -> t.Id)
    Assert.Equal("auto", ids.Head)
    Assert.Equal(ids.Length, (List.distinct ids).Length)
    Assert.Empty(Themes.builtin.Head.Vars)

    for t in Themes.builtin.Tail do
        for (k, v) in t.Vars do
            Assert.True(
                (Themes.normalizeColor v).IsSome
                || (v.Length = 9 && (Themes.normalizeColor (v.Substring(0, 7))).IsSome),
                t.Id + " --" + k + " = " + v
            )

[<Fact>]
let ``every built-in theme is readable`` () =
    for t in Themes.builtin.Tail do
        let v name = Themes.var name t
        let need = if t.Contrast = "high" then 7.0 else 4.5

        let check a b min =
            Assert.True(Themes.contrast (v a) (v b) >= min, t.Id + ": " + a + " on " + b)

        check "fg" "bg" need
        check "mut" "bg" need
        check "mut" "card" need
        check "fg" "card" need
        check "brand" "bg" need
        check "brand-fg" "brand" need

        for role in [ "keyword"; "string"; "number"; "comment"; "type"; "function"; "meta"; "op" ] do
            check ("syn-" + role) "ed-bg" need

        for status in [ "bad"; "warn"; "ok" ] do
            check status "bg" 3.0

[<Fact>]
let ``a theme carries every variable the page styles with`` () =
    let names (t: ThemeSpec) = t.Vars |> List.map fst |> Set.ofList
    let reference = names Themes.builtin.[1]

    for t in Themes.builtin.Tail do
        Assert.Equal<Set<string>>(reference, names t)

let private vs name kind colors rules : VsTheme =
    { Name = name
      Type = kind
      Colors = colors
      TokenColors = rules }

let private rule scopes fg = { Scopes = scopes; Foreground = fg }

[<Fact>]
let ``a VS Code theme maps its token scopes to the editor's colours`` () =
    let t =
        vs
            "Test"
            "dark"
            [ "editor.background", "#000000"; "editor.foreground", "#eeeeee" ]
            [ rule [ "keyword" ] "#ff0000"
              rule [ "keyword.control" ] "#00ff00"
              rule [ "string" ] "#ffff00"
              rule [ "comment"; "punctuation.definition.comment" ] "#aaaaaa" ]

    let r = Themes.ofVsCode t
    let v name = Themes.var name r.Theme
    Assert.Equal("#000000", v "ed-bg")
    Assert.Equal("#eeeeee", v "fg")
    Assert.Equal("#00ff00", v "syn-keyword") // the more specific scope wins
    Assert.Equal("#ff0000", v "syn-op") // keyword.operator falls under plain "keyword"
    Assert.Equal("#ffff00", v "syn-string")
    Assert.Equal("#aaaaaa", v "syn-comment")
    Assert.Equal("dark", r.Theme.Scheme)
    Assert.Equal("vsc-test", r.Theme.Id)

[<Fact>]
let ``a later rule beats an earlier one with the same scope`` () =
    let t =
        vs "T" "dark" [ "editor.background", "#000000" ] [ rule [ "string" ] "#ff0000"; rule [ "string" ] "#00ff00" ]

    Assert.Equal("#00ff00", Themes.var "syn-string" (Themes.ofVsCode t).Theme)

[<Fact>]
let ``a descendant selector is matched by its last part`` () =
    let t =
        vs "T" "dark" [ "editor.background", "#000000" ] [ rule [ "source.js string.quoted" ] "#ff00ff" ]

    Assert.Equal("#ff00ff", Themes.var "syn-string" (Themes.ofVsCode t).Theme)

[<Fact>]
let ``invalid colours are ignored and missing ones come from a built-in theme`` () =
    let t =
        vs
            "Sparse"
            "light"
            [ "editor.background", "#ffffff"; "editor.foreground", "javascript:alert(1)" ]
            [ rule [ "keyword" ] "url(x)" ]

    let r = Themes.ofVsCode t
    Assert.Equal("light", r.Theme.Scheme)
    Assert.Equal(Themes.var "syn-keyword" Themes.builtin.[1], Themes.var "syn-keyword" r.Theme)
    Assert.Equal(Themes.var "fg" Themes.builtin.[1], Themes.var "fg" r.Theme)

[<Fact>]
let ``a theme with no declared type is judged by its background`` () =
    let light = Themes.ofVsCode (vs "L" "" [ "editor.background", "#fafafa" ] [])
    let dark = Themes.ofVsCode (vs "D" "" [ "editor.background", "#101010" ] [])
    let unknown = Themes.ofVsCode (vs "U" "" [] [])
    Assert.Equal("light", light.Theme.Scheme)
    Assert.Equal("dark", dark.Theme.Scheme)
    Assert.Equal("dark", unknown.Theme.Scheme)

[<Fact>]
let ``a high-contrast type is carried over`` () =
    Assert.Equal("high", (Themes.ofVsCode (vs "H" "hc" [] [])).Theme.Contrast)
    Assert.Equal("light", (Themes.ofVsCode (vs "H" "hcLight" [] [])).Theme.Scheme)

[<Fact>]
let ``poor contrast is reported, not hidden`` () =
    let t =
        vs
            "Dim"
            "dark"
            [ "editor.background", "#000000"; "editor.foreground", "#222222" ]
            [ rule [ "comment" ] "#111111" ]

    let r = Themes.ofVsCode t
    Assert.True(r.Warnings.Length >= 2)
    Assert.Contains(r.Warnings, fun w -> w.Contains "comment")
    Assert.Equal("#111111", Themes.var "syn-comment" r.Theme)

[<Fact>]
let ``an imported theme is complete and every colour is hex`` () =
    let r = Themes.ofVsCode (vs "Nothing" "dark" [] [])
    let reference = Themes.builtin.[1].Vars |> List.map fst |> Set.ofList
    Assert.Equal<Set<string>>(reference, r.Theme.Vars |> List.map fst |> Set.ofList)
    Assert.Empty r.Warnings

[<Fact>]
let ``the id and name of an imported theme are made safe`` () =
    let r = Themes.ofVsCode (vs "<b>My Cool Theme!</b>" "dark" [] [])
    Assert.Equal("bMy Cool Theme!/b", r.Theme.Name)
    Assert.Matches("^vsc-[a-z0-9-]+$", r.Theme.Id)

[<Fact>]
let ``the button and link colours become the accent only when readable`` () =
    let readable =
        Themes.ofVsCode (vs "A" "dark" [ "editor.background", "#000000"; "button.background", "#3794ff" ] [])

    let dim =
        Themes.ofVsCode (vs "B" "dark" [ "editor.background", "#000000"; "button.background", "#0a1a2a" ] [])

    Assert.Equal("#3794ff", Themes.var "brand" readable.Theme)
    Assert.Equal(Themes.var "brand" Themes.builtin.[2], Themes.var "brand" dim.Theme)

// ---- settings and VS Code export -----------------------------------------------------------------------

[<Fact>]
let ``settings are brought into range`` () =
    let s =
        Settings.sanitize
            { Settings.defaults with
                FontSize = 3
                TabSize = 40
                Theme = "Bad Theme!" }

    Assert.Equal(Settings.fontMin, s.FontSize)
    Assert.Equal(Settings.tabMax, s.TabSize)
    Assert.Equal("auto", s.Theme)

    Assert.Equal(
        Settings.fontMax,
        (Settings.sanitize
            { Settings.defaults with
                FontSize = 999 })
            .FontSize
    )

    Assert.Equal(
        "dark-plus",
        (Settings.sanitize
            { Settings.defaults with
                Theme = "dark-plus" })
            .Theme
    )

[<Fact>]
let ``settings are written as VS Code keys`` () =
    let json =
        Settings.toVsCode
            { Settings.defaults with
                TabSize = 2
                WordWrap = true
                Theme = "dark-plus" }
        |> JObj
        |> Json.render

    Assert.Contains("\"editor.tabSize\": 2", json)
    Assert.Contains("\"editor.wordWrap\": \"on\"", json)
    Assert.Contains("\"workbench.colorTheme\": \"Default Dark+\"", json)
    Assert.DoesNotContain("workbench.colorTheme", Json.render (JObj(Settings.toVsCode Settings.defaults)))

[<Fact>]
let ``VS Code settings are read back and strangers are reported`` () =
    let r =
        Settings.ofVsCode
            Settings.defaults
            [ "editor.tabSize", VNum 2.0
              "editor.fontSize", VNum 99.0
              "editor.wordWrap", VText "on"
              "editor.insertSpaces", VFlag false
              "editor.renderWhitespace", VText "all"
              "editor.lineNumbers", VText "off"
              "editor.tabSize ", VNum 3.0
              "editor.formatOnSave", VFlag true
              "editor.fontSize", VText "big"
              "editor.tabSize", VNum 2.5 ]

    Assert.Equal(2, r.Settings.TabSize)
    Assert.Equal(Settings.fontMax, r.Settings.FontSize)
    Assert.True r.Settings.WordWrap
    Assert.False r.Settings.InsertSpaces
    Assert.True r.Settings.RenderWhitespace
    Assert.False r.Settings.LineNumbers

    Assert.Equal<string list>(
        [ "editor.formatOnSave"
          "editor.tabSize "
          "editor.fontSize"
          "editor.tabSize" ]
        |> List.sort,
        r.Ignored |> List.sort
    )

[<Fact>]
let ``a VS Code colour theme name selects the matching built-in theme`` () =
    let read name =
        (Settings.ofVsCode Settings.defaults [ "workbench.colorTheme", VText name ]).Settings.Theme

    Assert.Equal("dark-plus", read "Default Dark+")
    Assert.Equal("monokai", read "  MONOKAI ")
    Assert.Equal("dracula", read "Dracula Official")
    Assert.Equal("hc-light", read "Default High Contrast Light")
    // a theme the IDE does not have is reported, and the theme stays as it was
    let unknown =
        Settings.ofVsCode Settings.defaults [ "workbench.colorTheme", VText "Solarized Dark" ]

    Assert.Equal("auto", unknown.Settings.Theme)
    Assert.Equal<string list>([ "workbench.colorTheme" ], unknown.Ignored)

    // what the IDE writes, it reads back
    for t in [ "light"; "dark"; "light-plus"; "dark-plus"; "monokai"; "hc-dark"; "hc-light" ] do
        let name = (Settings.vsCodeTheme t).Value
        Assert.Equal(t, read name)

[<Fact>]
let ``written JSON is valid and escaped`` () =
    let text =
        Json.render (
            JObj
                [ "a\"b", JStr "line\nbreak \\ \t \u0001"
                  "list", JArr [ JNum 1; JBool true ]
                  "empty", JObj []
                  "none", JArr [] ]
        )

    use doc = System.Text.Json.JsonDocument.Parse text
    Assert.Equal("line\nbreak \\ \t \u0001", doc.RootElement.GetProperty("a\"b").GetString())
    Assert.EndsWith("\n", text)

let private project files =
    files
    |> List.fold
        (fun p (path, text) ->
            match Project.addFile path text p with
            | Ok q -> q
            | Error e -> failwith e)
        (Project.create "My project!")

[<Fact>]
let ``a project recommends the extension of each language it uses, then Copilot`` () =
    let p = project [ "a.rs", ""; "b.py", ""; "c.js", ""; "d.c", ""; "e.cpp", "" ]

    Assert.Equal<string list>(
        [ "ms-python.python"
          "rust-lang.rust-analyzer"
          "ms-vscode.cpptools"
          "GitHub.copilot"
          "GitHub.copilot-chat" ],
        VsCode.recommendations p
    )

    Assert.Equal<string list>(VsCode.copilot, VsCode.recommendations (Project.create "Empty"))

[<Fact>]
let ``every language maps to an extension id or to none on purpose`` () =
    let without = Language.all |> List.filter (fun l -> (VsCode.extensionFor l).IsNone)
    Assert.Equal<Language list>([ VisualBasic; JavaScript; TypeScript ], without)

[<Fact>]
let ``the export makes three valid JSON files`` () =
    let p = project [ "main.rs", "fn main() {}" ]
    let files = VsCode.files p Settings.defaults

    Assert.Equal<string list>(
        [ ".vscode/extensions.json"
          ".vscode/settings.json"
          "My-project.code-workspace" ],
        files |> List.map fst
    )

    for (path, text) in files do
        use doc = System.Text.Json.JsonDocument.Parse text
        Assert.True(doc.RootElement.ValueKind = System.Text.Json.JsonValueKind.Object, path)

    use ws = System.Text.Json.JsonDocument.Parse(snd files.[2])
    let recs = ws.RootElement.GetProperty("extensions").GetProperty("recommendations")
    Assert.Equal(3, recs.GetArrayLength())
    Assert.Equal("GitHub.copilot-chat", recs.[2].GetString())
    Assert.Equal(".", ws.RootElement.GetProperty("folders").[0].GetProperty("path").GetString())

    // every exported path is a path the project itself accepts, so a re-import keeps them
    for (path, _) in files do
        Assert.True((ProjectPath.normalize path) = Ok path, path)

[<Fact>]
let ``files the project already has are not overwritten`` () =
    let p = project [ "main.rs", ""; ".vscode/settings.json", "{ \"mine\": true }" ]
    let paths = VsCode.files p Settings.defaults |> List.map fst
    Assert.DoesNotContain(".vscode/settings.json", paths)
    Assert.Contains(".vscode/extensions.json", paths)

[<Fact>]
let ``the workspace file name is safe`` () =
    Assert.Equal("My-project.code-workspace", VsCode.workspaceName (Project.create "My project!"))
    Assert.Equal("project.code-workspace", VsCode.workspaceName (Project.create "???"))
