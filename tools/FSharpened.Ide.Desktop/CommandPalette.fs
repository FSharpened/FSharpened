/// The command palette and quick open: ranking rows, modes, and running the chosen one.
namespace FSharpened.Ide.Desktop

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Styling
open Avalonia.Threading
open AvaloniaEdit.CodeCompletion
open AvaloniaEdit
open AvaloniaEdit.Document
open AvaloniaEdit.Rendering
open AvaloniaEdit.Search
open AvaloniaEdit.TextMate
open TextMateSharp.Grammars
open FSharpened.Ide
open FSharpened.Ide.Host

type CommandPalette(ui: Ui, commands: Commands) =
    let commands = commands.Commands
    let docs = ui.Docs
    let editor = ui.Editor
    let palette = ui.Palette
    let paletteInput = ui.PaletteInput
    let paletteList = ui.PaletteList

    let hidePalette () =
        palette.IsVisible <- false

        if docs.Count > 0 then
            editor.Focus() |> ignore

    /// What a row of the palette does when chosen, with the text to show and the matched letters to emphasise.
    let mutable paletteActions: (unit -> unit) list = []

    let paletteRow (text: string) (positions: int list) (detail: string) =
        let t = TextBlock(FontSize = 14.0, Foreground = ui.Roles.Fg)
        let set = Set.ofList positions

        for i in 0 .. text.Length - 1 do
            let r = Documents.Run(string text[i])

            if set.Contains i then
                r.FontWeight <- FontWeight.Bold
                r.Foreground <- ui.Roles.Brand

            t.Inlines.Add r

        let row = DockPanel(Margin = Thickness(2.0, 3.0))

        if detail <> "" then
            let d =
                TextBlock(
                    Text = detail,
                    Foreground = ui.Roles.Muted,
                    FontSize = 12.0,
                    VerticalAlignment = VerticalAlignment.Center
                )

            DockPanel.SetDock(d, Dock.Right)
            row.Children.Add d

        row.Children.Add t
        row

    let refreshPalette () =
        let raw = paletteInput.Text |> Option.ofObj |> Option.defaultValue ""
        let q = Quick.parse raw
        paletteList.Items.Clear()
        paletteActions <- []

        let add (row: Control) (act: unit -> unit) =
            paletteList.Items.Push row
            paletteActions <- paletteActions @ [ act ]

        match q.Mode with
        | "command" ->
            let titles = commands |> List.map (fun c -> c.Title)

            let ranked =
                if q.Text = "" then
                    titles
                    |> List.mapi (fun i t ->
                        { Index = i
                          Text = t
                          Score = 0
                          Positions = [] })
                    |> List.truncate 40
                else
                    Fuzzy.rank q.Text titles 40

            for r in ranked do
                let c = commands[r.Index]

                add (paletteRow r.Text r.Positions c.Keys) (fun () ->
                    hidePalette ()
                    c.Run())
        | "line" ->
            if q.Line > 0 then
                add
                    (paletteRow
                        (sprintf "Go to line %d%s" q.Line (if q.Column > 0 then sprintf ", column %d" q.Column else ""))
                        []
                        "")
                    (fun () ->
                        hidePalette ()
                        editor.TextArea.Caret.Line <- q.Line
                        editor.TextArea.Caret.Column <- max 1 q.Column
                        editor.ScrollToLine q.Line
                        editor.Focus() |> ignore)
        | "symbol" ->
            match ui.Active with
            | Some d ->
                let symbols = d.Outline |> List.toArray
                let names = symbols |> Array.map (fun s -> s.Name) |> Array.toList

                let ranked =
                    if q.Text = "" then
                        names
                        |> List.mapi (fun i t ->
                            { Index = i
                              Text = t
                              Score = 0
                              Positions = [] })
                        |> List.truncate 60
                    else
                        Fuzzy.rank q.Text names 60

                for r in ranked do
                    let s = symbols[r.Index]

                    add (paletteRow r.Text r.Positions (sprintf "%s · line %d" s.Kind s.Line)) (fun () ->
                        hidePalette ()
                        editor.TextArea.Caret.Line <- s.Line
                        editor.TextArea.Caret.Column <- max 1 s.Column
                        editor.ScrollToLine s.Line
                        editor.Focus() |> ignore)
            | None -> ()
        | _ ->
            let paths = ui.FileCache |> List.map (fun e -> e.Rel)

            let ranked =
                if q.Text = "" then
                    let recent = docs |> Seq.map (fun d -> d.Rel) |> Seq.toList

                    (recent @ (paths |> List.filter (fun p -> not (List.contains p recent))))
                    |> List.truncate 40
                    |> List.mapi (fun i t ->
                        { Index = i
                          Text = t
                          Score = 0
                          Positions = [] })
                else
                    Fuzzy.rank q.Text paths 40

            for r in ranked do
                let rel = r.Text

                add (paletteRow rel r.Positions "") (fun () ->
                    hidePalette ()
                    ui.OpenFileFn rel q.Line q.Column)

        if paletteList.Items.Count > 0 then
            paletteList.SelectedIndex <- 0

    do
        ui.ShowPaletteFn <-
            fun prefix ->
                palette.IsVisible <- true
                paletteInput.Text <- prefix
                paletteInput.Focus() |> ignore
                paletteInput.CaretIndex <- prefix.Length
                refreshPalette ()


    member _.HidePalette = hidePalette

    member _.PaletteActions
        with get () = paletteActions
        and set v = paletteActions <- v

    member _.RefreshPalette = refreshPalette
