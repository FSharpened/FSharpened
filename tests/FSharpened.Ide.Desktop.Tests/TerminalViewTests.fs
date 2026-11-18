module TerminalViewTests

open Xunit
open Avalonia.Input
open Avalonia.Media
open FSharpened.Ide.Desktop
open FSharpened.Ide.Host

let private none = KeyModifiers.None
let private ctrl = KeyModifiers.Control
let private alt = KeyModifiers.Alt
let private shift = KeyModifiers.Shift

let private encoded (key: Key) (m: KeyModifiers) =
    TerminalKeys.ofAvalonia key m
    |> Option.bind (fun (k, tm) -> TermKeys.encode false tm k)

[<Fact>]
let ``Ctrl+C and the other Ctrl letters become control characters`` () =
    Assert.Equal(Some "\u0003", encoded Key.C ctrl)
    Assert.Equal(Some "\u0004", encoded Key.D ctrl)
    Assert.Equal(Some "\u0001", encoded Key.A (ctrl ||| shift))
    Assert.Equal(Some "\u0000", encoded Key.Space ctrl)
    Assert.Equal(Some "\u001b", encoded Key.OemOpenBrackets ctrl)

[<Fact>]
let ``arrows, editing keys and function keys are sent whatever the modifiers`` () =
    Assert.Equal(Some "\u001b[A", encoded Key.Up none)
    Assert.Equal(Some "\u001b[1;5D", encoded Key.Left ctrl)
    Assert.Equal(Some "\u001b[3~", encoded Key.Delete none)
    Assert.Equal(Some "\u001b[6~", encoded Key.PageDown none)
    Assert.Equal(Some "\r", encoded Key.Enter none)
    Assert.Equal(Some "\u007f", encoded Key.Back none)
    Assert.Equal(Some "\u001b[Z", encoded Key.Tab shift)
    Assert.Equal(Some "\u001bOP", encoded Key.F1 none)
    Assert.Equal(Some "\u001b[24~", encoded Key.F12 none)

[<Fact>]
let ``Alt plus a letter sends escape first, and a plain letter is left to the text event`` () =
    Assert.Equal(Some "\u001bb", encoded Key.B alt)
    Assert.Equal(Some "\u001bB", encoded Key.B (alt ||| shift))
    Assert.Equal(None, encoded Key.B none)
    Assert.Equal(None, encoded Key.B shift)
    Assert.Equal(None, encoded Key.D5 none)

[<Fact>]
let ``Ctrl+Alt is AltGr on Windows keyboards and types a character, so it is not a terminal key`` () =
    Assert.Equal(None, encoded Key.Q (ctrl ||| alt))
    Assert.Equal(None, encoded Key.D2 (ctrl ||| alt))

[<Fact>]
let ``the Windows key and lone modifier keys mean nothing to a terminal`` () =
    Assert.Equal(None, encoded Key.C KeyModifiers.Meta)
    Assert.Equal(None, encoded Key.LeftCtrl ctrl)
    Assert.Equal(None, encoded Key.LeftShift shift)

[<Fact>]
let ``only a few window shortcuts stay with the window while the terminal has the focus`` () =
    for k in [ Key.OemTilde; Key.J; Key.B; Key.P; Key.OemPlus; Key.OemMinus ] do
        Assert.True(TerminalKeys.windowKeeps k ctrl)

    for k in [ Key.P; Key.E; Key.F; Key.H; Key.M ] do
        Assert.True(TerminalKeys.windowKeeps k (ctrl ||| shift))

    // a shell's own Ctrl keys and plain keys are the shell's
    for k in [ Key.C; Key.D; Key.W; Key.N; Key.G; Key.O; Key.S; Key.H; Key.Z; Key.Space ] do
        Assert.False(TerminalKeys.windowKeeps k ctrl)

    Assert.False(TerminalKeys.windowKeeps Key.F5 none)
    Assert.False(TerminalKeys.windowKeeps Key.F12 none)
    Assert.False(TerminalKeys.windowKeeps Key.P none)
    Assert.False(TerminalKeys.windowKeeps Key.P (ctrl ||| alt))

[<Fact>]
let ``terminal colours resolve over the theme's defaults`` () =
    let fallback = Color.FromRgb(1uy, 2uy, 3uy)
    let resolve = TerminalColors.resolve Vt.basic16Dark fallback

    Assert.Equal(fallback, resolve false Default)
    Assert.Equal(Color.FromRgb(9uy, 8uy, 7uy), resolve false (Rgb(9uy, 8uy, 7uy)))
    Assert.Equal(Color.FromRgb(205uy, 49uy, 49uy), resolve false (Indexed 1))
    // bold brightens the eight basic colours only
    Assert.Equal(Color.FromRgb(241uy, 76uy, 76uy), resolve true (Indexed 1))
    Assert.Equal(Color.FromRgb(241uy, 76uy, 76uy), resolve true (Indexed 9))
    Assert.Equal(Color.FromRgb(0uy, 0uy, 0uy), resolve true (Indexed 16))

[<Fact>]
let ``dim text is the foreground part of the way to the background`` () =
    let a = Color.FromRgb(200uy, 100uy, 0uy)
    let b = Color.FromRgb(0uy, 100uy, 200uy)
    Assert.Equal(a, TerminalColors.blend a b 0.0)
    Assert.Equal(b, TerminalColors.blend a b 1.0)
    Assert.Equal(Color.FromRgb(100uy, 100uy, 100uy), TerminalColors.blend a b 0.5)
