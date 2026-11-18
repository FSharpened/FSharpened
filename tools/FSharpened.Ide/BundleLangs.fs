namespace FSharpened.Ide

open System.Collections.Generic
open System.Text.RegularExpressions
open FSharpened.Ide.BundleKit

/// Bundling Python projects (ADR-0024). The files are embedded as text in a small import hook that is installed before
/// the entry file runs, so `import util`, `from pkg import mod` and relative imports behave as they do on disk, and a
/// traceback names the real file and the real line.
module BundlePython =
    let private reImport =
        Regex("^[ \\t]*import[ \\t]+([^\\n;]+)", RegexOptions.Multiline)

    let private reFrom =
        Regex("^[ \\t]*from[ \\t]+(\\.*)([A-Za-z_][\\w.]*)?[ \\t]+import[ \\t]+(\\(?[^\\n]*)", RegexOptions.Multiline)

    type private Mod =
        { Name: string
          File: ProjectFile
          IsPackage: bool }

    /// The dotted name of a file below the entry's folder, or None for a file outside it or one that is not Python.
    let private nameOf (root: string) (path: string) : (string * bool) option =
        if ProjectPath.extensionOf path <> "py" then
            None
        elif root <> "" && not (ProjectPath.isUnder root path) then
            None
        else
            let rel = if root = "" then path else path.Substring(root.Length + 1)
            let noExt = rel.Substring(0, rel.Length - 3)

            if noExt = "__init__" then
                None
            elif noExt.EndsWith "/__init__" then
                Some(noExt.Substring(0, noExt.Length - 9).Replace('/', '.'), true)
            else
                Some(noExt.Replace('/', '.'), false)

    let private parents (name: string) : string list =
        let parts = name.Split('.') |> Array.toList

        [ for n in 1 .. parts.Length - 1 -> String.concat "." (List.take n parts) ]

    let bundle (project: Project) (entry: ProjectFile) : Result<Bundle, BundleProblem list> =
        let root = ProjectPath.parent entry.Path

        let mods =
            project.Files
            |> List.choose (fun f ->
                match nameOf root f.Path with
                | Some(n, isPkg) when ProjectPath.key f.Path <> ProjectPath.key entry.Path ->
                    Some
                        { Name = n
                          File = f
                          IsPackage = isPkg }
                | _ -> None)

        let byName = mods |> List.map (fun m -> m.Name, m) |> Map.ofList

        // folders below the root that hold Python files act as namespace packages
        let folders =
            project.Files
            |> List.choose (fun f -> nameOf root f.Path)
            |> List.collect (fun (n, _) -> parents n)
            |> Set.ofList

        let exists (name: string) =
            byName.ContainsKey name || folders.Contains name

        let problems = ResizeArray<BundleProblem>()
        let included = ResizeArray<string>()
        let queue = Queue<string * ProjectFile>()

        // a module for this dotted name and each of its parents, in import order
        let need (name: string) =
            for n in parents name @ [ name ] do
                if not (included.Contains n) then
                    included.Add n

                    match Map.tryFind n byName with
                    | Some m -> queue.Enqueue((n, m.File))
                    | None -> ()

        let scan (modName: string) (file: ProjectFile) =
            let text = file.Text.Replace("\r\n", "\n").Replace('\r', '\n')
            let masked = Scan.mask PyLike text
            let isEntry = (ProjectPath.key file.Path = ProjectPath.key entry.Path)

            let isPkg =
                (match Map.tryFind modName byName with
                 | Some m -> m.IsPackage
                 | None -> false)

            let package =
                if isEntry then
                    ""
                elif isPkg then
                    modName
                else
                    (parents modName |> List.tryLast |> Option.defaultValue "")

            let at (pos: int) = lineOf text pos

            let absolute (target: string) (pos: int) (names: string list) =
                let top = target.Split('.').[0]

                if exists top then
                    let missing = parents target @ [ target ] |> List.tryFind (fun n -> not (exists n))

                    match missing with
                    | Some n ->
                        problems.Add(
                            problem
                                file.Path
                                (at pos)
                                ("cannot find module \""
                                 + n
                                 + "\" (looked for "
                                 + n.Replace('.', '/')
                                 + ".py and "
                                 + n.Replace('.', '/')
                                 + "/__init__.py)")
                        )
                    | None ->
                        need target

                        for nm in names do
                            if exists (target + "." + nm) then
                                need (target + "." + nm)

            for m in reImport.Matches masked |> Seq.cast<Match> do
                for part in m.Groups.[1].Value.Split(',') do
                    let name = (Regex.Replace(part.Trim(), "\\s+as\\s+.*$", "")).Trim()

                    if name <> "" && Regex.IsMatch(name, "^[A-Za-z_][\\w.]*$") then
                        absolute name m.Index []

            for m in reFrom.Matches masked |> Seq.cast<Match> do
                let dots = m.Groups.[1].Value.Length
                let name = m.Groups.[2].Value

                let rest =
                    let g = m.Groups.[3]
                    let start = m.Index + m.Length - g.Value.Length
                    let first = g.Value

                    if first.StartsWith "(" && not (first.Contains ")") then
                        let close = masked.IndexOf(')', start)
                        masked.Substring(start, (if close < 0 then masked.Length else close + 1) - start)
                    else
                        first

                let names =
                    rest.Replace("(", " ").Replace(")", " ").Split(',')
                    |> Array.map (fun s -> (Regex.Replace(s.Trim(), "\\s+as\\s+.*$", "")).Trim())
                    |> Array.filter (fun s -> Regex.IsMatch(s, "^[A-Za-z_]\\w*$"))
                    |> Array.toList

                if dots = 0 then
                    absolute name m.Index names
                elif isEntry then
                    problems.Add(
                        problem
                            file.Path
                            (at m.Index)
                            "a relative import in the entry file: the entry runs as a script, so use an absolute import (from pkg import name)"
                    )
                else
                    let up = dots - 1

                    let parts =
                        if package = "" then
                            []
                        else
                            package.Split('.') |> Array.toList

                    if up > parts.Length then
                        problems.Add(
                            problem file.Path (at m.Index) "the relative import climbs above the top of the project"
                        )
                    else
                        let basePkg = List.take (parts.Length - up) parts |> String.concat "."

                        let target =
                            if name = "" then basePkg
                            elif basePkg = "" then name
                            else basePkg + "." + name

                        if target = "" then
                            for nm in names do
                                if exists nm then
                                    need nm
                        elif exists target then
                            need target

                            for nm in names do
                                if exists (target + "." + nm) then
                                    need (target + "." + nm)
                        else
                            problems.Add(
                                problem
                                    file.Path
                                    (at m.Index)
                                    ("cannot find module \""
                                     + target
                                     + "\" (looked for "
                                     + target.Replace('.', '/')
                                     + ".py and "
                                     + target.Replace('.', '/')
                                     + "/__init__.py)")
                            )

        scan "__main__" entry

        while queue.Count > 0 do
            let (n, f) = queue.Dequeue()
            scan n f

        if problems.Count > 0 then
            Error(List.ofSeq problems)
        elif included.Count = 0 then
            Ok(alone Python entry [])
        else
            let table =
                included
                |> Seq.map (fun n ->
                    match Map.tryFind n byName with
                    | Some m ->
                        "  "
                        + quote n
                        + ": ("
                        + quote m.File.Path
                        + ", "
                        + (if m.IsPackage then "1" else "0")
                        + ", "
                        + quote m.File.Text
                        + "),"
                    | None -> "  " + quote n + ": (" + quote (n.Replace('.', '/') + "/") + ", 1, \"\"),")
                |> List.ofSeq

            let header =
                [ "import sys as _fsh_sys, importlib.abc as _fsh_abc, importlib.util as _fsh_util"
                  "_fsh_mods = {" ]
                @ table
                @ [ "}"
                    "class _FshLoader(_fsh_abc.Loader):"
                    "    def create_module(self, spec): return None"
                    "    def exec_module(self, module):"
                    "        path, _, text = _fsh_mods[module.__name__]"
                    "        module.__file__ = path"
                    "        exec(compile(text, path, \"exec\"), module.__dict__)"
                    "class _FshFinder(_fsh_abc.MetaPathFinder):"
                    "    def find_spec(self, name, path=None, target=None):"
                    "        m = _fsh_mods.get(name)"
                    "        if m: return _fsh_util.spec_from_loader(name, _FshLoader(), is_package=bool(m[1]))"
                    "_fsh_sys.meta_path.insert(0, _FshFinder())"
                    "__file__ = " + quote entry.Path
                    "exec(compile(" + quote entry.Text + ", " + quote entry.Path + ", \"exec\"))" ]

            let files =
                entry.Path
                :: (included
                    |> Seq.choose (fun n -> Map.tryFind n byName |> Option.map (fun m -> m.File.Path))
                    |> List.ofSeq)

            let others =
                mods
                |> List.filter (fun m -> not (List.contains m.File.Path files))
                |> List.map (fun m -> m.File.Path)

            let notes =
                if others.IsEmpty then
                    []
                else
                    [ "not reached from " + entry.Path + ": " + String.concat ", " others ]

            finish Python entry.Path [ generated header ] files notes

