/// Several cursors: adding them, and typing, deleting, pasting, breaking lines and moving at all of them at once.
module CursorTests

open Xunit
open FSharpened.Ide.Host

let private caret o = Carets.caretAt o
let private set (cs: EditCursor list) (p: int) = { Cursors = cs; Primary = p }
let private carets (os: int list) = set (os |> List.map caret) 0

/// Applies a change and returns the new text and the caret offsets.
let private run (text: string) (change: CursorChange) =
    Carets.apply text change.Edits, change.Set.Cursors |> List.map (fun c -> c.Caret)

[<Fact>]
let ``typing goes in at every caret and the carets end up after it`` () =
    let text = "aa\nbb\ncc"
    let t, cs = run text (Carets.insert text "X" (carets [ 0; 3; 6 ]))
    Assert.Equal("Xaa\nXbb\nXcc", t)
    Assert.Equal<int list>([ 1; 5; 9 ], cs)

[<Fact>]
let ``typing over selections replaces each of them`` () =
    let text = "one two three"

    let sel = set [ { Anchor = 0; Caret = 3 }; { Anchor = 4; Caret = 7 } ] 1

    let change = Carets.insert text "#" sel
    let t, cs = run text change
    Assert.Equal("# # three", t)
    Assert.Equal<int list>([ 1; 3 ], cs)
    Assert.Equal(1, change.Set.Primary)

[<Fact>]
let ``backspace and delete act on each caret and on selections`` () =
    let text = "abc\ndef"
    let t, cs = run text (Carets.backspace text (carets [ 2; 6 ]))
    Assert.Equal("ac\ndf", t)
    Assert.Equal<int list>([ 1; 4 ], cs)

    let t2, _ = run text (Carets.delete text (carets [ 0; 4 ]))
    Assert.Equal("bc\nef", t2)

    // at the start (or the end) there is nothing to delete
    let t3, _ = run text (Carets.backspace text (carets [ 0 ]))
    Assert.Equal(text, t3)
    let t4, _ = run text (Carets.delete text (carets [ text.Length ]))
    Assert.Equal(text, t4)

[<Fact>]
let ``a line break is one character to backspace over, in both line endings`` () =
    let text = "a\r\nb"
    let t, cs = run text (Carets.backspace text (carets [ 3 ]))
    Assert.Equal("ab", t)
    Assert.Equal<int list>([ 1 ], cs)
    let t2, _ = run text (Carets.delete text (carets [ 1 ]))
    Assert.Equal("ab", t2)

[<Fact>]
let ``two carets that would delete the same character delete it once`` () =
    // a selection ending where the next caret's backspace starts must not be deleted twice
    let text = "abcdef"
    let sel = set [ { Anchor = 1; Caret = 4 }; caret 4 ] 0
    let t, cs = run text (Carets.backspace text sel)
    Assert.Equal("aef", t)
    Assert.All(cs, fun c -> Assert.InRange(c, 0, t.Length))

[<Fact>]
let ``enter keeps the indentation of each line`` () =
    let text = "  a\nb\n\tc"
    let t, cs = run text (Carets.newline text "\n" (carets [ 3; 5; 8 ]))
    Assert.Equal("  a\n  \nb\n\n\tc\n\t", t)
    Assert.Equal<int list>([ 6; 9; 14 ], cs)

[<Fact>]
let ``paste gives each cursor one line when the counts match, else the whole text to each`` () =
    let text = "x\nx\nx"
    let t, _ = run text (Carets.paste text "1\n2\n3" (carets [ 1; 3; 5 ]))
    Assert.Equal("x1\nx2\nx3", t)
    let t2, _ = run text (Carets.paste text "ab" (carets [ 1; 3; 5 ]))
    Assert.Equal("xab\nxab\nxab", t2)
    // two lines for three cursors: all of it to each
    let t3, _ = run text (Carets.paste text "p\nq" (carets [ 1; 3; 5 ]))
    Assert.Equal("xp\nq\nxp\nq\nxp\nq", t3)

[<Fact>]
let ``cursors that land on the same place become one`` () =
    let text = "ab"
    let t, cs = run text (Carets.backspace text (carets [ 1; 2 ]))
    Assert.Equal("", t)
    Assert.Equal<int list>([ 0 ], cs)

[<Fact>]
let ``adding a cursor below keeps the column and stops at a shorter line`` () =
    let text = "abcd\nxy\nlonger line"
    let one = carets [ 3 ]
    let two = Carets.addOnLine text 1 one |> Option.get
    Assert.Equal<int list>([ 3; 7 ], two.Cursors |> List.map (fun c -> c.Caret))
    Assert.Equal(1, two.Primary)
    let three = Carets.addOnLine text 1 two |> Option.get
    Assert.Equal<int list>([ 3; 7; 10 ], three.Cursors |> List.map (fun c -> c.Caret))
    // nothing below the last line
    Assert.True((Carets.addOnLine text 1 three).IsNone)

