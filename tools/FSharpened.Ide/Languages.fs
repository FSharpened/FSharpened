namespace FSharpened.Ide

type Language =
    | FSharp
    | CSharp
    | VisualBasic
    | JavaScript
    | TypeScript
    | Python
    | Rust
    | Dart
    | C
    | Cpp
    | Go
    | Java
    | Ruby
    | Lua
    | Php
    | R
    | Haskell
    | OCaml

/// Everything the IDE needs to know about a language that is not behaviour: the tab, the file name and the group.
type LanguageInfo =
    {
        Lang: Language
        /// Stable id used in storage, share links and the gateway protocol.
        Id: string
        /// Other spellings accepted by `Language.tryOfId` (lower case).
        Aliases: string list
        Name: string
        /// The file name shown on the tab and used for diagnostic locations.
        File: string
        /// Tabs are grouped (a divider between groups); see ADR-0020.
        Group: string
        /// Only these languages take compiler flags. The gateway enforces the same rule.
        TakesFlags: bool
    }

module Language =
    let private info lang id aliases name file group flags =
        { Lang = lang
          Id = id
          Aliases = aliases
          Name = name
          File = file
          Group = group
          TakesFlags = flags }

    /// In tab order.
    let infos =
        [ info FSharp "fsharp" [ "f#"; "fs" ] "F#" "Program.fs" "dotnet" false
          info CSharp "csharp" [ "c#"; "cs" ] "C#" "Program.cs" "dotnet" false
          info VisualBasic "vb" [ "vb.net"; "vbnet"; "visualbasic" ] "VB.NET" "Program.vb" "dotnet" false
          info JavaScript "javascript" [ "js"; "node"; "nodejs" ] "JavaScript" "main.js" "fable" false
          info TypeScript "typescript" [ "ts" ] "TypeScript" "main.ts" "fable" false
          info Python "python" [ "py"; "python3" ] "Python" "main.py" "fable" false
          info Rust "rust" [ "rs" ] "Rust" "main.rs" "fable" true
          info Dart "dart" [] "Dart" "main.dart" "fable" false
          info C "c" [] "C" "main.c" "systems" true
          info Cpp "cpp" [ "c++"; "cxx" ] "C++" "main.cpp" "systems" true
          info Go "go" [ "golang" ] "Go" "main.go" "systems" false
          info Java "java" [] "Java" "Main.java" "systems" false
          info Ruby "ruby" [ "rb" ] "Ruby" "main.rb" "scripting" false
          info Lua "lua" [] "Lua" "main.lua" "scripting" false
          info Php "php" [] "PHP" "main.php" "scripting" false
          info R "r" [] "R" "main.R" "scripting" false
          info Haskell "haskell" [ "hs" ] "Haskell" "Main.hs" "functional" false
          info OCaml "ocaml" [ "ml" ] "OCaml" "main.ml" "functional" false ]

    let all = infos |> List.map (fun i -> i.Lang)

    let infoOf (lang: Language) =
        infos |> List.find (fun i -> i.Lang = lang)

    let id (lang: Language) = (infoOf lang).Id

    let tryOfId (s: string) =
        let key = s.Trim().ToLowerInvariant()

        infos
        |> List.tryFind (fun i -> i.Id = key || List.contains key i.Aliases)
        |> Option.map (fun i -> i.Lang)

    let displayName (lang: Language) = (infoOf lang).Name
    let fileName (lang: Language) = (infoOf lang).File
    let group (lang: Language) = (infoOf lang).Group
    let takesFlags (lang: Language) = (infoOf lang).TakesFlags
