/// The website's colour roles, as Avalonia brushes. The colours come from the same F# `Themes` module the page uses
/// (ADR-0023): one list of built-in themes, one conversion of VS Code themes, two front ends.
module FSharpened.Ide.Desktop.Palette

open Avalonia.Media
open FSharpened.Ide

/// Parses `#rrggbb` or `#rrggbbaa` (the form `Themes` guarantees); anything else is a visible magenta, never an exception.
let parseColor (hex: string) : Color =
    let h = if isNull hex then "" else hex.TrimStart('#')

    let byteAt (i: int) =
        System.Convert.ToByte(h.Substring(i, 2), 16)

    try
        match h.Length with
        | 6 -> Color.FromRgb(byteAt 0, byteAt 2, byteAt 4)
        | 8 -> Color.FromArgb(byteAt 6, byteAt 0, byteAt 2, byteAt 4)
        | _ -> Colors.Magenta
    with _ ->
        Colors.Magenta

/// The colour roles one theme gives the window.
type Roles =
    { Id: string
      Dark: bool
      Bg: IBrush
      Fg: IBrush
      Muted: IBrush
      Line: IBrush
      Card: IBrush
      Brand: IBrush
      BrandFg: IBrush
      Accent: IBrush
      Code: IBrush
      EditorBg: IBrush
      Selection: IBrush
      Bad: IBrush
      Warn: IBrush
      Ok: IBrush }

let private brush (hex: string) : IBrush =
    SolidColorBrush(parseColor hex) :> IBrush

let ofTheme (t: ThemeSpec) : Roles =
    let v name = Themes.var name t

    { Id = t.Id
      Dark = (t.Scheme = "dark")
      Bg = brush (v "bg")
      Fg = brush (v "fg")
      Muted = brush (v "mut")
      Line = brush (v "line")
      Card = brush (v "card")
      Brand = brush (v "brand")
      BrandFg = brush (v "brand-fg")
      Accent = brush (v "acc")
      Code = brush (v "code")
      EditorBg = brush (v "ed-bg")
      Selection = brush (v "brand-25")
      Bad = brush (v "bad")
      Warn = brush (v "warn")
      Ok = brush (v "ok") }

/// The themes a person can choose from. "auto" (follow the site) means nothing on a desktop, so it is left out.
let choices: ThemeSpec list =
    Themes.builtin |> List.filter (fun t -> t.Id <> "auto")

let find (id: string) : ThemeSpec =
    match Themes.tryFind id with
    | Some t when t.Id <> "auto" -> t
    | _ -> (Themes.tryFind "dark").Value

/// The brush of a hex colour from a theme variable, for the places that need a single colour (a syntax colour).
let var (name: string) (t: ThemeSpec) : Color = parseColor (Themes.var name t)
