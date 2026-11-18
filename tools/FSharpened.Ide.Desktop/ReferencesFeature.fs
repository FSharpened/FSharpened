/// Find all references (Shift+F12): asks the language server, lists the places in the References panel, and opens one
/// when it is chosen.
namespace FSharpened.Ide.Desktop

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.Media
open FSharpened.Ide
open FSharpened.Ide.Host

type ReferencesFeature(ui: Ui, languageServers: LanguageServers, appearance: Appearance) =
    let box = ui.ReferencesBox
    let say = ui.Say
    let sessionOf = languageServers.SessionOf
    let syncLsp = languageServers.SyncLsp

    /// The text of line `line` (1-based) of each file in `wanted`: the editor's when the file is open, else the disk's.
    let linesOf (r: string) (open': (string * string) list) (wanted: string list) =
        let cache = Dictionary<string, string[]>()

        for rel in wanted |> List.distinct do
            let text =
                match open' |> List.tryFind (fun (p, _) -> p = rel) with
                | Some(_, t) -> Some t
                | None ->
                    match DiskWorkspace.readText r rel with
                    | Ok t -> Some t
                    | Error _ -> None

            cache[rel] <-
                match text with
                | Some t -> t.Replace("\r\n", "\n").Split('\n')
                | None -> [||]

        cache

    let lineText (cache: Dictionary<string, string[]>) (rel: string) (line: int) =
        match cache.TryGetValue rel with
        | true, lines when line >= 1 && line <= lines.Length -> lines[line - 1]
        | _ -> ""

    let show (hits: (string * int * int * string) list) =
        box.Items.Clear()

        for (rel, line, col, text) in hits do
            let row =
                TextBlock(
                    Text = ReferenceRows.row rel line col text,
                    FontFamily = ui.MonoFont,
                    Foreground = ui.Roles.Fg,
                    Tag = (rel, line, col)
                )

            Access.describe (sprintf "Reference in %s, line %d" rel line) text row |> ignore
            box.Items.Add row |> ignore

    let findNow () =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            match sessionOf d with
            | None ->
                syncLsp d // starts the server if the folder is trusted
                say "Find references needs a language server for this file; none is running yet"
            | Some s when not s.Client.SupportsReferences ->
                say "The language server for this file does not support find references"
            | Some s ->
                syncLsp d
                let caret = ui.Editor.TextArea.Caret

                let pos =
                    { Line = caret.Line - 1
                      Character = caret.Column - 1 }

                let full = Paths.fullPath r d.Rel
                let openTexts = [ for x in ui.Docs -> x.Rel, x.Text ]

                task {
                    let! res = s.Client.References(full, pos)

                    match res with
                    | Error m -> say ("Find references failed: " + m)
                    | Ok locs ->
                        let hits = ReferenceRows.inside r locs
                        // reading the files is done off the UI thread
                        let! lines = Task.Run(fun () -> linesOf r openTexts (hits |> List.map (fun (p, _, _) -> p)))
                        show [ for (p, l, c) in hits -> p, l, c, lineText lines p l ]
                        say (ReferenceRows.summary hits)

                        if not hits.IsEmpty then
                            appearance.SetPanelVisible true
                            appearance.ShowPanel "references"
                }
                |> ignore
        | _ -> say "Open a file in a folder first"

    do
        box.SelectionChanged.Add(fun _ ->
            match box.SelectedItem with
            | :? TextBlock as t ->
                match t.Tag with
                | :? (string * int * int) as hit ->
                    let (p, l, c) = hit
                    box.SelectedItem <- null // so the same row can be chosen again
                    ui.OpenFileFn p l c
                | _ -> ()
            | _ -> ())

    member _.FindNow = findNow
