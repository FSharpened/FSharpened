namespace FSharpened.Ide

/// A remote toolchain the run gateway can drive.
type Compiler =
    { Id: string
      Label: string
      Language: Language
      DefaultOptions: string }

type Step =
    /// Layers 1 and 2: the selected compiler, then siblings of the same language.
    | TryCompiler of Compiler
    /// Layer 4: the last successful result for identical input, if the client has one.
    | WarmResult
    /// Layer 3: offline symbolic checks (no execution).
    | LocalChecks
    /// Layer 5: structured escalation with next actions.
    | Escalate of message: string

module Compilers =
    let all =
        [ { Id = "dotnet90fsharpcoreclr"
            Label = "F# 9 on .NET 9"
            Language = FSharp
            DefaultOptions = "" }
          { Id = "dotnet80fsharpcoreclr"
            Label = "F# 8 on .NET 8"
            Language = FSharp
            DefaultOptions = "" }
          { Id = "dotnet90csharpcoreclr"
            Label = "C# 13 on .NET 9"
            Language = CSharp
            DefaultOptions = "" }
          { Id = "dotnet80csharpcoreclr"
            Label = "C# 12 on .NET 8"
            Language = CSharp
            DefaultOptions = "" }
          { Id = "dotnet90vbcoreclr"
            Label = "VB.NET on .NET 9"
            Language = VisualBasic
            DefaultOptions = "" }
          { Id = "dotnet80vbcoreclr"
            Label = "VB.NET on .NET 8"
            Language = VisualBasic
            DefaultOptions = "" }
          { Id = "nodejs-20.17.0"
            Label = "Node.js 20"
            Language = JavaScript
            DefaultOptions = "" }
          { Id = "nodejs-18.20.4"
            Label = "Node.js 18"
            Language = JavaScript
            DefaultOptions = "" }
          { Id = "typescript-5.6.2"
            Label = "TypeScript 5.6 (tsc, then Node.js 20)"
            Language = TypeScript
            DefaultOptions = "" }
          { Id = "python314"
            Label = "Python 3.14"
            Language = Python
            DefaultOptions = "" }
          { Id = "python313"
            Label = "Python 3.13"
            Language = Python
            DefaultOptions = "" }
          { Id = "python312"
            Label = "Python 3.12"
            Language = Python
            DefaultOptions = "" }
          { Id = "r1990"
            Label = "Rust 1.99"
            Language = Rust
            DefaultOptions = "-Copt-level=2 --edition=2024" }
          { Id = "r1960"
            Label = "Rust 1.96"
            Language = Rust
            DefaultOptions = "-Copt-level=2 --edition=2024" }
          { Id = "r1900"
            Label = "Rust 1.90"
            Language = Rust
            DefaultOptions = "-Copt-level=2 --edition=2024" }
          { Id = "dart3136"
            Label = "Dart 3.13"
            Language = Dart
            DefaultOptions = "" }
          { Id = "dart3122"
            Label = "Dart 3.12"
            Language = Dart
            DefaultOptions = "" }
          { Id = "dart3116"
            Label = "Dart 3.11"
            Language = Dart
            DefaultOptions = "" }
          { Id = "cg142"
            Label = "GCC 14.2"
            Language = C
            DefaultOptions = "-O2 -Wall -Wextra -std=c17" }
          { Id = "cclang1910"
            Label = "Clang 19.1"
            Language = C
            DefaultOptions = "-O2 -Wall -Wextra -std=c17" }
          { Id = "cg132"
            Label = "GCC 13.2"
            Language = C
            DefaultOptions = "-O2 -Wall -Wextra -std=c17" }
          { Id = "g142"
            Label = "GCC 14.2"
            Language = Cpp
            DefaultOptions = "-O2 -Wall -Wextra -std=c++20" }
          { Id = "clang1910"
            Label = "Clang 19.1"
            Language = Cpp
            DefaultOptions = "-O2 -Wall -Wextra -std=c++20" }
          { Id = "g132"
            Label = "GCC 13.2"
            Language = Cpp
            DefaultOptions = "-O2 -Wall -Wextra -std=c++20" }
          { Id = "gl1260"
            Label = "Go 1.26"
            Language = Go
            DefaultOptions = "" }
          { Id = "gl1257"
            Label = "Go 1.25"
            Language = Go
            DefaultOptions = "" }
          { Id = "gl12413"
            Label = "Go 1.24"
            Language = Go
            DefaultOptions = "" }
          { Id = "java2700"
            Label = "JDK 27"
            Language = Java
            DefaultOptions = "" }
          { Id = "java2502"
            Label = "JDK 25 (LTS)"
            Language = Java
            DefaultOptions = "" }
          { Id = "java2102"
            Label = "JDK 21 (LTS)"
            Language = Java
            DefaultOptions = "" }
          { Id = "ruby405"
            Label = "Ruby 4.0"
            Language = Ruby
            DefaultOptions = "" }
          { Id = "ruby347"
            Label = "Ruby 3.4"
            Language = Ruby
            DefaultOptions = "" }
          { Id = "ruby334"
            Label = "Ruby 3.3"
            Language = Ruby
            DefaultOptions = "" }
          { Id = "lua550"
            Label = "Lua 5.5"
            Language = Lua
            DefaultOptions = "" }
          { Id = "lua547"
            Label = "Lua 5.4"
            Language = Lua
            DefaultOptions = "" }
          { Id = "lua536"
            Label = "Lua 5.3"
            Language = Lua
            DefaultOptions = "" }
          { Id = "php-8.3.12"
            Label = "PHP 8.3"
            Language = Php
            DefaultOptions = "" }
          { Id = "php-8.2.24"
            Label = "PHP 8.2"
            Language = Php
            DefaultOptions = "" }
          { Id = "r-4.4.1"
            Label = "R 4.4"
            Language = R
            DefaultOptions = "" }
          { Id = "ghc9122"
            Label = "GHC 9.12"
            Language = Haskell
            DefaultOptions = "" }
          { Id = "ghc9102"
            Label = "GHC 9.10"
            Language = Haskell
            DefaultOptions = "" }
          { Id = "ghc984"
            Label = "GHC 9.8"
            Language = Haskell
            DefaultOptions = "" }
          { Id = "ocaml5502"
            Label = "OCaml 5.5"
            Language = OCaml
            DefaultOptions = "" }
          { Id = "ocaml5401"
            Label = "OCaml 5.4"
            Language = OCaml
            DefaultOptions = "" }
          { Id = "ocaml5300"
            Label = "OCaml 5.3"
            Language = OCaml
            DefaultOptions = "" } ]

    let forLanguage (lang: Language) =
        all |> List.filter (fun c -> c.Language = lang)

    let tryFind (id: string) =
        all |> List.tryFind (fun c -> c.Id = id)

    let defaultFor (lang: Language) = (forLanguage lang).Head

module Fallback =
    /// The deterministic degradation chain (ADR-0012 applied to the IDE): never a dead end, never a silent failure.
    let chain (lang: Language) (selectedId: string) : Step list =
        let selected =
            Compilers.tryFind selectedId
            |> Option.filter (fun c -> c.Language = lang)
            |> Option.defaultValue (Compilers.defaultFor lang)

        let siblings =
            Compilers.forLanguage lang |> List.filter (fun c -> c.Id <> selected.Id)

        [ TryCompiler selected ]
        @ (siblings |> List.map TryCompiler)
        @ [ WarmResult
            LocalChecks
            Escalate
                "No execution engine is reachable. Your code was checked offline; copy it and run it locally with your own toolchain, or retry in a minute." ]
