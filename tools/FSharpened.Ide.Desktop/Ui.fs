/// The window's shared state and its controls: every widget the IDE draws is made here, once, and handed to the other parts.
namespace FSharpened.Ide.Desktop

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Automation
open Avalonia.Automation.Peers
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

type Ui(env: HostEnv) =
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

    let mutable problemsList: FSharpened.Diagnostics.StructuredDiagnostic list = []
    let mutable watcher: RunningWatch option = None
    let mutable watchSub: IDisposable option = None
    let mutable lastSearchEngine = ""
    let mutable quitting = false
    let expanded = HashSet<string>()
    let mutable gitBranch: string option = None
    let mutable gitChanges: Map<string, GitChange> = Map.empty
    let mutable gitEntries: GitEntry list = []
    /// Run after git's state (branch, changes) was read again.
    let onGitChanged = List<unit -> unit>()
    /// Sidebar views other parts add: name -> (title, builder).
    let extraViews = Dictionary<string, string * (unit -> Control)>()
    let mutable runDiagnostics: FSharpened.Diagnostics.StructuredDiagnostic list = []

    let lspDiagnostics =
        Dictionary<string, FSharpened.Diagnostics.StructuredDiagnostic list>()

    /// The diagnostics as the server sent them, per file, to go back with a code action request.
    let lspRaw = Dictionary<string, LspDiagnostic list>()

    /// Run when the active file changes or its language server becomes ready, and when its text has settled after an
    /// edit: for the features that read the server (outline) or git (the diff gutter).
    let onDocShown = List<OpenDoc -> unit>()
    let onSettled = List<OpenDoc -> unit>()

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
        // a button of symbols ("✕") is named by what its tip says it does
        Access.describe (Access.plainName tip) tip b |> ignore
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

        // "Files to include, e.g. src/**, *.go" is named "Files to include"
        let named =
            match hint.IndexOf ", e.g." with
            | -1 -> hint
            | i -> hint.Substring(0, i)

        Access.describe named hint t |> ignore
        themed (restyleBox t)
        t

    let menuItem (header: string) (gesture: string) (click: unit -> unit) =
        let m = MenuItem(Header = header)

        if gesture <> "" then
            try
                m.InputGesture <- KeyGesture.Parse gesture
            with _ ->
                () // a gesture only shown in the palette, such as Ctrl+=, has no menu form

        Access.nameOnly (Access.labelName header) (if gesture = "" then "" else "Shortcut " + gesture) m
        |> ignore

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
    let referencesBox = ListBox(Background = Brushes.Transparent)

    // signature help: a small box by the caret while a call's arguments are typed
    let signatureText =
        TextBlock(TextWrapping = TextWrapping.Wrap, FontFamily = monoFont, FontSize = 12.0)

    let signatureBox =
        Border(
            Child = signatureText,
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 640.0,
            Padding = Thickness(10.0, 6.0),
            CornerRadius = CornerRadius 4.0,
            BorderThickness = Thickness 1.0
        )

    // the quick-fix lightbulb: shown at the caret's line when the language server offers a fix
    let lightbulb =
        Button(
            Content = "💡",
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = Thickness(2.0, 0.0),
            Background = Brushes.Transparent,
            BorderThickness = Thickness 0.0
        )

    do
        themed (fun r ->
            signatureBox.Background <- r.Card
            signatureBox.BorderBrush <- r.Line
            signatureText.Foreground <- r.Fg)

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

    // the terminal panel: the user's shell on a pseudo-terminal, drawn by a terminal view (PLAN P7.8)
    let termView = TerminalView()

    let mutable term: TermBackend option = None
    let mutable terminalShownFn: unit -> unit = ignore

    // the debug panel: the call stack, the variables of the selected frame, and a console that also evaluates
    let dbgStack = ListBox(Background = Brushes.Transparent)
    let dbgVars = TreeView(Background = Brushes.Transparent)

    let dbgConsole =
        TextBox(
            IsReadOnly = true,
            FontFamily = monoFont,
            AcceptsReturn = true,
            BorderThickness = Thickness 0.0,
            TextWrapping = TextWrapping.NoWrap
        )

    let dbgInput =
        TextBox(
            FontFamily = monoFont,
            BorderThickness = Thickness(0.0, 1.0, 0.0, 0.0),
            PlaceholderText = "Evaluate an expression while the program is paused, then press Enter"
        )

    let dbgStatus =
        TextBlock(VerticalAlignment = VerticalAlignment.Center, Margin = Thickness(10.0, 0.0), FontSize = 12.0)

    let dbgButtons = Dictionary<string, Button>()

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
    let mutable saveBreakpointsFn: OpenDoc -> unit = ignore
    let mutable restoreBreakpointsFn: OpenDoc -> unit = ignore
    let mutable refreshExecFn: unit -> unit = ignore
    let mutable stopDebugFn: string -> unit = ignore

    let say (text: string) = statusRun.Text <- text

    let setOutput (text: string) = outputBox.Text <- text

    // ---- accessibility: what a screen reader says about each control ------------------------------
    do
        let describe name help (c: #Control) = Access.describe name help c |> ignore

        describe
            "Code editor"
            "Edits the open file. Ctrl+Space completes, F12 goes to the definition, F2 renames, F9 sets a breakpoint."
            editor

        describe
            "Folder tree"
            "The files of the open folder. Arrow keys move, Right and Left open and close a folder, Enter opens a file."
            fileTree

        describe "Output" "What the last run printed. Read only." outputBox

        describe
            "Problems"
            "Errors and warnings from the language server and the last run. Enter opens the place."
            problemsBox

        describe "References" "Every place that uses the symbol you asked about. Enter opens the place." referencesBox

        describe "Signature help" "The parameters of the call being typed, the current one in bold." signatureBox

        describe
            "Quick fix"
            "The language server offers a fix here. Press Enter, or Ctrl+Period, to see the choices."
            lightbulb

        describe "Standard input" "Text the program reads from standard input when it runs." inputBox

        describe
            "Compiler flags"
            "Extra flags for the compiler or interpreter when this file runs, for example -O2."
            flagsBox

        describe
            "Terminal"
            "Your shell, in the open folder. Keys go to the shell; Ctrl+Shift+C copies and Ctrl+Shift+V pastes."
            termView

        describe "Call stack" "The frames of the paused program. Enter shows the frame's variables." dbgStack
        describe "Variables" "The variables of the selected frame. Right opens a variable's children." dbgVars
        describe "Debug console output" "What the program and the debugger printed. Read only." dbgConsole

        describe
            "Evaluate expression"
            "Type an expression and press Enter to evaluate it while the program is paused."
            dbgInput

        describe
            "Command palette input"
            "Type to open a file, a greater-than sign for commands, a colon for a line, an at sign for a symbol."
            paletteInput

        describe "Command palette results" "Up and Down choose, Enter runs, Escape closes." paletteList
        describe "Command palette" "" palette
        describe "Tabs" "The open files." tabStrip
        describe "Sidebar" "Explorer, search, outline or tools, as chosen in the activity bar." sidebarHost
        describe "Breadcrumb" "The open file's path and the symbols around the caret." crumbs
        describe "Welcome" "Open a folder to begin." welcome
        describe "Sidebar title" "" sidebarTitle
        // the focus lands on the editor's text area, so a screen reader needs the name there too
        Access.nameOnly "Code editor" "Edits the open file." editor.TextArea |> ignore
        Access.role AutomationControlType.Tab tabStrip |> ignore
        Access.landmark AutomationLandmarkType.Navigation sidebarHost |> ignore
        Access.landmark AutomationLandmarkType.Main editor |> ignore
        Access.role AutomationControlType.Pane palette |> ignore
        Access.live statusRun |> ignore
        Access.describe "Status message" "" statusRun |> ignore
        Access.describe "Folder name" "" statusFolder |> ignore
        Access.describe "Git branch and changes" "" statusGit |> ignore
        Access.describe "File watcher" "" statusWatch |> ignore
        Access.describe "Cursor position" "" statusPos |> ignore
        Access.describe "Language of the open file" "" statusLang |> ignore
        Access.describe "Problem counts" "" statusProblems |> ignore

    member _.Active
        with get () = active
        and set v = active <- v

    member _.BodyGrid = bodyGrid
    member _.BottomRow = bottomRow
    member _.BottomSplitRow = bottomSplitRow
    member _.Crumbs = crumbs

    member _.CurrentPanel
        with get () = currentPanel
        and set v = currentPanel <- v

    member _.DbgButtons = dbgButtons
    member _.DbgConsole = dbgConsole
    member _.DbgInput = dbgInput
    member _.DbgStack = dbgStack
    member _.DbgStatus = dbgStatus
    member _.DbgVars = dbgVars
    member _.Docs = docs
    member _.Editor = editor
    member _.Env = env
    member _.Expanded = expanded

    member _.FileCache
        with get () = fileCache
        and set v = fileCache <- v

    member _.FileTree = fileTree
    member _.FlagsBox = flagsBox
    member _.Flat = flat

    member _.GitBranch
        with get () = gitBranch
        and set v = gitBranch <- v

    member _.GitChanges
        with get () = gitChanges
        and set v = gitChanges <- v

    member _.GitEntries
        with get () = gitEntries
        and set v = gitEntries <- v

    member _.OnGitChanged = onGitChanged
    member _.ExtraViews = extraViews

    member _.Hooks = hooks
    member _.InputBox = inputBox
    member _.Label = label

    member _.LastSearchEngine
        with get () = lastSearchEngine
        and set v = lastSearchEngine <- v

    member _.LspDiagnostics = lspDiagnostics
    member _.LspRaw = lspRaw
    member _.OnDocShown = onDocShown
    member _.OnSettled = onSettled
    member _.LspFailed = lspFailed
    member _.LspSessions = lspSessions
    member _.LspStarting = lspStarting
    member _.MainGrid = mainGrid
    member _.MenuItem = menuItem
    member _.MonoFont = monoFont
    member _.Muted = muted

    member _.OpenFileFn
        with get () = openFileFn
        and set v = openFileFn <- v

    member _.OutputBox = outputBox
    member _.Palette = palette
    member _.PaletteInput = paletteInput
    member _.PaletteList = paletteList
    member _.PanelBorder = panelBorder

    member _.PanelShown
        with get () = panelShown
        and set v = panelShown <- v

    member _.PanelTabs = panelTabs

    member _.Prefs
        with get () = prefs
        and set v = prefs <- v

    member _.ProblemsBox = problemsBox
    member _.ReferencesBox = referencesBox
    member _.SignatureBox = signatureBox
    member _.SignatureText = signatureText
    member _.Lightbulb = lightbulb

    member _.ProblemsList
        with get () = problemsList
        and set v = problemsList <- v

    member _.Quitting
        with get () = quitting
        and set v = quitting <- v

    member _.RefreshExecFn
        with get () = refreshExecFn
        and set v = refreshExecFn <- v

    member _.RefreshSidebarFn
        with get () = refreshSidebarFn
        and set v = refreshSidebarFn <- v

    member _.RefreshTreeFn
        with get () = refreshTreeFn
        and set v = refreshTreeFn <- v

    member _.RegistryOptions = registryOptions

    member _.RestoreBreakpointsFn
        with get () = restoreBreakpointsFn
        and set v = restoreBreakpointsFn <- v

    member _.RestyleBox = restyleBox

    member _.Roles
        with get () = roles
        and set v = roles <- v

    member _.Root
        with get () = root
        and set v = root <- v

    member _.RunDiagnostics
        with get () = runDiagnostics
        and set v = runDiagnostics <- v

    member _.SaveBreakpointsFn
        with get () = saveBreakpointsFn
        and set v = saveBreakpointsFn <- v

    member _.Say = say
    member _.SearchPanel = searchPanel
    member _.SetOutput = setOutput

    member _.ShowPaletteFn
        with get () = showPaletteFn
        and set v = showPaletteFn <- v

    member _.SidebarColumn = sidebarColumn
    member _.SidebarHost = sidebarHost

    member _.SidebarShown
        with get () = sidebarShown
        and set v = sidebarShown <- v

    member _.SidebarTitle = sidebarTitle

    member _.SidebarView
        with get () = sidebarView
        and set v = sidebarView <- v

    member _.SplitColumn = splitColumn
    member _.Squiggles = squiggles
    member _.StatusFolder = statusFolder
    member _.StatusGit = statusGit
    member _.StatusLang = statusLang
    member _.StatusPos = statusPos
    member _.StatusProblems = statusProblems
    member _.StatusRun = statusRun
    member _.StatusWatch = statusWatch

    member _.StopDebugFn
        with get () = stopDebugFn
        and set v = stopDebugFn <- v

    member _.TabStrip = tabStrip

    member _.Term
        with get () = term
        and set v = term <- v

    member _.TermView = termView

    member _.TerminalShownFn
        with get () = terminalShownFn
        and set v = terminalShownFn <- v

    member _.TextBox = textBox
    member _.TextMate = textMate
    member _.TextMateTheme = textMateTheme

    member _.Theme
        with get () = theme
        and set v = theme <- v

    member _.Themed = themed

    member _.WatchSub
        with get () = watchSub
        and set v = watchSub <- v

    member _.Watcher
        with get () = watcher
        and set v = watcher <- v

    member _.Welcome = welcome
    member _.Window = window
