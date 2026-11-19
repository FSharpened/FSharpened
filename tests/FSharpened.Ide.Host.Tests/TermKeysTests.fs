module TermKeysTests

open Xunit
open FSharpened.Ide.Host

let private enc app mods key = TermKeys.encode app mods key
let private ctrl = { TermKeys.noMods with Ctrl = true }
let private alt = { TermKeys.noMods with Alt = true }
let private shift = { TermKeys.noMods with Shift = true }

[<Fact>]
let ``Ctrl plus a letter is the control character, so Ctrl+C is the interrupt`` () =
    Assert.Equal(Some "\u0003", enc false ctrl (Char "c"))
    Assert.Equal(Some "\u0003", enc false ctrl (Char "C"))
    Assert.Equal(Some "\u0004", enc false ctrl (Char "d"))
    Assert.Equal(Some "\u001a", enc false ctrl (Char "z"))
    Assert.Equal(Some "\u0000", enc false ctrl (Char " "))
    Assert.Equal(Some "\u001b", enc false ctrl (Char "["))
    Assert.Equal(None, enc false ctrl (Char "é"))

[<Fact>]
let ``Alt prefixes escape and plain characters pass through`` () =
    Assert.Equal(Some "x", enc false TermKeys.noMods (Char "x"))
    Assert.Equal(Some "\u001bx", enc false alt (Char "x"))
    Assert.Equal(Some "é", enc false TermKeys.noMods (Char "é"))

[<Fact>]
let ``arrows send ESC [ or ESC O depending on the application cursor mode`` () =
    Assert.Equal(Some "\u001b[A", enc false TermKeys.noMods Up)
    Assert.Equal(Some "\u001b[B", enc false TermKeys.noMods Down)
    Assert.Equal(Some "\u001b[C", enc false TermKeys.noMods Right)
    Assert.Equal(Some "\u001b[D", enc false TermKeys.noMods Left)
    Assert.Equal(Some "\u001bOA", enc true TermKeys.noMods Up)
    Assert.Equal(Some "\u001b[1;5C", enc true ctrl Right)
    Assert.Equal(Some "\u001b[1;2D", enc false shift Left)
    Assert.Equal(Some "\u001b[1;3A", enc false alt Up)

[<Fact>]
let ``editing keys use the tilde sequences`` () =
    Assert.Equal(Some "\u001b[3~", enc false TermKeys.noMods Delete)
    Assert.Equal(Some "\u001b[2~", enc false TermKeys.noMods Insert)
    Assert.Equal(Some "\u001b[5~", enc false TermKeys.noMods PageUp)
    Assert.Equal(Some "\u001b[6~", enc false TermKeys.noMods PageDown)
    Assert.Equal(Some "\u001b[3;5~", enc false ctrl Delete)
    Assert.Equal(Some "\u001b[H", enc false TermKeys.noMods Home)
    Assert.Equal(Some "\u001b[F", enc false TermKeys.noMods End)

[<Fact>]
let ``Enter, Tab, Backspace and Escape`` () =
    Assert.Equal(Some "\r", enc false TermKeys.noMods Enter)
    Assert.Equal(Some "\t", enc false TermKeys.noMods Tab)
    Assert.Equal(Some "\u001b[Z", enc false shift Tab)
    Assert.Equal(Some "\u007f", enc false TermKeys.noMods Backspace)
    Assert.Equal(Some "\b", enc false ctrl Backspace)
    Assert.Equal(Some "\u001b", enc false TermKeys.noMods Escape)

[<Fact>]
let ``function keys`` () =
    Assert.Equal(Some "\u001bOP", enc false TermKeys.noMods (F 1))
    Assert.Equal(Some "\u001bOS", enc false TermKeys.noMods (F 4))
    Assert.Equal(Some "\u001b[15~", enc false TermKeys.noMods (F 5))
    Assert.Equal(Some "\u001b[24~", enc false TermKeys.noMods (F 12))
    Assert.Equal(Some "\u001b[1;2P", enc false shift (F 1))
    Assert.Equal(None, enc false TermKeys.noMods (F 13))

[<Fact>]
let ``pasted text turns line breaks into Enter and is bracketed when the program asked for it`` () =
    Assert.Equal("a\rb\rc", TermKeys.paste false "a\r\nb\nc")
    Assert.Equal("\u001b[200~a\rb\u001b[201~", TermKeys.paste true "a\nb")
    // an escape inside pasted text cannot end the bracket early
    Assert.Equal("\u001b[200~x[201~y\u001b[201~", TermKeys.paste true "x\u001b[201~y")
