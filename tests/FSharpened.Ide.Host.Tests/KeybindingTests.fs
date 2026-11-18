/// Shortcuts as data: chords in one form, the shipped table, the person's file laid over it, and rebinding.
module KeybindingTests

open Xunit
open FSharpened.Ide.Host

let private ok (s: string) =
    match Keybindings.normalize s with
    | Ok c -> c
    | Error e -> failwithf "%s: %s" s e

[<Fact>]
let ``chords are written in one form whatever the spelling`` () =
    Assert.Equal("ctrl+shift+p", ok "Ctrl+Shift+P")
    Assert.Equal("ctrl+shift+p", ok "shift + ctrl + p")
    Assert.Equal("ctrl+alt+down", ok "Control+Option+ArrowDown")
    Assert.Equal("meta+comma", ok "cmd+,")
    Assert.Equal("ctrl+equal", ok "ctrl++")
    Assert.Equal("ctrl+backquote", ok "ctrl+`")
    Assert.Equal("f5", ok "F5")
    Assert.Equal("shift+f12", ok "Shift+F12")

[<Fact>]
let ``chords that cannot be shortcuts say why`` () =
    for bad in
        [ ""
          "p"
          "shift+p"
          "enter"
          "ctrl"
          "ctrl+shift"
          "ctrl+nope"
          "hyper+p"
          "ctrl+ctrl+p"
          "ctrl+k ctrl+s"
          "left" ] do
        match Keybindings.normalize bad with
        | Error why -> Assert.False(System.String.IsNullOrEmpty why)
        | Ok c -> failwithf "%s should be refused, got %s" bad c

[<Fact>]
let ``chords read back the way people write them`` () =
    Assert.Equal("Ctrl+Shift+P", Keybindings.display "ctrl+shift+p")
    Assert.Equal("Ctrl+,", Keybindings.display "ctrl+comma")
    Assert.Equal("F12", Keybindings.display "f12")
    Assert.Equal("Ctrl+Alt+Down", Keybindings.display "ctrl+alt+down")
    Assert.Equal("Ctrl+PageDown", Keybindings.display "ctrl+pagedown")

[<Fact>]
let ``every shipped chord is in the normal form and none is used twice`` () =
    for b in Keybindings.defaults do
        Assert.Equal(b.Chord, ok b.Chord)

    let groups =
        Keybindings.defaults
        |> List.groupBy (fun b -> b.Chord, b.When)
        |> List.filter (fun (_, g) -> g.Length > 1)

    Assert.Empty groups

[<Fact>]
let ``a chord runs the last binding that fits, and editor-only ones need the editor`` () =
    let t = Keybindings.defaults
    Assert.Equal(Some "palette", Keybindings.resolve t "ctrl+shift+p" false)
    Assert.Equal(None, Keybindings.resolve t "f2" false)
    Assert.Equal(Some "rename-symbol", Keybindings.resolve t "f2" true)
    Assert.Equal(None, Keybindings.resolve t "ctrl+shift+9" true)

let private known (id: string) =
    id = "save" || id = "palette" || id = "wrap" || id = "find"

[<Fact>]
let ``the person's file adds, replaces and removes bindings`` () =
    let json =
        """
        // my keys
        [
          { "key": "ctrl+shift+s", "command": "save" },
          { "key": "ctrl+s", "command": "-save" },
          { "key": "alt+q", "command": "wrap", },
        ]
        """

    let entries, problems = Keybindings.parseUser json known
    Assert.Empty problems
    Assert.Equal(3, entries.Length)
    let t = Keybindings.build entries
    // ctrl+shift+s was save-all and is now save; ctrl+s no longer saves
    Assert.Equal(Some "save", Keybindings.resolve t "ctrl+shift+s" false)
    Assert.Equal(None, Keybindings.resolve t "ctrl+s" false)
    Assert.Equal(None, Keybindings.chordsOf t "save-all" |> List.tryHead)
    Assert.Equal(Some "wrap", Keybindings.resolve t "alt+q" false)
    // the shipped alt+z for wrap is still there
    Assert.Equal(Some "wrap", Keybindings.resolve t "alt+z" false)

[<Fact>]
let ``a removal without a key takes away every chord of the command`` () =
    let entries, problems = Keybindings.parseUser """[ { "command": "-wrap" } ]""" known
    Assert.Empty problems
    let t = Keybindings.build entries
    Assert.Empty(Keybindings.chordsOf t "wrap")

[<Fact>]
let ``bad lines are skipped and each is reported`` () =
    let json =
        """[ { "key": "ctrl+9", "command": "nope" }, { "key": "p", "command": "save" }, 5, { "command": "save" },
              { "key": "ctrl+9", "command": "save", "when": "terminalFocus" }, { "key": "ctrl+8", "command": "save" } ]"""

    let entries, problems = Keybindings.parseUser json known
    Assert.Equal(1, entries.Length)
    Assert.Equal("ctrl+8", entries[0].Key)
    Assert.Equal(5, problems.Length)
    Assert.Contains("entry 1", problems[0])
    Assert.Contains("nope", problems[0])

[<Fact>]
let ``a file that is not a list or not JSON is reported and changes nothing`` () =
    let e1, p1 = Keybindings.parseUser "{}" known
    Assert.Empty e1
    Assert.Single p1 |> ignore
    let e2, p2 = Keybindings.parseUser "[ {" known
    Assert.Empty e2
    Assert.Contains("not valid JSON", p2[0])
    Assert.Equal<Binding list>(Keybindings.defaults, Keybindings.build [])

[<Fact>]
let ``binding a chord to a command takes it from the one that had it and says which`` () =
    let table, displaced = Keybindings.setChord Keybindings.defaults "wrap" "ctrl+s"
    Assert.Equal<string list>([ "save" ], displaced)
    Assert.Equal(Some "wrap", Keybindings.resolve table "ctrl+s" false)
    Assert.Equal<string list>([ "ctrl+s" ], Keybindings.chordsOf table "wrap")
    Assert.Empty(Keybindings.chordsOf table "save")

[<Fact>]
let ``what is written to the file rebuilds the same table`` () =
    let t1, _ = Keybindings.setChord Keybindings.defaults "wrap" "ctrl+s"
    let t2 = Keybindings.unbind t1 "find"
    let t3, _ = Keybindings.setChord t2 "format" "ctrl+alt+f"
    let text = Keybindings.render (Keybindings.entriesFor t3)
    let all (_: string) = true
    let entries, problems = Keybindings.parseUser text all
    Assert.Empty problems
    let rebuilt = Keybindings.build entries

    Assert.Equal<string list>(
        t3 |> List.map (fun b -> b.Command + "@" + b.Chord) |> List.sort,
        rebuilt |> List.map (fun b -> b.Command + "@" + b.Chord) |> List.sort
    )

[<Fact>]
let ``an unchanged table writes an empty list`` () =
    Assert.Equal("[]\n", Keybindings.render (Keybindings.entriesFor Keybindings.defaults))

[<Fact>]
let ``the shown shortcut of a command is its chords`` () =
    Assert.Equal("Ctrl+S", Keybindings.shownFor Keybindings.defaults "save")
    Assert.Equal("", Keybindings.shownFor Keybindings.defaults "git-blame")
