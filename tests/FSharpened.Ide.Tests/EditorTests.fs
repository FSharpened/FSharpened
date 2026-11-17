module FSharpened.Ide.Tests.EditorTests

open Xunit
open FSharpened.Ide

// ---- fuzzy matching and quick open ---------------------------------------------------------------------

[<Fact>]
let ``a fuzzy match is a subsequence and reports where it matched`` () =
    let m = Fuzzy.score "mn" "main.rs" |> Option.get
    Assert.Equal<int list>([ 0; 3 ], m.Positions)
    Assert.True(Fuzzy.score "xyz" "main.rs" |> Option.isNone)
    Assert.True(Fuzzy.score "mainn" "main" |> Option.isNone)

[<Fact>]
let ``fuzzy matching ignores case and spaces in the query`` () =
    Assert.True(Fuzzy.score "M A I N" "src/main.rs" |> Option.isSome)
    Assert.True(Fuzzy.score "MAIN" "src/main.rs" |> Option.isSome)

[<Fact>]
let ``a word start and a file name rank above a scattered match`` () =
    let ranked =
        Fuzzy.rank "main" [ "domain.fs"; "docs/maintenance.md"; "src/main.rs" ] 10
        |> List.map (fun r -> r.Text)

    Assert.Equal<string list>([ "src/main.rs"; "docs/maintenance.md"; "domain.fs" ], ranked)

[<Fact>]
let ``camel case humps count as word starts`` () =
    let first = Fuzzy.rank "fb" [ "foobar"; "FooBar" ] 10 |> List.head
    Assert.Equal("FooBar", first.Text)

[<Fact>]
let ``an empty query keeps the order and honours the limit`` () =
    let r = Fuzzy.rank "" [ "c"; "a"; "b" ] 2
    Assert.Equal<string list>([ "c"; "a" ], r |> List.map (fun x -> x.Text))

[<Fact>]
let ``long texts are scored without failing`` () =
    let long = String.replicate 5000 "ab"
    Assert.True((Fuzzy.score "ba" long).IsSome)

[<Theory>]
[<InlineData(">format file", "command", "format file", 0, 0)>]
[<InlineData("@main", "symbol", "main", 0, 0)>]
[<InlineData(":12", "line", "", 12, 0)>]
[<InlineData(":12:5", "line", "", 12, 5)>]
[<InlineData("main.rs:12", "file", "main.rs", 12, 0)>]
[<InlineData("src/a.rs:3:4", "file", "src/a.rs", 3, 4)>]
[<InlineData("readme", "file", "readme", 0, 0)>]
[<InlineData("  readme  ", "file", "readme", 0, 0)>]
let ``quick open splits what was typed`` (raw: string, mode: string, text: string, line: int, col: int) =
    let q = Quick.parse raw
    Assert.Equal(mode, q.Mode)
    Assert.Equal(text, q.Text)
    Assert.Equal(line, q.Line)
    Assert.Equal(col, q.Column)

// ---- search and replace --------------------------------------------------------------------------------

let private plain q =
    { Query = q
      CaseSensitive = false
      WholeWord = false
      Regex = false }

let private hitsOf (o: SearchOptions) text =
    match Search.findInText o "f" text with
    | Ok fh -> fh.Hits
    | Error e -> failwith e

[<Fact>]
let ``a plain search is case-insensitive unless asked`` () =
    let hits = hitsOf (plain "foo") "Foo bar\nxfoo"
    Assert.Equal<(int * int) list>([ 1, 1; 2, 2 ], hits |> List.map (fun h -> h.Line, h.Column))

    let exact =
        hitsOf
            { plain "foo" with
                CaseSensitive = true }
            "Foo bar\nxfoo"

    Assert.Equal(1, exact.Length)

[<Fact>]
let ``whole word does not match inside a longer word`` () =
    let hits = hitsOf { plain "foo" with WholeWord = true } "foo foobar _foo foo."
    Assert.Equal<int list>([ 1; 17 ], hits |> List.map (fun h -> h.Column))

[<Fact>]
let ``a regular expression reports every match with its length`` () =
    let hits = hitsOf { plain "\\d+" with Regex = true } "a1 b22"
    Assert.Equal<(int * int) list>([ 2, 1; 5, 2 ], hits |> List.map (fun h -> h.Column, h.Length))

[<Fact>]
let ``an invalid regular expression is reported, not thrown`` () =
    let r = Search.run { plain "(" with Regex = true } "" "" [ "a.txt", "(" ]
    Assert.True(r.Error <> "")
    Assert.Empty r.Files

