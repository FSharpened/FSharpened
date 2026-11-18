module VtTests

open Xunit
open FSharpened.Ide.Host

let private screen (cols: int) (rows: int) (input: string) =
    let replies = System.Text.StringBuilder()
    let s = Screen(cols, rows, (fun r -> replies.Append r |> ignore))
    s.Feed input
    s, replies

let private cellAt (s: Screen) r c = (s.Row r)[c]

[<Fact>]
let ``plain text lands on the screen and line ends move the cursor`` () =
    let s, _ = screen 20 4 "hello\r\nworld"
    Assert.Equal("hello", s.RowText 0)
    Assert.Equal("world", s.RowText 1)
    Assert.Equal(5, s.CursorX)
    Assert.Equal(1, s.CursorY)

[<Fact>]
let ``a bare line feed keeps the column and a carriage return rewrites the line`` () =
    let s, _ = screen 20 4 "abc\ndef\rXY"
    Assert.Equal("abc", s.RowText 0)
    Assert.Equal("XY def", s.RowText 1)

[<Fact>]
let ``colours and attributes from SGR are stored on the cells`` () =
    let s, _ =
        screen 20 2 "\u001b[1;31mR\u001b[0mx\u001b[38;5;200mI\u001b[48;2;1;2;3mT\u001b[0m"

    let r = cellAt s 0 0
    Assert.Equal(int 'R', r.Cp)
    Assert.Equal(Indexed 1, r.Style.Fg)
    Assert.True r.Style.Bold
    Assert.Equal(Vt.plain, (cellAt s 0 1).Style)
    Assert.Equal(Indexed 200, (cellAt s 0 2).Style.Fg)
    Assert.Equal(Rgb(1uy, 2uy, 3uy), (cellAt s 0 3).Style.Bg)

[<Fact>]
let ``bright colours, the colon form of true colour and resets work`` () =
    let s, _ = screen 20 2 "\u001b[92ma\u001b[38:2::10:20:30mb\u001b[39;49mc"
    Assert.Equal(Indexed 10, (cellAt s 0 0).Style.Fg)
    Assert.Equal(Rgb(10uy, 20uy, 30uy), (cellAt s 0 1).Style.Fg)
    Assert.Equal(Default, (cellAt s 0 2).Style.Fg)

[<Fact>]
let ``cursor movement, absolute positioning and erasing`` () =
    let s, _ =
        screen 10 5 "aaaaaaaaaa\r\nbbbbbbbbbb\u001b[1;3H\u001b[2Kxy\u001b[2;5H\u001b[1Kz\u001b[3;1Hq\u001b[B\u001b[5D!"

    Assert.Equal("  xy", s.RowText 0)
    Assert.Equal("    z" + "bbbbb", s.RowText 1)
    Assert.Equal("q", s.RowText 2)
    Assert.Equal("!", s.RowText 3)