/// Bundling Rust projects: `mod name;` becomes `mod name { ... }` with the file's text, as the compiler would read it.
module BundleRust =
    let private reMod =
        Regex("^(\\s*(?:#\\[[^\\]]*\\]\\s*)*(?:pub(?:\\([^)]*\\))?\\s+)?)mod\\s+([A-Za-z_]\\w*)\\s*;(.*)$")

    let bundle (project: Project) (entry: ProjectFile) : Result<Bundle, BundleProblem list> =
        let files = index project
        let problems = ResizeArray<BundleProblem>()
        let included = ResizeArray<string>()

        let rec expand (file: ProjectFile) (isRoot: bool) (depth: int) : Piece list =
            included.Add file.Path
            let text = file.Text.Replace("\r\n", "\n").Replace('\r', '\n')
            let lines = bodyLines text |> List.toArray
            let masked = bodyLines (Scan.mask RustLike text) |> List.toArray
            let dir = ProjectPath.parent file.Path
            let stem = (let n = ProjectPath.name file.Path in n.Substring(0, n.Length - 3))

            let childDir =
                if isRoot || stem = "mod" || stem = "main" || stem = "lib" then
                    dir
                else
                    ProjectPath.join dir stem

            let pieces = ResizeArray<Piece>()
            let runStart = ref 0

            let flush (upTo: int) =
                if upTo > runStart.Value then
                    pieces.Add
                        { Path = file.Path
                          From = runStart.Value + 1
                          Lines = [ for k in runStart.Value .. upTo - 1 -> lines.[k] ] }

            for i in 0 .. lines.Length - 1 do
                let m = reMod.Match masked.[i]

                if m.Success then
                    flush i
                    runStart.Value <- i + 1
                    let name = m.Groups.[2].Value
                    let prefix = lines.[i].Substring(0, m.Groups.[1].Length)
                    let rest = lines.[i].Substring(lines.[i].Length - m.Groups.[3].Length)

                    let pathAttr = i > 0 && masked.[i - 1].Trim().StartsWith "#[path"

                    if pathAttr then
                        problems.Add(
                            problem
                                file.Path
                                (i + 1)
                                "#[path = \"...\"] is not supported when bundling; name the file the way `mod` looks for it"
                        )
                    else
                        let candidates =
                            [ ProjectPath.join childDir (name + ".rs")
                              ProjectPath.join (ProjectPath.join childDir name) "mod.rs" ]

                        match candidates |> List.tryPick (find files) with
                        | None ->
                            problems.Add(
                                problem
                                    file.Path
                                    (i + 1)
                                    ("`mod "
                                     + name
                                     + ";` has no file: add "
                                     + candidates.[0]
                                     + " or "
                                     + candidates.[1])
                            )
                        | Some child when included.Contains child.Path ->
                            problems.Add(problem file.Path (i + 1) (child.Path + " is already part of the crate"))
                        | Some child when depth >= 16 ->
                            problems.Add(problem file.Path (i + 1) "modules are nested more than 16 deep")
                        | Some child ->
                            pieces.Add
                                { Path = file.Path
                                  From = i + 1
                                  Lines = [ prefix + "mod " + name + " {" ] }

                            pieces.AddRange(expand child false (depth + 1))

                            pieces.Add
                                { Path = file.Path
                                  From = i + 1
                                  Lines = [ "}" + rest ] }

            flush lines.Length
            List.ofSeq pieces

        let pieces = expand entry true 0

        if problems.Count > 0 then
            Error(List.ofSeq problems)
        elif included.Count = 1 then
            Ok(alone Rust entry [])
        else
            let others =
                project.Files
                |> List.filter (fun f -> ProjectPath.languageOf f.Path = Some Rust && not (included.Contains f.Path))
                |> List.map (fun f -> f.Path)

            let notes =
                if others.IsEmpty then
                    []
                else
                    [ "not reached from " + entry.Path + ": " + String.concat ", " others ]

            finish Rust entry.Path pieces (List.ofSeq included) notes

