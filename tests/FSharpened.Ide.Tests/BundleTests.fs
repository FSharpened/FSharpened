module FSharpened.Ide.Tests.BundleTests

open Xunit
open FSharpened.Ide

let private project (files: (string * string) list) : Project =
    { Name = "t"
      Files = files |> List.map (fun (p, t) -> { Path = p; Text = t })
      Entry = None
      Pipeline = [] }

let private bundle files path =
    match Bundler.make (project files) path with
    | Ok b -> b
    | Error ps -> failwith ("expected a bundle, got: " + String.concat "; " (Bundler.describe ps))

let private refused files path =
    match Bundler.make (project files) path with
    | Ok _ -> failwith "expected the bundle to be refused"
    | Error ps -> ps

// ---- the scanner ---------------------------------------------------------------------------------

[<Fact>]
let ``the mask blanks comments and string contents but keeps every line and column`` () =
    let text = "let a = \"import x\"; // import y\n/* import z */ import w from 'q';\n"
    let masked = Scan.mask JsLike text
    Assert.Equal(text.Length, masked.Length)
    Assert.Equal(text.Split('\n').Length, masked.Split('\n').Length)
    Assert.DoesNotContain("import x", masked)
    Assert.DoesNotContain("import y", masked)
    Assert.DoesNotContain("import z", masked)
    Assert.Contains("import w from '", masked)

[<Fact>]
let ``a template literal hides its text but not the code inside its placeholders`` () =
    let masked = Scan.mask JsLike "const t = `hello import q ${ f(\"a\") + g } bye`;"
    Assert.DoesNotContain("hello", masked)
    Assert.Contains("f(", masked)
    Assert.Contains("g", masked)

[<Fact>]
let ``a slash is a division after a value and a regular expression after an operator`` () =
    Assert.Contains("a / b", Scan.mask JsLike "x = a / b; // c")
    Assert.DoesNotContain("quote", Scan.mask JsLike "x = /\"quote/.test(s);")

[<Fact>]
let ``Rust lifetimes are not char literals, raw strings are strings`` () =
    let masked =
        Scan.mask RustLike "fn f<'a>(x: &'a str) { let c = 'x'; let r = r#\"mod z;\"#; }"

    Assert.Contains("'a", masked)
    Assert.DoesNotContain("mod z", masked)

[<Fact>]
let ``Python triple quotes and comments are blanked`` () =
    let masked = Scan.mask PyLike "\"\"\"import os\"\"\"\nx = 1  # import sys\n"
    Assert.DoesNotContain("import", masked)

// ---- the bundles ---------------------------------------------------------------------------------

[<Fact>]
let ``a file that needs no other is returned byte for byte`` () =
    let text = "import fs from 'fs';\r\nconsole.log(1)"
    let b = bundle [ "main.js", text; "notes.md", "x" ] "main.js"
    Assert.Equal(text, b.Source)
    Assert.Equal<string list>([ "main.js" ], b.Included)

[<Fact>]
let ``a JavaScript bundle lists the files it took and maps its lines back`` () =
    let b =
        bundle
            [ "main.js", "const { x } = require('./lib/x.js');\nconsole.log(x);\n"
              "lib/x.js", "// the helper\nexports.x = 1;\n"
              "spare.js", "" ]
            "main.js"

    Assert.Equal<string list>([ "lib/x.js"; "main.js" ], b.Included |> List.sort)
    Assert.Contains("spare.js", String.concat " " b.Notes)
    let line = b.Source.Substring(0, b.Source.IndexOf "exports.x").Split('\n').Length
    Assert.Equal(Some("lib/x.js", 2), Bundler.locate b line)
    Assert.Equal(None, Bundler.locate b 1)

[<Fact>]
let ``an unresolved relative import is an error with its file and line`` () =
    let ps = refused [ "main.js", "\n\nimport a from './gone';\n" ] "main.js"
    Assert.Equal(1, ps.Length)
    Assert.Equal("main.js", ps.Head.Path)
    Assert.Equal(3, ps.Head.Line)
    Assert.Contains("cannot find \"./gone\"", ps.Head.Message)

[<Fact>]
let ``a bundle over the gateway limit is refused with its size`` () =
    let big = "// " + String.replicate 40000 "x" + "\n"

    let ps =
        refused [ "main.js", "require('./a');require('./b');"; "a.js", big; "b.js", big ] "main.js"

    Assert.Contains("the gateway takes at most 65536", ps.Head.Message)

[<Fact>]
let ``package imports stay imports and are not looked up in the project`` () =
    let b =
        bundle
            [ "main.js", "import fs from 'fs';\nconst x = require('./y');\n"
              "y.js", "module.exports = 1;" ]
            "main.js"

    Assert.Contains("require(\"fs\")", b.Source)
    Assert.Contains("__r(\"y.js\")", b.Source)

[<Fact>]
let ``TypeScript files share one scope and imports are removed`` () =
    let b =
        bundle
            [ "main.ts", "import { area } from './shapes';\nimport fs from 'fs';\nconsole.log(area(2), fs);\n"
              "shapes.ts", "export function area(r: number): number { return r * r; }\n" ]
            "main.ts"

    Assert.StartsWith("import fs from 'fs';\n", b.Source)
    Assert.DoesNotContain("export", b.Source)
    Assert.DoesNotContain("./shapes", b.Source)
    Assert.Equal<string list>([ "shapes.ts"; "main.ts" ], b.Included)

[<Fact>]
let ``Rust mod declarations are replaced by the module text`` () =
    let b =
        bundle
            [ "main.rs", "mod util;\nfn main() { util::f(); }\n"
              "util.rs", "pub fn f() {}\n" ]
            "main.rs"

    Assert.Contains("mod util {", b.Source)
    Assert.Equal(Some("util.rs", 1), Bundler.locate b 2)
    Assert.Equal(Some("main.rs", 2), Bundler.locate b 4)