[<Fact>]
let ``adding a cursor above works upward and stops at the first line`` () =
    let text = "abcd\nxy\nlonger line"
    let up = Carets.addOnLine text -1 (carets [ 13 ]) |> Option.get
    Assert.Equal<int list>([ 7; 13 ], up.Cursors |> List.map (fun c -> c.Caret))
    let top = Carets.addOnLine text -1 up |> Option.get
    Assert.Equal<int list>([ 2; 7; 13 ], top.Cursors |> List.map (fun c -> c.Caret))
    Assert.True((Carets.addOnLine text -1 top).IsNone)

[<Fact>]
let ``select next match starts with the word, then adds each following occurrence and wraps`` () =
    let text = "foo bar foo baz foo"
    let first = Carets.addNextMatch text (carets [ 1 ]) |> Option.get
    Assert.Equal({ Anchor = 0; Caret = 3 }, Carets.primary first)
    let second = Carets.addNextMatch text first |> Option.get
    Assert.Equal(2, second.Cursors.Length)
    Assert.Equal({ Anchor = 8; Caret = 11 }, Carets.primary second)
    let third = Carets.addNextMatch text second |> Option.get
    Assert.Equal(3, third.Cursors.Length)
    // all of them have a cursor now
    Assert.True((Carets.addNextMatch text third).IsNone)
    // wrapping: starting from the last occurrence finds the first
    let last = set [ { Anchor = 16; Caret = 19 } ] 0
    let wrapped = Carets.addNextMatch text last |> Option.get
    Assert.Equal({ Anchor = 0; Caret = 3 }, Carets.primary wrapped)

[<Fact>]
let ``select next match on a caret between non-word characters does nothing`` () =
    Assert.True((Carets.addNextMatch "a  b" (carets [ 2 ])).IsNone)

[<Fact>]
let ``select all matches puts a cursor on every occurrence`` () =
    let text = "x = x + x"
    let all = Carets.selectAllMatches text (carets [ 0 ]) |> Option.get
    Assert.Equal(3, all.Cursors.Length)
    let t, _ = run text (Carets.insert text "yy" all)
    Assert.Equal("yy = yy + yy", t)
    Assert.True((Carets.selectAllMatches "  " (carets [ 1 ])).IsNone)

[<Fact>]
let ``moving every caret, with and without extending, and collapsing a selection`` () =
    let text = "abc\ndefg\nhi"
    let down = Carets.move text MoveDown false (carets [ 2 ])
    Assert.Equal<int list>([ 6 ], down.Cursors |> List.map (fun c -> c.Caret))
    // a shorter line puts the caret at its end
    let down2 = Carets.move text MoveDown false down
    Assert.Equal<int list>([ 11 ], down2.Cursors |> List.map (fun c -> c.Caret))
    let ends = Carets.move text MoveLineEnd false (carets [ 0; 4 ])
    Assert.Equal<int list>([ 3; 8 ], ends.Cursors |> List.map (fun c -> c.Caret))
    let extended = Carets.move text MoveRight true (carets [ 0; 4 ])
    Assert.Equal<EditCursor list>([ { Anchor = 0; Caret = 1 }; { Anchor = 4; Caret = 5 } ], extended.Cursors)
    let collapsed = Carets.move text MoveLeft false extended
    Assert.Equal<int list>([ 0; 4 ], collapsed.Cursors |> List.map (fun c -> c.Caret))
    let words = Carets.move text MoveWordRight false (carets [ 0 ])
    Assert.Equal<int list>([ 3 ], words.Cursors |> List.map (fun c -> c.Caret))

[<Fact>]
let ``there is a limit to how many cursors are made`` () =
    let text = String.replicate 800 "a\n"
    let mutable s = carets [ 0 ]

    for _ in 1..600 do
        match Carets.addOnLine text 1 s with
        | Some n -> s <- n
        | None -> ()

    Assert.Equal(Carets.maxCursors, s.Cursors.Length)

[<Fact>]
let ``the primary is the cursor the editor shows, and the others are the extras`` () =
    let s = Carets.ofPrimaryAndExtras (caret 5) [ caret 1; caret 9 ]
    Assert.Equal(1, s.Primary)
    Assert.Equal(caret 5, Carets.primary s)
    Assert.Equal<EditCursor list>([ caret 1; caret 9 ], Carets.extras s)
