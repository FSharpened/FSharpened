/// The outline from the language server: when a server gives document symbols they replace the pattern-based outline
/// of the sidebar, the breadcrumb and Ctrl+Shift+O (which stays the answer for languages without a server).
namespace FSharpened.Ide.Desktop

open System
open FSharpened.Ide
open FSharpened.Ide.Host

type OutlineFeature(ui: Ui, languageServers: LanguageServers, statusBar: StatusBar) =
    let sessionOf = languageServers.SessionOf
    let syncLsp = languageServers.SyncLsp
    let mutable generation = 0

    /// Asks for the outline of an open file and, when the text has not changed meanwhile, shows it. A server that gives
    /// nothing leaves the pattern-based outline in place.
    let refresh (d: OpenDoc) =
        match ui.Root, sessionOf d with
        | Some r, Some s when s.Client.SupportsDocumentSymbols ->
            syncLsp d

            if d.LspOpen then
                generation <- generation + 1
                let mine = generation
                let text = d.Text

                task {
                    let! res = s.Client.DocumentSymbols(Paths.fullPath r d.Rel)

                    match res with
                    | Ok symbols when mine = generation && d.Text = text ->
                        match LspOutline.toOutline symbols with
                        | [] -> ()
                        | outline ->
                            d.Outline <- outline

                            if ui.Active = Some d then
                                statusBar.UpdateStatus()
                                ui.RefreshSidebarFn()
                    | _ -> ()
                }
                |> ignore
        | _ -> ()

    member _.Refresh = refresh
