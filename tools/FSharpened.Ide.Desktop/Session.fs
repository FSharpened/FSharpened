/// What the window needs from the machine that is not drawing: where the app keeps its data, the language packs, the
/// tool probe, folder trust and the person's settings. No Avalonia types, so it could be tested without a window.
namespace FSharpened.Ide.Desktop

open System
open System.IO
open System.Text.Json
open FSharpened.Ide
open FSharpened.Ide.Host

/// Choices that belong to this computer rather than to a project.
type Prefs =
    {
        Editor: EditorSettings
        /// The last folders opened, most recent first (at most 8).
        Recent: string list
    }

module Prefs =
    let defaults: Prefs =
        { Editor =
            { Settings.defaults with
                Theme = "dark" }
          Recent = [] }

    let private maxRecent = 8

    /// Moves a folder to the front of the recent list.
    let remember (folder: string) (p: Prefs) : Prefs =
        let rest =
            p.Recent
            |> List.filter (fun r -> not (String.Equals(r, folder, StringComparison.OrdinalIgnoreCase)))

        { p with
            Recent = folder :: rest |> List.truncate maxRecent }

    let private getInt (o: JsonElement) (name: string) (fallback: int) =
        match o.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, n -> n
            | _ -> fallback
        | _ -> fallback

    let private getBool (o: JsonElement) (name: string) (fallback: bool) =
        match o.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.True -> true
        | true, v when v.ValueKind = JsonValueKind.False -> false
        | _ -> fallback

    let private getText (o: JsonElement) (name: string) (fallback: string) =
        match o.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> fallback

    /// Reads preferences; a missing or damaged file gives the defaults, and every value is brought into range.
    let parse (json: string) : Prefs =
        try
            use doc = JsonDocument.Parse json
            let o = doc.RootElement
            let d = defaults.Editor

            let editor =
                Settings.sanitize
                    { Theme = getText o "theme" d.Theme
                      FontSize = getInt o "fontSize" d.FontSize
                      TabSize = getInt o "tabSize" d.TabSize
                      InsertSpaces = getBool o "insertSpaces" d.InsertSpaces
                      WordWrap = getBool o "wordWrap" d.WordWrap
                      LineNumbers = getBool o "lineNumbers" d.LineNumbers
                      RenderWhitespace = getBool o "renderWhitespace" d.RenderWhitespace
                      AutoCloseBrackets = getBool o "autoCloseBrackets" d.AutoCloseBrackets
                      Breadcrumbs = getBool o "breadcrumbs" d.Breadcrumbs }

            let recent =
                match o.TryGetProperty "recent" with
                | true, a when a.ValueKind = JsonValueKind.Array ->
                    [ for e in a.EnumerateArray() do
                          if e.ValueKind = JsonValueKind.String then
                              yield e.GetString() ]
                    |> List.truncate maxRecent
                | _ -> []

            { Editor = editor; Recent = recent }
        with _ ->
            defaults

    let render (p: Prefs) : string =
        let e = p.Editor

        let fields =
            [ "theme", JStr e.Theme
              "fontSize", JNum e.FontSize
              "tabSize", JNum e.TabSize
              "insertSpaces", JBool e.InsertSpaces
              "wordWrap", JBool e.WordWrap
              "lineNumbers", JBool e.LineNumbers
              "renderWhitespace", JBool e.RenderWhitespace
              "autoCloseBrackets", JBool e.AutoCloseBrackets
              "breadcrumbs", JBool e.Breadcrumbs
              "recent", JArr [ for r in p.Recent -> JStr r ] ]

        Json.render (JObj fields)

/// The machine the window runs on.
type HostEnv(dataDir: string, shippedPacks: string) =
    let prefsFile = Path.Combine(dataDir, "settings.json")
    let userPacks = Path.Combine(dataDir, "languages")
    let trust = TrustStore(Path.Combine(dataDir, "trust.json"))
    let mutable registry = Languages.merge []
    let mutable box = ToolBox []

    do
        try
            Directory.CreateDirectory dataDir |> ignore
        with _ ->
            ()

    /// The folder the app keeps settings, trust and the user's language packs in.
    member _.DataDir = dataDir
    member _.UserPacks = userPacks

    /// The person's own keyboard shortcuts (VS Code's keybindings.json format) and snippet files.
    member _.KeybindingsFile = Path.Combine(dataDir, "keybindings.json")

    member _.UserSnippets = Path.Combine(dataDir, "snippets")
    member _.Trust = trust
    member _.Registry = registry
    member _.Box = box

    /// (Re)reads the shipped packs and then the user's, which win, and starts a fresh tool probe.
    member _.ReloadLanguages() =
        let shipped = Languages.loadDirectory shippedPacks

        let user =
            if Directory.Exists userPacks then
                Languages.loadDirectory userPacks
            else
                [], []

        registry <- Languages.merge [ shipped; user ]
        box <- ToolBox(Languages.tools registry)

    member _.LoadPrefs() : Prefs =
        try
            if File.Exists prefsFile then
                Prefs.parse (File.ReadAllText prefsFile)
            else
                Prefs.defaults
        with _ ->
            Prefs.defaults

    member _.SavePrefs(p: Prefs) =
        try
            Directory.CreateDirectory dataDir |> ignore
            File.WriteAllText(prefsFile + ".tmp", Prefs.render p)
            File.Move(prefsFile + ".tmp", prefsFile, true)
        with _ ->
            ()

module HostEnv =
    /// The environment of the installed app: data under the user's application-data folder (or the folder named by
    /// FSHARPENED_DATA, which makes the app portable and lets tests use a clean one), packs next to the program.
    let standard () : HostEnv =
        let dataDir =
            match System.Environment.GetEnvironmentVariable "FSHARPENED_DATA" with
            | null
            | "" ->
                let appData =
                    System.Environment.GetFolderPath System.Environment.SpecialFolder.ApplicationData

                Path.Combine(appData, "FSharpened")
            | custom -> Path.GetFullPath custom

        let env = HostEnv(dataDir, Path.Combine(AppContext.BaseDirectory, "languages"))
        env.ReloadLanguages()
        env