[<Fact>]
let ``clearing the screen with ESC [ 2 J and homing leaves it empty`` () =
    let s, _ = screen 10 3 "one\r\ntwo\u001b[2J\u001b[Hnew"
    Assert.Equal("new", s.Text())

[<Fact>]
let ``text past the last column wraps, and scrolling pushes lines into the scrollback`` () =
    let s, _ = screen 5 2 "abcdefgh\r\nxyz\r\nlast"
    // 5 columns: "abcde" wraps to "fgh"; then xyz, last scroll the screen
    Assert.Equal(2, s.Rows)
    Assert.Equal("xyz", s.RowText 0)
    Assert.Equal("last", s.RowText 1)
    Assert.True(s.ScrollbackCount >= 2)
    Assert.Equal("abcde", s.ScrollbackText 0)
    Assert.Equal("fgh", s.ScrollbackText 1)

[<Fact>]
let ``a scrolling region keeps the lines outside it still`` () =
    let s, _ = screen 6 5 "head\r\n\u001b[2;4r\u001b[2;1Ha\r\nb\r\nc\r\nd"
    Assert.Equal("head", s.RowText 0)
    Assert.Equal("b", s.RowText 1)
    Assert.Equal("c", s.RowText 2)
    Assert.Equal("d", s.RowText 3)
    Assert.Equal(0, s.ScrollbackCount)

[<Fact>]
let ``the alternate screen is separate and the main screen comes back untouched`` () =
    let s, _ = screen 10 3 "shell$ \u001b[?1049h\u001b[Hvim here"
    Assert.True s.InAlternateScreen
    Assert.Equal("vim here", s.RowText 0)
    s.Feed "\u001b[?1049l"
    Assert.False s.InAlternateScreen
    Assert.Equal("shell$", s.RowText 0)
    Assert.Equal(7, s.CursorX)

[<Fact>]
let ``titles are taken and other operating system commands are ignored`` () =
    let s, _ = screen 10 2 "\u001b]0;my title\u0007\u001b]8;;http://x\u001b\\ok"
    Assert.Equal("my title", s.Title)
    Assert.Equal("ok", s.RowText 0)

[<Fact>]
let ``sequences split across reads are finished by the next one`` () =
    let s, _ = screen 10 2 "a\u001b[3"
    s.Feed "1mb\u001b"
    s.Feed "[0mc"
    Assert.Equal("abc", s.RowText 0)
    Assert.Equal(Indexed 1, (cellAt s 0 1).Style.Fg)
    Assert.Equal(Default, (cellAt s 0 2).Style.Fg)

[<Fact>]
let ``the replies a shell waits for are sent back`` () =
    let _, r = screen 10 5 "ab\r\ncd\u001b[6n\u001b[c\u001b[5n"
    Assert.Equal("\u001b[2;3R" + "\u001b[?1;2c" + "\u001b[0n", r.ToString())

[<Fact>]
let ``modes for cursor keys, bracketed paste and cursor visibility are tracked`` () =
    let s, _ = screen 10 2 "\u001b[?1h\u001b[?2004h\u001b[?25l"
    Assert.True s.AppCursor
    Assert.True s.BracketedPaste
    Assert.False s.CursorVisible
    s.Feed "\u001b[?1l\u001b[?2004l\u001b[?25h"
    Assert.False s.AppCursor
    Assert.False s.BracketedPaste
    Assert.True s.CursorVisible

[<Fact>]
let ``line drawing characters are mapped while that set is chosen`` () =
    let s, _ = screen 10 2 "\u001b(0lqk\u001b(Bx"
    Assert.Equal("┌─┐x", s.RowText 0)

[<Fact>]
let ``wide characters take two cells and combining marks none`` () =
    let s, _ = screen 10 2 "a中b́c"
    Assert.Equal(int '中', (cellAt s 0 1).Cp)
    Assert.Equal(-1, (cellAt s 0 2).Cp)
    Assert.Equal(int 'b', (cellAt s 0 3).Cp)
    Assert.Equal(int 'c', (cellAt s 0 4).Cp)

[<Fact>]
let ``characters outside the basic plane arrive as pairs, even split across reads`` () =
    let s, _ = screen 10 2 (string (char 0xD83D))
    s.Feed(string (char 0xDE00) + "!")
    Assert.Equal(0x1F600, (cellAt s 0 0).Cp)
    Assert.Equal(int '!', (cellAt s 0 2).Cp)

[<Fact>]
let ``insert, delete and erase characters shift the rest of the line`` () =
    let s, _ =
        screen 10 2 "abcdef\u001b[1;3H\u001b[2@XY\u001b[1;1H\u001b[1P\u001b[1;6H\u001b[2X"

    Assert.Equal("bXYcd", s.RowText 0)

[<Fact>]
let ``resizing keeps what fits and moves the cursor into the new size`` () =
    let s, _ = screen 10 4 "one\r\ntwo\r\nthree\r\nfour\r\nfive"
    s.Resize(6, 2)
    Assert.Equal(6, s.Cols)
    Assert.Equal(2, s.Rows)
    Assert.True(s.CursorY < 2 && s.CursorX < 6)
    Assert.Equal("five", s.RowText(s.CursorY))
    s.Resize(20, 6)
    Assert.Equal(20, s.Cols)

[<Fact>]
let ``backspace moves left and tabs go to the next multiple of eight`` () =
    let s, _ = screen 20 2 "ab\bX\tY"
    Assert.Equal("aX      Y", s.RowText 0)

[<Fact>]
let ``a full reset clears everything`` () =
    let s, _ = screen 10 3 "junk\u001b[?1h\u001bcok"
    Assert.Equal("ok", s.Text())
    Assert.False s.AppCursor

[<Fact>]
let ``the indexed colours cover the basic set, the cube and the grey ramp`` () =
    Assert.Equal((0uy, 0uy, 0uy), Vt.indexedRgb Vt.basic16Dark 0)
    Assert.Equal(Vt.basic16Dark[9], Vt.indexedRgb Vt.basic16Dark 9)
    Assert.Equal((0uy, 0uy, 0uy), Vt.indexedRgb Vt.basic16Dark 16)
    Assert.Equal((255uy, 255uy, 255uy), Vt.indexedRgb Vt.basic16Dark 231)
    Assert.Equal((95uy, 135uy, 175uy), Vt.indexedRgb Vt.basic16Dark (16 + 36 * 1 + 6 * 2 + 3))
    Assert.Equal((8uy, 8uy, 8uy), Vt.indexedRgb Vt.basic16Dark 232)
    Assert.Equal((238uy, 238uy, 238uy), Vt.indexedRgb Vt.basic16Dark 255)

[<Fact>]
let ``garbage never throws`` () =
    let rnd = System.Random 7
    let s = Screen(17, 5, ignore)

    for _ in 1..300 do
        let chunk = System.String(Array.init 200 (fun _ -> char (rnd.Next(0, 0x2f0))))
        s.Feed chunk
        s.Resize(rnd.Next(1, 30), rnd.Next(1, 10))

    Assert.True(s.Rows >= 1)
