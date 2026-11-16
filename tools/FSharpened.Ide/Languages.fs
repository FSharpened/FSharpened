namespace FSharpened.Ide

type Language =
    | FSharp
    | C
    | CSharp
    | Cpp

module Language =
    let all = [ FSharp; C; CSharp; Cpp ]

    let id =
        function
        | FSharp -> "fsharp"
        | C -> "c"
        | CSharp -> "csharp"
        | Cpp -> "cpp"

    let tryOfId (s: string) =
        match s.Trim().ToLowerInvariant() with
        | "fsharp"
        | "f#"
        | "fs" -> Some FSharp
        | "c" -> Some C
        | "csharp"
        | "c#"
        | "cs" -> Some CSharp
        | "cpp"
        | "c++"
        | "cxx" -> Some Cpp
        | _ -> None

    let displayName =
        function
        | FSharp -> "F#"
        | C -> "C"
        | CSharp -> "C#"
        | Cpp -> "C++"

    let fileName =
        function
        | FSharp -> "Program.fs"
        | C -> "main.c"
        | CSharp -> "Program.cs"
        | Cpp -> "main.cpp"

    /// Name Compiler Explorer uses for the language.
    let remoteName =
        function
        | FSharp -> "fsharp"
        | C -> "c"
        | CSharp -> "csharp"
        | Cpp -> "c++"
