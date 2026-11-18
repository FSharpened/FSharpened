/// Signature help: while the arguments of a call are typed, a box by the caret shows the signature with the current
/// parameter in bold. It opens on the characters the server names (usually "(" and ","), closes on Escape or when the
/// caret leaves the line, and can be opened by hand (Ctrl+Shift+Space).
namespace FSharpened.Ide.Desktop

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Input
open Avalonia.Media
open AvaloniaEdit
open AvaloniaEdit.Rendering
open FSharpened.Ide
open FSharpened.Ide.Host

type SignatureFeature(ui: Ui, languageServers: LanguageServers) =
    let editor = ui.Editor
    let box = ui.SignatureBox
    let text = ui.SignatureText
    let sessionOf = languageServers.SessionOf
    let syncLsp = languageServers.SyncLsp
    let mutable request = 0
    let mutable shownLine = 0

    let hide () =
        request <- request + 1
        box.IsVisible <- false

    let render (h: SignatureHelp) =
        let before, active, after = SignatureView.parts h
        text.Inlines.Clear()
        text.Inlines.Add(Documents.Run(before))
        text.Inlines.Add(Documents.Run(active, FontWeight = FontWeight.Bold, Foreground = ui.Roles.Accent))
        text.Inlines.Add(Documents.Run(after))

        match SignatureView.counter h with
        | "" -> ()
        | c -> text.Inlines.Add(Documents.Run("   " + c, Foreground = ui.Roles.Muted))

        match SignatureView.documentation h with
        | "" -> ()
        | doc ->
            text.Inlines.Add(Documents.LineBreak())

            let shown =
                if doc.Length > 240 then
                    doc.Substring(0, 240) + "…"
                else
                    doc

            text.Inlines.Add(Documents.Run(shown, Foreground = ui.Roles.Muted, FontFamily = FontFamily.Default))

        Avalonia.Automation.AutomationProperties.SetHelpText(box, before + active + after)

    /// Puts the box under the caret's line.
    let place () =
        let c = editor.TextArea.Caret
        let tv = editor.TextArea.TextView

        let v =
            tv.GetVisualPosition(TextViewPosition(c.Line, c.Column), VisualYPosition.LineBottom)

        match tv.TranslatePoint(Point(v.X - tv.ScrollOffset.X, v.Y - tv.ScrollOffset.Y), editor) with
        | p when p.HasValue -> box.Margin <- Thickness(max 0.0 p.Value.X, max 0.0 p.Value.Y + 2.0, 0.0, 0.0)
        | _ -> ()

    let showNow () =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            match sessionOf d with
            | Some s when s.Client.SupportsSignatureHelp ->
                syncLsp d
                request <- request + 1
                let mine = request
                let caret = editor.TextArea.Caret
                let line = caret.Line

                let pos =
                    { Line = caret.Line - 1
                      Character = caret.Column - 1 }

                task {
                    let! h = s.Client.SignatureHelp(Paths.fullPath r d.Rel, pos)

                    if mine = request then
                        match h with
                        | Some h ->
                            render h
                            shownLine <- line
                            place ()
                            box.IsVisible <- true
                        | None -> box.IsVisible <- false
                }
                |> ignore
            | _ -> ()
        | _ -> ()

    do
        editor.TextArea.TextEntered.Add(fun e ->
            match ui.Active with
            | Some d ->
                match sessionOf d with
                | Some s when s.Client.SupportsSignatureHelp ->
                    if SignatureView.isTrigger s.Client.SignatureTriggers e.Text then
                        showNow ()
                    elif e.Text = ")" && box.IsVisible then
                        showNow () // an outer call may still be open
                | _ -> ()
            | None -> ())

        // Escape closes the box even where the editor itself has handled the key
        editor.TextArea.AddHandler(
            InputElement.KeyDownEvent,
            EventHandler<KeyEventArgs>(fun _ e ->
                if e.Key = Key.Escape && box.IsVisible then
                    hide ()),
            Interactivity.RoutingStrategies.Tunnel
            ||| Interactivity.RoutingStrategies.Bubble,
            true
        )

        editor.TextArea.Caret.PositionChanged.Add(fun _ ->
            if box.IsVisible && editor.TextArea.Caret.Line <> shownLine then
                hide ())

    member _.Hide = hide
    member _.ShowNow = showNow
