namespace FSharpened.Ide

/// A small JSON value, enough to write the files VS Code reads (`.vscode/settings.json`, `extensions.json`,
/// `*.code-workspace`). The page reads JSON with the browser's own parser; only writing is done here.
type Json =
    | JStr of string
    | JNum of int
    | JBool of bool
    | JArr of Json list
    | JObj of (string * Json) list

module Json =
    let private digits = "0123456789abcdef"

    let private escape (s: string) : string =
        let sb = System.Text.StringBuilder()

        for c in s do
            if c = '"' then
                sb.Append "\\\"" |> ignore
            elif c = '\\' then
                sb.Append "\\\\" |> ignore
            elif c = '\n' then
                sb.Append "\\n" |> ignore
            elif c = '\r' then
                sb.Append "\\r" |> ignore
            elif c = '\t' then
                sb.Append "\\t" |> ignore
            elif c < ' ' then
                let n = int c
                sb.Append("\\u00").Append(digits.[n / 16]).Append(digits.[n % 16]) |> ignore
            else
                sb.Append c |> ignore

        sb.ToString()

    /// Two-space indented JSON with a trailing newline, the way VS Code writes its own settings files.
    let render (value: Json) : string =
        let rec go (indent: string) (j: Json) : string =
            let inner = indent + "  "

            match j with
            | JStr s -> "\"" + escape s + "\""
            | JNum n -> string n
            | JBool b -> if b then "true" else "false"
            | JArr [] -> "[]"
            | JArr items ->
                "[\n"
                + (items |> List.map (fun i -> inner + go inner i) |> String.concat ",\n")
                + "\n"
                + indent
                + "]"
            | JObj [] -> "{}"
            | JObj fields ->
                "{\n"
                + (fields
                   |> List.map (fun (k, v) -> inner + "\"" + escape k + "\": " + go inner v)
                   |> String.concat ",\n")
                + "\n"
                + indent
                + "}"

        go "" value + "\n"

/// What the editor lets a person choose. Stored in the browser, validated on every read.
type EditorSettings =
    {
        /// A theme id: a built-in one, an imported one ("vsc-..."), or "auto" to follow the site.
        Theme: string
        FontSize: int
        TabSize: int
        InsertSpaces: bool
        WordWrap: bool
        LineNumbers: bool
        RenderWhitespace: bool
        AutoCloseBrackets: bool
        Breadcrumbs: bool
    }

/// A value read from a VS Code `settings.json`.
type VsValue =
    | VNum of float
    | VText of string
    | VFlag of bool

/// What reading a VS Code settings file changed and what it left alone.
type SettingsImport =
    { Settings: EditorSettings
      Applied: string list
      Ignored: string list }