/// Bundling Go projects: the files of the entry's folder that belong to its package are merged into one file.
module BundleGo =
    let private rePackage = Regex("^\\s*package\\s+([A-Za-z_]\\w*)")

    let private reImportOne =
        Regex("^\\s*import\\s+(?:([A-Za-z_.][\\w]*)\\s+)?\"([^\"]+)\"")

    let private reImportBlock = Regex("^\\s*import\\s*\\(")
    let private reSpec = Regex("^\\s*(?:([A-Za-z_.][\\w]*)\\s+)?\"([^\"]+)\"")
    let private reMain = Regex("^func\\s+main\\s*\\(")

    type private Parsed =
        {
            File: ProjectFile
            Package: string
            HasMain: bool
            Lines: string[]
            /// the import specs, as written ("fmt", "m \"math\""), with the 1-based line each is on
            Imports: (string * string * int) list
            /// lines (0-based) that are the package clause or an import, to be left empty
            Blank: Set<int>
        }

    let private parse (file: ProjectFile) : Parsed =
        let text = file.Text.Replace("\r\n", "\n").Replace('\r', '\n')
        let lines = bodyLines text |> List.toArray
        let masked = bodyLines (Scan.mask GoLike text) |> List.toArray
        let mutable pkg = ""
        let mutable hasMain = false
        let imports = ResizeArray<string * string * int>()
        let blank = HashSet<int>()
        let mutable inBlock = false

        for i in 0 .. lines.Length - 1 do
            let m = masked.[i]

            if inBlock then
                blank.Add i |> ignore

                if m.Trim().StartsWith ")" then
                    inBlock <- false
                else
                    let s = reSpec.Match lines.[i]

                    if s.Success then
                        imports.Add((s.Groups.[1].Value, s.Groups.[2].Value, i + 1))
            elif pkg = "" && rePackage.IsMatch m then
                pkg <- (rePackage.Match m).Groups.[1].Value
                blank.Add i |> ignore
            elif reImportBlock.IsMatch m then
                blank.Add i |> ignore
                inBlock <- not (m.TrimEnd().EndsWith ")")
            elif reImportOne.IsMatch lines.[i] && Regex.IsMatch(m, "^\\s*import\\b") then
                let s = reImportOne.Match lines.[i]
                imports.Add((s.Groups.[1].Value, s.Groups.[2].Value, i + 1))
                blank.Add i |> ignore
            elif reMain.IsMatch m then
                hasMain <- true

        { File = file
          Package = pkg
          HasMain = hasMain
          Lines = lines
          Imports = List.ofSeq imports
          Blank = Set.ofSeq blank }

    let bundle (project: Project) (entry: ProjectFile) : Result<Bundle, BundleProblem list> =
        let dir = ProjectPath.parent entry.Path
        let main = parse entry
        let notes = ResizeArray<string>()

        let siblings =
            project.Files
            |> List.filter (fun f ->
                ProjectPath.extensionOf f.Path = "go"
                && ProjectPath.parent f.Path = dir
                && ProjectPath.key f.Path <> ProjectPath.key entry.Path
                && not (f.Path.EndsWith "_test.go"))
            |> List.map parse
            |> List.filter (fun p ->
                if p.Package <> main.Package then
                    notes.Add("left out " + p.File.Path + ": package " + p.Package + " is not " + main.Package)
                    false
                elif p.HasMain && main.HasMain then
                    notes.Add("left out " + p.File.Path + ": it has its own main")
                    false
                else
                    true)

        let all = main :: siblings

        // import paths that point into the project itself cannot be resolved: there is no module to build them from
        let modulePath =
            project.Files
            |> List.tryFind (fun f -> ProjectPath.name f.Path = "go.mod")
            |> Option.bind (fun f ->
                let m = Regex("^module\\s+(\\S+)", RegexOptions.Multiline).Match f.Text
                if m.Success then Some m.Groups.[1].Value else None)

        let problems =
            [ for p in all do
                  for (_, spec, line) in p.Imports do
                      match modulePath with
                      | Some mp when spec = mp || spec.StartsWith(mp + "/") ->
                          yield
                              problem
                                  p.File.Path
                                  line
                                  ("\""
                                   + spec
                                   + "\" is a package of this project; only package "
                                   + main.Package
                                   + " in one folder can be bundled, put that code next to the entry")
                      | _ -> () ]

        if not problems.IsEmpty then
            Error problems
        elif siblings.IsEmpty then
            Ok(alone Go entry (List.ofSeq notes))
        else
            let specs =
                all
                |> List.collect (fun p -> p.Imports)
                |> List.map (fun (alias, spec, _) ->
                    if alias = "" then
                        "\"" + spec + "\""
                    else
                        alias + " \"" + spec + "\"")
                |> List.distinct

            let header =
                [ "package " + main.Package ]
                @ (if specs.IsEmpty then
                       []
                   else
                       [ "import (" ] @ (specs |> List.map (fun s -> "\t" + s)) @ [ ")" ])

            let pieces =
                [ generated header ]
                @ [ for p in all ->
                        fromFile
                            p.File.Path
                            [ for i in 0 .. p.Lines.Length - 1 -> if p.Blank.Contains i then "" else p.Lines.[i] ] ]

            finish Go entry.Path pieces (all |> List.map (fun p -> p.File.Path)) (List.ofSeq notes)

