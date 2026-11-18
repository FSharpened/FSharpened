/// Keyboard shortcuts as data: which chord runs which command, the shipped table, and the person's own
/// `keybindings.json` laid over it (the format of VS Code's file: `[{ "key": "ctrl+k", "command": "id", "when": "..." }]`,
/// a command starting with "-" takes a binding away). Pure: no keys of any UI toolkit, so it is tested without a window.
namespace FSharpened.Ide.Host

open System
open System.Text.Json

/// One shortcut. `Chord` is in the normal form ("ctrl+shift+p": modifiers in the order ctrl, shift, alt, meta, then
/// the key's name); `When` is "" (anywhere) or "editorFocus" (only while the editor has the keyboard).
type Binding =
    { Chord: string
      Command: string
      When: string }

/// One line of the person's file, as read.
type UserEntry =
    {
        Key: string
        Command: string
        When: string
        /// The command was written with a leading "-": the binding is taken away.
        Remove: bool
    }

module Keybindings =
    let private modifiers = [ "ctrl"; "shift"; "alt"; "meta" ]

    let private modifierAlias (s: string) =
        match s with
        | "ctrl"
        | "control" -> Some "ctrl"
        | "shift" -> Some "shift"
        | "alt"
        | "option" -> Some "alt"
        | "meta"
        | "cmd"
        | "command"
        | "win"
        | "windows"
        | "super" -> Some "meta"
        | _ -> None

    /// The names of keys that are not a letter, digit or function key, with the character shown for them.
    let private named =
        [ "space", "Space"
          "enter", "Enter"
          "tab", "Tab"
          "escape", "Esc"
          "backspace", "Backspace"
          "delete", "Delete"
          "insert", "Insert"
          "home", "Home"
          "end", "End"
          "pageup", "PageUp"
          "pagedown", "PageDown"
          "up", "Up"
          "down", "Down"
          "left", "Left"
          "right", "Right"
          "comma", ","
          "period", "."
          "minus", "-"
          "equal", "="
          "backquote", "`"
          "semicolon", ";"
          "slash", "/"
          "backslash", "\\"
          "bracketleft", "["
          "bracketright", "]"
          "quote", "'" ]

    /// Spellings other than the normal name.
    let private keyAlias (s: string) =
        match s with
        | "esc" -> "escape"
        | "return" -> "enter"
        | "del" -> "delete"
        | "ins" -> "insert"
        | "pgup" -> "pageup"
        | "pgdn"
        | "pgdown" -> "pagedown"
        | "arrowup" -> "up"
        | "arrowdown" -> "down"
        | "arrowleft" -> "left"
        | "arrowright" -> "right"
        | "," -> "comma"
        | "." -> "period"
        | "-" -> "minus"
        | "=" -> "equal"
        | "+"
        | "plus" -> "equal"
        | "`" -> "backquote"
        | ";" -> "semicolon"
        | "/" -> "slash"
        | "\\" -> "backslash"
        | "[" -> "bracketleft"
        | "]" -> "bracketright"
        | "'" -> "quote"
        | other -> other

    let private isFunctionKey (k: string) =
        k.Length >= 2
        && k[0] = 'f'
        && (match Int32.TryParse(k.Substring 1) with
            | true, n -> n >= 1 && n <= 24
            | _ -> false)

    let private isKnownKey (k: string) =
        (k.Length = 1 && ((k[0] >= 'a' && k[0] <= 'z') || (k[0] >= '0' && k[0] <= '9')))
        || isFunctionKey k
        || named |> List.exists (fun (n, _) -> n = k)

    /// All the key names a chord can end in (the normal spellings), for the window's own key table.
    let keyNames: string list =
        [ for c in 'a' .. 'z' -> string c ]
        @ [ for c in '0' .. '9' -> string c ]
        @ [ for i in 1..24 -> "f" + string i ]
        @ (named |> List.map fst)

    /// A chord as written ("Ctrl+Shift+P", "cmd + ,") in the normal form, or why it cannot be a shortcut. A chord of
    /// several steps ("ctrl+k ctrl+s") is refused; so is one that would take over typing (a plain letter, Shift and
    /// a letter, Enter or an arrow key with nothing held down).
    let normalize (text: string) : Result<string, string> =
        let raw = (if isNull text then "" else text).Trim().ToLowerInvariant()

        if raw = "" then
            Error "the shortcut is empty"
        elif raw.Contains ' ' && not (raw.Contains '+') then
            Error "a shortcut of several steps (like Ctrl+K Ctrl+S) is not supported"
        else
            // "ctrl + k", and a "+" that is the key itself ("ctrl++")
            let compact = raw.Replace(" + ", "+").Replace("+ ", "+").Replace(" +", "+")

            if compact.Contains ' ' then
                Error "a shortcut of several steps (like Ctrl+K Ctrl+S) is not supported"
            else
                let parts =
                    if compact.EndsWith "++" then
                        (compact.Substring(0, compact.Length - 2).Split('+') |> Array.toList) @ [ "+" ]
                    else
                        compact.Split('+') |> Array.toList

                let mods = parts |> List.take (parts.Length - 1)
                let key = keyAlias (List.last parts)

                let modNames = mods |> List.map modifierAlias

                if modNames |> List.exists Option.isNone then
                    Error(
                        sprintf
                            "“%s” is not a modifier (use ctrl, shift, alt or meta)"
                            (mods |> List.find (fun m -> (modifierAlias m).IsNone))
                    )
                elif not (isKnownKey key) then
                    Error(sprintf "“%s” is not a key the editor knows" (List.last parts))
                else
                    let held = modNames |> List.choose id |> Set.ofList

                    if held.Count <> modNames.Length then
                        Error "a modifier is written twice"
                    elif
                        not (
                            held.Contains "ctrl"
                            || held.Contains "alt"
                            || held.Contains "meta"
                            || isFunctionKey key
                        )
                    then
                        Error
                            "a shortcut needs Ctrl, Alt or Meta held down (or be a function key), or it would take over typing"
                    else
                        let ordered = modifiers |> List.filter held.Contains
                        Ok(String.Join("+", ordered @ [ key ]))

    /// A chord in the form people read: "Ctrl+Shift+P", "Ctrl+,".
    let display (chord: string) : string =
        chord.Split('+')
        |> Array.map (fun p ->
            match p with
            | "ctrl" -> "Ctrl"
            | "shift" -> "Shift"
            | "alt" -> "Alt"
            | "meta" -> "Meta"
            | k when isFunctionKey k -> k.ToUpperInvariant()
            | k when k.Length = 1 -> k.ToUpperInvariant()
            | k ->
                match named |> List.tryFind (fun (n, _) -> n = k) with
                | Some(_, shown) -> shown
                | None -> k)
        |> String.concat "+"

    // ---- the shipped table ---------------------------------------------------------------------------

    let private b (chord: string) (command: string) : Binding =
        { Chord = chord
          Command = command
          When = "" }

    /// The shortcuts the window ships with.
    let defaults: Binding list =
        [ b "ctrl+shift+p" "palette"
          b "ctrl+p" "quick-open"
          b "ctrl+shift+g" "git-view"
          b "ctrl+g" "goto-line"
          b "ctrl+shift+o" "goto-symbol"
          b "ctrl+o" "open-folder"
          b "ctrl+shift+s" "save-all"
          b "ctrl+s" "save"
          b "ctrl+n" "new-file"
          b "ctrl+w" "close-tab"
          b "ctrl+pagedown" "next-tab"
          b "ctrl+pageup" "previous-tab"
          b "ctrl+1" "focus-editor"
          b "ctrl+0" "focus-explorer"
          b "ctrl+b" "toggle-sidebar"
          b "ctrl+j" "toggle-panel"
          b "ctrl+shift+e" "explorer"
          b "ctrl+shift+f" "find-files"
          b "ctrl+f" "find"
          b "ctrl+backquote" "terminal"
          b "ctrl+shift+h" "replace-in-files"
          b "ctrl+h" "replace-in-file"
          b "ctrl+shift+m" "problems"
          b "ctrl+shift+space" "signature-help"
          b "ctrl+space" "complete"
          b "ctrl+period" "quick-fix"
          b "shift+f12" "references"
          b "f12" "definition"
          b "ctrl+f5" "run"
          b "shift+f5" "debug-stop"
          b "f5" "debug-start"
          b "f9" "toggle-breakpoint"
          b "f10" "debug-step-over"
          b "shift+f11" "debug-step-out"
          b "f11" "debug-step-into"
          { Chord = "f2"
            Command = "rename-symbol"
            When = "editorFocus" }
          b "shift+alt+f" "format"
          b "alt+z" "wrap"
          b "ctrl+equal" "zoom-in"
          b "ctrl+minus" "zoom-out"
          b "ctrl+alt+down" "cursor-below"
          b "ctrl+alt+up" "cursor-above"
          b "ctrl+d" "select-next-match"
          b "ctrl+shift+l" "select-all-matches"
          b "ctrl+alt+k" "keybindings" ]

    /// The command a chord runs now: the last binding that matches (the person's come after the shipped ones), the
    /// ones for the editor only while it has the keyboard.
    let resolve (table: Binding list) (chord: string) (editorFocused: bool) : string option =
        table
        |> List.rev
        |> List.tryFind (fun x -> x.Chord = chord && (x.When = "" || (x.When = "editorFocus" && editorFocused)))
        |> Option.map (fun x -> x.Command)

    /// The chords that run a command, in the order they were added.
    let chordsOf (table: Binding list) (command: string) : string list =
        table
        |> List.filter (fun x -> x.Command = command)
        |> List.map (fun x -> x.Chord)

    /// What to show next to a command: its chords, "" when it has none.
    let shownFor (table: Binding list) (command: string) : string =
        chordsOf table command |> List.map display |> String.concat ", "

    // ---- the person's file ------------------------------------------------------------------------

    let maxEntries = 2000

    /// Reads the person's file. `isCommand` says which command ids exist; a line that names a command that does not,
    /// has a shortcut that cannot be used, or is not an object is skipped and reported (with its number).
    let parseUser (json: string) (isCommand: string -> bool) : UserEntry list * string list =
        let options =
            JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)

        try
            use doc = JsonDocument.Parse(json, options)

            if doc.RootElement.ValueKind <> JsonValueKind.Array then
                [], [ "keybindings.json must hold a list: [ { \"key\": \"ctrl+k\", \"command\": \"save\" } ]" ]
            else
                let entries = ResizeArray<UserEntry>()
                let problems = ResizeArray<string>()

                doc.RootElement.EnumerateArray()
                |> Seq.truncate maxEntries
                |> Seq.iteri (fun i el ->
                    let at = sprintf "entry %d: " (i + 1)

                    let str (name: string) =
                        match el.ValueKind with
                        | JsonValueKind.Object ->
                            match el.TryGetProperty name with
                            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                            | _ -> ""
                        | _ -> ""

                    if el.ValueKind <> JsonValueKind.Object then
                        problems.Add(at + "not an object")
                    else
                        let cmd = str "command"
                        let remove = cmd.StartsWith "-"
                        let id = if remove then cmd.Substring 1 else cmd
                        let key = str "key"
                        let whenText = (str "when").Trim()

                        if id = "" then
                            problems.Add(at + "no command")
                        elif not (isCommand id) then
                            problems.Add(sprintf "%sno command named “%s”" at id)
                        elif whenText <> "" && whenText <> "editorFocus" then
                            problems.Add(sprintf "%s“when” can only be editorFocus here, not “%s”" at whenText)
                        elif key = "" && not remove then
                            problems.Add(at + "no key")
                        elif key = "" then
                            entries.Add
                                { Key = ""
                                  Command = id
                                  When = whenText
                                  Remove = true }
                        else
                            match normalize key with
                            | Ok chord ->
                                entries.Add
                                    { Key = chord
                                      Command = id
                                      When = whenText
                                      Remove = remove }
                            | Error why -> problems.Add(sprintf "%s%s" at why))

                Seq.toList entries, Seq.toList problems
        with :? JsonException as e ->
            [], [ "keybindings.json is not valid JSON: " + e.Message ]

    /// The shipped table with the person's entries laid over it, in order.
    let build (user: UserEntry list) : Binding list =
        let step (table: Binding list) (e: UserEntry) =
            if e.Remove then
                table
                |> List.filter (fun x -> not (x.Command = e.Command && (e.Key = "" || x.Chord = e.Key)))
            else
                // a chord that was another command's goes to this one (for the same place: "" covers every place)
                let kept =
                    table
                    |> List.filter (fun x -> not (x.Chord = e.Key && (x.When = e.When || e.When = "")))

                kept
                @ [ { Chord = e.Key
                      Command = e.Command
                      When = e.When } ]

        user |> List.fold step defaults

    /// Takes every chord of a command away.
    let unbind (table: Binding list) (command: string) : Binding list =
        table |> List.filter (fun x -> x.Command <> command)

    /// Gives a command exactly one chord (its others are taken away). Returns the table and the commands that
    /// lost this chord to it.
    let setChord (table: Binding list) (command: string) (chord: string) : Binding list * string list =
        let without = unbind table command

        let displaced =
            without
            |> List.filter (fun x -> x.Chord = chord)
            |> List.map (fun x -> x.Command)
            |> List.distinct

        let whenNow =
            match table |> List.tryFind (fun x -> x.Command = command) with
            | Some x -> x.When
            | None -> ""

        let kept =
            without
            |> List.filter (fun x -> not (x.Chord = chord && (x.When = whenNow || whenNow = "")))

        kept
        @ [ { Chord = chord
              Command = command
              When = whenNow } ],
        displaced

    /// The lines that make `table` out of the shipped one: a removal for each shipped binding that is gone, an
    /// addition for each binding that is new. This is what is written to the person's file.
    let entriesFor (table: Binding list) : UserEntry list =
        let removed =
            defaults
            |> List.filter (fun d -> not (List.contains d table))
            |> List.map (fun d ->
                { Key = d.Chord
                  Command = d.Command
                  When = d.When
                  Remove = true })

        let added =
            table
            |> List.filter (fun x -> not (List.contains x defaults))
            |> List.map (fun x ->
                { Key = x.Chord
                  Command = x.Command
                  When = x.When
                  Remove = false })

        removed @ added

    /// The file's text for some entries.
    let render (entries: UserEntry list) : string =
        let q (s: string) = JsonSerializer.Serialize s

        let line (e: UserEntry) =
            let whenPart =
                if e.When = "" then
                    ""
                else
                    sprintf ", \"when\": %s" (q e.When)

            sprintf
                "  { \"key\": %s, \"command\": %s%s }"
                (q e.Key)
                (q ((if e.Remove then "-" else "") + e.Command))
                whenPart

        if entries.IsEmpty then
            "[]\n"
        else
            "[\n" + String.Join(",\n", entries |> List.map line) + "\n]\n"
