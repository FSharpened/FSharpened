/// The editing features that work on the text itself: several cursors and snippets. One place builds them, in the order
/// their key handlers must run (several cursors first: with extra cursors present, Tab is theirs).
namespace FSharpened.Ide.Desktop

type EditorFeatures(ui: Ui) =
    let multi = MultiCursorFeature(ui)
    let snippets = SnippetFeature(ui)

    member _.Cursors = multi
    member _.Snippets = snippets