/// Bundling Java projects: the classes the entry file names are merged into one file, in the default package.
module BundleJava =
    let private rePackage = Regex("^\\s*package\\s+[\\w.]+\\s*;")

    let private reImport =
        Regex("^\\s*import\\s+(static\\s+)?([\\w.]+(?:\\.\\*)?)\\s*;")

    let private rePublic =
        Regex("\\bpublic\\s+((?:(?:abstract|final|sealed|strictfp)\\s+)*(?:class|interface|enum|record)\\b)")

    let private reType = Regex("\\b[A-Z][A-Za-z0-9_]*\\b")

    let private stemOf (path: string) =
        let n = ProjectPath.name path
        n.Substring(0, n.Length - 5)

    let bundle (project: Project) (entry: ProjectFile) : Result<Bundle, BundleProblem list> =
        let javas =
            project.Files |> List.filter (fun f -> ProjectPath.extensionOf f.Path = "java")

        let byStem = javas |> List.groupBy (fun f -> stemOf f.Path) |> Map.ofList
        let problems = ResizeArray<BundleProblem>()
        let order = ResizeArray<ProjectFile>()
        let texts = Dictionary<string, string>()
        let maskedOf = Dictionary<string, string>()

        let rec visit (file: ProjectFile) =
            if
                not (
                    order
                    |> Seq.exists (fun f -> ProjectPath.key f.Path = ProjectPath.key file.Path)
                )
            then
                order.Add file
                let text = file.Text.Replace("\r\n", "\n").Replace('\r', '\n')
                let masked = Scan.mask JavaLike text
                texts.[file.Path] <- text
                maskedOf.[file.Path] <- masked
                let own = stemOf file.Path

                for name in
                    reType.Matches masked
                    |> Seq.cast<Match>
                    |> Seq.map (fun m -> m.Value)
                    |> Seq.distinct do
                    if name <> own then
                        match Map.tryFind name byStem with
                        | None -> ()
                        | Some [ one ] -> visit one
                        | Some many ->
                            let near =
                                many
                                |> List.filter (fun f -> ProjectPath.parent f.Path = ProjectPath.parent file.Path)

                            match near with
                            | [ one ] -> visit one
                            | _ ->
                                problems.Add(
                                    problem
                                        file.Path
                                        0
                                        ("`"
                                         + name
                                         + "` could be "
                                         + String.concat " or " (many |> List.map (fun f -> f.Path))
                                         + "; keep one of them or move one next to "
                                         + file.Path)
                                )

        visit entry

        if problems.Count > 0 then
            Error(List.ofSeq problems)
        elif order.Count = 1 then
            Ok(alone Java entry [])
        else
            let stems = order |> Seq.map (fun f -> stemOf f.Path) |> Set.ofSeq
            let hoisted = ResizeArray<string>()

            let pieces =
                [ for f in order ->
                      let lines = bodyLines texts.[f.Path] |> List.toArray
                      let masked = bodyLines maskedOf.[f.Path] |> List.toArray
                      let depth = Scan.depthAtLineStarts (String.concat "\n" (List.ofArray masked))
                      let isEntry = (ProjectPath.key f.Path = ProjectPath.key entry.Path)

                      let out =
                          [ for i in 0 .. lines.Length - 1 ->
                                let m = masked.[i]

                                if rePackage.IsMatch m then
                                    ""
                                else
                                    let im = reImport.Match m

                                    if im.Success then
                                        let path = im.Groups.[2].Value
                                        let own = path.Split('.') |> Array.exists (fun seg -> stems.Contains seg)

                                        if not own then
                                            let line =
                                                (if im.Groups.[1].Success then
                                                     "import static "
                                                 else
                                                     "import ")
                                                + path
                                                + ";"

                                            if not (hoisted.Contains line) then
                                                hoisted.Add line

                                        ""
                                    elif not isEntry && depth.[i] = 0 then
                                        let pm = rePublic.Match m

                                        if pm.Success then
                                            lines.[i].Remove(pm.Index, "public ".Length)
                                        else
                                            lines.[i]
                                    else
                                        lines.[i] ]

                      fromFile f.Path out ]

            let pieces = generated (List.ofSeq hoisted) :: pieces

            let notes =
                let left =
                    javas
                    |> List.filter (fun f -> not (order |> Seq.exists (fun o -> o.Path = f.Path)))
                    |> List.map (fun f -> f.Path)

                if left.IsEmpty then
                    []
                else
                    [ "not reached from " + entry.Path + ": " + String.concat ", " left ]

            finish Java entry.Path pieces (order |> Seq.map (fun f -> f.Path) |> List.ofSeq) notes

