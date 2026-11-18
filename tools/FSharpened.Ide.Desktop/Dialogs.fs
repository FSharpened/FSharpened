/// Small modal dialogs: a message, a yes/no question and a one-line prompt. Each is a plain window so it follows the theme.
module FSharpened.Ide.Desktop.Dialogs

open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media

let private shell (owner: Window) (title: string) (roles: Palette.Roles) (width: float) (body: Control) : Window =
    let w = Window()
    w.Title <- title
    w.Width <- width
    w.SizeToContent <- SizeToContent.Height
    w.CanResize <- false
    w.ShowInTaskbar <- false
    w.WindowStartupLocation <- WindowStartupLocation.CenterOwner
    w.Background <- roles.Bg
    w.Content <- body
    w

let private button (roles: Palette.Roles) (text: string) (primary: bool) (click: unit -> unit) : Button =
    let b = Button()
    b.Content <- text
    b.MinWidth <- 84.0
    b.Padding <- Thickness(14.0, 6.0)
    b.Background <- (if primary then roles.Brand else roles.Card)
    b.Foreground <- (if primary then roles.BrandFg else roles.Fg)
    b.BorderBrush <- roles.Line
    b.Click.Add(fun _ -> click ())
    b

let private message (roles: Palette.Roles) (text: string) : TextBlock =
    let t = TextBlock()
    t.Text <- text
    t.TextWrapping <- TextWrapping.Wrap
    t.Foreground <- roles.Fg
    t

let private buttons (items: Button list) : StackPanel =
    let row = StackPanel()
    row.Orientation <- Orientation.Horizontal
    row.HorizontalAlignment <- HorizontalAlignment.Right
    row.Spacing <- 8.0

    for b in items do
        row.Children.Add b

    row

let private column (children: Control list) : StackPanel =
    let c = StackPanel()
    c.Spacing <- 14.0
    c.Margin <- Thickness 20.0

    for x in children do
        c.Children.Add x

    c

/// A message with one button.
let info (owner: Window) (roles: Palette.Roles) (title: string) (text: string) : System.Threading.Tasks.Task =
    let mutable win: Window = null
    let ok = button roles "OK" true (fun () -> win.Close())
    win <- shell owner title roles 460.0 (column [ message roles text; buttons [ ok ] ])
    win.ShowDialog(owner)

/// A question with a primary and a cancel button; true when the primary one was chosen.
let confirm
    (owner: Window)
    (roles: Palette.Roles)
    (title: string)
    (text: string)
    (primary: string)
    : System.Threading.Tasks.Task<bool> =
    let mutable win: Window = null
    let yes = button roles primary true (fun () -> win.Close true)
    let no = button roles "Cancel" false (fun () -> win.Close false)
    win <- shell owner title roles 480.0 (column [ message roles text; buttons [ no; yes ] ])
    win.ShowDialog<bool>(owner)

/// A one-line prompt; None when cancelled. `validate` returns an error text to show, or "" when the value is acceptable.
let ask
    (owner: Window)
    (roles: Palette.Roles)
    (title: string)
    (label: string)
    (initial: string)
    (validate: string -> string)
    : System.Threading.Tasks.Task<string option> =
    let mutable win: Window = null
    let input = TextBox()
    input.Text <- initial
    input.Background <- roles.Code
    input.Foreground <- roles.Fg
    input.BorderBrush <- roles.Line
    let problem = message roles ""
    problem.Foreground <- roles.Bad

    let accept () =
        let v = input.Text |> Option.ofObj |> Option.defaultValue ""
        let err = validate v

        if err = "" then win.Close(Some v) else problem.Text <- err

    let ok = button roles "OK" true accept
    let cancel = button roles "Cancel" false (fun () -> win.Close(None: string option))

    win <-
        shell
            owner
            title
            roles
            460.0
            (column
                [ message roles label
                  input :> Control
                  problem :> Control
                  buttons [ cancel; ok ] ])

    input.KeyDown.Add(fun e ->
        if e.Key = Key.Enter then
            e.Handled <- true
            accept ()
        elif e.Key = Key.Escape then
            e.Handled <- true
            win.Close(None: string option))

    win.Opened.Add(fun _ ->
        input.Focus() |> ignore
        input.SelectAll())

    win.ShowDialog<string option>(owner)

/// What a rename is about to change, file by file, and a question: true when the primary button was chosen. `files` are
/// (name to show, number of changes, the lines that change as (line, before, after)).
let preview
    (owner: Window)
    (roles: Palette.Roles)
    (title: string)
    (summary: string)
    (files: (string * int * (int * string * string) list) list)
    (primary: string)
    : System.Threading.Tasks.Task<bool> =
    let mutable win: Window = null
    let yes = button roles primary true (fun () -> win.Close true)
    let no = button roles "Cancel" false (fun () -> win.Close false)
    let list = StackPanel()
    list.Spacing <- 2.0

    for (name, count, lines) in files do
        let head = TextBlock()
        head.Text <- sprintf "%s   (%d change%s)" name count (if count = 1 then "" else "s")
        head.FontWeight <- FontWeight.SemiBold
        head.Foreground <- roles.Fg
        head.Margin <- Thickness(0.0, 8.0, 0.0, 2.0)
        list.Children.Add head

        if lines.IsEmpty then
            let n = TextBlock()
            n.Text <- "  (lines move in this file, so they are not listed)"
            n.Foreground <- roles.Muted
            list.Children.Add n

        for (line, before, after) in lines |> List.truncate 12 do
            let row = StackPanel()
            row.Orientation <- Orientation.Horizontal
            row.Spacing <- 8.0
            row.Margin <- Thickness(12.0, 0.0, 0.0, 0.0)
            let num = TextBlock()
            num.Text <- string line
            num.Foreground <- roles.Muted
            num.Width <- 40.0
            num.TextAlignment <- TextAlignment.Right
            let b = TextBlock()
            b.Text <- before
            b.Foreground <- roles.Bad
            b.TextDecorations <- TextDecorations.Strikethrough
            b.FontFamily <- FontFamily "Cascadia Code, Consolas, Menlo, monospace"
            let a = TextBlock()
            a.Text <- "→  " + after
            a.Foreground <- roles.Ok
            a.FontFamily <- FontFamily "Cascadia Code, Consolas, Menlo, monospace"
            row.Children.Add num
            row.Children.Add b
            row.Children.Add a
            list.Children.Add row

        if lines.Length > 12 then
            let more = TextBlock()

            more.Text <-
                sprintf "  … and %d more line%s" (lines.Length - 12) (if lines.Length - 12 = 1 then "" else "s")

            more.Foreground <- roles.Muted
            list.Children.Add more

    let scroll = ScrollViewer()
    scroll.Content <- list
    scroll.MaxHeight <- 340.0
    scroll.HorizontalScrollBarVisibility <- Primitives.ScrollBarVisibility.Auto
    let border = Border()
    border.Child <- scroll
    border.BorderBrush <- roles.Line
    border.BorderThickness <- Thickness 1.0
    border.Padding <- Thickness 8.0
    border.Background <- roles.Code

    let heading = message roles title
    heading.FontWeight <- FontWeight.Bold
    heading.FontSize <- 16.0

    win <- shell owner title roles 720.0 (column [ heading; message roles summary; border; buttons [ no; yes ] ])
    win.Title <- title
    win.ShowDialog<bool>(owner)
