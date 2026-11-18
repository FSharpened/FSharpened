/// The IDE window: the website IDE's layout (activity bar, sidebar, tabs, editor, panel, status bar, command palette) as
/// native controls over the same F# core. Imperative on purpose: one place that builds the tree and one that reacts.
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

type Shell(env: HostEnv) =
    let window =
        Window(Title = "FSharpened", Width = 1320.0, Height = 820.0, MinWidth = 760.0, MinHeight = 480.0)

    let mutable prefs = env.LoadPrefs()
    let mutable theme = Palette.find prefs.Editor.Theme
    let mutable roles = Palette.ofTheme theme
    let hooks = List<Palette.Roles -> unit>()

    // ---- state ------------------------------------------------------------------------------------
    let mutable root: string option = None
    let docs = List<OpenDoc>()
    let mutable active: OpenDoc option = None
    let mutable fileCache: Entry list = []
    let mutable running = false
    let mutable problemsList: FSharpened.Diagnostics.StructuredDiagnostic list = []
    let mutable watcher: RunningWatch option = None
    let mutable watchSub: IDisposable option = None
    let mutable lastSearchEngine = ""
    let mutable quitting = false
    let expanded = HashSet<string>()
    let mutable gitBranch: string option = None
    let mutable gitChanges: Map<string, GitChange> = Map.empty
    let mutable runDiagnostics: FSharpened.Diagnostics.StructuredDiagnostic list = []

    let lspDiagnostics =
        Dictionary<string, FSharpened.Diagnostics.StructuredDiagnostic list>()

    let lspSessions = Dictionary<string, LspSession>()
    let lspFailed = HashSet<string>()
    let lspStarting = HashSet<string>()
    let squiggles = Squiggles()

    let monoFont =
        FontFamily "Cascadia Code, Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"

    let themed (apply: Palette.Roles -> unit) =
        hooks.Add apply
        apply roles

    // ---- small builders ---------------------------------------------------------------------------
    let label (text: string) =
        let t = TextBlock(Text = text, VerticalAlignment = VerticalAlignment.Center)
        themed (fun r -> t.Foreground <- r.Fg)
        t

    let muted (text: string) =
        let t =
            TextBlock(Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap)

        themed (fun r -> t.Foreground <- r.Muted)
        t

    let flat (content: obj) (tip: string) (click: unit -> unit) =
        let b =
            Button(
                Content = content,
                Padding = Thickness(8.0, 3.0),
                Background = Brushes.Transparent,
                BorderThickness = Thickness 0.0
            )

        ToolTip.SetTip(b, tip)
        themed (fun r -> b.Foreground <- r.Fg)
        b.Click.Add(fun _ -> click ())
        b

    let panelBorder (child: Control) (pick: Palette.Roles -> IBrush) =
        let b = Border(Child = child)
        themed (fun r -> b.Background <- pick r)
        b

    /// Fluent paints a focused or hovered text box with its own colours; use the theme's instead.
    let restyleBox (t: TextBox) (r: Palette.Roles) =
        for key in
            [ "TextControlBackground"
              "TextControlBackgroundPointerOver"
              "TextControlBackgroundFocused"
              "TextControlBorderBrush"
              "TextControlBorderBrushPointerOver" ] do
            t.Resources[key] <-
                (if key.StartsWith "TextControlBorder" then
                     r.Line
                 else
                     r.Code)

        t.Background <- r.Code
        t.Foreground <- r.Fg
        t.BorderBrush <- r.Line

    let textBox (hint: string) =
        let t =
            TextBox(PlaceholderText = hint, FontFamily = monoFont, VerticalContentAlignment = VerticalAlignment.Center)

        themed (restyleBox t)
        t

    let menuItem (header: string) (gesture: string) (click: unit -> unit) =
        let m = MenuItem(Header = header)

        if gesture <> "" then
            try
                m.InputGesture <- KeyGesture.Parse gesture
            with _ ->
                () // a gesture only shown in the palette, such as Ctrl+=, has no menu form

        m.Click.Add(fun _ -> click ())
        m

    // ---- editor -----------------------------------------------------------------------------------
    let editor =
        AvaloniaEdit.TextEditor(
            FontFamily = monoFont,
            FontSize = float prefs.Editor.FontSize,
            ShowLineNumbers = prefs.Editor.LineNumbers,
            WordWrap = prefs.Editor.WordWrap,
            HorizontalScrollBarVisibility = Primitives.ScrollBarVisibility.Auto,
            Padding = Thickness(4.0, 6.0, 4.0, 6.0)
        )

    let registryOptions = RegistryOptions(ThemeName.DarkPlus)

    // grammars from the app's `grammars` folder, then the user's (which win), in front of TextMateSharp's own
    let grammarFiles =
        Grammars.index
            [ Path.Combine(AppContext.BaseDirectory, "grammars")
              Path.Combine(env.DataDir, "grammars") ]

    let packRegistry = Grammars.PackRegistry(registryOptions, grammarFiles)

    let textMate =
        editor.InstallTextMate(packRegistry :> TextMateSharp.Registry.IRegistryOptions)

    let searchPanel = SearchPanel.Install(editor)

    let textMateTheme (t: ThemeSpec) =
        match t.Id with
        | "light"
        | "light-plus" -> ThemeName.LightPlus
        | "monokai" -> ThemeName.Monokai
        | "hc-dark" -> ThemeName.HighContrastDark
        | "hc-light" -> ThemeName.HighContrastLight
        | _ -> ThemeName.DarkPlus

    // ---- chrome -----------------------------------------------------------------------------------
    let statusFolder =
        TextBlock(Text = "No folder open", VerticalAlignment = VerticalAlignment.Center)

    let statusRun = TextBlock(Text = "", VerticalAlignment = VerticalAlignment.Center)
    let statusPos = TextBlock(Text = "", VerticalAlignment = VerticalAlignment.Center)
    let statusLang = TextBlock(Text = "", VerticalAlignment = VerticalAlignment.Center)
    let statusGit = TextBlock(Text = "", VerticalAlignment = VerticalAlignment.Center)
    let statusWatch = TextBlock(Text = "", VerticalAlignment = VerticalAlignment.Center)

    let statusProblems =
        TextBlock(Text = "", VerticalAlignment = VerticalAlignment.Center)

    let crumbs =
        TextBlock(
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.0,
            Margin = Thickness(12.0, 4.0)
        )

    let tabStrip = StackPanel(Orientation = Orientation.Horizontal)

    let welcome =
        StackPanel(
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10.0
        )

    let fileTree = TreeView(Background = Brushes.Transparent)
    let sidebarHost = ContentControl()

    let sidebarTitle =
        TextBlock(FontSize = 11.0, FontWeight = FontWeight.SemiBold, Margin = Thickness(12.0, 10.0, 12.0, 6.0))

    let outputBox =
        TextBox(
            IsReadOnly = true,
            FontFamily = monoFont,
            AcceptsReturn = true,
            BorderThickness = Thickness 0.0,
            TextWrapping = TextWrapping.NoWrap
        )

    let problemsBox = ListBox(Background = Brushes.Transparent)

    let inputBox =
        TextBox(
            AcceptsReturn = true,
            FontFamily = monoFont,
            BorderThickness = Thickness 0.0,
            PlaceholderText = "Text the program reads from standard input"
        )

    let flagsBox =
        TextBox(
            Width = 150.0,
            PlaceholderText = "flags, e.g. -O2",
            FontFamily = monoFont,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12.0
        )

    // the terminal panel: the user's shell on pipes (no pseudo-terminal), output above, one input line below
    let termOutput =
        TextBox(
            IsReadOnly = true,
            FontFamily = monoFont,
            AcceptsReturn = true,
            BorderThickness = Thickness 0.0,
            TextWrapping = TextWrapping.NoWrap
        )

    let termInput =
        TextBox(
            FontFamily = monoFont,
            BorderThickness = Thickness(0.0, 1.0, 0.0, 0.0),
            PlaceholderText = "Type a command and press Enter"
        )

    let mutable term: TermSession option = None
    let termHistory = List<string>()
    let mutable termHistoryAt = 0
    let mutable terminalShownFn: unit -> unit = ignore

    let paletteInput =
        TextBox(
            PlaceholderText = "Type to open a file, > for commands, : for a line, @ for a symbol",
            FontFamily = monoFont
        )

    let paletteList = ListBox(Background = Brushes.Transparent, MaxHeight = 360.0)

    let palette =
        Border(
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = Thickness(0.0, 54.0, 0.0, 0.0),
            Width = 640.0,
            CornerRadius = CornerRadius 8.0,
            BorderThickness = Thickness 1.0,
            ZIndex = 50
        )

    let bodyGrid = Grid()
    let mainGrid = Grid()
    let sidebarColumn = ColumnDefinition(Width = GridLength 280.0)
    let splitColumn = ColumnDefinition(Width = GridLength 4.0)
    let bottomRow = RowDefinition(Height = GridLength 230.0)
    let bottomSplitRow = RowDefinition(Height = GridLength 4.0)
    let panelTabs = List<string * Button * Control>()
    let mutable panelShown = true
    let mutable sidebarShown = true
    let mutable sidebarView = "files"
    let mutable currentPanel = "output"

    // ---- forward references (the pieces call each other) ------------------------------------------
    let mutable openFileFn: string -> int -> int -> unit = fun _ _ _ -> ()
    let mutable refreshTreeFn: unit -> unit = ignore
    let mutable refreshSidebarFn: unit -> unit = ignore
    let mutable showPaletteFn: string -> unit = ignore

    let say (text: string) = statusRun.Text <- text

    let setOutput (text: string) = outputBox.Text <- text

    let showPanel (name: string) =
        currentPanel <- name

        if name = "terminal" then
            terminalShownFn ()

        for (n, button, content) in panelTabs do
            content.IsVisible <- (n = name)
            button.FontWeight <- (if n = name then FontWeight.Bold else FontWeight.Normal)

    let setPanelVisible (v: bool) =
        panelShown <- v
        bottomRow.Height <- (if v then GridLength 230.0 else GridLength 0.0)
        bottomSplitRow.Height <- (if v then GridLength 4.0 else GridLength 0.0)

    let setSidebarVisible (v: bool) =
        sidebarShown <- v
        sidebarColumn.Width <- (if v then GridLength 280.0 else GridLength 0.0)
        splitColumn.Width <- (if v then GridLength 4.0 else GridLength 0.0)

    let packFor (rel: string) : LanguagePack option = Languages.forFile env.Registry rel

    let rootName () =
        match root with
        | Some r -> Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        | None -> ""

    let applyEditorPrefs () =
        let e = prefs.Editor
        editor.FontSize <- float e.FontSize
        editor.ShowLineNumbers <- e.LineNumbers
        editor.WordWrap <- e.WordWrap
        editor.Options.ShowSpaces <- e.RenderWhitespace
        editor.Options.ShowTabs <- e.RenderWhitespace
        editor.Options.IndentationSize <- e.TabSize
        editor.Options.ConvertTabsToSpaces <- e.InsertSpaces
        crumbs.IsVisible <- e.Breadcrumbs

    let savePrefs () = env.SavePrefs prefs

    let applyGrammar (d: OpenDoc option) =
        try
            match d with
            | Some(doc: OpenDoc) when
                (match doc.Pack with
                 | Some p -> p.Scope <> ""
                 | None -> false)
                ->
                textMate.SetGrammar doc.Pack.Value.Scope
            | Some doc ->
                let ext = Path.GetExtension doc.Rel
                let scope = registryOptions.GetScopeByExtension ext
                textMate.SetGrammar(if String.IsNullOrEmpty scope then null else scope)
            | None -> textMate.SetGrammar null
        with _ ->
            ()

    let applyTheme (t: ThemeSpec) =
        theme <- t
        roles <- Palette.ofTheme t

        prefs <-
            { prefs with
                Editor = { prefs.Editor with Theme = t.Id } }

        match Application.Current with
        | null -> ()
        | app -> app.RequestedThemeVariant <- (if roles.Dark then ThemeVariant.Dark else ThemeVariant.Light)

        try
            textMate.SetTheme(registryOptions.LoadTheme(textMateTheme t))
        with _ ->
            ()

        editor.Background <- roles.EditorBg
        editor.Foreground <- roles.Fg
        editor.LineNumbersForeground <- roles.Muted
        editor.TextArea.SelectionBrush <- roles.Selection
        window.Background <- roles.Bg

        for h in hooks do
            h roles

        savePrefs ()

    // ---- diagnostics and positions ----------------------------------------------------------------
    let relOfDiagnosticFile (file: string) : string option =
        match root with
        | None -> None
        | Some r ->
            let f = file.Replace('\\', '/')
            let rr = r.Replace('\\', '/').TrimEnd('/') + "/"

            let rel =
                if f.StartsWith(rr, StringComparison.OrdinalIgnoreCase) then
                    f.Substring rr.Length
                elif f.StartsWith "./" then
                    f.Substring 2
                else
                    f

            match DiskWorkspace.resolve r rel with
            | Ok p when File.Exists p -> Some rel
            | _ ->
                let name = Path.GetFileName f

                fileCache
                |> List.tryFind (fun e -> e.Name = name)
                |> Option.map (fun e -> e.Rel)

    let renderProblems (list: FSharpened.Diagnostics.StructuredDiagnostic list) =
        problemsList <- list
        problemsBox.Items.Clear()

        for d in list do
            let colour =
                match d.Severity with
                | FSharpened.Diagnostics.Severity.Error -> roles.Bad
                | FSharpened.Diagnostics.Severity.Warning -> roles.Warn
                | _ -> roles.Muted

            let glyph =
                match d.Severity with
                | FSharpened.Diagnostics.Severity.Error -> "✖"
                | FSharpened.Diagnostics.Severity.Warning -> "⚠"
                | _ -> "ℹ"

            let where =
                match d.Range with
                | Some r -> sprintf "%s:%d:%d" r.File r.StartLine r.StartColumn
                | None -> ""

            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0, Tag = d)
            row.Children.Add(TextBlock(Text = glyph, Foreground = colour))

            let msg =
                TextBlock(
                    Text =
                        (if d.Code <> "" then
                             d.Code + "  " + d.Message
                         else
                             d.Message),
                    Foreground = roles.Fg
                )

            row.Children.Add msg
            row.Children.Add(TextBlock(Text = where, Foreground = roles.Muted))
            problemsBox.Items.Push row

        let errors =
            list
            |> List.filter (fun d -> d.Severity = FSharpened.Diagnostics.Severity.Error)
            |> List.length

        let warns =
            list
            |> List.filter (fun d -> d.Severity = FSharpened.Diagnostics.Severity.Warning)
            |> List.length

        statusProblems.Text <- sprintf "✖ %d   ⚠ %d" errors warns

    // ---- tabs and documents -----------------------------------------------------------------------
    let updateStatus () =
        match active with
        | Some d ->
            let p = editor.TextArea.Caret
            d.Line <- p.Line
            d.Column <- p.Column
            statusPos.Text <- sprintf "Ln %d, Col %d" p.Line p.Column

            statusLang.Text <-
                match d.Pack with
                | Some pack -> pack.Name
                | None -> "Plain text"

            let trail = Symbols.enclosing d.Outline p.Line |> List.map (fun s -> s.Name)
            let parts = (d.Rel.Split('/') |> Array.toList) @ trail
            crumbs.Text <- String.Join("  ›  ", parts)
        | None ->
            statusPos.Text <- ""
            statusLang.Text <- ""
            crumbs.Text <- ""

    let recomputeOutline (d: OpenDoc) =
        d.Outline <-
            try
                Symbols.outlineOfPath d.Rel d.Text
            with _ ->
                []

    let outlineTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 350.0)

    // ---- language servers and the problems list ---------------------------------------------------
    let toStructured (rel: string) (d: LspDiagnostic) : FSharpened.Diagnostics.StructuredDiagnostic =
        let sev =
            match d.Severity with
            | 1 -> FSharpened.Diagnostics.Severity.Error
            | 2 -> FSharpened.Diagnostics.Severity.Warning
            | _ -> FSharpened.Diagnostics.Severity.Info

        { FSharpened.Diagnostics.StructuredDiagnostic.create d.Code sev d.Message with
            Range =
                Some
                    { File = rel
                      StartLine = d.Range.Start.Line + 1
                      StartColumn = d.Range.Start.Character + 1
                      EndLine = d.Range.End.Line + 1
                      EndColumn = d.Range.End.Character + 1 } }

    let refreshProblems () =
        renderProblems (
            runDiagnostics
            @ [ for kv in lspDiagnostics do
                    yield! kv.Value ]
        )

    let updateSquiggles () =
        match active with
        | Some d ->
            let doc = d.Doc

            let offsetOf (line: int) (col: int) =
                let l = max 1 (min doc.LineCount line)
                let ln = doc.GetLineByNumber l
                ln.Offset + max 0 (min ln.Length (col - 1))

            squiggles.Ranges <-
                match lspDiagnostics.TryGetValue d.Rel with
                | true, list ->
                    [ for x in list do
                          match x.Range with
                          | Some r ->
                              let a = offsetOf r.StartLine r.StartColumn
                              let b = offsetOf r.EndLine r.EndColumn

                              let brush =
                                  match x.Severity with
                                  | FSharpened.Diagnostics.Severity.Error -> roles.Bad
                                  | FSharpened.Diagnostics.Severity.Warning -> roles.Warn
                                  | _ -> roles.Accent

                              yield (a, max 1 (b - a), brush)
                          | None -> () ]
                | _ -> []
        | None -> squiggles.Ranges <- []

        editor.TextArea.TextView.InvalidateLayer KnownLayer.Selection

    let storeLspDiagnostics (path: string) (list: LspDiagnostic list) =
        match root with
        | Some r ->
            let rel = Path.GetRelativePath(r, path).Replace('\\', '/')

            if not (rel.StartsWith "..") && not (Path.IsPathRooted rel) then
                if list.IsEmpty then
                    lspDiagnostics.Remove rel |> ignore
                else
                    lspDiagnostics[rel] <- list |> List.map (toStructured rel)

                refreshProblems ()
                updateSquiggles ()
        | None -> ()

    let sessionOf (d: OpenDoc) : LspSession option =
        match d.Pack with
        | Some p ->
            match lspSessions.TryGetValue p.Id with
            | true, s when s.Client.IsOpen -> Some s
            | _ -> None
        | None -> None

    let rec syncLsp (d: OpenDoc) =
        match root, d.Pack with
        | Some r, Some pack when pack.Server.IsSome ->
            match sessionOf d with
            | Some s ->
                let full = Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar))

                if not d.LspOpen then
                    s.Client.DidOpen(full, pack.Id, d.Text)
                    d.LspOpen <- true
                elif d.LspDirty then
                    s.Client.DidChange(full, d.Text)

                d.LspDirty <- false
            | None ->
                if
                    env.Trust.IsTrusted r
                    && not (lspFailed.Contains pack.Id)
                    && lspStarting.Add pack.Id
                then
                    let spec = pack.Server.Value
                    say (sprintf "Starting %s…" spec.Command)

                    task {
                        let! started =
                            Task.Run(fun () ->
                                match
                                    Lsp.start spec (fun p ds ->
                                        Dispatcher.UIThread.Post(fun () -> storeLspDiagnostics p ds))
                                with
                                | Ok s ->
                                    if s.Client.Initialize(r).Result then
                                        Ok s
                                    else
                                        (s.Stop()
                                         Error(sprintf "%s did not answer" spec.Command))
                                | Error e -> Error e)

                        lspStarting.Remove pack.Id |> ignore

                        match started with
                        | Ok s ->
                            lspSessions[pack.Id] <- s
                            say (sprintf "Language server %s is ready" spec.Command)

                            for doc in docs |> Seq.toList do
                                if doc.Pack.IsSome && doc.Pack.Value.Id = pack.Id then
                                    doc.LspOpen <- false
                                    syncLsp doc
                        | Error e ->
                            lspFailed.Add pack.Id |> ignore
                            say (sprintf "No language server for %s: %s" pack.Name (e.Split('\n')[0]))
                    }
                    |> ignore
        | _ -> ()

    // ---- terminal ---------------------------------------------------------------------------------
    let termMaxChars = 200000

    /// Appends text to the terminal view from any thread, keeping the last part when it grows large.
    let termAppend (text: string) =
        Dispatcher.UIThread.Post(fun () ->
            let current = termOutput.Text |> Option.ofObj |> Option.defaultValue ""
            let joined = current + text

            termOutput.Text <-
                if joined.Length > termMaxChars then
                    joined.Substring(joined.Length - termMaxChars)
                else
                    joined

            termOutput.CaretIndex <- termOutput.Text.Length)

    let stopTerminal () =
        match term with
        | Some t ->
            t.Stop()
            term <- None
        | None -> ()

    /// Starts the shell in the opened folder, after the same trust question Run asks: a terminal runs whatever is typed.
    let startTerminal () =
        match root, term with
        | None, _ -> termAppend "Open a folder first: the terminal starts in it.\n"
        | Some _, Some t when t.IsRunning -> ()
        | Some r, _ ->
            task {
                let! allowed =
                    task {
                        if env.Trust.IsTrusted r then
                            return true
                        else
                            let! yes =
                                Dialogs.confirm
                                    window
                                    roles
                                    "Trust this folder?"
                                    (sprintf
                                        "A terminal starts your shell on this computer, with your permissions, in:\n\n%s\n\nAnything typed into it runs. Only trust folders whose code you would run anyway. You can revoke this from the command palette."
                                        r)
                                    "Trust and open terminal"

                            if yes then
                                env.Trust.Trust r

                            return yes
                    }

                if allowed then
                    let shell = Terminal.defaultShell ()

                    let session =
                        TermSession(
                            shell,
                            r,
                            termAppend,
                            (fun code ->
                                termAppend (sprintf "\n[%s ended with code %d]\n" shell.Name code)
                                Dispatcher.UIThread.Post(fun () -> term <- None))
                        )

                    match session.Start() with
                    | Ok() ->
                        term <- Some session

                        termAppend (
                            sprintf
                                "%s in %s (no pseudo-terminal: full-screen programs and Ctrl+C do not work)\n"
                                shell.Name
                                r
                        )
                    | Error e -> termAppend (e + "\n")
            }
            |> ignore

    let restartTerminal () =
        stopTerminal ()
        termOutput.Text <- ""
        startTerminal ()

    let sendTerminalLine (line: string) =
        match term with
        | Some t when t.IsRunning ->
            if
                line.Trim() <> ""
                && (termHistory.Count = 0 || termHistory[termHistory.Count - 1] <> line)
            then
                termHistory.Add line

            termHistoryAt <- termHistory.Count
            termAppend ("> " + line + "\n")
            t.Send line
        | _ -> termAppend "The shell is not running. Use Terminal: Restart.\n"

    do
        terminalShownFn <-
            (fun () ->
                if term.IsNone then
                    startTerminal ())

    let stopServers () =
        for kv in lspSessions do
            kv.Value.Stop()

        stopTerminal ()

        lspSessions.Clear()
        lspFailed.Clear()
        lspStarting.Clear()
        lspDiagnostics.Clear()

    let completeNow () =
        match root, active with
        | Some r, Some d ->
            match sessionOf d with
            | None -> say "No language server is running for this file"
            | Some s ->
                syncLsp d
                let caret = editor.TextArea.Caret

                let pos =
                    { Line = caret.Line - 1
                      Character = caret.Column - 1 }

                let full = Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar))

                task {
                    let! items = s.Client.Completion(full, pos)

                    if items.IsEmpty then
                        say "No suggestions"
                    else
                        let doc = editor.Document
                        let mutable start = editor.CaretOffset

                        while start > 0
                              && (Char.IsLetterOrDigit(doc.GetCharAt(start - 1)) || doc.GetCharAt(start - 1) = '_') do
                            start <- start - 1

                        let win = CompletionWindow(editor.TextArea)
                        win.StartOffset <- start

                        for i in items |> Seq.truncate 200 do
                            win.CompletionList.CompletionData.Add(LspItem i)

                        win.Show()
                }
                |> ignore
        | _ -> ()

    let gotoDefinitionNow () =
        match root, active with
        | Some r, Some d ->
            match sessionOf d with
            | None -> say "No language server is running for this file"
            | Some s ->
                syncLsp d
                let caret = editor.TextArea.Caret
                let full = Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar))

                task {
                    let! found =
                        s.Client.Definition(
                            full,
                            { Line = caret.Line - 1
                              Character = caret.Column - 1 }
                        )

                    match found with
                    | [] -> say "No definition found"
                    | loc :: _ ->
                        let rel = Path.GetRelativePath(r, loc.Path).Replace('\\', '/')

                        if rel.StartsWith ".." then
                            say (sprintf "The definition is outside this folder: %s" loc.Path)
                        else
                            openFileFn rel (loc.Range.Start.Line + 1) (loc.Range.Start.Character + 1)
                }
                |> ignore
        | _ -> ()

    // ---- hover ------------------------------------------------------------------------------------
    let hoverText =
        TextBlock(TextWrapping = TextWrapping.Wrap, FontFamily = monoFont, FontSize = 12.0)

    let hoverBox =
        Border(
            Child = hoverText,
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 560.0,
            Padding = Thickness(10.0, 6.0),
            CornerRadius = CornerRadius 4.0,
            BorderThickness = Thickness 1.0
        )

    do
        themed (fun r ->
            hoverBox.Background <- r.Card
            hoverBox.BorderBrush <- r.Line
            hoverText.Foreground <- r.Fg)

    let hoverTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds 500.0)
    let mutable hoverPoint = Point(0.0, 0.0)
    let mutable hoverSeq = 0

    let hideHover () =
        hoverTimer.Stop()
        hoverSeq <- hoverSeq + 1
        hoverBox.IsVisible <- false

    /// The problems under a position and what the language server says about it, in a box beside it.
    let showHoverAt (line: int) (col: int) (at: Point) =
        match root, active with
        | Some r, Some d ->
            hoverSeq <- hoverSeq + 1
            let mine = hoverSeq
            let rel = d.Rel

            let own =
                [ match lspDiagnostics.TryGetValue rel with
                  | true, l -> yield! l
                  | _ -> ()
                  yield!
                      runDiagnostics
                      |> List.filter (fun x ->
                          match x.Range with
                          | Some rg -> relOfDiagnosticFile rg.File = Some rel
                          | None -> false) ]

            let problems = Hover.problemsAt own line col

            task {
                let! server =
                    match sessionOf d with
                    | Some s when d.LspOpen ->
                        syncLsp d
                        let full = Path.Combine(r, rel.Replace('/', Path.DirectorySeparatorChar))
                        s.Client.Hover(full, { Line = line - 1; Character = col - 1 })
                    | _ -> Task.FromResult(None: string option)

                if mine = hoverSeq then
                    match Hover.compose problems server with
                    | Some text ->
                        hoverText.Text <- text
                        hoverBox.Margin <- Thickness(max 0.0 at.X, max 0.0 at.Y, 0.0, 0.0)
                        hoverBox.IsVisible <- true
                    | None -> hoverBox.IsVisible <- false
            }
            |> ignore
        | _ -> ()

    let showHoverAtCaret () =
        let c = editor.TextArea.Caret
        let tv = editor.TextArea.TextView

        let v =
            tv.GetVisualPosition(TextViewPosition(c.Line, c.Column), VisualYPosition.LineBottom)

        match tv.TranslatePoint(Point(v.X - tv.ScrollOffset.X, v.Y - tv.ScrollOffset.Y), editor) with
        | p when p.HasValue -> showHoverAt c.Line c.Column p.Value
        | _ -> ()

    let refreshGit () =
        match root with
        | Some r ->
            task {
                let! branch, changes = Task.Run(fun () -> Git.branch r, Git.status r |> Map.ofList)
                gitBranch <- branch
                gitChanges <- changes

                statusGit.Text <-
                    match branch with
                    | Some b ->
                        sprintf
                            "⎇ %s%s"
                            b
                            (if changes.IsEmpty then
                                 ""
                             else
                                 sprintf "  ● %d" changes.Count)
                    | None -> ""

                refreshTreeFn ()
            }
            |> ignore
        | None ->
            gitBranch <- None
            gitChanges <- Map.empty
            statusGit.Text <- ""

    let rec rebuildTabs () =
        tabStrip.Children.Clear()

        for d in docs |> Seq.toList do
            let isActive =
                (match active with
                 | Some a -> Object.ReferenceEquals(a, d)
                 | None -> false)

            let name = Path.GetFileName d.Rel

            let title =
                TextBlock(
                    Text =
                        name
                        + (if d.Dirty then " ●" else "")
                        + (if d.MissingOnDisk then " (deleted on disk)" else ""),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (if isActive then roles.Fg else roles.Muted),
                    FontWeight = (if isActive then FontWeight.SemiBold else FontWeight.Normal)
                )

            let close =
                Button(
                    Content = "✕",
                    Padding = Thickness(4.0, 0.0),
                    Background = Brushes.Transparent,
                    BorderThickness = Thickness 0.0,
                    Foreground = roles.Muted,
                    FontSize = 11.0,
                    VerticalAlignment = VerticalAlignment.Center
                )

            let row = StackPanel(Orientation = Orientation.Horizontal, Spacing = 6.0)
            row.Children.Add title
            row.Children.Add close

            let tab =
                Border(
                    Child = row,
                    Padding = Thickness(12.0, 7.0, 6.0, 7.0),
                    BorderThickness = Thickness(0.0, 0.0, 1.0, (if isActive then 2.0 else 0.0)),
                    BorderBrush = (if isActive then roles.Brand else roles.Line),
                    Background = (if isActive then roles.EditorBg else Brushes.Transparent),
                    Cursor = new Cursor(StandardCursorType.Hand)
                )

            ToolTip.SetTip(tab, d.Rel)

            tab.PointerPressed.Add(fun e ->
                let p = e.GetCurrentPoint tab

                if p.Properties.IsMiddleButtonPressed then
                    closeDoc d
                elif p.Properties.IsLeftButtonPressed then
                    activate (Some d))

            let menu = ContextMenu()
            menu.Items.Push(menuItem "Close" "Ctrl+W" (fun () -> closeDoc d))

            menu.Items.Push(
                menuItem "Close Others" "" (fun () ->
                    for o in docs |> Seq.toList do
                        if not (Object.ReferenceEquals(o, d)) then
                            closeNow o)
            )

            menu.Items.Push(menuItem "Copy Relative Path" "" (fun () -> window.Clipboard.SetTextAsync d.Rel |> ignore))

            menu.Items.Push(
                menuItem "Copy Full Path" "" (fun () ->
                    match root with
                    | Some r -> window.Clipboard.SetTextAsync(Path.Combine(r, d.Rel)) |> ignore
                    | None -> ())
            )

            tab.ContextMenu <- menu
            close.Click.Add(fun _ -> closeDoc d)
            tabStrip.Children.Add tab

        welcome.IsVisible <- docs.Count = 0
        editor.IsVisible <- docs.Count > 0

    and activate (d: OpenDoc option) =
        active <- d
        hideHover ()

        match d with
        | Some doc ->
            editor.Document <- doc.Doc
            applyGrammar d
            recomputeOutline doc
            syncLsp doc
            editor.TextArea.Caret.Line <- doc.Line
            editor.TextArea.Caret.Column <- doc.Column

            editor.Options.IndentationSize <-
                (match doc.Pack with
                 | Some p -> p.IndentSize
                 | None -> prefs.Editor.TabSize)

            editor.Options.ConvertTabsToSpaces <-
                (match doc.Pack with
                 | Some p -> not p.UseTabs
                 | None -> prefs.Editor.InsertSpaces)

            editor.ScrollToLine doc.Line
            editor.Focus() |> ignore
        | None -> editor.Document <- TextDocument("")

        rebuildTabs ()
        updateStatus ()
        updateSquiggles ()
        refreshSidebarFn ()

    and closeNow (d: OpenDoc) =
        let index = docs.IndexOf d
        docs.Remove d |> ignore

        match root, sessionOf d with
        | Some r, Some s when d.LspOpen ->
            s.Client.DidClose(Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar)))
        | _ -> ()

        let wasActive =
            (match active with
             | Some a -> Object.ReferenceEquals(a, d)
             | None -> false)

        if wasActive then
            if docs.Count = 0 then
                activate None
            else
                activate (Some docs[max 0 (min (docs.Count - 1) (index - 1))])
        else
            rebuildTabs ()

    and closeDoc (d: OpenDoc) =
        if d.Dirty then
            task {
                let! ok =
                    Dialogs.confirm
                        window
                        roles
                        "Unsaved changes"
                        (sprintf "“%s” has changes that are not saved. Close it and lose them?" (Path.GetFileName d.Rel))
                        "Discard changes"

                if ok then
                    closeNow d
            }
            |> ignore
        else
            closeNow d

    let saveDoc (d: OpenDoc) : bool =
        match root with
        | None -> false
        | Some r ->
            match DiskWorkspace.writeText r d.Rel d.Text with
            | Ok() ->
                d.MarkSaved()

                match sessionOf d with
                | Some sess when d.LspOpen ->
                    syncLsp d
                    sess.Client.DidSave(Path.Combine(r, d.Rel.Replace('/', Path.DirectorySeparatorChar)))
                | _ -> ()

                true
            | Error e ->
                setOutput (sprintf "Could not save %s: %s" d.Rel e)
                showPanel "output"
                false

    let saveAll () =
        for d in docs do
            if d.Dirty then
                saveDoc d |> ignore

        rebuildTabs ()

    let openFile (rel: string) (line: int) (col: int) =
        match root with
        | None -> ()
        | Some r ->
            let existing = docs |> Seq.tryFind (fun d -> d.Rel = rel)

            let target =
                match existing with
                | Some d -> Some d
                | None ->
                    match DiskWorkspace.readText r rel with
                    | Ok text ->
                        let d = OpenDoc(rel, text, packFor rel)

                        d.Doc.TextChanged.Add(fun _ ->
                            d.LspDirty <- true
                            outlineTimer.Stop()
                            outlineTimer.Start()
                            rebuildTabs ())

                        docs.Add d
                        Some d
                    | Error e ->
                        say (sprintf "Cannot open %s: %s" rel e)
                        None

            match target with
            | Some d ->
                if line > 0 then
                    d.Line <- line
                    d.Column <- max 1 col

                activate (Some d)
            | None -> ()

    do openFileFn <- openFile

    // ---- run, format -------------------------------------------------------------------------------
    let explainMissing (missing: ToolInfo list) =
        let lines =
            missing
            |> List.map (fun t ->
                sprintf
                    "  • %s is not installed%s"
                    t.Label
                    (if t.Install <> "" then
                         " — get it from " + t.Install
                     else
                         ""))

        "This file needs a program that was not found on this computer:\n"
        + String.Join("\n", lines)
        + "\n\nInstall it, then choose Tools ▸ Refresh Tools."

    let showReport (pack: LanguagePack) (raw: RawResult) =
        let report = Interpret.report pack.Id raw
        let sb = Text.StringBuilder()

        sb.AppendLine(
            sprintf
                "%s · %d ms · exit %d%s"
                raw.Engine
                raw.DurationMs
                raw.ExitCode
                (if raw.TimedOut then " · TIMED OUT" else "")
        )
        |> ignore

        if raw.BuildFailed then
            sb.AppendLine("\nBuild failed:").AppendLine(raw.BuildOutput) |> ignore
        else
            if raw.BuildOutput.Trim() <> "" then
                sb.AppendLine("\nBuild output:").AppendLine(raw.BuildOutput) |> ignore

            sb.AppendLine().Append(raw.Stdout) |> ignore

            if raw.Stderr.Trim() <> "" then
                sb.AppendLine("\n— error output —").AppendLine(raw.Stderr) |> ignore

        setOutput (sb.ToString())
        runDiagnostics <- report.Diagnostics
        refreshProblems ()

        showPanel (
            if report.Diagnostics.IsEmpty || report.Ok then
                "output"
            else
                "problems"
        )

        say (
            if report.Ok then
                "✔ " + report.Summary
            else
                "✖ " + report.Summary
        )

    let runCurrent (timeoutSec: int) =
        match root, active with
        | Some r, Some d when not running ->
            task {
                saveAll ()

                match d.Pack with
                | None ->
                    setOutput (
                        sprintf
                            "There is no language pack for %s, so FSharpened does not know how to run it.\nAdd one in %s (see docs/operations/desktop.md)."
                            d.Rel
                            env.UserPacks
                    )

                    showPanel "output"
                | Some pack ->
                    match Plans.choose r d.Rel pack.Run with
                    | None ->
                        setOutput (
                            sprintf "%s has no way to run %s here (no run plan applies in this folder)." pack.Name d.Rel
                        )

                        showPanel "output"
                    | Some plan ->
                        let missing = Runner.missing env.Box plan

                        if not missing.IsEmpty then
                            setOutput (explainMissing missing)
                            showPanel "output"
                        else
                            let! allowed =
                                task {
                                    if env.Trust.IsTrusted r then
                                        return true
                                    else
                                        let! yes =
                                            Dialogs.confirm
                                                window
                                                roles
                                                "Trust this folder?"
                                                (sprintf
                                                    "Running code starts programs on this computer, with your permissions, from:\n\n%s\n\nOnly trust folders whose code you would run anyway. You can revoke this from the command palette."
                                                    r)
                                                "Trust and run"

                                        if yes then
                                            env.Trust.Trust r
                                            syncLsp d

                                        return yes
                                }

                            if allowed then
                                match Plans.parseFlags (flagsBox.Text |> Option.ofObj |> Option.defaultValue "") with
                                | Error e ->
                                    setOutput e
                                    showPanel "output"
                                | Ok flags ->
                                    running <- true
                                    say (sprintf "Running %s…" d.Rel)

                                    let req: RunRequest =
                                        { Root = r
                                          Entry = d.Rel
                                          Pack = pack
                                          Plan = plan
                                          Stdin = inputBox.Text |> Option.ofObj |> Option.defaultValue ""
                                          Flags = flags
                                          TimeoutSec = timeoutSec }

                                    let! result = Task.Run(fun () -> Runner.run env.Box req)
                                    running <- false

                                    match result with
                                    | Ok raw -> showReport pack raw
                                    | Error e ->
                                        setOutput e
                                        showPanel "output"
                                        say "✖ could not run"
            }
            |> ignore
        | _ -> ()

    let formatCurrent () =
        match root, active with
        | Some r, Some d ->
            match d.Pack |> Option.bind (fun p -> p.Format |> Option.map (fun s -> p, s)) with
            | None -> say "No formatter is set up for this language"
            | Some(pack, step) ->
                task {
                    saveAll ()

                    match env.Box.Get step.Tool with
                    | Some info when info.Found ->
                        let ctx: PlanContext =
                            { Root = r
                              Entry = d.Rel
                              OutDir = Path.GetTempPath()
                              Flags = []
                              ExeSuffix = "" }

                        let argv = info.Exe @ Plans.expandArgs ctx step.Args
                        say (sprintf "Formatting with %s…" info.Label)
                        let! res = Task.Run(fun () -> Proc.run argv r None (TimeSpan.FromSeconds 30.0))

                        match res with
                        | Ok p when p.ExitCode = Some 0 ->
                            match DiskWorkspace.readText r d.Rel with
                            | Ok text when text <> d.Text ->
                                d.Reload text
                                say "Formatted"
                            | _ -> say "Already formatted"
                        | Ok p ->
                            setOutput (sprintf "%s could not format %s:\n%s%s" info.Label d.Rel p.Stdout p.Stderr)
                            showPanel "output"
                        | Error e -> say e
                    | _ -> say (sprintf "%s is not installed" pack.Name)
                }
                |> ignore
        | _ -> ()

    // ---- file tree ---------------------------------------------------------------------------------
    let childRel (parent: string) (name: string) =
        if parent = "" then name else parent + "/" + name

    let parentOf (rel: string) =
        match rel.LastIndexOf '/' with
        | -1 -> ""
        | i -> rel.Substring(0, i)

    let refreshFileCache () =
        match root with
        | Some r ->
            Task.Run(fun () -> DiskWorkspace.allFiles r 6000 |> fst)
            |> fun t ->
                t.ContinueWith(
                    (fun (x: Task<Entry list>) -> Dispatcher.UIThread.Post(fun () -> fileCache <- x.Result)),
                    TaskContinuationOptions.OnlyOnRanToCompletion
                )
            |> ignore
        | None -> fileCache <- []

    let rec fillNode (parent: ItemsControl) (rel: string) =
        parent.Items.Clear()

        match root with
        | None -> ()
        | Some r ->
            match DiskWorkspace.list r rel with
            | Error e -> parent.Items.Push(TreeViewItem(Header = muted e))
            | Ok entries ->
                for e in entries do
                    let item = TreeViewItem(Tag = e)

                    let name =
                        TextBlock(
                            Text = e.Name,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontWeight = (if e.IsDir then FontWeight.SemiBold else FontWeight.Normal)
                        )

                    name.Foreground <-
                        if e.IsDir then
                            (if gitChanges |> Map.exists (fun k _ -> k = e.Rel || k.StartsWith(e.Rel + "/")) then
                                 roles.Warn
                             else
                                 roles.Fg)
                        else
                            match gitChanges.TryFind e.Rel with
                            | Some Untracked -> roles.Ok
                            | Some Deleted
                            | Some Conflicted -> roles.Bad
                            | Some _ -> roles.Warn
                            | None -> roles.Fg

                    item.Header <- name

                    let menu = ContextMenu()
                    buildTreeMenu menu e
                    item.ContextMenu <- menu

                    if e.IsDir then
                        if expanded.Contains e.Rel then
                            fillNode item e.Rel
                            item.IsExpanded <- true
                        else
                            item.Items.Push(TreeViewItem(Header = "…"))

                        item.PropertyChanged.Add(fun a ->
                            if a.Property = TreeViewItem.IsExpandedProperty then
                                if item.IsExpanded then
                                    expanded.Add e.Rel |> ignore
                                    fillNode item e.Rel
                                else
                                    expanded.Remove e.Rel |> ignore)
                    else
                        item.DoubleTapped.Add(fun ev ->
                            ev.Handled <- true
                            openFileFn e.Rel 0 0)

                    parent.Items.Push item

    and buildTreeMenu (menu: ContextMenu) (e: Entry) =
        let dir = if e.IsDir then e.Rel else parentOf e.Rel

        if not e.IsDir then
            menu.Items.Push(menuItem "Open" "" (fun () -> openFileFn e.Rel 0 0))

        menu.Items.Push(menuItem "New File…" "" (fun () -> newEntry dir false))
        menu.Items.Push(menuItem "New Folder…" "" (fun () -> newEntry dir true))
        menu.Items.Push(Separator())
        menu.Items.Push(menuItem "Rename…" "" (fun () -> renameEntry e))
        menu.Items.Push(menuItem "Delete…" "" (fun () -> deleteEntry e))
        menu.Items.Push(Separator())
        menu.Items.Push(menuItem "Copy Relative Path" "" (fun () -> window.Clipboard.SetTextAsync e.Rel |> ignore))

        menu.Items.Push(
            menuItem "Copy Full Path" "" (fun () ->
                match root with
                | Some r ->
                    window.Clipboard.SetTextAsync(Path.Combine(r, e.Rel.Replace('/', Path.DirectorySeparatorChar)))
                    |> ignore
                | None -> ())
        )

        menu.Items.Push(
            menuItem "Reveal in File Manager" "" (fun () ->
                match root with
                | Some r ->
                    let full =
                        Path.Combine(
                            r,
                            (if e.IsDir then e.Rel else parentOf e.Rel).Replace('/', Path.DirectorySeparatorChar)
                        )

                    if Directory.Exists full then
                        window.Launcher.LaunchDirectoryInfoAsync(DirectoryInfo full) |> ignore
                | None -> ())
        )

    and refreshTree () =
        match root with
        | None -> fileTree.Items.Clear()
        | Some _ -> fillNode fileTree ""

    and newEntry (dir: string) (isFolder: bool) =
        match root with
        | None -> ()
        | Some r ->
            task {
                let! name =
                    Dialogs.ask
                        window
                        roles
                        (if isFolder then "New folder" else "New file")
                        (if dir = "" then
                             "Name (in the folder root):"
                         else
                             sprintf "Name (in %s):" dir)
                        ""
                        (fun v -> if v.Trim() = "" then "Enter a name" else "")

                match name with
                | Some n ->
                    let rel = childRel dir (n.Trim().Replace('\\', '/'))

                    let result =
                        if isFolder then
                            DiskWorkspace.createFolder r rel
                        else
                            DiskWorkspace.createFile r rel

                    match result with
                    | Ok() ->
                        if dir <> "" then
                            expanded.Add dir |> ignore

                        refreshTree ()
                        refreshFileCache ()

                        if not isFolder then
                            openFileFn rel 0 0
                    | Error e -> do! Dialogs.info window roles "Cannot create it" e
                | None -> ()
            }
            |> ignore

    and renameEntry (e: Entry) =
        match root with
        | None -> ()
        | Some r ->
            task {
                let! name =
                    Dialogs.ask window roles "Rename" "New name:" e.Name (fun v ->
                        if v.Trim() = "" || v.Contains "/" || v.Contains "\\" then
                            "Enter a name without slashes"
                        else
                            "")

                match name with
                | Some n when n.Trim() <> e.Name ->
                    let target = childRel (parentOf e.Rel) (n.Trim())

                    match DiskWorkspace.move r e.Rel target with
                    | Ok() ->
                        for d in docs do
                            if d.Rel = e.Rel then
                                d.Rel <- target
                            elif d.Rel.StartsWith(e.Rel + "/") then
                                d.Rel <- target + d.Rel.Substring e.Rel.Length

                        refreshTree ()
                        refreshFileCache ()
                        rebuildTabs ()
                    | Error err -> do! Dialogs.info window roles "Cannot rename" err
                | _ -> ()
            }
            |> ignore

    and deleteEntry (e: Entry) =
        match root with
        | None -> ()
        | Some r ->
            task {
                let! yes =
                    Dialogs.confirm
                        window
                        roles
                        "Delete"
                        (sprintf
                            "Permanently delete “%s”%s? This cannot be undone and does not use the recycle bin."
                            e.Name
                            (if e.IsDir then " and everything in it" else ""))
                        "Delete"

                if yes then
                    match DiskWorkspace.delete r e.Rel with
                    | Ok() ->
                        for d in docs |> Seq.toList do
                            if d.Rel = e.Rel || d.Rel.StartsWith(e.Rel + "/") then
                                closeNow d

                        refreshTree ()
                        refreshFileCache ()
                    | Error err -> do! Dialogs.info window roles "Cannot delete" err
            }
            |> ignore

    // ---- sidebar views ----------------------------------------------------------------------------
    let searchBox = textBox "Find in files"
    let replaceBox = textBox "Replace with"
    let includeBox = textBox "Files to include, e.g. src/**, *.go"
    let excludeBox = textBox "Files to exclude, e.g. **/test/**"
    let searchResults = ListBox(Background = Brushes.Transparent)
    let caseToggle = ToggleButton(Content = "Aa", Padding = Thickness(6.0, 2.0))
    let wordToggle = ToggleButton(Content = "ab", Padding = Thickness(6.0, 2.0))
    let regexToggle = ToggleButton(Content = ".*", Padding = Thickness(6.0, 2.0))
    let searchSummary = muted ""
    let outlineList = ListBox(Background = Brushes.Transparent)
    let toolsPanel = StackPanel(Spacing = 6.0, Margin = Thickness(12.0, 4.0))

    let textOf (b: TextBox) =
        b.Text |> Option.ofObj |> Option.defaultValue ""

    let searchOptions () : SearchOptions =
        { Query = searchBox.Text
          CaseSensitive = caseToggle.IsChecked = Nullable true
          WholeWord = wordToggle.IsChecked = Nullable true
          Regex = regexToggle.IsChecked = Nullable true }

    let runSearch () =
        match root with
        | Some r when textOf searchBox <> "" ->
            let opts = searchOptions ()
            let inc, exc = textOf includeBox, textOf excludeBox
            let openText = [ for d in docs -> d.Rel, d.Text ]

            searchSummary.Text <- "Searching…"

            task {
                let! found = Task.Run(fun () -> FolderSearch.runDefault r opts inc exc openText)
                let result = found.Result
                lastSearchEngine <- FolderSearch.engineLabel found.Engine

                searchResults.Items.Clear()

                if result.Error <> "" then
                    searchSummary.Text <- result.Error
                else
                    searchSummary.Text <-
                        sprintf
                            "%d results in %d files%s · %s search"
                            result.Total
                            result.Files.Length
                            (if result.Truncated then " (more not shown)" else "")
                            (match found.Engine with
                             | NativeEngine -> "native"
                             | ManagedEngine _ -> "managed")

                    ToolTip.SetTip(searchSummary, lastSearchEngine)

                    for f in result.Files do
                        searchResults.Items.Push(
                            TextBlock(
                                Text = f.Path,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = roles.Fg,
                                Margin = Thickness(0.0, 6.0, 0.0, 2.0)
                            )
                        )

                        for h in f.Hits do
                            let row =
                                TextBlock(
                                    Text = sprintf "%d:  %s" h.Line (h.Preview.Trim()),
                                    Foreground = roles.Muted,
                                    FontFamily = monoFont,
                                    FontSize = 12.0,
                                    Tag = (f.Path, h.Line, h.Column),
                                    Margin = Thickness(10.0, 0.0, 0.0, 0.0)
                                )

                            searchResults.Items.Push row
            }
            |> ignore
        | _ -> ()

    /// Applies a replace that has been confirmed: files open in the editor are changed there (one edit, so Undo works, and
    /// they stay unsaved); any other file is written to disk straight away.
    let applyReplace
        (r: string)
        (opts: SearchOptions)
        (replacement: string)
        (files: FileHits list)
        (openText: (string * string) list)
        =
        let outcome = Replace.apply r opts replacement files openText

        for rel, text in outcome.Editor do
            match docs |> Seq.tryFind (fun d -> d.Rel = rel) with
            | Some d ->
                d.Doc.Text <- text
                d.LspDirty <- true
            | None -> ()

        searchSummary.Text <-
            sprintf
                "Replaced %d matches in %d files%s"
                outcome.Matches
                outcome.Files
                (if outcome.Failed.IsEmpty then
                     ""
                 else
                     sprintf ", %d failed (see Output)" outcome.Failed.Length)

        if not outcome.Failed.IsEmpty then
            setOutput (String.concat "\n" outcome.Failed)
            showPanel "output"

        searchResults.Items.Clear()
        rebuildTabs ()
        refreshGit ()

    /// Replace every match, after saying how many there are and asking.
    let replaceAll () =
        match root with
        | Some r when textOf searchBox <> "" ->
            let opts = searchOptions ()
            let replacement = textOf replaceBox
            let inc, exc = textOf includeBox, textOf excludeBox
            let openText = [ for d in docs -> d.Rel, d.Text ]

            task {
                let! found = Task.Run(fun () -> FolderSearch.runDefault r opts inc exc openText)
                let result = found.Result

                if result.Error <> "" then
                    searchSummary.Text <- result.Error
                elif result.Files.IsEmpty then
                    searchSummary.Text <- "Nothing to replace"
                else
                    let! ok =
                        Dialogs.confirm
                            window
                            roles
                            "Replace in files"
                            (sprintf
                                "Replace %d matches in %d files with “%s”?\n\nFiles open in the editor are changed there and left unsaved, so Undo works. Other files are written to disk now and cannot be undone."
                                result.Total
                                result.Files.Length
                                replacement)
                            "Replace all"

                    if ok then
                        applyReplace r opts replacement result.Files openText
            }
            |> ignore
        | _ -> ()

    let fillOutline () =
        outlineList.Items.Clear()

        match active with
        | Some d ->
            for s in d.Outline do
                let row =
                    TextBlock(
                        Text = sprintf "%s%s  %s" (String(' ', s.Depth * 2)) s.Name s.Kind,
                        Foreground = roles.Fg,
                        FontFamily = monoFont,
                        FontSize = 12.0,
                        Tag = s.Line
                    )

                outlineList.Items.Push row
        | None -> ()

    let fillTools () =
        toolsPanel.Children.Clear()
        toolsPanel.Children.Add(muted "Looking for the programs the language packs use…")

        task {
            let! infos = Task.Run(fun () -> env.Box.Refresh())
            toolsPanel.Children.Clear()
            let found = infos |> List.filter (fun t -> t.Found)

            toolsPanel.Children.Add(
                label (
                    sprintf
                        "%d of %d tools found · %d language packs"
                        found.Length
                        infos.Length
                        env.Registry.Packs.Length
                )
            )

            for p in env.Registry.Problems do
                toolsPanel.Children.Add(
                    TextBlock(
                        Text = sprintf "⚠ %s: %s" (Path.GetFileName p.Origin) p.Message,
                        Foreground = roles.Warn,
                        TextWrapping = TextWrapping.Wrap
                    )
                )

            for t in infos |> List.sortBy (fun t -> not t.Found, t.Label) do
                let line =
                    if t.Found then
                        sprintf "✔ %s  %s" t.Label t.Version
                    else
                        sprintf "✖ %s%s" t.Label (if t.Install <> "" then "  → " + t.Install else "")

                toolsPanel.Children.Add(
                    TextBlock(
                        Text = line,
                        Foreground = (if t.Found then roles.Ok else roles.Muted),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12.0
                    )
                )
        }
        |> ignore

    let searchView () : Control =
        let s = StackPanel(Spacing = 6.0, Margin = Thickness(10.0, 0.0))
        s.Children.Add searchBox
        s.Children.Add replaceBox
        let toggles = StackPanel(Orientation = Orientation.Horizontal, Spacing = 4.0)
        toggles.Children.Add caseToggle
        toggles.Children.Add wordToggle
        toggles.Children.Add regexToggle
        toggles.Children.Add(flat "Replace all" "Replace every match in the files listed" replaceAll)
        s.Children.Add toggles
        s.Children.Add includeBox
        s.Children.Add excludeBox
        s.Children.Add searchSummary
        let d = DockPanel()
        DockPanel.SetDock(s, Dock.Top)
        d.Children.Add s
        d.Children.Add searchResults
        d :> Control

    let showSidebar (name: string) =
        sidebarView <- name
        setSidebarVisible true

        let title, content =
            match name with
            | "search" -> "SEARCH", searchView ()
            | "outline" ->
                fillOutline ()
                "OUTLINE", (outlineList :> Control)
            | "tools" ->
                fillTools ()
                "TOOLS AND LANGUAGES", (ScrollViewer(Content = toolsPanel) :> Control)
            | _ -> "EXPLORER", (fileTree :> Control)

        sidebarTitle.Text <- title
        sidebarHost.Content <- content

        if name = "search" then
            searchBox.Focus() |> ignore

    do
        refreshSidebarFn <-
            (fun () ->
                if sidebarView = "outline" then
                    fillOutline ())

    // ---- folder -----------------------------------------------------------------------------------
    let conflictOpen = HashSet<string>()

    /// A file with unsaved edits changed on disk: ask which side wins. Keeping the editor's version is remembered for
    /// that disk text, so the same outside change does not ask again.
    let askConflict (r: string) (d: OpenDoc) (diskText: string) =
        if conflictOpen.Add d.Rel then
            task {
                let! reload =
                    Dialogs.confirm
                        window
                        roles
                        "File changed on disk"
                        (sprintf
                            "“%s” was changed outside the IDE, and it has unsaved edits here.\n\nReload it from disk and lose the edits, or keep the editor's version (saving then overwrites the disk)?"
                            (Path.GetFileName d.Rel))
                        "Reload from disk"

                conflictOpen.Remove d.Rel |> ignore

                if reload && docs.Contains d then
                    match DiskWorkspace.readText r d.Rel with
                    | Ok t ->
                        d.Reload t
                        d.LspDirty <- true
                        d.KeptOverDisk <- None
                        d.MissingOnDisk <- false
                        say (sprintf "Reloaded %s from disk" (Path.GetFileName d.Rel))
                    | Error _ -> ()
                else
                    d.KeptOverDisk <- Some diskText

                rebuildTabs ()
            }
            |> ignore

    /// Brings the open files in line with the disk: an unmodified one is reloaded, a modified one asks, a deleted one
    /// stays open and is marked. `paths` limits the look to those files; None looks at every open file.
    let syncOpenFiles (paths: Set<string> option) =
        match root with
        | Some r ->
            let mutable reloaded = []
            let mutable touched = false

            for d in docs |> Seq.toList do
                if paths.IsNone || paths.Value.Contains d.Rel then
                    match DiskWorkspace.resolve r d.Rel with
                    | Ok full when not (File.Exists full) ->
                        if not d.MissingOnDisk then
                            d.MissingOnDisk <- true
                            touched <- true
                            say (sprintf "%s was deleted or moved on disk" (Path.GetFileName d.Rel))
                    | _ ->
                        if d.MissingOnDisk then
                            d.MissingOnDisk <- false
                            touched <- true

                        match DiskWorkspace.readText r d.Rel with
                        | Ok t when not d.Dirty && t <> d.Text ->
                            d.Reload t
                            d.LspDirty <- true
                            reloaded <- Path.GetFileName d.Rel :: reloaded
                        | Ok t when d.Dirty && t <> d.DiskText && d.KeptOverDisk <> Some t -> askConflict r d t
                        | _ -> ()

            if not reloaded.IsEmpty then
                say (sprintf "Reloaded %s from disk" (String.Join(", ", List.rev reloaded)))

            if touched || not reloaded.IsEmpty then
                rebuildTabs ()
        | None -> ()

    /// Something changed on disk that the IDE did not do itself: refresh the explorer, git and quick open, and the
    /// open files it touches.
    let onDiskChanged (r: string) (change: DiskChange) =
        if root = Some r then
            match change with
            | SomePaths paths ->
                if paths.Count = 1 then
                    say (sprintf "%s changed on disk" (Seq.head paths))
                else
                    say (sprintf "%d files changed on disk" paths.Count)

                syncOpenFiles (Some paths)
            | Everything ->
                say "Many files changed on disk"
                syncOpenFiles None

            refreshGit ()
            refreshFileCache ()

    let stopWatching () =
        watchSub |> Option.iter (fun s -> s.Dispose())
        watchSub <- None

        match watcher with
        | Some w ->
            w.Watcher.Dispose()
            watcher <- None
        | None -> ()

        statusWatch.Text <- ""

    let startWatching (r: string) =
        stopWatching ()

        try
            let running = FolderWatch.start true r
            watcher <- Some running

            watchSub <-
                Some(
                    FolderWatch.subscribe running (fun change ->
                        Dispatcher.UIThread.Post(fun () -> onDiskChanged r change))
                )

            statusWatch.Text <-
                match running.Engine with
                | NativeWatch -> "👁 native"
                | PollingWatch _ -> "👁 polling"

            ToolTip.SetTip(
                statusWatch,
                "Watching the folder for changes made outside the IDE: "
                + FolderWatch.engineLabel running.Engine
            )
        with e ->
            statusWatch.Text <- "👁 off"
            ToolTip.SetTip(statusWatch, "The folder is not watched: " + e.Message)

    let openFolder (path: string) =
        if Directory.Exists path then
            for d in docs |> Seq.toList do
                closeNow d

            stopServers ()
            runDiagnostics <- []
            root <- Some(Path.GetFullPath path)
            expanded.Clear()
            window.Title <- sprintf "%s — FSharpened" (rootName ())
            statusFolder.Text <- "📁 " + rootName ()
            prefs <- Prefs.remember root.Value prefs
            savePrefs ()
            refreshTree ()
            refreshFileCache ()
            startWatching root.Value
            refreshProblems ()
            refreshGit ()
            setOutput ""
            showSidebar "files"

            say (
                if env.Trust.IsTrusted root.Value then
                    "Folder is trusted to run code"
                else
                    "Running code here will ask for your trust"
            )

    let closeFolder () =
        for d in docs |> Seq.toList do
            closeNow d

        stopWatching ()
        stopServers ()
        runDiagnostics <- []
        refreshProblems ()
        root <- None
        refreshGit ()
        fileCache <- []
        refreshTree ()
        statusFolder.Text <- "No folder open"
        window.Title <- "FSharpened"

    let openFolderDialog () =
        task {
            let! picked =
                window.StorageProvider.OpenFolderPickerAsync(
                    FolderPickerOpenOptions(Title = "Open Folder", AllowMultiple = false)
                )

            if picked.Count > 0 then
                match picked[0].TryGetLocalPath() with
                | null -> ()
                | p -> openFolder p
        }
        |> ignore

    // ---- command palette ---------------------------------------------------------------------------
    let setFont (delta: int) =
        prefs <-
            { prefs with
                Editor =
                    Settings.sanitize
                        { prefs.Editor with
                            FontSize = prefs.Editor.FontSize + delta } }

        applyEditorPrefs ()
        savePrefs ()

    let toggle (pick: EditorSettings -> bool) (set: EditorSettings -> bool -> EditorSettings) =
        prefs <-
            { prefs with
                Editor = set prefs.Editor (not (pick prefs.Editor)) }

        applyEditorPrefs ()
        savePrefs ()

    let commands: Command list =
        [ { Id = "open-folder"
            Title = "File: Open Folder…"
            Keys = "Ctrl+O"
            Run = openFolderDialog }
          { Id = "new-file"
            Title = "File: New File…"
            Keys = "Ctrl+N"
            Run = (fun () -> newEntry "" false) }
          { Id = "save"
            Title = "File: Save"
            Keys = "Ctrl+S"
            Run =
              (fun () ->
                  active
                  |> Option.iter (fun d ->
                      saveDoc d |> ignore
                      rebuildTabs ())) }
          { Id = "save-all"
            Title = "File: Save All"
            Keys = "Ctrl+Shift+S"
            Run = saveAll }
          { Id = "close-tab"
            Title = "View: Close Editor"
            Keys = "Ctrl+W"
            Run = (fun () -> active |> Option.iter closeDoc) }
          { Id = "close-folder"
            Title = "File: Close Folder"
            Keys = ""
            Run = closeFolder }
          { Id = "run"
            Title = "Run: Run This File"
            Keys = "F5"
            Run = (fun () -> runCurrent Runner.defaultTimeoutSec) }
          { Id = "run-long"
            Title = "Run: Run This File (120 s limit)"
            Keys = ""
            Run = (fun () -> runCurrent Runner.maxTimeoutSec) }
          { Id = "show-hover"
            Title = "Editor: Show Hover"
            Keys = ""
            Run = showHoverAtCaret }
          { Id = "format"
            Title = "Format Document"
            Keys = "Shift+Alt+F"
            Run = formatCurrent }
          { Id = "complete"
            Title = "Trigger Suggest (language server)"
            Keys = "Ctrl+Space"
            Run = completeNow }
          { Id = "definition"
            Title = "Go to Definition (language server)"
            Keys = "F12"
            Run = gotoDefinitionNow }
          { Id = "quick-open"
            Title = "Go to File…"
            Keys = "Ctrl+P"
            Run = (fun () -> showPaletteFn "") }
          { Id = "goto-line"
            Title = "Go to Line…"
            Keys = "Ctrl+G"
            Run = (fun () -> showPaletteFn ":") }
          { Id = "goto-symbol"
            Title = "Go to Symbol in Editor…"
            Keys = "Ctrl+Shift+O"
            Run = (fun () -> showPaletteFn "@") }
          { Id = "palette"
            Title = "Show All Commands"
            Keys = "Ctrl+Shift+P"
            Run = (fun () -> showPaletteFn ">") }
          { Id = "find"
            Title = "Find in File"
            Keys = "Ctrl+F"
            Run =
              (fun () ->
                  searchPanel.Open()
                  ()) }
          { Id = "find-files"
            Title = "Search: Find in Files"
            Keys = "Ctrl+Shift+F"
            Run = (fun () -> showSidebar "search") }
          { Id = "replace-in-file"
            Title = "Edit: Replace in This File"
            Keys = "Ctrl+H"
            Run =
              (fun () ->
                  searchPanel.IsReplaceMode <- true
                  searchPanel.Open()) }
          { Id = "replace-in-files"
            Title = "Search: Replace in Files"
            Keys = "Ctrl+Shift+H"
            Run =
              (fun () ->
                  showSidebar "search"
                  replaceBox.Focus() |> ignore) }
          { Id = "terminal"
            Title = "Terminal: Open"
            Keys = "Ctrl+`"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "terminal"
                  termInput.Focus() |> ignore) }
          { Id = "terminal-restart"
            Title = "Terminal: Restart"
            Keys = ""
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "terminal"
                  restartTerminal ()) }
          { Id = "terminal-clear"
            Title = "Terminal: Clear"
            Keys = ""
            Run = (fun () -> termOutput.Text <- "") }
          { Id = "explorer"
            Title = "View: Show Explorer"
            Keys = "Ctrl+Shift+E"
            Run = (fun () -> showSidebar "files") }
          { Id = "outline"
            Title = "View: Show Outline"
            Keys = ""
            Run = (fun () -> showSidebar "outline") }
          { Id = "tools"
            Title = "View: Show Tools and Languages"
            Keys = ""
            Run = (fun () -> showSidebar "tools") }
          { Id = "toggle-sidebar"
            Title = "View: Toggle Side Bar"
            Keys = "Ctrl+B"
            Run = (fun () -> setSidebarVisible (not sidebarShown)) }
          { Id = "toggle-panel"
            Title = "View: Toggle Panel"
            Keys = "Ctrl+J"
            Run = (fun () -> setPanelVisible (not panelShown)) }
          { Id = "problems"
            Title = "View: Show Problems"
            Keys = "Ctrl+Shift+M"
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "problems") }
          { Id = "output"
            Title = "View: Show Output"
            Keys = ""
            Run =
              (fun () ->
                  setPanelVisible true
                  showPanel "output") }
          { Id = "zoom-in"
            Title = "View: Increase Font Size"
            Keys = "Ctrl+="
            Run = (fun () -> setFont 1) }
          { Id = "zoom-out"
            Title = "View: Decrease Font Size"
            Keys = "Ctrl+-"
            Run = (fun () -> setFont -1) }
          { Id = "wrap"
            Title = "View: Toggle Word Wrap"
            Keys = "Alt+Z"
            Run = (fun () -> toggle (fun e -> e.WordWrap) (fun e v -> { e with WordWrap = v })) }
          { Id = "whitespace"
            Title = "View: Toggle Render Whitespace"
            Keys = ""
            Run = (fun () -> toggle (fun e -> e.RenderWhitespace) (fun e v -> { e with RenderWhitespace = v })) }
          { Id = "line-numbers"
            Title = "View: Toggle Line Numbers"
            Keys = ""
            Run = (fun () -> toggle (fun e -> e.LineNumbers) (fun e v -> { e with LineNumbers = v })) }
          { Id = "breadcrumbs"
            Title = "View: Toggle Breadcrumbs"
            Keys = ""
            Run = (fun () -> toggle (fun e -> e.Breadcrumbs) (fun e v -> { e with Breadcrumbs = v })) }
          { Id = "refresh-tools"
            Title = "Tools: Refresh Installed Tools"
            Keys = ""
            Run = (fun () -> showSidebar "tools") }
          { Id = "reload-packs"
            Title = "Languages: Reload Language Packs"
            Keys = ""
            Run =
              (fun () ->
                  env.ReloadLanguages()
                  showSidebar "tools") }
          { Id = "packs-folder"
            Title = "Languages: Open My Language Packs Folder"
            Keys = ""
            Run =
              (fun () ->
                  Directory.CreateDirectory env.UserPacks |> ignore
                  window.Launcher.LaunchDirectoryInfoAsync(DirectoryInfo env.UserPacks) |> ignore) }
          { Id = "revoke-trust"
            Title = "Run: Revoke Trust for This Folder"
            Keys = ""
            Run =
              (fun () ->
                  match root with
                  | Some r ->
                      env.Trust.Revoke r
                      say "Trust revoked: running code here will ask again"
                  | None -> ()) } ]
        @ [ for t in Palette.choices ->
                { Id = "theme-" + t.Id
                  Title = "Color Theme: " + t.Name
                  Keys = ""
                  Run = (fun () -> applyTheme t) } ]

    let hidePalette () =
        palette.IsVisible <- false

        if docs.Count > 0 then
            editor.Focus() |> ignore

    /// What a row of the palette does when chosen, with the text to show and the matched letters to emphasise.
    let mutable paletteActions: (unit -> unit) list = []

    let paletteRow (text: string) (positions: int list) (detail: string) =
        let t = TextBlock(FontSize = 14.0, Foreground = roles.Fg)
        let set = Set.ofList positions

        for i in 0 .. text.Length - 1 do
            let r = Documents.Run(string text[i])

            if set.Contains i then
                r.FontWeight <- FontWeight.Bold
                r.Foreground <- roles.Brand

            t.Inlines.Add r

        let row = DockPanel(Margin = Thickness(2.0, 3.0))

        if detail <> "" then
            let d =
                TextBlock(
                    Text = detail,
                    Foreground = roles.Muted,
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
            match active with
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
            let paths = fileCache |> List.map (fun e -> e.Rel)

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
                    openFileFn rel q.Line q.Column)

        if paletteList.Items.Count > 0 then
            paletteList.SelectedIndex <- 0

    do
        showPaletteFn <-
            fun prefix ->
                palette.IsVisible <- true
                paletteInput.Text <- prefix
                paletteInput.Focus() |> ignore
                paletteInput.CaretIndex <- prefix.Length
                refreshPalette ()

    // ---- menus and activity bar -------------------------------------------------------------------
    let command (id: string) =
        commands |> List.find (fun c -> c.Id = id)

    let item (id: string) =
        let c = command id
        menuItem (c.Title.Substring(c.Title.IndexOf ':' + 1).Trim()) c.Keys c.Run

    let buildMenu () =
        let bar = Menu()

        themed (fun r ->
            bar.Background <- r.Card
            bar.Foreground <- r.Fg)

        let top (header: string) (items: Control list) =
            let m = MenuItem(Header = header)

            for i in items do
                m.Items.Push i

            bar.Items.Push m

        let recent = MenuItem(Header = "Open Recent")

        recent.SubmenuOpened.Add(fun _ ->
            recent.Items.Clear()

            if prefs.Recent.IsEmpty then
                recent.Items.Push(MenuItem(Header = "(none)", IsEnabled = false))

            for p in prefs.Recent do
                recent.Items.Push(menuItem p "" (fun () -> openFolder p)))

        recent.Items.Push(MenuItem(Header = "(none)", IsEnabled = false))

        let themeMenu = MenuItem(Header = "Color Theme")

        for t in Palette.choices do
            themeMenu.Items.Push(menuItem t.Name "" (fun () -> applyTheme t))

        top
            "_File"
            [ item "open-folder"
              recent
              Separator()
              item "new-file"
              item "save"
              item "save-all"
              Separator()
              item "close-tab"
              item "close-folder"
              Separator()
              menuItem "Exit" "Alt+F4" (fun () -> window.Close()) ]

        top "_Edit" [ item "find"; item "find-files"; Separator(); item "format" ]

        top
            "_View"
            [ item "palette"
              item "quick-open"
              item "goto-line"
              item "goto-symbol"
              Separator()
              item "explorer"
              item "find-files"
              item "outline"
              item "problems"
              item "output"
              item "tools"
              Separator()
              item "toggle-sidebar"
              item "toggle-panel"
              Separator()
              themeMenu
              item "zoom-in"
              item "zoom-out"
              item "wrap"
              item "whitespace"
              item "line-numbers"
              item "breadcrumbs" ]

        top "_Run" [ item "run"; item "run-long"; Separator(); item "revoke-trust" ]
        top "_Tools" [ item "refresh-tools"; item "reload-packs"; item "packs-folder" ]
        bar

    let activityButton (glyph: string) (tip: string) (view: string) =
        let b =
            Button(
                Content = glyph,
                FontSize = 20.0,
                Width = 46.0,
                Height = 46.0,
                Background = Brushes.Transparent,
                BorderThickness = Thickness 0.0,
                HorizontalContentAlignment = HorizontalAlignment.Center
            )

        ToolTip.SetTip(b, tip)
        themed (fun r -> b.Foreground <- r.Muted)

        b.Click.Add(fun _ ->
            if sidebarShown && sidebarView = view then
                setSidebarVisible false
            else
                showSidebar view)

        b

    // ---- build the tree ---------------------------------------------------------------------------
    let layout () =
        // activity bar
        let activity = StackPanel()
        activity.Children.Add(activityButton "🗂" "Explorer (Ctrl+Shift+E)" "files")
        activity.Children.Add(activityButton "🔍" "Search (Ctrl+Shift+F)" "search")
        activity.Children.Add(activityButton "☰" "Outline" "outline")
        activity.Children.Add(activityButton "🛠" "Tools and languages" "tools")

        let activityBorder =
            Border(Child = activity, BorderThickness = Thickness(0.0, 0.0, 1.0, 0.0))

        themed (fun r ->
            activityBorder.Background <- r.Card
            activityBorder.BorderBrush <- r.Line)

        // sidebar
        let sidebarBody = DockPanel()
        DockPanel.SetDock(sidebarTitle, Dock.Top)
        themed (fun r -> sidebarTitle.Foreground <- r.Muted)
        sidebarBody.Children.Add sidebarTitle
        sidebarBody.Children.Add sidebarHost
        let sidebar = panelBorder sidebarBody (fun r -> r.Card)

        let vsplit =
            GridSplitter(Width = 4.0, ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent)

        // tab strip with run controls
        let runButton =
            Button(
                Content = "▶  Run",
                Padding = Thickness(12.0, 4.0),
                Margin = Thickness(6.0, 4.0),
                FontWeight = FontWeight.SemiBold
            )

        themed (fun r ->
            runButton.Background <- r.Brand
            runButton.Foreground <- r.BrandFg)

        ToolTip.SetTip(runButton, "Run this file (F5)")
        runButton.Click.Add(fun _ -> runCurrent Runner.defaultTimeoutSec)
        themed (restyleBox flagsBox)
        flagsBox.Margin <- Thickness(0.0, 4.0)

        let runBar = StackPanel(Orientation = Orientation.Horizontal)
        runBar.Children.Add flagsBox
        runBar.Children.Add runButton
        DockPanel.SetDock(runBar, Dock.Right)

        let tabRow = DockPanel()
        tabRow.Children.Add runBar

        tabRow.Children.Add(
            ScrollViewer(
                Content = tabStrip,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            )
        )

        let tabBorder =
            Border(Child = tabRow, BorderThickness = Thickness(0.0, 0.0, 0.0, 1.0))

        themed (fun r ->
            tabBorder.Background <- r.Card
            tabBorder.BorderBrush <- r.Line
            crumbs.Foreground <- r.Muted)

        // editor with welcome screen behind
        let title =
            TextBlock(
                Text = "FSharpened",
                FontSize = 34.0,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            )

        themed (fun r -> title.Foreground <- r.Brand)
        welcome.Children.Add title
        welcome.Children.Add(muted "A native F# IDE for any language that has a language pack")

        let openBtn =
            Button(
                Content = "Open Folder…",
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = Thickness(18.0, 8.0)
            )

        themed (fun r ->
            openBtn.Background <- r.Brand
            openBtn.Foreground <- r.BrandFg)

        openBtn.Click.Add(fun _ -> openFolderDialog ())
        welcome.Children.Add openBtn
        welcome.Children.Add(muted "Ctrl+Shift+P  commands      Ctrl+P  go to file      F5  run")

        let editorHost = Grid()
        editorHost.Children.Add welcome
        editorHost.Children.Add editor
        editorHost.Children.Add hoverBox
        editor.IsVisible <- false
        themed (fun r -> editorHost.Background <- r.EditorBg)

        let editorColumn = DockPanel()
        DockPanel.SetDock(tabBorder, Dock.Top)
        DockPanel.SetDock(crumbs, Dock.Top)
        editorColumn.Children.Add tabBorder
        editorColumn.Children.Add crumbs
        editorColumn.Children.Add editorHost

        // bottom panel
        let tabButton (name: string) (title: string) (content: Control) =
            let b =
                Button(
                    Content = title,
                    Background = Brushes.Transparent,
                    BorderThickness = Thickness 0.0,
                    Padding = Thickness(12.0, 6.0)
                )

            themed (fun r -> b.Foreground <- r.Fg)
            b.Click.Add(fun _ -> showPanel name)
            panelTabs.Add((name, b, content))
            b

        let outputHost = Grid()
        outputHost.Children.Add outputBox
        let problemsHost = Grid()
        problemsHost.Children.Add problemsBox
        let inputHost = Grid()
        inputHost.Children.Add inputBox
        let termHost = DockPanel()
        DockPanel.SetDock(termInput, Dock.Bottom)
        termHost.Children.Add termInput
        termHost.Children.Add termOutput

        themed (fun r ->
            outputBox.Background <- r.EditorBg
            outputBox.Foreground <- r.Fg
            inputBox.Background <- r.EditorBg
            inputBox.Foreground <- r.Fg
            termOutput.Background <- r.EditorBg
            termOutput.Foreground <- r.Fg
            termInput.Background <- r.EditorBg
            termInput.Foreground <- r.Fg
            termInput.BorderBrush <- r.Line)

        let headerButtons = StackPanel(Orientation = Orientation.Horizontal)
        headerButtons.Children.Add(tabButton "output" "OUTPUT" outputHost)
        headerButtons.Children.Add(tabButton "problems" "PROBLEMS" problemsHost)
        headerButtons.Children.Add(tabButton "input" "INPUT" inputHost)
        headerButtons.Children.Add(tabButton "terminal" "TERMINAL" termHost)

        let closePanel = flat "✕" "Close panel (Ctrl+J)" (fun () -> setPanelVisible false)
        DockPanel.SetDock(closePanel, Dock.Right)

        let clearBtn =
            flat "Clear" "Clear output and problems" (fun () ->
                setOutput ""
                runDiagnostics <- []
                refreshProblems ())

        DockPanel.SetDock(clearBtn, Dock.Right)
        let header = DockPanel()
        header.Children.Add closePanel
        header.Children.Add clearBtn
        header.Children.Add headerButtons

        let headerBorder =
            Border(Child = header, BorderThickness = Thickness(0.0, 1.0, 0.0, 1.0))

        themed (fun r ->
            headerBorder.Background <- r.Card
            headerBorder.BorderBrush <- r.Line)

        let panelContent = Grid()
        panelContent.Children.Add outputHost
        panelContent.Children.Add problemsHost
        panelContent.Children.Add inputHost
        panelContent.Children.Add termHost
        let panelBody = DockPanel()
        DockPanel.SetDock(headerBorder, Dock.Top)
        panelBody.Children.Add headerBorder
        panelBody.Children.Add panelContent
        let panelBorderBox = panelBorder panelBody (fun r -> r.EditorBg)
        showPanel "output"

        problemsBox.SelectionChanged.Add(fun _ ->
            match problemsBox.SelectedItem with
            | :? StackPanel as row ->
                match row.Tag with
                | :? FSharpened.Diagnostics.StructuredDiagnostic as d ->
                    match d.Range with
                    | Some range ->
                        match relOfDiagnosticFile range.File with
                        | Some rel -> openFile rel range.StartLine range.StartColumn
                        | None -> say (sprintf "%s is not in the open folder" range.File)
                    | None -> ()
                | _ -> ()
            | _ -> ())

        let hsplit =
            GridSplitter(
                Height = 4.0,
                ResizeDirection = GridResizeDirection.Rows,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch
            )

        mainGrid.RowDefinitions.Add(RowDefinition(Height = GridLength.Star))
        mainGrid.RowDefinitions.Add bottomSplitRow
        mainGrid.RowDefinitions.Add bottomRow
        Grid.SetRow(editorColumn, 0)
        Grid.SetRow(hsplit, 1)
        Grid.SetRow(panelBorderBox, 2)
        mainGrid.Children.Add editorColumn
        mainGrid.Children.Add hsplit
        mainGrid.Children.Add panelBorderBox

        bodyGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength 46.0))
        bodyGrid.ColumnDefinitions.Add sidebarColumn
        bodyGrid.ColumnDefinitions.Add splitColumn
        bodyGrid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        Grid.SetColumn(activityBorder, 0)
        Grid.SetColumn(sidebar, 1)
        Grid.SetColumn(vsplit, 2)
        Grid.SetColumn(mainGrid, 3)
        bodyGrid.Children.Add activityBorder
        bodyGrid.Children.Add sidebar
        bodyGrid.Children.Add vsplit
        bodyGrid.Children.Add mainGrid

        // palette overlay
        let paletteBody = StackPanel(Spacing = 6.0, Margin = Thickness 8.0)
        paletteBody.Children.Add paletteInput
        paletteBody.Children.Add paletteList
        palette.Child <- paletteBody

        themed (fun r ->
            palette.Background <- r.Card
            palette.BorderBrush <- r.Line
            restyleBox paletteInput r)

        paletteInput.TextChanged.Add(fun _ ->
            if palette.IsVisible then
                refreshPalette ())

        paletteInput.KeyDown.Add(fun e ->
            match e.Key with
            | Key.Escape ->
                e.Handled <- true
                hidePalette ()
            | Key.Down ->
                e.Handled <- true
                paletteList.SelectedIndex <- min (paletteList.Items.Count - 1) (paletteList.SelectedIndex + 1)
            | Key.Up ->
                e.Handled <- true
                paletteList.SelectedIndex <- max 0 (paletteList.SelectedIndex - 1)
            | Key.Enter ->
                e.Handled <- true
                let i = paletteList.SelectedIndex

                if i >= 0 && i < paletteActions.Length then
                    paletteActions[i]()
            | _ -> ())

        paletteList.DoubleTapped.Add(fun _ ->
            let i = paletteList.SelectedIndex

            if i >= 0 && i < paletteActions.Length then
                paletteActions[i]())

        // status bar: folder, branch and the latest message on the left (the message is cut with an ellipsis when it is
        // too long), problems, position and language on the right
        let status = Grid(Margin = Thickness(10.0, 0.0))
        status.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        status.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        let right = StackPanel(Orientation = Orientation.Horizontal, Spacing = 18.0)
        right.Children.Add statusProblems
        right.Children.Add statusPos
        right.Children.Add statusLang
        Grid.SetColumn(right, 1)
        let left = DockPanel(LastChildFill = true)
        statusFolder.Margin <- Thickness(0.0, 0.0, 18.0, 0.0)
        statusGit.Margin <- Thickness(0.0, 0.0, 18.0, 0.0)
        statusWatch.Margin <- Thickness(0.0, 0.0, 18.0, 0.0)
        statusRun.TextTrimming <- TextTrimming.CharacterEllipsis
        statusRun.Margin <- Thickness(0.0, 0.0, 18.0, 0.0)
        DockPanel.SetDock(statusFolder, Dock.Left)
        DockPanel.SetDock(statusGit, Dock.Left)
        DockPanel.SetDock(statusWatch, Dock.Left)
        left.Children.Add statusFolder
        left.Children.Add statusGit
        left.Children.Add statusWatch
        left.Children.Add statusRun
        Grid.SetColumn(left, 0)
        status.Children.Add left
        status.Children.Add right
        let statusBorder = Border(Child = status, Height = 24.0)

        themed (fun r ->
            statusBorder.Background <- r.Brand

            for t in
                [ statusFolder
                  statusGit
                  statusWatch
                  statusRun
                  statusPos
                  statusLang
                  statusProblems ] do
                t.Foreground <- r.BrandFg
                t.FontSize <- 12.0)

        // the whole window
        let menu = buildMenu ()
        let content = Grid()
        content.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        content.RowDefinitions.Add(RowDefinition(Height = GridLength.Star))
        content.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
        Grid.SetRow(menu, 0)
        Grid.SetRow(bodyGrid, 1)
        Grid.SetRow(statusBorder, 2)
        content.Children.Add menu
        content.Children.Add bodyGrid
        content.Children.Add statusBorder

        let overlay = Grid()
        overlay.Children.Add content
        overlay.Children.Add palette
        overlay

    // ---- wiring -----------------------------------------------------------------------------------
    do
        refreshTreeFn <- refreshTree
        editor.TextArea.TextView.BackgroundRenderers.Add squiggles
        hooks.Add(fun _ -> refreshTree ())
        window.Content <- layout ()
        window.Icon <- null
        applyTheme theme
        applyEditorPrefs ()
        showSidebar "files"
        rebuildTabs ()

        outlineTimer.Tick.Add(fun _ ->
            outlineTimer.Stop()

            match active with
            | Some d ->
                recomputeOutline d

                if d.LspDirty then
                    syncLsp d

                updateStatus ()
                refreshSidebarFn ()
            | None -> ())

        editor.TextArea.Caret.PositionChanged.Add(fun _ -> updateStatus ())

        termInput.KeyDown.Add(fun e ->
            match e.Key with
            | Key.Enter ->
                e.Handled <- true
                let line = termInput.Text |> Option.ofObj |> Option.defaultValue ""
                termInput.Text <- ""
                sendTerminalLine line
            | Key.Up when termHistory.Count > 0 ->
                e.Handled <- true
                termHistoryAt <- max 0 (termHistoryAt - 1)
                termInput.Text <- termHistory[termHistoryAt]
                termInput.CaretIndex <- termInput.Text.Length
            | Key.Down when termHistory.Count > 0 ->
                e.Handled <- true
                termHistoryAt <- min termHistory.Count (termHistoryAt + 1)

                termInput.Text <-
                    if termHistoryAt >= termHistory.Count then
                        ""
                    else
                        termHistory[termHistoryAt]

                termInput.CaretIndex <- termInput.Text.Length
            | _ -> ())

        // hover: rest the pointer on code for a moment and a box appears; moving, typing or leaving hides it
        hoverTimer.Tick.Add(fun _ ->
            hoverTimer.Stop()
            let pos = editor.GetPositionFromPoint hoverPoint

            if pos.HasValue then
                showHoverAt pos.Value.Line pos.Value.Column (Point(hoverPoint.X + 12.0, hoverPoint.Y + 20.0)))

        editor.PointerMoved.Add(fun e ->
            let p = e.GetPosition editor

            if hoverBox.IsVisible then
                if abs (p.X - hoverPoint.X) > 4.0 || abs (p.Y - hoverPoint.Y) > 4.0 then
                    hideHover ()
                    hoverPoint <- p
                    hoverTimer.Start()
            else
                hoverPoint <- p
                hoverTimer.Stop()
                hoverTimer.Start())

        editor.PointerExited.Add(fun _ -> hideHover ())
        editor.TextArea.TextEntered.Add(fun _ -> hideHover ())
        editor.TextArea.TextView.ScrollOffsetChanged.Add(fun _ -> hideHover ())

        // type a closing bracket after an opening one, and step over it when it is typed again
        editor.TextArea.TextEntered.Add(fun e ->
            if prefs.Editor.AutoCloseBrackets && e.Text.Length = 1 then
                let closing =
                    match e.Text[0] with
                    | '(' -> Some ')'
                    | '[' -> Some ']'
                    | '{' -> Some '}'
                    | _ -> None

                match closing with
                | Some c ->
                    let doc = editor.Document
                    let at = editor.CaretOffset
                    let next = if at < doc.TextLength then doc.GetCharAt at else '\n'

                    if Char.IsWhiteSpace next || next = ')' || next = ']' || next = '}' then
                        doc.Insert(at, string c)
                        editor.CaretOffset <- at
                | None -> ())

        editor.TextArea.TextEntering.Add(fun e ->
            if
                prefs.Editor.AutoCloseBrackets
                && e.Text.Length = 1
                && (e.Text = ")" || e.Text = "]" || e.Text = "}")
            then
                let doc = editor.Document
                let at = editor.CaretOffset

                if at < doc.TextLength && string (doc.GetCharAt at) = e.Text then
                    editor.CaretOffset <- at + 1
                    e.Handled <- true)

        // the editor's own context menu
        let editorMenu = ContextMenu()
        editorMenu.Items.Push(menuItem "Cut" "Ctrl+X" (fun () -> editor.Cut()))
        editorMenu.Items.Push(menuItem "Copy" "Ctrl+C" (fun () -> editor.Copy()))
        editorMenu.Items.Push(menuItem "Paste" "Ctrl+V" (fun () -> editor.Paste()))
        editorMenu.Items.Push(menuItem "Select All" "Ctrl+A" (fun () -> editor.SelectAll()))
        editorMenu.Items.Push(Separator())
        editorMenu.Items.Push(menuItem "Go to Symbol…" "Ctrl+Shift+O" (fun () -> showPaletteFn "@"))
        editorMenu.Items.Push(menuItem "Format Document" "Shift+Alt+F" formatCurrent)
        editorMenu.Items.Push(menuItem "Run This File" "F5" (fun () -> runCurrent Runner.defaultTimeoutSec))
        editorMenu.Items.Push(menuItem "Command Palette…" "Ctrl+Shift+P" (fun () -> showPaletteFn ">"))
        editor.ContextMenu <- editorMenu

        searchBox.KeyDown.Add(fun e ->
            if e.Key = Key.Enter then
                e.Handled <- true
                runSearch ())

        searchResults.SelectionChanged.Add(fun _ ->
            match searchResults.SelectedItem with
            | :? TextBlock as t ->
                match t.Tag with
                | :? (string * int * int) as hit ->
                    let (p, l, c) = hit
                    openFile p l c
                | _ -> ()
            | _ -> ())

        outlineList.SelectionChanged.Add(fun _ ->
            match outlineList.SelectedItem with
            | :? TextBlock as t ->
                match t.Tag with
                | :? int as line ->
                    editor.TextArea.Caret.Line <- line
                    editor.ScrollToLine line
                    editor.Focus() |> ignore
                | _ -> ()
            | _ -> ())

        // keys that work wherever the focus is
        window.AddHandler(
            InputElement.KeyDownEvent,
            EventHandler<KeyEventArgs>(fun _ e ->
                let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control
                let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift
                let alt = e.KeyModifiers.HasFlag KeyModifiers.Alt

                let pick (id: string) =
                    e.Handled <- true
                    (command id).Run()

                match e.Key with
                | Key.P when ctrl && shift -> pick "palette"
                | Key.P when ctrl -> pick "quick-open"
                | Key.G when ctrl -> pick "goto-line"
                | Key.O when ctrl && shift -> pick "goto-symbol"
                | Key.O when ctrl -> pick "open-folder"
                | Key.S when ctrl && shift -> pick "save-all"
                | Key.S when ctrl -> pick "save"
                | Key.N when ctrl -> pick "new-file"
                | Key.W when ctrl -> pick "close-tab"
                | Key.B when ctrl -> pick "toggle-sidebar"
                | Key.J when ctrl -> pick "toggle-panel"
                | Key.E when ctrl && shift -> pick "explorer"
                | Key.F when ctrl && shift -> pick "find-files"
                | Key.OemTilde when ctrl -> pick "terminal"
                | Key.H when ctrl && shift -> pick "replace-in-files"
                | Key.H when ctrl -> pick "replace-in-file"
                | Key.M when ctrl && shift -> pick "problems"
                | Key.Space when ctrl -> pick "complete"
                | Key.F12 -> pick "definition"
                | Key.F5 -> pick "run"
                | Key.F when shift && alt -> pick "format"
                | Key.Z when alt -> pick "wrap"
                | Key.OemPlus when ctrl -> pick "zoom-in"
                | Key.OemMinus when ctrl -> pick "zoom-out"
                | Key.Escape when palette.IsVisible -> hidePalette ()
                | _ -> ()),
            RoutingStrategies.Tunnel
        )

        window.Closing.Add(fun e ->
            if not quitting && docs |> Seq.exists (fun d -> d.Dirty) then
                e.Cancel <- true

                task {
                    let! ok =
                        Dialogs.confirm
                            window
                            roles
                            "Unsaved changes"
                            "Some files have changes that are not saved. Quit and lose them?"
                            "Quit"

                    if ok then
                        quitting <- true
                        window.Close()
                }
                |> ignore
            else
                stopWatching ()
                stopServers ())

    member _.Window = window
    member _.Env = env
    member _.Editor = editor
    member _.OpenFolder(path: string) = openFolder path
    member _.OpenFile(rel: string, line: int, col: int) = openFile rel line col
    member _.Run(id: string) = (command id).Run()
    member _.Palette(text: string) = showPaletteFn text
    /// Sends a line to the terminal's shell (for the screenshot switches).
    member _.TerminalSend(line: string) = sendTerminalLine line

    /// Fills the search panel and runs the search (for the screenshot switches and the tests).
    member _.Search(query: string, replacement: string) =
        showSidebar "search"
        searchBox.Text <- query
        replaceBox.Text <- replacement
        runSearch ()

    member _.Say = say
    member _.TextMateReady = registryOptions
    member _.ThemeName = theme.Id
    member _.SetTheme(id: string) = applyTheme (Palette.find id)

    /// What the editor looks like right now, for the screenshot check.
    member _.Diagnostics() =
        let tv = editor.TextArea.TextView

        sprintf
            "watch=%s search=%s editor bounds=%A visible=%b docLen=%d textView bounds=%A visualLines=%b fg=%A bg=%A fontSize=%f lineNumbers=%b"
            (match watcher with
             | Some w -> FolderWatch.engineLabel w.Engine
             | None -> "none")
            lastSearchEngine
            editor.Bounds
            editor.IsVisible
            editor.Document.TextLength
            tv.Bounds
            tv.VisualLinesValid
            editor.Foreground
            editor.Background
            editor.FontSize
            editor.ShowLineNumbers

    member _.ForceQuit() =
        quitting <- true
        window.Close()