/// Bundling C and C++ projects: `#include "x.h"` becomes the text of x.h, once, and the matching x.c / x.cpp follows.
module BundleC =
    let private reInclude = Regex("^(\\s*)#\\s*include\\s*\"([^\"]+)\"(.*)$")
    let private rePragma = Regex("^\\s*#\\s*pragma\\s+once\\b")

    let private headerExts = [ "h"; "hpp"; "hh"; "hxx" ]

    let bundle (project: Project) (entry: ProjectFile) (lang: Language) : Result<Bundle, BundleProblem list> =
        let files = index project
        let dialect = CLike
        let included = ResizeArray<string>()
        let notes = ResizeArray<string>()
        let problems = ResizeArray<BundleProblem>()
        let implementations = Queue<ProjectFile>()
        let implExts = if lang = C then [ "c" ] else [ "cpp"; "cc"; "cxx" ]
        let entryDir = ProjectPath.parent entry.Path

        let rec expand (file: ProjectFile) (isEntry: bool) (depth: int) : Piece list =
            included.Add file.Path
            let text = file.Text.Replace("\r\n", "\n").Replace('\r', '\n')
            let lines = bodyLines text |> List.toArray
            let masked = bodyLines (Scan.mask dialect text) |> List.toArray
            let dir = ProjectPath.parent file.Path
            let pieces = ResizeArray<Piece>()
            let runStart = ref 0

            let flush (upTo: int) =
                if upTo > runStart.Value then
                    pieces.Add
                        { Path = file.Path
                          From = runStart.Value + 1
                          Lines = [ for k in runStart.Value .. upTo - 1 -> lines.[k] ] }

            for i in 0 .. lines.Length - 1 do
                let m = reInclude.Match lines.[i]

                if rePragma.IsMatch masked.[i] && not isEntry then
                    // `#pragma once` in the middle of the entry file would only draw a warning
                    flush i
                    runStart.Value <- i + 1

                    pieces.Add
                        { Path = file.Path
                          From = i + 1
                          Lines = [ "" ] }
                elif m.Success && masked.[i].TrimStart().StartsWith "#" then
                    let spec = m.Groups.[2].Value

                    let target =
                        [ resolveRel dir spec; resolveRel entryDir spec; resolveRel "" spec ]
                        |> List.choose id
                        |> List.tryPick (find files)

                    match target with
                    | None ->
                        notes.Add(
                            "#include \""
                            + spec
                            + "\" in "
                            + file.Path
                            + " is left as it is: no such file in the project"
                        )
                    | Some t ->
                        flush i
                        runStart.Value <- i + 1

                        if included.Contains t.Path then
                            ()
                        elif depth >= 24 then
                            problems.Add(problem file.Path (i + 1) "includes are nested more than 24 deep")
                        else
                            pieces.AddRange(expand t false (depth + 1))

                            // a header's implementation file is part of the program: x.h pulls in x.c
                            if List.contains (ProjectPath.extensionOf t.Path) headerExts then
                                let stem = t.Path.Substring(0, t.Path.LastIndexOf '.')

                                match implExts |> List.tryPick (fun e -> find files (stem + "." + e)) with
                                | Some impl when
                                    not (included.Contains impl.Path)
                                    && not (implementations |> Seq.exists (fun f -> f.Path = impl.Path))
                                    ->
                                    implementations.Enqueue impl
                                | _ -> ()

            flush lines.Length
            List.ofSeq pieces

        let head = expand entry true 0
        let tail = ResizeArray<Piece>()

        while implementations.Count > 0 do
            let impl = implementations.Dequeue()

            if not (included.Contains impl.Path) then
                tail.AddRange(expand impl false 1)

        if problems.Count > 0 then
            Error(List.ofSeq problems)
        elif included.Count = 1 then
            Ok(alone lang entry (List.ofSeq notes))
        else
            let others =
                project.Files
                |> List.filter (fun f ->
                    ProjectPath.languageOf f.Path = Some lang
                    && ProjectPath.extensionOf f.Path <> "h"
                    && ProjectPath.extensionOf f.Path <> "hpp"
                    && not (included.Contains f.Path))
                |> List.map (fun f -> f.Path)

            if not others.IsEmpty then
                notes.Add("not reached from " + entry.Path + ": " + String.concat ", " others)

            finish lang entry.Path (head @ List.ofSeq tail) (List.ofSeq included) (List.ofSeq notes)