module Settings =
    let fontMin = 10
    let fontMax = 28
    let tabMin = 1
    let tabMax = 8

    let defaults: EditorSettings =
        { Theme = "auto"
          FontSize = 14
          TabSize = 4
          InsertSpaces = true
          WordWrap = false
          LineNumbers = true
          RenderWhitespace = false
          AutoCloseBrackets = true
          Breadcrumbs = true }

    let private clamp (lo: int) (hi: int) (v: int) = max lo (min hi v)

    // Theme ids end up in a CSS selector and in storage: lower-case letters, digits and hyphens only.
    let private cleanThemeId (id: string) =
        let ok =
            id.Length > 0
            && id.Length <= 60
            && id
               |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-')

        if ok then id else defaults.Theme

    /// Brings any settings into range, so a hand-edited or damaged value cannot break the editor.
    let sanitize (s: EditorSettings) : EditorSettings =
        { s with
            Theme = cleanThemeId s.Theme
            FontSize = clamp fontMin fontMax s.FontSize
            TabSize = clamp tabMin tabMax s.TabSize }

    // ---- VS Code ------------------------------------------------------------------------------------

    /// The name of the VS Code theme closest to a built-in theme, or None when VS Code has no such theme.
    let vsCodeTheme (id: string) : string option =
        match id with
        | "light" -> Some "Default Light Modern"
        | "dark" -> Some "Default Dark Modern"
        | "light-plus" -> Some "Default Light+"
        | "dark-plus" -> Some "Default Dark+"
        | "monokai" -> Some "Monokai"
        | "hc-dark" -> Some "Default High Contrast"
        | "hc-light" -> Some "Default High Contrast Light"
        | _ -> None

    /// The built-in theme closest to a VS Code theme name (the reverse of `vsCodeTheme`), or None. Matching ignores case.
    let themeOfVsCode (name: string) : string option =
        match name.Trim().ToLowerInvariant() with
        | "default light modern" -> Some "light"
        | "default dark modern" -> Some "dark"
        | "default light+" -> Some "light-plus"
        | "default dark+" -> Some "dark-plus"
        | "monokai" -> Some "monokai"
        | "dracula"
        | "dracula official"
        | "dracula theme" -> Some "dracula"
        | "default high contrast" -> Some "hc-dark"
        | "default high contrast light" -> Some "hc-light"
        | _ -> None

    /// The settings as VS Code's `settings.json` keys, in a stable order.
    let toVsCode (s: EditorSettings) : (string * Json) list =
        [ yield "editor.fontSize", JNum s.FontSize
          yield "editor.tabSize", JNum s.TabSize
          yield "editor.insertSpaces", JBool s.InsertSpaces
          yield "editor.wordWrap", JStr(if s.WordWrap then "on" else "off")
          yield "editor.lineNumbers", JStr(if s.LineNumbers then "on" else "off")
          yield "editor.renderWhitespace", JStr(if s.RenderWhitespace then "all" else "none")
          yield "editor.autoClosingBrackets", JStr(if s.AutoCloseBrackets then "languageDefined" else "never")
          yield "breadcrumbs.enabled", JBool s.Breadcrumbs
          yield "files.insertFinalNewline", JBool true

          match vsCodeTheme s.Theme with
          | Some name -> yield "workbench.colorTheme", JStr name
          | None -> () ]

    let private whole (v: float) = v = System.Math.Floor v

    /// Reads the settings the IDE understands from a VS Code `settings.json` (already parsed to key/value pairs). A key
    /// with a value of the wrong type, and every key the IDE has no setting for, is reported as ignored.
    let ofVsCode (current: EditorSettings) (pairs: (string * VsValue) list) : SettingsImport =
        let mutable s = current
        let applied = ResizeArray<string>()
        let ignored = ResizeArray<string>()

        for (key, v) in pairs do
            let ok =
                match key, v with
                | "editor.fontSize", VNum n when whole n ->
                    s <- { s with FontSize = int n }
                    true
                | "editor.tabSize", VNum n when whole n ->
                    s <- { s with TabSize = int n }
                    true
                | "editor.insertSpaces", VFlag b ->
                    s <- { s with InsertSpaces = b }
                    true
                | "editor.wordWrap", VText t ->
                    s <- { s with WordWrap = t <> "off" }
                    true
                | "editor.lineNumbers", VText t ->
                    s <- { s with LineNumbers = t <> "off" }
                    true
                | "editor.renderWhitespace", VText t ->
                    s <-
                        { s with
                            RenderWhitespace = t <> "none" }

                    true
                | "editor.autoClosingBrackets", VText t ->
                    s <-
                        { s with
                            AutoCloseBrackets = t <> "never" }

                    true
                | "breadcrumbs.enabled", VFlag b ->
                    s <- { s with Breadcrumbs = b }
                    true
                | "workbench.colorTheme", VText t when (themeOfVsCode t).IsSome ->
                    s <-
                        { s with
                            Theme = (themeOfVsCode t).Value }

                    true
                | _ -> false

            if ok then applied.Add key else ignored.Add key

        { Settings = sanitize s
          Applied = applied |> Seq.toList
          Ignored = ignored |> Seq.toList }
