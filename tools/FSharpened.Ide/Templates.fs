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

    let private rustHello =
        "// Rust on rustc. Press Ctrl+Enter to compile and run.\n\nfn greet(name: &str) -> String {\n    format!(\"Hello, {name}!\")\n}\n\nfn main() {\n    for name in [\"Rust\", \"FSharpened\"] {\n        println!(\"{}\", greet(name));\n    }\n}\n"

    let private rustShapes =
        "use std::f64::consts::PI;\n\nenum Shape {\n    Circle { radius: f64 },\n    Rect { w: f64, h: f64 },\n}\n\nfn area(shape: &Shape) -> f64 {\n    match shape {\n        Shape::Circle { radius } => PI * radius * radius,\n        Shape::Rect { w, h } => w * h,\n    }\n}\n\nfn main() {\n    let shapes = [Shape::Circle { radius: 1.5 }, Shape::Rect { w: 2.0, h: 3.0 }, Shape::Circle { radius: 0.5 }];\n    let mut areas: Vec<f64> = shapes.iter().map(area).collect();\n    areas.sort_by(|a, b| b.total_cmp(a));\n    for (i, a) in areas.iter().enumerate() {\n        println!(\"{}: {:.3}\", i + 1, a);\n    }\n}\n"

    let private rustFfi =
        "// The shape F# binds to: a C ABI function over plain data.\n// In a real crate this lives in a `cdylib`; F# calls it with [<DllImport>].\n// Rules: extern \"C\", no mangling, no panics across the boundary, plain integers, pointers and lengths.\n\n#[unsafe(no_mangle)]\npub extern \"C\" fn fsh_add(a: i32, b: i32) -> i32 {\n    a.wrapping_add(b)\n}\n\n#[unsafe(no_mangle)]\npub extern \"C\" fn fsh_sum(values: *const i32, len: usize) -> i64 {\n    if values.is_null() {\n        return 0;\n    }\n    // SAFETY: the caller promises `values` points to `len` readable i32 values.\n    let slice = unsafe { std::slice::from_raw_parts(values, len) };\n    slice.iter().map(|&v| i64::from(v)).sum()\n}\n\nfn main() {\n    let data = [1, 2, 3, 4];\n    println!(\"fsh_add(2, 40) = {}\", fsh_add(2, 40));\n    println!(\"fsh_sum([1, 2, 3, 4]) = {}\", fsh_sum(data.as_ptr(), data.len()));\n}\n"

    let private vbHello =
        "' VB.NET on .NET 9. Press Ctrl+Enter to compile and run.\nImports System\nImports System.Linq\n\nModule Program\n    Sub Main()\n        Dim words = {\"sharpen\", \"your\", \"F#\"}\n        Console.WriteLine(String.Join(\" \", words.Select(Function(w) w.ToUpperInvariant())))\n    End Sub\nEnd Module\n"

    let private vbShapes =
        "Imports System\nImports System.Linq\n\nModule Program\n    Interface IShape\n        Function Area() As Double\n    End Interface\n\n    Class Circle\n        Implements IShape\n        Private ReadOnly radius As Double\n        Sub New(r As Double)\n            radius = r\n        End Sub\n        Function Area() As Double Implements IShape.Area\n            Return Math.PI * radius * radius\n        End Function\n    End Class\n\n    Class Rect\n        Implements IShape\n        Private ReadOnly w, h As Double\n        Sub New(width As Double, height As Double)\n            w = width\n            h = height\n        End Sub\n        Function Area() As Double Implements IShape.Area\n            Return w * h\n        End Function\n    End Class\n\n    Sub Main()\n        Dim shapes As IShape() = {New Circle(1.5), New Rect(2, 3), New Circle(0.5)}\n        Dim areas = shapes.Select(Function(s) s.Area()).OrderByDescending(Function(a) a).ToList()\n        For i = 0 To areas.Count - 1\n            Console.WriteLine(\"{0}: {1:F3}\", i + 1, areas(i))\n        Next\n    End Sub\nEnd Module\n"

    let private jsHello =
        "// JavaScript on Node.js. Press Ctrl+Enter to run.\nconst greet = (name) => `Hello, ${name}!`;\n\nfor (const who of [\"JavaScript\", \"FSharpened\"]) {\n  console.log(greet(who));\n}\n"

    let private jsPipeline =
        "// The pipeline F# writes with |>, in plain JavaScript.\nconst orders = [\n  { id: 1, total: 42.5, paid: true },\n  { id: 2, total: 19.0, paid: false },\n  { id: 3, total: 88.25, paid: true },\n];\n\nconst revenue = orders\n  .filter((o) => o.paid)\n  .map((o) => o.total)\n  .reduce((sum, t) => sum + t, 0);\n\nconsole.log(`Paid revenue: ${revenue.toFixed(2)}`);\n"

    let private tsHello =
        "// TypeScript: type-checked by tsc 5.6, then run on Node.js 20.\ninterface Shape {\n  area(): number;\n}\n\nclass Circle implements Shape {\n  constructor(private readonly radius: number) {}\n  area(): number {\n    return Math.PI * this.radius ** 2;\n  }\n}\n\nclass Rect implements Shape {\n  constructor(private readonly w: number, private readonly h: number) {}\n  area(): number {\n    return this.w * this.h;\n  }\n}\n\nconst shapes: Shape[] = [new Circle(1.5), new Rect(2, 3), new Circle(0.5)];\nshapes\n  .map((s) => s.area())\n  .sort((a, b) => b - a)\n  .forEach((a, i) => console.log(`${i + 1}: ${a.toFixed(3)}`));\n"

    let private tsUnion =
        "// A discriminated union: the TypeScript spelling of an F# union.\ntype Shape =\n  | { kind: \"circle\"; radius: number }\n  | { kind: \"rect\"; w: number; h: number };\n\nfunction area(s: Shape): number {\n  switch (s.kind) {\n    case \"circle\":\n      return Math.PI * s.radius ** 2;\n    case \"rect\":\n      return s.w * s.h;\n  }\n}\n\nconst shapes: Shape[] = [\n  { kind: \"circle\", radius: 1.5 },\n  { kind: \"rect\", w: 2, h: 3 },\n];\nconsole.log(shapes.map(area).map((a) => a.toFixed(3)).join(\", \"));\n"

    let private pyHello =
        "# Python 3. Press Ctrl+Enter to run.\ndef greet(name: str) -> str:\n    return f\"Hello, {name}!\"\n\n\nfor who in [\"Python\", \"FSharpened\"]:\n    print(greet(who))\n"

    let private pyDataclass =
        "# Dataclasses and structural pattern matching: Python's cousins of F# records and unions.\nfrom dataclasses import dataclass\nfrom math import pi\n\n\n@dataclass(frozen=True)\nclass Circle:\n    radius: float\n\n\n@dataclass(frozen=True)\nclass Rect:\n    w: float\n    h: float\n\n\ndef area(shape) -> float:\n    match shape:\n        case Circle(radius=r):\n            return pi * r * r\n        case Rect(w=w, h=h):\n            return w * h\n    raise TypeError(f\"unknown shape: {shape!r}\")\n\n\nshapes = [Circle(1.5), Rect(2.0, 3.0), Circle(0.5)]\nfor i, a in enumerate(sorted((area(s) for s in shapes), reverse=True), start=1):\n    print(f\"{i}: {a:.3f}\")\n"

    let private dartHello =
        "// Dart 3. Press Ctrl+Enter to compile and run.\nString greet(String name) => 'Hello, $name!';\n\nvoid main() {\n  for (final who in ['Dart', 'FSharpened']) {\n    print(greet(who));\n  }\n}\n"

    let private dartSealed =
        "// Sealed classes and exhaustive switch: Dart's version of an F# union.\nimport 'dart:math';\n\nsealed class Shape {}\n\nclass Circle extends Shape {\n  final double radius;\n  Circle(this.radius);\n}\n\nclass Rect extends Shape {\n  final double w, h;\n  Rect(this.w, this.h);\n}\n\ndouble area(Shape s) => switch (s) {\n      Circle(:final radius) => pi * radius * radius,\n      Rect(:final w, :final h) => w * h,\n    };\n\nvoid main() {\n  final areas = [Circle(1.5), Rect(2, 3), Circle(0.5)].map(area).toList()\n    ..sort((a, b) => b.compareTo(a));\n  for (var i = 0; i < areas.length; i++) {\n    print('${i + 1}: ${areas[i].toStringAsFixed(3)}');\n  }\n}\n"

    let private goHello =
        "// Go. Press Ctrl+Enter to build and run.\npackage main\n\nimport \"fmt\"\n\nfunc greet(name string) string {\n\treturn \"Hello, \" + name + \"!\"\n}\n\nfunc main() {\n\tfor _, who := range []string{\"Go\", \"FSharpened\"} {\n\t\tfmt.Println(greet(who))\n\t}\n}\n"

    let private goGoroutines =
        "package main\n\nimport (\n\t\"fmt\"\n\t\"sort\"\n\t\"sync\"\n)\n\nfunc square(n int) int { return n * n }\n\nfunc main() {\n\tvar wg sync.WaitGroup\n\tresults := make(chan int, 5)\n\tfor n := 1; n <= 5; n++ {\n\t\twg.Add(1)\n\t\tgo func(n int) {\n\t\t\tdefer wg.Done()\n\t\t\tresults <- square(n)\n\t\t}(n)\n\t}\n\twg.Wait()\n\tclose(results)\n\n\tvar squares []int\n\tfor r := range results {\n\t\tsquares = append(squares, r)\n\t}\n\tsort.Ints(squares)\n\tfmt.Println(squares)\n}\n"

    let private javaHello =
        "// Java on the JDK. Press Ctrl+Enter to compile and run.\nimport java.util.List;\n\nclass Main {\n    static String greet(String name) {\n        return \"Hello, \" + name + \"!\";\n    }\n\n    public static void main(String[] args) {\n        for (String who : List.of(\"Java\", \"FSharpened\")) {\n            System.out.println(greet(who));\n        }\n    }\n}\n"

    let private javaRecords =
        "// Records, sealed interfaces and pattern-matching switch: Java's take on F# records and unions.\nimport java.util.ArrayList;\nimport java.util.Comparator;\nimport java.util.List;\n\nsealed interface Shape permits Circle, Rect {}\n\nrecord Circle(double radius) implements Shape {}\n\nrecord Rect(double w, double h) implements Shape {}\n\nclass Main {\n    static double area(Shape s) {\n        return switch (s) {\n            case Circle c -> Math.PI * c.radius() * c.radius();\n            case Rect r -> r.w() * r.h();\n        };\n    }\n\n    public static void main(String[] args) {\n        List<Shape> shapes = List.of(new Circle(1.5), new Rect(2, 3), new Circle(0.5));\n        List<Double> areas = new ArrayList<>();\n        for (Shape s : shapes) areas.add(area(s));\n        areas.sort(Comparator.reverseOrder());\n        for (int i = 0; i < areas.size(); i++) {\n            System.out.printf(\"%d: %.3f%n\", i + 1, areas.get(i));\n        }\n    }\n}\n"

    let private rbHello =
        "# Ruby. Press Ctrl+Enter to run.\ndef greet(name) = \"Hello, #{name}!\"\n\n%w[Ruby FSharpened].each { |who| puts greet(who) }\n"

    let private rbPipeline =
        "Order = Struct.new(:id, :total, :paid)\n\norders = [Order.new(1, 42.5, true), Order.new(2, 19.0, false), Order.new(3, 88.25, true)]\nrevenue = orders.select(&:paid).sum(&:total)\nputs format(\"Paid revenue: %.2f\", revenue)\n"

    let private luaHello =
        "-- Lua. Press Ctrl+Enter to run.\nlocal function greet(name)\n  return \"Hello, \" .. name .. \"!\"\nend\n\nfor _, who in ipairs({ \"Lua\", \"FSharpened\" }) do\n  print(greet(who))\nend\n"

    let private luaClosures =
        "local function counter()\n  local n = 0\n  return function()\n    n = n + 1\n    return n\n  end\nend\n\nlocal nextId = counter()\nlocal names = { \"pear\", \"apple\", \"fig\" }\ntable.sort(names)\nfor _, name in ipairs(names) do\n  print(nextId(), name)\nend\n"

    let private phpHello =
        "<?php\n// PHP 8.3. Press Ctrl+Enter to run.\nfunction greet(string $name): string\n{\n    return \"Hello, $name!\";\n}\n\nforeach ([\"PHP\", \"FSharpened\"] as $who) {\n    echo greet($who), \"\\n\";\n}\n"

    let private phpEnum =
        "<?php\nenum Suit: string\n{\n    case Hearts = 'H';\n    case Spades = 'S';\n\n    public function label(): string\n    {\n        return match ($this) {\n            Suit::Hearts => 'Hearts',\n            Suit::Spades => 'Spades',\n        };\n    }\n}\n\nforeach (Suit::cases() as $suit) {\n    echo $suit->value, ' => ', $suit->label(), \"\\n\";\n}\n"

    let private rHello =
        "# R 4.4. Press Ctrl+Enter to run.\nx <- c(4.2, 5.1, 3.8, 6.0, 5.5)\ncat(\"mean:\", mean(x), \"\\n\")\ncat(\"sd:  \", round(sd(x), 3), \"\\n\")\nprint(summary(x))\n"

    let private rModel =
        "# Fit a line to the built-in mtcars data and print the coefficients.\nfit <- lm(mpg ~ wt, data = mtcars)\nprint(round(coef(fit), 3))\ncat(\"R squared:\", round(summary(fit)$r.squared, 3), \"\\n\")\n"

    let private hsHello =
        "-- Haskell on GHC. Press Ctrl+Enter to compile and run.\ngreet :: String -> String\ngreet name = \"Hello, \" ++ name ++ \"!\"\n\nmain :: IO ()\nmain = mapM_ (putStrLn . greet) [\"Haskell\", \"FSharpened\"]\n"

    let private hsAdt =
        "import Data.List (sortBy)\nimport Data.Ord (Down (..), comparing)\nimport Text.Printf (printf)\n\ndata Shape = Circle Double | Rect Double Double\n\narea :: Shape -> Double\narea (Circle r) = pi * r * r\narea (Rect w h) = w * h\n\nmain :: IO ()\nmain = do\n  let areas = sortBy (comparing Down) (map area [Circle 1.5, Rect 2 3, Circle 0.5])\n  mapM_ (\\(i, a) -> printf \"%d: %.3f\\n\" (i :: Int) a) (zip [1 ..] areas)\n"

    let private mlHello =
        "(* OCaml. Press Ctrl+Enter to compile and run. *)\nlet greet name = \"Hello, \" ^ name ^ \"!\"\n\nlet () = List.iter (fun who -> print_endline (greet who)) [ \"OCaml\"; \"FSharpened\" ]\n"

    let private mlVariants =
        "type shape = Circle of float | Rect of float * float\n\nlet area = function\n  | Circle r -> Float.pi *. r *. r\n  | Rect (w, h) -> w *. h\n\nlet () =\n  [ Circle 1.5; Rect (2.0, 3.0); Circle 0.5 ]\n  |> List.map area\n  |> List.sort (fun a b -> compare b a)\n  |> List.iteri (fun i a -> Printf.printf \"%d: %.3f\\n\" (i + 1) a)\n"

    let all =
        [ { Key = "fsharp-hello"
            Title = "Hello, F#"
            Language = FSharp
            Code = fsHello }
          { Key = "fsharp-shapes"
            Title = "Unions and pipelines"
            Language = FSharp
            Code = fsPipelines }
          { Key = "csharp-hello"
            Title = "Hello, C# (top-level)"
            Language = CSharp
            Code = csHello }
          { Key = "vb-hello"
            Title = "Hello, VB.NET (LINQ)"
            Language = VisualBasic
            Code = vbHello }
          { Key = "vb-shapes"
            Title = "Interfaces and classes"
            Language = VisualBasic
            Code = vbShapes }
          { Key = "js-hello"
            Title = "Hello, JavaScript"
            Language = JavaScript
            Code = jsHello }
          { Key = "js-pipeline"
            Title = "Array pipelines"
            Language = JavaScript
            Code = jsPipeline }
          { Key = "ts-hello"
            Title = "Interfaces and classes"
            Language = TypeScript
            Code = tsHello }
          { Key = "ts-union"
            Title = "Discriminated unions"
            Language = TypeScript
            Code = tsUnion }
          { Key = "py-hello"
            Title = "Hello, Python"
            Language = Python
            Code = pyHello }
          { Key = "py-dataclass"
            Title = "Dataclasses and match"
            Language = Python
            Code = pyDataclass }
          { Key = "rust-hello"
            Title = "Hello, Rust"
            Language = Rust
            Code = rustHello }
          { Key = "rust-shapes"
            Title = "Enums and iterators"
            Language = Rust
            Code = rustShapes }
          { Key = "rust-ffi"
            Title = "C ABI export (what F# binds to)"
            Language = Rust
            Code = rustFfi }
          { Key = "dart-hello"
            Title = "Hello, Dart"
            Language = Dart
            Code = dartHello }
          { Key = "dart-sealed"
            Title = "Sealed classes and patterns"
            Language = Dart
            Code = dartSealed }
          { Key = "c-hello"
            Title = "Hello, C"
            Language = C
            Code = cHello }
          { Key = "cpp-hello"
            Title = "Hello, C++"
            Language = Cpp
            Code = cppHello }
          { Key = "go-hello"
            Title = "Hello, Go"
            Language = Go
            Code = goHello }
          { Key = "go-goroutines"
            Title = "Goroutines and channels"
            Language = Go
            Code = goGoroutines }
          { Key = "java-hello"
            Title = "Hello, Java"
            Language = Java
            Code = javaHello }
          { Key = "java-records"
            Title = "Records and sealed types"
            Language = Java
            Code = javaRecords }
          { Key = "rb-hello"
            Title = "Hello, Ruby"
            Language = Ruby
            Code = rbHello }
          { Key = "rb-pipeline"
            Title = "Structs and blocks"
            Language = Ruby
            Code = rbPipeline }
          { Key = "lua-hello"
            Title = "Hello, Lua"
            Language = Lua
            Code = luaHello }
          { Key = "lua-closures"
            Title = "Closures and tables"
            Language = Lua
            Code = luaClosures }
          { Key = "php-hello"
            Title = "Hello, PHP"
            Language = Php
            Code = phpHello }
          { Key = "php-enum"
            Title = "Enums and match"
            Language = Php
            Code = phpEnum }
          { Key = "r-hello"
            Title = "Summary statistics"
            Language = R
            Code = rHello }
          { Key = "r-model"
            Title = "A linear model"
            Language = R
            Code = rModel }
          { Key = "hs-hello"
            Title = "Hello, Haskell"
            Language = Haskell
            Code = hsHello }
          { Key = "hs-adt"
            Title = "Algebraic data types"
            Language = Haskell
            Code = hsAdt }
          { Key = "ml-hello"
            Title = "Hello, OCaml"
            Language = OCaml
            Code = mlHello }
          { Key = "ml-variants"
            Title = "Variants and pipelines"
            Language = OCaml
            Code = mlVariants } ]

    let defaultFor (lang: Language) =
        all |> List.find (fun t -> t.Language = lang)

    let tryFind (key: string) =
        all |> List.tryFind (fun t -> t.Key = key)
