/// Format Document: the language server's formatting when it has one, otherwise the formatter the language pack names
/// (which runs a tool on the saved file).
namespace FSharpened.Ide.Desktop

open System
open FSharpened.Ide
open FSharpened.Ide.Host

type FormatFeature(ui: Ui, languageServers: LanguageServers, runPanel: RunPanel, renameFeature: RenameFeature) =
    let say = ui.Say
    let sessionOf = languageServers.SessionOf
    let syncLsp = languageServers.SyncLsp

    let formatNow () =
        match ui.Root, ui.Active with
        | Some r, Some d ->
            match sessionOf d with
            | Some s when s.Client.SupportsFormatting ->
                syncLsp d
                let full = Paths.fullPath r d.Rel
                let before = d.Text

                task {
                    let! res = s.Client.FormatDocument(full, ServerFormat.options d.Pack ui.Prefs.Editor)

                    match res with
                    | Error m ->
                        // the server could not: the pack's formatter gets its turn
                        say (sprintf "The language server could not format (%s); trying the language's formatter" m)
                        runPanel.FormatCurrent()
                    | Ok [] -> say "Already formatted"
                    | Ok edits ->
                        if d.Text <> before then
                            say "The text changed while it was being formatted; run Format Document again"
                        else
                            let we =
                                { Files = [ full, edits ]
                                  Unsupported = [] }

                            match renameFeature.ApplyServerEdit "Formatted" we with
                            | m when m.StartsWith "Formatted" -> say "Formatted by the language server"
                            | m -> say m
                }
                |> ignore
            | _ ->
                syncLsp d // a server not started yet may be, for next time
                runPanel.FormatCurrent()
        | _ -> ()

    member _.FormatNow = formatNow
