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
          { Id = "dotnet90csharpcoreclr"
            Label = "C# 13 on .NET 9"
            Language = CSharp
            DefaultOptions = "" }
          { Id = "dotnet80csharpcoreclr"
            Label = "C# 12 on .NET 8"
            Language = CSharp
            DefaultOptions = "" }
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
            DefaultOptions = "-O2 -Wall -Wextra -std=c++20" } ]

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
                "No execution engine is reachable. Your code was checked offline; copy it and run it locally with `dotnet fsi`/`gcc`, or retry in a minute." ]
