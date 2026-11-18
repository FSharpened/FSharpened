/// The editor features that ask the language server: find references, the outline, signature help, quick fixes and
/// formatting. One place builds them and connects them to the window's events.
namespace FSharpened.Ide.Desktop

open System.Threading.Tasks
open FSharpened.Ide.Host

type ServerFeatures
    (
        ui: Ui,
        appearance: Appearance,
        statusBar: StatusBar,
        languageServers: LanguageServers,
        runPanel: RunPanel,
        renameFeature: RenameFeature
    ) =
    let references = ReferencesFeature(ui, languageServers, appearance)
    let outline = OutlineFeature(ui, languageServers, statusBar)
    let signature = SignatureFeature(ui, languageServers)
    let codeActions = CodeActionFeature(ui, languageServers, renameFeature)
    let format = FormatFeature(ui, languageServers, runPanel, renameFeature)

    do
        ui.OnDocShown.Add outline.Refresh
        ui.OnSettled.Add outline.Refresh

    member _.FindReferences = references.FindNow
    member _.ShowSignatureHelp = signature.ShowNow
    member _.QuickFix = codeActions.ShowMenu
    member _.FormatDocument = format.FormatNow

    /// The quick fixes offered at the caret now, as (title, can be applied): for the tests.
    member _.OfferedFixes = codeActions.Offered
    member _.RefreshFixes = codeActions.Refresh
    member _.ApplyFix(title: string) : Task = codeActions.ApplyByTitle title
