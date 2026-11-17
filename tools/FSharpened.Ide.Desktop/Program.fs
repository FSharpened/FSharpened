module FSharpened.Ide.Desktop.Program

open System
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml.Styling
open Avalonia.Media.Imaging
open Avalonia.Themes.Fluent
open Avalonia.Threading

/// Command-line switches. The first four are for people; `--screenshot` exists so the real window can be checked
/// without a person looking at it (used by the build checks and the docs).
type Options =
    { Folder: string option
      File: string option
      Commands: string list
      LateCommands: string list
      TerminalLines: string list
      Theme: string option
      Palette: string option
      Search: string option
      ReplaceWith: string
      Screenshot: string option
      Width: int
      Height: int
      Delay: int }

let parse (argv: string[]) : Options =
    let rec go (o: Options) (rest: string list) =
        match rest with
        | "--folder" :: v :: t -> go { o with Folder = Some v } t
        | "--file" :: v :: t -> go { o with File = Some v } t
        | "--command" :: v :: t -> go { o with Commands = o.Commands @ [ v ] } t
        | "--late-command" :: v :: t ->
            go
                { o with
                    LateCommands = o.LateCommands @ [ v ] }
                t
        | "--search" :: v :: t -> go { o with Search = Some v } t
        | "--replace-with" :: v :: t -> go { o with ReplaceWith = v } t
        | "--terminal-line" :: v :: t ->
            go
                { o with
                    TerminalLines = o.TerminalLines @ [ v ] }
                t
        | "--theme" :: v :: t -> go { o with Theme = Some v } t
        | "--palette" :: v :: t -> go { o with Palette = Some v } t
        | "--screenshot" :: v :: t -> go { o with Screenshot = Some v } t
        | "--delay" :: v :: t when fst (Int32.TryParse v) -> go { o with Delay = int v } t
        | "--size" :: v :: t ->
            match v.Split('x') with
            | [| w; h |] when fst (Int32.TryParse w) && fst (Int32.TryParse h) ->
                go { o with Width = int w; Height = int h } t
            | _ -> go o t
        | v :: t when o.Folder.IsNone && not (v.StartsWith "--") -> go { o with Folder = Some v } t
        | _ :: t -> go o t
        | [] -> o

    go
        { Folder = None
          File = None
          Commands = []
          LateCommands = []
          TerminalLines = []
          Theme = None
          Palette = None
          Search = None
          ReplaceWith = ""
          Screenshot = None
          Width = 1320
          Height = 820
          Delay = 6 }
        (List.ofArray argv)

type App() =
    inherit Application()

    static member val Options: Options = parse [||] with get, set

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        // the editor control's own template and colours (AvaloniaEdit ships them as XAML)
        let edit = StyleInclude(Uri "avares://FSharpened.Ide.Desktop/")
        edit.Source <- Uri "avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"
        this.Styles.Add edit

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let o = App.Options
            let shell = Shell(HostEnv.standard ())
            let window = shell.Window
            window.Width <- float o.Width
            window.Height <- float o.Height
            desktop.MainWindow <- window

            window.Opened.Add(fun _ ->
                o.Theme |> Option.iter shell.SetTheme
                o.Folder |> Option.iter shell.OpenFolder
                o.File |> Option.iter (fun f -> shell.OpenFile(f, 0, 0))

                for c in o.Commands do
                    shell.Run c

                o.Palette |> Option.iter shell.Palette
                o.Search |> Option.iter (fun q -> shell.Search(q, o.ReplaceWith))

                if not o.TerminalLines.IsEmpty then
                    let typed =
                        DispatcherTimer(Interval = TimeSpan.FromSeconds(float (max 1 (o.Delay - 3))))

                    typed.Tick.Add(fun _ ->
                        typed.Stop()

                        for l in o.TerminalLines do
                            shell.TerminalSend l)

                    typed.Start()

                // commands that need the language server to be up first (a hover box, for one) run a little later
                if not o.LateCommands.IsEmpty then
                    let late =
                        DispatcherTimer(Interval = TimeSpan.FromSeconds(float (max 1 (o.Delay - 2))))

                    late.Tick.Add(fun _ ->
                        late.Stop()

                        for c in o.LateCommands do
                            shell.Run c)

                    late.Start()

                match o.Screenshot with
                | Some path ->
                    // give the folder listing, the highlighter and a run a moment to finish, then draw the window to a PNG and quit
                    let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds(float o.Delay))

                    timer.Tick.Add(fun _ ->
                        timer.Stop()

                        try
                            let size = PixelSize(int window.Bounds.Width, int window.Bounds.Height)
                            use bmp = new RenderTargetBitmap(size, Avalonia.Vector(96.0, 96.0))
                            bmp.Render window
                            bmp.Save(path, PngBitmapEncoderOptions())
                            File.WriteAllText(path + ".txt", shell.Diagnostics())
                        with e ->
                            File.WriteAllText(path + ".error.txt", e.ToString())

                        shell.ForceQuit())

                    timer.Start()
                | None -> ())
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

[<EntryPoint>]
[<STAThread>]
let main argv =
    App.Options <- parse argv
    AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(argv)
