namespace FSharpened.Ide

/// A colour theme as the page uses it: CSS custom properties (names without the leading "--") to set on the root element.
/// Every value is a plain `#rrggbb` or `#rrggbbaa` colour, so a theme can never carry anything else into a stylesheet.
type ThemeSpec =
    {
        Id: string
        Name: string
        /// "dark" or "light": the colour scheme for form controls and scrollbars.
        Scheme: string
        /// "normal" or "high" (a high-contrast theme).
        Contrast: string
        Vars: (string * string) list
    }

/// One `tokenColors` rule of a VS Code theme. `Scopes` is empty for the rule that sets the default colours.
type VsTokenRule =
    { Scopes: string list
      Foreground: string }

/// The parts of a VS Code colour theme the IDE can use, read from its JSON by the page.
type VsTheme =
    {
        Name: string
        /// "dark", "light", "hc" or "hcLight", or "" when the file does not say.
        Type: string
        /// `colors`: workbench colour id to value.
        Colors: (string * string) list
        TokenColors: VsTokenRule list
    }

/// A theme made from a VS Code theme, and what could not be honoured.
type ThemeImport =
    { Theme: ThemeSpec
      Warnings: string list }

/// Built-in themes and the conversion of VS Code themes (ADR-0022). Colours are validated and normalised, contrast is
/// measured with the WCAG formula, and a colour the theme did not define is taken from a built-in theme of the same
/// brightness, so an imported theme is always complete.
module Themes =
    let private hexVal (c: char) =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
        else -1

    let private isHex (s: string) =
        s |> Seq.forall (fun c -> hexVal c >= 0)

    /// `#rgb`, `#rgba`, `#rrggbb` or `#rrggbbaa` as lower-case `#rrggbb`. Alpha is dropped. Anything else is None.
    let normalizeColor (raw: string) : string option =
        let s = raw.Trim()

        if s.Length = 0 || s.[0] <> '#' then
            None
        else
            let h = s.Substring(1).ToLowerInvariant()

            if not (isHex h) then
                None
            elif h.Length = 3 || h.Length = 4 then
                Some("#" + System.String [| h.[0]; h.[0]; h.[1]; h.[1]; h.[2]; h.[2] |])
            elif h.Length = 6 || h.Length = 8 then
                Some("#" + h.Substring(0, 6))
            else
                None

    let private channel (hex: string) (i: int) =
        hexVal hex.[1 + 2 * i] * 16 + hexVal hex.[2 + 2 * i]

    let private digits = "0123456789abcdef"

    let private hexOf (r: int) (g: int) (b: int) =
        let two (v: int) =
            let v = max 0 (min 255 v)
            System.String [| digits.[v / 16]; digits.[v % 16] |]

        "#" + two r + two g + two b

    /// Relative luminance (WCAG 2.x) of a `#rrggbb` colour.
    let luminance (hex: string) : float =
        let lin (i: int) =
            let c = float (channel hex i) / 255.0

            if c <= 0.03928 then
                c / 12.92
            else
                System.Math.Pow((c + 0.055) / 1.055, 2.4)

        0.2126 * lin 0 + 0.7152 * lin 1 + 0.0722 * lin 2

    /// WCAG contrast ratio between two `#rrggbb` colours, from 1 to 21.
    let contrast (a: string) (b: string) : float =
        let la = luminance a
        let lb = luminance b
        (max la lb + 0.05) / (min la lb + 0.05)

    /// `fg` laid over `bg` at `percent` opacity.
    let blend (fg: string) (bg: string) (percent: int) : string =
        let mix (i: int) =
            (channel fg i * percent + channel bg i * (100 - percent) + 50) / 100

        hexOf (mix 0) (mix 1) (mix 2)

    let private withAlpha (hex: string) (percent: int) =
        let a = percent * 255 / 100
        hex + System.String [| digits.[a / 16]; digits.[a % 16] |]

    // ---- built-in palettes --------------------------------------------------------------------------

    type private Palette =
        { Bg: string
          Fg: string
          Mut: string
          Line: string
          Card: string
          Brand: string
          BrandFg: string
          Acc: string
          Code: string
          EdBg: string
          Keyword: string
          Str: string
          Number: string
          Comment: string
          Typ: string
          Func: string
          Meta: string
          Op: string
          Bad: string
          Warn: string
          Ok: string }

    let private varsOf (p: Palette) : (string * string) list =
        [ "bg", p.Bg
          "fg", p.Fg
          "mut", p.Mut
          "line", p.Line
          "card", p.Card
          "brand", p.Brand
          "brand-fg", p.BrandFg
          "acc", p.Acc
          "code", p.Code
          "ed-bg", p.EdBg
          "syn-keyword", p.Keyword
          "syn-string", p.Str
          "syn-number", p.Number
          "syn-comment", p.Comment
          "syn-type", p.Typ
          "syn-function", p.Func
          "syn-meta", p.Meta
          "syn-op", p.Op
          "bad", p.Bad
          "warn", p.Warn
          "ok", p.Ok
          "brand-7", withAlpha p.Brand 7
          "brand-14", withAlpha p.Brand 14
          "brand-18", withAlpha p.Brand 18
          "brand-22", withAlpha p.Brand 22
          "brand-25", withAlpha p.Brand 25
          "bg-glass", withAlpha p.Bg 88 ]

    let private spec (id: string) (name: string) (scheme: string) (contrast: string) (p: Palette) : ThemeSpec =
        { Id = id
          Name = name
          Scheme = scheme
          Contrast = contrast
          Vars = varsOf p }

    let private fsLight =
        { Bg = "#ffffff"
          Fg = "#0f172a"
          Mut = "#475569"
          Line = "#e2e8f0"
          Card = "#f8fafc"
          Brand = "#4338ca"
          BrandFg = "#ffffff"
          Acc = "#0e7490"
          Code = "#f1f5f9"
          EdBg = "#ffffff"
          Keyword = "#7c3aed"
          Str = "#047857"
          Number = "#b45309"
          Comment = "#5b6b80"
          Typ = "#0e7490"
          Func = "#1d4ed8"
          Meta = "#be185d"
          Op = "#475569"
          Bad = "#dc2626"
          Warn = "#b45309"
          Ok = "#15803d" }

    let private fsDark =
        { Bg = "#0b1020"
          Fg = "#e6e9f5"
          Mut = "#9aa6c4"
          Line = "#222a47"
          Card = "#111936"
          Brand = "#818cf8"
          BrandFg = "#0b1020"
          Acc = "#22d3ee"
          Code = "#141c3a"
          EdBg = "#0b1020"
          Keyword = "#c4b5fd"
          Str = "#6ee7b7"
          Number = "#fbbf24"
          Comment = "#7c8aaa"
          Typ = "#67e8f9"
          Func = "#93c5fd"
          Meta = "#f9a8d4"
          Op = "#9aa6c4"
          Bad = "#f87171"
          Warn = "#fbbf24"
          Ok = "#4ade80" }

    let private lightPlus =
        { Bg = "#ffffff"
          Fg = "#1f1f1f"
          Mut = "#5a5a5a"
          Line = "#d4d4d4"
          Card = "#f3f3f3"
          Brand = "#005fb8"
          BrandFg = "#ffffff"
          Acc = "#0b7285"
          Code = "#f3f3f3"
          EdBg = "#ffffff"
          Keyword = "#0000ff"
          Str = "#a31515"
          Number = "#067a4f"
          Comment = "#007a00"
          Typ = "#1f7a8c"
          Func = "#795e26"
          Meta = "#a000c8"
          Op = "#3b3b3b"
          Bad = "#c4281c"
          Warn = "#8a6100"
          Ok = "#107c10" }

    let private darkPlus =
        { Bg = "#1e1e1e"
          Fg = "#d4d4d4"
          Mut = "#9d9d9d"
          Line = "#3c3c3c"
          Card = "#252526"
          Brand = "#3794ff"
          BrandFg = "#1e1e1e"
          Acc = "#4ec9b0"
          Code = "#2d2d2d"
          EdBg = "#1e1e1e"
          Keyword = "#569cd6"
          Str = "#ce9178"
          Number = "#b5cea8"
          Comment = "#6a9955"
          Typ = "#4ec9b0"
          Func = "#dcdcaa"
          Meta = "#c586c0"
          Op = "#d4d4d4"
          Bad = "#f48771"
          Warn = "#cca700"
          Ok = "#89d185" }

    let private monokai =
        { Bg = "#272822"
          Fg = "#f8f8f2"
          Mut = "#b4b3a6"
          Line = "#49483e"
          Card = "#1e1f1c"
          Brand = "#a6e22e"
          BrandFg = "#272822"
          Acc = "#66d9ef"
          Code = "#1e1f1c"
          EdBg = "#272822"
          Keyword = "#ff5a8f"
          Str = "#e6db74"
          Number = "#ae81ff"
          Comment = "#a8a68e"
          Typ = "#66d9ef"
          Func = "#a6e22e"
          Meta = "#fd971f"
          Op = "#f8f8f2"
          Bad = "#ff6b6b"
          Warn = "#e6db74"
          Ok = "#a6e22e" }

    let private dracula =
        { Bg = "#282a36"
          Fg = "#f8f8f2"
          Mut = "#aab2d5"
          Line = "#44475a"
          Card = "#21222c"
          Brand = "#bd93f9"
          BrandFg = "#282a36"
          Acc = "#8be9fd"
          Code = "#21222c"
          EdBg = "#282a36"
          Keyword = "#ff79c6"
          Str = "#f1fa8c"
          Number = "#bd93f9"
          Comment = "#9aa5ce"
          Typ = "#8be9fd"
          Func = "#50fa7b"
          Meta = "#ffb86c"
          Op = "#ff79c6"
          Bad = "#ff6e6e"
          Warn = "#ffb86c"
          Ok = "#50fa7b" }

    let private hcDark =
        { Bg = "#000000"
          Fg = "#ffffff"
          Mut = "#e6e6e6"
          Line = "#6fc3df"
          Card = "#000000"
          Brand = "#6fc3ff"
          BrandFg = "#000000"
          Acc = "#ffd700"
          Code = "#0a0a0a"
          EdBg = "#000000"
          Keyword = "#569cd6"
          Str = "#ce9178"
          Number = "#b5cea8"
          Comment = "#7ca668"
          Typ = "#4ec9b0"
          Func = "#dcdcaa"
          Meta = "#c586c0"
          Op = "#ffffff"
          Bad = "#ff8080"
          Warn = "#ffd700"
          Ok = "#6bff6b" }

    let private hcLight =
        { Bg = "#ffffff"
          Fg = "#000000"
          Mut = "#1a1a1a"
          Line = "#0f4a85"
          Card = "#ffffff"
          Brand = "#0f4a85"
          BrandFg = "#ffffff"
          Acc = "#0f4a85"
          Code = "#f2f2f2"
          EdBg = "#ffffff"
          Keyword = "#0f4a85"
          Str = "#a31515"
          Number = "#05583a"
          Comment = "#515151"
          Typ = "#185e73"
          Func = "#5e2cbc"
          Meta = "#8b0b8b"
          Op = "#000000"
          Bad = "#a31515"
          Warn = "#7a4a00"
          Ok = "#05583a" }

    /// "auto" follows the site's own light or dark setting and sets nothing; it is always first.
    let builtin: ThemeSpec list =
        [ { Id = "auto"
            Name = "Match the site"
            Scheme = ""
            Contrast = "normal"
            Vars = [] }
          spec "light" "FSharpened Light" "light" "normal" fsLight
          spec "dark" "FSharpened Dark" "dark" "normal" fsDark
          spec "light-plus" "Light+" "light" "normal" lightPlus
          spec "dark-plus" "Dark+" "dark" "normal" darkPlus
          spec "monokai" "Monokai" "dark" "normal" monokai
          spec "dracula" "Dracula" "dark" "normal" dracula
          spec "hc-dark" "High Contrast Dark" "dark" "high" hcDark
          spec "hc-light" "High Contrast Light" "light" "high" hcLight ]

    let tryFind (id: string) : ThemeSpec option =
        builtin |> List.tryFind (fun t -> t.Id = id)

    /// The value of a variable in a theme, or "".
    let var (name: string) (t: ThemeSpec) : string =
        match t.Vars |> List.tryFind (fun (k, _) -> k = name) with
        | Some(_, v) -> v
        | None -> ""

    // ---- VS Code themes -----------------------------------------------------------------------------

    // The scopes each role looks for, most specific first.
    let private roles: (string * string list) list =
        [ "syn-keyword", [ "keyword.control"; "keyword"; "storage.type"; "storage.modifier"; "storage" ]
          "syn-string", [ "string.quoted"; "string" ]
          "syn-number", [ "constant.numeric"; "constant.language"; "constant" ]
          "syn-comment", [ "comment.line"; "comment.block"; "comment" ]
          "syn-type", [ "entity.name.type"; "entity.name.class"; "support.type"; "support.class" ]
          "syn-function", [ "entity.name.function"; "support.function"; "meta.function-call" ]
          "syn-meta", [ "meta.preprocessor"; "entity.name.tag"; "entity.other.attribute-name" ]
          "syn-op", [ "keyword.operator"; "punctuation" ] ]

    // "source.js string.quoted" selects by its last part; a scope applies to the ones below it ("keyword" to
    // "keyword.control"). Among the rules that apply, the longest scope wins and a later rule beats an earlier one.
    let private resolve (rules: (string * string) list) (candidate: string) : string option =
        let mutable best = ""
        let mutable colour = None

        for (scope, fg) in rules do
            if scope <> "" && (candidate = scope || candidate.StartsWith(scope + ".")) then
                if scope.Length >= best.Length then
                    best <- scope
                    colour <- Some fg

        colour

    let private slug (name: string) =
        let s =
            name.ToLowerInvariant()
            |> Seq.map (fun c ->
                if (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') then
                    c
                else
                    '-')
            |> Seq.toArray
            |> System.String

        let parts = s.Split('-') |> Array.filter (fun x -> x <> "")
        let joined = String.concat "-" parts

        if joined = "" then "theme"
        elif joined.Length > 40 then joined.Substring(0, 40)
        else joined

    let private cleanName (name: string) =
        let n =
            name
            |> Seq.filter (fun c -> c >= ' ' && c <> '<' && c <> '>')
            |> Seq.toArray
            |> System.String
            |> fun s -> s.Trim()

        if n = "" then "Imported theme"
        elif n.Length > 60 then n.Substring(0, 60)
        else n

    /// Turns a VS Code theme into a page theme. Colours that are not valid hex are ignored; anything the theme leaves out
    /// comes from the built-in theme of the same brightness.
    let ofVsCode (t: VsTheme) : ThemeImport =
        let colors =
            t.Colors
            |> List.choose (fun (k, v) -> normalizeColor v |> Option.map (fun c -> k, c))
            |> Map.ofList

        let pick (keys: string list) =
            keys |> List.tryPick (fun k -> Map.tryFind k colors)

        let rules =
            t.TokenColors
            |> List.collect (fun r ->
                match normalizeColor r.Foreground with
                | None -> []
                | Some fg ->
                    match r.Scopes with
                    | [] -> [ "", fg ]
                    | scopes ->
                        scopes
                        |> List.map (fun s ->
                            let last = s.Trim().Split(' ') |> Array.filter (fun x -> x <> "") |> Array.tryLast
                            defaultArg last "", fg))

        let defaultFg = rules |> List.tryFind (fun (s, _) -> s = "") |> Option.map snd

        let high = t.Type = "hc" || t.Type = "hcLight"

        let editorBg = pick [ "editor.background" ]

        let isLight =
            match t.Type with
            | "light"
            | "hcLight" -> true
            | "dark"
            | "hc" -> false
            | _ ->
                match editorBg with
                | Some c -> luminance c > 0.4
                | None -> false

        let b =
            match isLight, high with
            | true, true -> hcLight
            | true, false -> fsLight
            | false, true -> hcDark
            | false, false -> fsDark

        let warnings = ResizeArray<string>()
        let edBg = defaultArg editorBg b.EdBg
        let bg = edBg

        let fg =
            defaultArg (pick [ "editor.foreground"; "foreground" ] |> Option.orElse defaultFg) b.Fg

        let card =
            defaultArg (pick [ "sideBar.background"; "editorWidget.background" ]) (blend fg bg 6)

        let line =
            defaultArg (pick [ "panel.border"; "editorGroup.border"; "contrastBorder" ]) (blend fg bg 22)

        let readable (c: string) (against: string) (need: float) = contrast c against >= need

        let derivedMut =
            // step from a soft grey toward the foreground until it reads
            [ 60; 70; 80; 90; 100 ]
            |> List.map (fun p -> blend fg bg p)
            |> List.tryFind (fun c -> readable c bg 4.5)
            |> Option.defaultValue fg

        let mut =
            match pick [ "descriptionForeground" ] with
            | Some c when readable c bg 4.5 -> c
            | _ -> derivedMut

        let brand =
            [ "button.background"
              "textLink.foreground"
              "focusBorder"
              "activityBarBadge.background" ]
            |> List.choose (fun k -> Map.tryFind k colors)
            |> List.tryFind (fun c -> readable c bg 4.5)
            |> Option.defaultValue b.Brand

        let brandFg =
            if contrast "#000000" brand >= contrast "#ffffff" brand then
                "#000000"
            else
                "#ffffff"

        let status (keys: string list) (fallback: string) =
            match pick keys with
            | Some c when readable c bg 3.0 -> c
            | _ -> fallback

        let syntax (role: string) (fallback: string) : string =
            let scopes = roles |> List.find (fun (r, _) -> r = role) |> snd

            let found = scopes |> List.tryPick (fun c -> resolve rules c)

            let colour = defaultArg found fallback

            if not (readable colour edBg 4.5) then
                warnings.Add(
                    "The "
                    + (role.Substring 4)
                    + " colour "
                    + colour
                    + " has a contrast of "
                    + string (System.Math.Round(contrast colour edBg, 1))
                    + ":1 against the editor background (WCAG asks for 4.5:1)."
                )

            colour

        if not (readable fg bg 4.5) then
            warnings.Add(
                "The text colour "
                + fg
                + " has a contrast of "
                + string (System.Math.Round(contrast fg bg, 1))
                + ":1 against the background (WCAG asks for 4.5:1)."
            )

        let p =
            { Bg = bg
              Fg = fg
              Mut = mut
              Line = line
              Card = card
              Brand = brand
              BrandFg = brandFg
              Acc = defaultArg (pick [ "textLink.activeForeground"; "textLink.foreground" ]) b.Acc
              Code = defaultArg (pick [ "textCodeBlock.background" ]) card
              EdBg = edBg
              Keyword = syntax "syn-keyword" b.Keyword
              Str = syntax "syn-string" b.Str
              Number = syntax "syn-number" b.Number
              Comment = syntax "syn-comment" b.Comment
              Typ = syntax "syn-type" b.Typ
              Func = syntax "syn-function" b.Func
              Meta = syntax "syn-meta" b.Meta
              Op = syntax "syn-op" fg
              Bad = status [ "errorForeground"; "editorError.foreground" ] b.Bad
              Warn = status [ "editorWarning.foreground" ] b.Warn
              Ok = status [ "testing.iconPassed"; "gitDecoration.addedResourceForeground" ] b.Ok }

        { Theme =
            spec
                ("vsc-" + slug t.Name)
                (cleanName t.Name)
                (if isLight then "light" else "dark")
                (if high then "high" else "normal")
                p
          Warnings = warnings |> Seq.toList }
