/// The types the window's parts share: a file open in a tab, the underlines, a completion item, a command.
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

/// A file open in a tab.
type OpenDoc(rel: string, text: string, pack: LanguagePack option) =
    member val Rel = rel with get, set
    member val Doc = TextDocument(text)
    member val Pack = pack
    member val Line = 1 with get, set
    member val Column = 1 with get, set
    member val Outline: Symbol list = [] with get, set
    /// The language server has been told about this file.
    member val LspOpen = false with get, set
    /// The text changed since the language server last saw it.
    member val LspDirty = false with get, set
    /// The file is gone from disk (deleted or renamed outside the IDE); the tab stays so the text is not lost.
    member val MissingOnDisk = false with get, set
    /// The text of the file on disk that the user already chose to keep their own version over.
    member val KeptOverDisk: string option = None with get, set
    /// What the IDE last read from or wrote to the file. A change on disk that equals it is the IDE's own save.
    member val DiskText = text with get, set
    /// Where the breakpoints of this file are; anchors move with the text, so a breakpoint stays on its line.
    member val Breakpoints = List<TextAnchor>()
    member d.Dirty = not d.Doc.UndoStack.IsOriginalFile
    member d.Text = d.Doc.Text

    /// Replaces the text with what is on disk and forgets the undo history.
    member d.Reload(text: string) =
        d.DiskText <- text
        d.Doc.Text <- text
        d.Doc.UndoStack.ClearAll()
        d.Doc.UndoStack.MarkAsOriginalFile()

    member d.MarkSaved() =
        d.DiskText <- d.Doc.Text
        d.Doc.UndoStack.MarkAsOriginalFile()

/// Wavy-looking underlines for the language server's problems, drawn behind the text.
type Squiggles() =
    member val Ranges: (int * int * IBrush) list = [] with get, set

    interface IBackgroundRenderer with
        member _.Layer = KnownLayer.Selection

        member this.Draw(textView: TextView, context: DrawingContext) =
            if textView.VisualLinesValid then
                for (start, length, brush) in this.Ranges do
                    let seg = TextSegment(StartOffset = start, Length = max 1 length)

                    for r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg) do
                        context.DrawLine(Pen(brush, 1.5), Point(r.Left, r.Bottom - 1.0), Point(r.Right, r.Bottom - 1.0))

/// One suggestion from a language server in the completion list.
type LspItem(item: LspCompletion) =
    interface ICompletionData with
        member _.Image = null
        member _.Text = item.Label
        member _.Content = box item.Label
        member _.Description = box item.Detail
        member _.Priority = 0.0

        member _.Complete(area, segment, _) =
            area.Document.Replace(segment, item.Insert)

/// One row of the command palette.
type Command =
    { Id: string
      Title: string
      Keys: string
      Run: unit -> unit }