[<Fact>]
let ``an empty query finds nothing`` () =
    let r = Search.run (plain "") "" "" [ "a.txt", "text" ]
    Assert.Equal(0, r.Total)
    Assert.Equal("", r.Error)

[<Fact>]
let ``a preview carries the line and where it starts`` () =
    let line = String.replicate 100 "x" + "needle" + String.replicate 100 "y"
    let h = (hitsOf (plain "needle") line).Head
    Assert.Equal(101, h.Column)
    Assert.Equal("needle", h.Preview.Substring(h.Column - 1 - h.PreviewStart, h.Length))
    Assert.True(h.Preview.Length <= 200)

[<Fact>]
let ``search honours the include and exclude patterns`` () =
    let files =
        [ "src/a.rs", "needle"
          "src/b.py", "needle"
          "tests/c.rs", "needle"
          "node_modules/d.js", "needle" ]

    let paths inc exc =
        (Search.run (plain "needle") inc exc files).Files |> List.map (fun f -> f.Path)

    Assert.Equal<string list>([ "src/a.rs"; "tests/c.rs" ], paths "*.rs" "")
    Assert.Equal<string list>([ "src/a.rs"; "src/b.py" ], paths "src/**" "")
    Assert.Equal<string list>([ "src/a.rs"; "src/b.py"; "tests/c.rs" ], paths "" "node_modules")
    Assert.Equal<string list>([ "src/a.rs" ], paths "*.rs, *.py" "tests, *.py")

[<Fact>]
let ``results are capped so a huge file cannot flood the page`` () =
    let text = String.replicate 5000 "x\n"
    let files = [ for i in 1..12 -> "f" + string i + ".txt", text ]
    let r = Search.run (plain "x") "" "" files
    let fh = r.Files.Head
    Assert.Equal(Search.maxPerFile, fh.Hits.Length)
    Assert.True fh.More
    Assert.Equal(Search.maxHits, r.Total)
    Assert.True r.Truncated
    Assert.Equal(10, r.Files.Length)

[<Fact>]
let ``replace keeps line endings and counts the changes`` () =
    match Search.replaceInText (plain "foo") "bar" "foo\r\nfoo foo\n" with
    | Ok(t, n) ->
        Assert.Equal("bar\r\nbar bar\n", t)
        Assert.Equal(3, n)
    | Error e -> failwith e

[<Fact>]
let ``a regular expression replacement can use groups`` () =
    let o =
        { plain "(\\w+)@(\\w+)" with
            Regex = true }

    match Search.replaceInText o "$2:$1 [$&] $$" "a@b" with
    | Ok(t, _) -> Assert.Equal("b:a [a@b] $", t)
    | Error e -> failwith e

[<Fact>]
let ``a replacement is literal in a plain search`` () =
    match Search.replaceInText (plain "a") "$1" "a" with
    | Ok(t, _) -> Assert.Equal("$1", t)
    | Error e -> failwith e

[<Fact>]
let ``an anchor can prefix every line`` () =
    let o = { plain "^" with Regex = true }

    match Search.replaceInText o "// " "ab\ncd" with
    | Ok(t, n) ->
        Assert.Equal("// ab\n// cd", t)
        Assert.Equal(2, n)
    | Error e -> failwith e

[<Fact>]
let ``replace one changes only the match at that position`` () =
    match Search.replaceOne (plain "foo") "X" "foo foo\nfoo" 1 5 with
    | Ok t -> Assert.Equal("foo X\nfoo", t)
    | Error e -> failwith e

    Assert.True(
        match Search.replaceOne (plain "foo") "X" "foo" 1 2 with
        | Error _ -> true
        | Ok _ -> false
    )

[<Fact>]
let ``replacing in a project reports a file that would grow past the limit`` () =
    let big = String.replicate (Project.maxFileChars - 1) "a"

    let p =
        Project.create "P"
        |> Project.addFile "big.txt" big
        |> Result.bind (Project.addFile "small.txt" "a")
        |> Result.defaultWith (fun e -> failwith e)

    let r = Search.replaceInProject (plain "a") "bb" [ "big.txt"; "small.txt" ] p
    Assert.Equal<(string * int) list>([ "small.txt", 1 ], r.Changed)
    Assert.Equal(1, r.Failed.Length)
    Assert.Equal("bb", (Project.tryFind "small.txt" r.Project).Value.Text)
    Assert.Equal(big, (Project.tryFind "big.txt" r.Project).Value.Text)

// ---- outline -------------------------------------------------------------------------------------------

let private names (syms: Symbol list) =
    syms |> List.map (fun s -> s.Name, s.Kind)

let private outline id text =
    Symbols.outline (Language.tryOfId id |> Option.get) text