[<Fact>]
let ``a Rust module without a file says what to add`` () =
    let ps = refused [ "main.rs", "mod ghost;\nfn main() {}\n" ] "main.rs"
    Assert.Contains("ghost.rs or ghost/mod.rs", ps.Head.Message)

[<Fact>]
let ``Go files of one package are merged and their imports hoisted once`` () =
    let b =
        bundle
            [ "main.go", "package main\n\nimport \"fmt\"\n\nfunc main() { fmt.Println(f()) }\n"
              "f.go",
              "package main\n\nimport (\n\t\"fmt\"\n\t\"os\"\n)\n\nfunc f() int { fmt.Print(); _ = os.Args; return 1 }\n"
              "x.go", "package other\n" ]
            "main.go"

    Assert.StartsWith("package main\nimport (\n", b.Source)
    Assert.Equal(1, b.Source.Split([| "\"fmt\"" |], System.StringSplitOptions.None).Length - 1)
    Assert.Contains("x.go: package other is not main", String.concat " " b.Notes)

[<Fact>]
let ``C includes are inlined once and the implementation file follows`` () =
    let b =
        bundle
            [ "main.c", "#include \"a.h\"\n#include \"a.h\"\nint main(void){return f();}\n"
              "a.h", "#pragma once\nint f(void);\n"
              "a.c", "#include \"a.h\"\nint f(void){return 0;}\n" ]
            "main.c"

    Assert.Equal<string list>([ "main.c"; "a.h"; "a.c" ], b.Included)
    Assert.DoesNotContain("#pragma once", b.Source)
    Assert.Equal(2, b.Source.Split([| "int f(void)" |], System.StringSplitOptions.None).Length - 1)

[<Fact>]
let ``Java classes the entry names are merged and only the entry stays public`` () =
    let b =
        bundle
            [ "Main.java", "public class Main { Helper h; }\n"
              "Helper.java", "import java.util.List;\npublic class Helper { List<String> l; }\n" ]
            "Main.java"

    Assert.StartsWith("import java.util.List;\n", b.Source)
    Assert.Contains("class Helper", b.Source)
    Assert.DoesNotContain("public class Helper", b.Source)
    Assert.Contains("public class Main", b.Source)

[<Fact>]
let ``Python modules are embedded and a traceback names the file the line is in`` () =
    let b =
        bundle [ "main.py", "import util\nutil.f()\n"; "util.py", "def f():\n    pass\n" ] "main.py"

    Assert.Contains("\"util\": (\"util.py\"", b.Source)

    let trace =
        "Traceback (most recent call last):\n  File \"main.py\", line 2, in <module>\n  File \"util.py\", line 2, in f\nValueError: x"

    Assert.Equal(Some("util.py", 2), Bundler.locateIn b trace 2)

[<Fact>]
let ``languages that cannot be merged send the entry alone and say why`` () =
    let b = bundle [ "main.rb", "puts 1"; "lib.rb", "x = 1" ] "main.rb"
    Assert.Equal("puts 1", b.Source)
    Assert.Contains("only main.rb is sent", String.concat " " b.Notes)

    for l in Language.all do
        match Bundler.support l with
        | Merged t
        | Alone t -> Assert.NotEmpty t

// ---- the pipeline ----------------------------------------------------------------------------------

let private ok path out : StepSummary =
    { Path = path
      Ok = true
      Stdout = out
      Summary = "Finished" }

[<Fact>]
let ``a project without a pipeline runs its entry as the single step`` () =
    let p = project [ "main.py", "print(1)"; "util.py", "" ]
    Assert.Equal<string list>([ "main.py" ], Pipeline.steps p)

[<Fact>]
let ``a pipeline runs its steps in order, each fed the previous output`` () =
    let paths = [ "a.py"; "b.js" ]
    Assert.Equal(RunStage(0, "typed"), Pipeline.next paths [] "typed")
    Assert.Equal(RunStage(1, "from a"), Pipeline.next paths [ ok "a.py" "from a" ] "typed")

    match Pipeline.next paths [ ok "a.py" "x"; ok "b.js" "y" ] "" with
    | Stop o -> Assert.True o.Ok
    | n -> failwith (sprintf "%A" n)

[<Fact>]
let ``a failed step stops the run and counts what did not run`` () =
    let bad: StepSummary =
        { Path = "a.py"
          Ok = false
          Stdout = ""
          Summary = "Exited with code 2" }

    match Pipeline.next [ "a.py"; "b.py"; "c.py" ] [ bad ] "" with
    | Stop o ->
        Assert.False o.Ok
        Assert.Contains("Step 1 of 3 (a.py) failed: Exited with code 2; 2 steps not run", o.Summary)
    | n -> failwith (sprintf "%A" n)

[<Fact>]
let ``output longer than the gateway's input limit stops the run instead of being cut`` () =
    match Pipeline.next [ "a.py"; "b.py" ] [ ok "a.py" (String.replicate 5000 "x") ] "" with
    | Stop o ->
        Assert.False o.Ok
        Assert.Contains("5000 characters", o.Summary)
        Assert.Contains("4096", o.Summary)
    | n -> failwith (sprintf "%A" n)

[<Fact>]
let ``a plan reports the problems of all steps`` () =
    let p =
        { project [ "a.js", "require('./n1')"; "b.js", "require('./n2')" ] with
            Pipeline = [ "a.js"; "b.js" ] }

    match Pipeline.plan p with
    | Error ps -> Assert.Equal<string list>([ "a.js"; "b.js" ], ps |> List.map (fun x -> x.Path))
    | Ok _ -> failwith "expected problems"
