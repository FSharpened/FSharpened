namespace FSharpened.Ide

type Template =
    { Key: string
      Title: string
      Language: Language
      Code: string }

module Templates =
    let private fsHello =
        "// Sharpen your existing F#.\n// Press Ctrl+Enter to compile and run on .NET 9.\n\nlet greet name = \"Hello, \" + name + \"!\"\n\n[<EntryPoint>]\nlet main _ =\n    [ \"F#\"; \"FSharpened\" ] |> List.iter (greet >> printfn \"%s\")\n    0\n"

    let private fsPipelines =
        "type Shape =\n    | Circle of radius: float\n    | Rect of w: float * h: float\n\nlet area =\n    function\n    | Circle r -> System.Math.PI * r * r\n    | Rect (w, h) -> w * h\n\n[<EntryPoint>]\nlet main _ =\n    [ Circle 1.5; Rect (2.0, 3.0); Circle 0.5 ]\n    |> List.map area\n    |> List.sortDescending\n    |> List.iteri (fun i a -> printfn \"%d: %.3f\" (i + 1) a)\n    0\n"

    let private cHello =
        "#include <stdio.h>\n\nint main(void) {\n    for (int i = 1; i <= 5; i++)\n        printf(\"%d squared is %d\\n\", i, i * i);\n    return 0;\n}\n"

    let private csHello =
        "using System;\nusing System.Linq;\n\nvar words = new[] { \"sharpen\", \"your\", \"F#\" };\nConsole.WriteLine(string.Join(\" \", words.Select(w => w.ToUpperInvariant())));\n"

    let private cppHello =
        "#include <algorithm>\n#include <iostream>\n#include <vector>\n\nint main() {\n    std::vector<int> v{5, 3, 8, 1, 9, 2};\n    std::sort(v.begin(), v.end());\n    for (int x : v) std::cout << x << ' ';\n    std::cout << '\\n';\n}\n"

    let all =
        [ { Key = "fsharp-hello"
            Title = "Hello, F#"
            Language = FSharp
            Code = fsHello }
          { Key = "fsharp-shapes"
            Title = "Unions and pipelines"
            Language = FSharp
            Code = fsPipelines }
          { Key = "c-hello"
            Title = "Hello, C"
            Language = C
            Code = cHello }
          { Key = "csharp-hello"
            Title = "Hello, C# (top-level)"
            Language = CSharp
            Code = csHello }
          { Key = "cpp-hello"
            Title = "Hello, C++"
            Language = Cpp
            Code = cppHello } ]

    let defaultFor (lang: Language) =
        all |> List.find (fun t -> t.Language = lang)

    let tryFind (key: string) =
        all |> List.tryFind (fun t -> t.Key = key)