let private samples: (string * string * (string * string) list) list =
    [ "fsharp",
      "module Geometry\n\ntype Shape = Circle of float\n\nlet area s =\n    let half = 2.0\n    half\n\nlet pi = 3.14\n",
      [ "Geometry", "module"; "Shape", "type"; "area", "function"; "pi", "variable" ]
      "csharp",
      "namespace Demo\n{\n    public class Board\n    {\n        public Board() { }\n        public int Size()\n        {\n            return Helper(1);\n        }\n    }\n}\n",
      [ "Demo", "namespace"
        "Board", "class"
        "Board", "constructor"
        "Size", "method" ]
      "vb", "Module Program\n    Sub Main()\n    End Sub\nEnd Module\n", [ "Program", "module"; "Main", "function" ]
      "javascript",
      "class Board {\n  place(x) {\n    if (x) {\n      return x;\n    }\n  }\n}\nfunction main() {}\nconst run = () => 1;\nexport const LIMIT = 4;\n",
      [ "Board", "class"
        "place", "method"
        "main", "function"
        "run", "function"
        "LIMIT", "constant" ]
      "typescript",
      "interface Cell { x: number }\ntype Grid = Cell[];\nexport enum Mode { A }\nexport function make(): Grid { return []; }\n",
      [ "Cell", "interface"; "Grid", "type"; "Mode", "enum"; "make", "function" ]
      "python",
      "MAX = 3\nclass Board:\n    def place(self):\n        \"\"\"def fake(): pass\"\"\"\n        def inner(): pass\n\nasync def main():\n    pass\n",
      [ "MAX", "constant"
        "Board", "class"
        "place", "method"
        "inner", "function"
        "main", "function" ]
      "rust",
      "pub struct Board { n: u8 }\nimpl Board {\n    pub fn new() -> Self { Board { n: 0 } }\n    fn helper(&self) {}\n}\nimpl Display for Board {}\n// fn commented() {}\nfn main() {}\n",
      [ "Board", "struct"
        "impl Board", "impl"
        "new", "method"
        "helper", "method"
        "impl Display for Board", "impl"
        "main", "function" ]
      "dart",
      "class Board {\n  int size() {\n    return 1;\n  }\n}\nvoid main() {\n  print(1);\n}\n",
      [ "Board", "class"; "size", "method"; "main", "function" ]
      "c",
      "#define MAX 10\nstruct Board {\n  int size;\n};\nint main(void) {\n  return 0;\n}\nstatic int add(int a, int b)\n{\n  return a + b;\n}\nint proto(int a);\n",
      [ "MAX", "macro"; "Board", "struct"; "main", "function"; "add", "function" ]
      "cpp",
      "namespace demo {\nclass Board {\npublic:\n  int size() const { return 1; }\n};\n}\nint main() { return 0; }\n",
      [ "demo", "namespace"; "Board", "class"; "size", "method"; "main", "function" ]
      "go",
      "package main\n\ntype Board struct{}\n\nfunc (b *Board) Place(x int) {}\n\nfunc main() {}\n",
      [ "main", "namespace"
        "Board", "struct"
        "Board.Place", "method"
        "main", "function" ]
      "java",
      "package demo;\npublic class Board {\n    public int size() {\n        return 1;\n    }\n}\n",
      [ "demo", "namespace"; "Board", "class"; "size", "method" ]
      "ruby",
      "module Demo\n  class Board\n    def place(x)\n    end\n  end\nend\n",
      [ "Demo", "module"; "Board", "class"; "place", "method" ]
      "lua",
      "local function add(a, b)\n  return a + b\nend\nfunction Board.place(x) end\nBoard.size = function() end\n",
      [ "add", "function"; "Board.place", "function"; "Board.size", "function" ]
      "php",
      "<?php\nnamespace Demo;\nclass Board {\n    public function size() { return 1; }\n}\nfunction main() {}\n",
      [ "Demo", "namespace"; "Board", "class"; "size", "method"; "main", "function" ]
      "r",
      "area <- function(x) {\n  x\n}\nsetClass(\"Board\", representation())\n",
      [ "area", "function"; "Board", "class" ]
      "haskell",
      "module Main where\n\ndata Board = Board Int\n\nsize :: Board -> Int\nsize (Board n) = n\n\nmain :: IO ()\nmain = pure ()\n",
      [ "Main", "module"; "Board", "type"; "size", "function"; "main", "function" ]
      "ocaml",
      "module Geo = struct\n  let area r = r\nend\ntype shape = Circle\nlet rec go n = n\nlet x = 5\n",
      [ "Geo", "module"
        "area", "function"
        "shape", "type"
        "go", "function"
        "x", "variable" ] ]

