/// Quick fixes: a lightbulb at the caret's line when the language server has a fix or refactoring there, and a menu of
/// them (Ctrl+Period, or a click on the bulb). An action's edit is applied like a rename's, without a preview, and stays
/// unsaved so Undo takes it back.
namespace FSharpened.Ide.Desktop

open System
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Threading
open AvaloniaEdit
open AvaloniaEdit.Rendering
open FSharpened.Ide
open FSharpened.Ide.Host

type CodeActionFeature(ui: Ui, languageServers: LanguageServers, renameFeature: RenameFeature) =
    let editor = ui.Editor
    let bulb = ui.Lightbulb
    let say = ui.Say
    let sessionOf = languageServers.SessionOf
    let syncLsp = languageServers.SyncLsp
    let timer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 450.0)
    let mutable generation = 0
    let mutable offered: LspCodeAction list = []

    /// The actions for the caret's line (or the selection), best first.
    let query () : Task<LspCodeAction list> =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            match sessionOf d with
            | Some s when s.Client.SupportsCodeActions && d.LspOpen ->
                syncLsp d
                let area = editor.TextArea
                let doc = editor.Document
                let sel = area.Selection

                let at (offset: int) =
                    let loc = doc.GetLocation offset

                    { Line = loc.Line - 1
                      Character = loc.Column - 1 }

                let range =
                    if sel.IsEmpty then
                        { Start = at editor.CaretOffset
                          End = at editor.CaretOffset }
                    else
                        { Start = at (min sel.SurroundingSegment.Offset sel.SurroundingSegment.EndOffset)
                          End = at (max sel.SurroundingSegment.Offset sel.SurroundingSegment.EndOffset) }

                let diagnostics =
                    match ui.LspRaw.TryGetValue d.Rel with
                    | true, all ->
                        all
                        |> List.filter (fun x ->
                            x.Range.Start.Line <= range.End.Line && range.Start.Line <= x.Range.End.Line)
                    | _ -> []

                task {
                    let! res = s.Client.CodeActions(Paths.fullPath r d.Rel, range, diagnostics)

                    match res with
                    | Ok list -> return QuickFix.order list
                    | Error _ -> return []
                }
            | _ -> Task.FromResult []
        | _ -> Task.FromResult []

    let placeBulb () =
        let c = editor.TextArea.Caret
        let tv = editor.TextArea.TextView

        let v = tv.GetVisualPosition(TextViewPosition(c.Line, 1), VisualYPosition.LineTop)

        match tv.TranslatePoint(Point(0.0, v.Y - tv.ScrollOffset.Y), editor) with
        | p when p.HasValue -> bulb.Margin <- Thickness(p.Value.X + 2.0, max 0.0 (p.Value.Y - 16.0), 0.0, 0.0)
        | _ -> ()

    let hideBulb () =
        generation <- generation + 1
        offered <- []
        bulb.IsVisible <- false

    /// Looks for fixes at the caret and shows or hides the bulb.
    let refresh () =
        generation <- generation + 1
        let mine = generation

        task {
            let! actions = query ()

            if mine = generation then
                offered <- actions

                if actions |> List.exists QuickFix.applicable then
                    placeBulb ()
                    bulb.IsVisible <- true
                else
                    bulb.IsVisible <- false
        }
        |> ignore

    /// Carries out one action: resolves it when the server left its edit out, then applies the edit.
    let apply (a: LspCodeAction) : Task =
        match ui.Active with
        | Some d ->
            match sessionOf d with
            | Some s ->
                task {
                    let! resolved = s.Client.ResolveCodeAction a

                    match resolved.Edit with
                    | Some we -> say (renameFeature.ApplyServerEdit (sprintf "Applied “%s”" a.Title) we)
                    | None when a.HasCommand ->
                        say (sprintf "“%s” needs the server to run a command, which the editor does not do yet" a.Title)
                    | None -> say (sprintf "The server gave no edit for “%s”" a.Title)

                    hideBulb ()
                }
            | None -> Task.CompletedTask
        | None -> Task.CompletedTask

    let showMenu () =
        task {
            let! actions = query ()
            offered <- actions

            if actions.IsEmpty then
                say "No quick fixes here"
            else
                let menu = ContextMenu(Placement = PlacementMode.Bottom)
                Access.describe "Quick fixes" "Choose a fix to apply." menu |> ignore

                for a in actions do
                    let item =
                        MenuItem(Header = QuickFix.menuTitle a, IsEnabled = QuickFix.applicable a)

                    Access.nameOnly (QuickFix.menuTitle a) "" item |> ignore
                    item.Click.Add(fun _ -> apply a |> ignore)
                    menu.Items.Add item |> ignore

                placeBulb ()

                menu.Open(
                    if bulb.IsVisible then
                        bulb :> Control
                    else
                        editor :> Control
                )
        }
        |> ignore

    do
        timer.Tick.Add(fun _ ->
            timer.Stop()
            refresh ())

        editor.TextArea.Caret.PositionChanged.Add(fun _ ->
            bulb.IsVisible <- false
            timer.Stop()
            timer.Start())

        bulb.Click.Add(fun _ -> showMenu ())

    /// The titles offered at the caret right now, and whether each can be applied (for the tests).
    member _.Offered = offered |> List.map (fun a -> a.Title, QuickFix.applicable a)

    member _.Query = query
    member _.Refresh = refresh
    member _.ShowMenu = showMenu

    /// Applies the offered action with this title (for the tests and the screenshot switches).
    member _.ApplyByTitle(title: string) : Task =
        match offered |> List.tryFind (fun a -> a.Title = title) with
        | Some a -> apply a
        | None -> Task.CompletedTask