[<Fact>]
let ``every language has an outline sample`` () =
    let covered = samples |> List.map (fun (id, _, _) -> id) |> Set.ofList
    let all = Language.infos |> List.map (fun i -> i.Id) |> Set.ofList
    Assert.Equal<Set<string>>(all, covered)

[<Fact>]
let ``the outline of each language lists its declarations`` () =
    for (id, text, expected) in samples do
        let got = names (outline id text)

        Assert.True((expected = got), id + ": expected " + (sprintf "%A" expected) + " but got " + (sprintf "%A" got))

[<Fact>]
let ``nesting gives depth, container and end line`` () =
    let syms =
        outline "rust" "impl Board {\n    fn new() {}\n    fn go() {}\n}\nfn main() {}\n"

    let impl = syms |> List.find (fun s -> s.Name = "impl Board")
    let newFn = syms |> List.find (fun s -> s.Name = "new")
    Assert.Equal(0, impl.Depth)
    Assert.Equal(1, newFn.Depth)
    Assert.Equal("impl Board", newFn.Container)
    Assert.Equal(4, impl.EndLine)
    Assert.Equal(2, newFn.EndLine)

[<Fact>]
let ``commented out code is not in the outline`` () =
    Assert.Empty(outline "rust" "// fn a() {}\n/* fn b() {}\nfn c() {} */\n")
    Assert.Empty(outline "python" "# def a(): pass\n\"\"\"\ndef b(): pass\n\"\"\"\n")
    Assert.Equal(1, (outline "csharp" "/* x */ class A {}\n").Length)

[<Fact>]
let ``a comment marker inside a string does not hide a declaration`` () =
    let syms =
        outline "javascript" "const s = \"/*\";\nconst t = '//';\nfunction f() {}\n"

    Assert.Equal<(string * string) list>([ "f", "function" ], names syms)

[<Fact>]
let ``the column points at the name`` () =
    let s = (outline "rust" "    pub fn hello() {}\n").Head
    Assert.Equal(1, s.Line)
    Assert.Equal(12, s.Column)

[<Fact>]
let ``markdown headings form an outline and skip code fences`` () =
    let syms =
        Symbols.markdown "# Title\ntext\n## Part\n```\n# not a heading\n```\n## Other\n"

    Assert.Equal<(string * int) list>(
        [ "Title", 0; "Part", 1; "Other", 1 ],
        syms |> List.map (fun s -> s.Name, s.Depth)
    )

    Assert.Equal(7, syms.Head.EndLine)

[<Fact>]
let ``the outline is chosen by the file extension`` () =
    Assert.Equal(1, (Symbols.outlineOfPath "src/a.py" "def f(): pass").Length)
    Assert.Equal(1, (Symbols.outlineOfPath "README.md" "# Hi").Length)
    Assert.Empty(Symbols.outlineOfPath "data.json" "{ }")

[<Fact>]
let ``breadcrumbs are the symbols around a line`` () =
    let syms =
        outline "rust" "impl Board {\n    fn new() {\n        let x = 1;\n    }\n}\nfn main() {}\n"

    Assert.Equal<string list>([ "impl Board"; "new" ], Symbols.enclosing syms 3 |> List.map (fun s -> s.Name))
    Assert.Equal<string list>([ "main" ], Symbols.enclosing syms 6 |> List.map (fun s -> s.Name))
    Assert.Empty(Symbols.enclosing syms 99)

[<Fact>]
let ``go to definition finds a name by its last part across files`` () =
    let files =
        [ "a.go", "package a\nfunc (s *Shape) Area() {}\n"
          "b.rs", "fn area() {}\nfn Area() {}\n"
          "c.md", "# Area\n" ]

    let found =
        Symbols.definitions "Area" files |> List.map (fun d -> d.Path, d.Symbol.Line)

    Assert.Equal<(string * int) list>([ "a.go", 2; "b.rs", 2 ], found)
    Assert.Empty(Symbols.definitions "" files)

[<Theory>]
[<InlineData("let value = compute(x)", 6, "value")>]
[<InlineData("let value = compute(x)", 5, "value")>]
[<InlineData("let value = compute(x)", 15, "compute")>]
[<InlineData("  ", 2, "")>]
let ``the word under the cursor`` (line: string, col: int, word: string) =
    Assert.Equal(word, Symbols.wordAt line col)

[<Fact>]
let ``outlining a large file stays quick`` () =
    let text = String.replicate 3000 "fn thing() {\n    let x = 1;\n}\n"
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let syms = outline "rust" text
    Assert.Equal(3000, syms.Length)
    Assert.True(sw.ElapsedMilliseconds < 5000L)
