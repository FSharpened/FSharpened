/// JavaScript-facing surface of the IDE core. Everything crossing this boundary is a plain object, array or string.
module FSharpened.Ide.Web.Api

open Fable.Core
open Fable.Core.JsInterop
open FSharpened.Diagnostics
open FSharpened.Ide

let private severity =
    function
    | Severity.Error -> "error"
    | Severity.Warning -> "warning"
    | Severity.Info -> "info"
    | Severity.Hidden -> "hidden"

let private diagnostic (d: StructuredDiagnostic) : obj =
    let r = d.Range

    createObj
        [ "code" ==> d.Code
          "severity" ==> severity d.Severity
          "message" ==> d.Message
          "line" ==> (r |> Option.map (fun x -> x.StartLine) |> Option.defaultValue 0)
          "column" ==> (r |> Option.map (fun x -> x.StartColumn) |> Option.defaultValue 0)
          "endLine" ==> (r |> Option.map (fun x -> x.EndLine) |> Option.defaultValue 0)
          "endColumn"
          ==> (r |> Option.map (fun x -> x.EndColumn) |> Option.defaultValue 0)
          "fix" ==> (d.SuggestedFix |> Option.defaultValue "") ]

let private compiler (c: Compiler) : obj =
    createObj
        [ "id" ==> c.Id
          "label" ==> c.Label
          "language" ==> Language.id c.Language
          "defaultOptions" ==> c.DefaultOptions ]

let private langOf (id: string) =
    Language.tryOfId id |> Option.defaultValue FSharp

let languages () : obj[] =
    Language.all
    |> List.map (fun l ->
        createObj
            [ "id" ==> Language.id l
              "name" ==> Language.displayName l
              "file" ==> Language.fileName l
              "group" ==> Language.group l
              "flags" ==> Language.takesFlags l ])
    |> List.toArray

let compilers (languageId: string) : obj[] =
    Compilers.forLanguage (langOf languageId) |> List.map compiler |> List.toArray

let templates () : obj[] =
    Templates.all
    |> List.map (fun t ->
        createObj
            [ "key" ==> t.Key
              "title" ==> t.Title
              "language" ==> Language.id t.Language
              "code" ==> t.Code ])
    |> List.toArray

let requestJson (compilerId: string) (options: string) (source: string) (stdin: string) : string =
    let c =
        Compilers.tryFind compilerId
        |> Option.defaultValue (Compilers.defaultFor FSharp)

    Protocol.requestJson c options source stdin

let fingerprint (parts: string[]) : string =
    Protocol.fingerprint (List.ofArray parts)

let localChecks (languageId: string) (text: string) : obj[] =
    Problems.localChecks (langOf languageId) text
    |> List.map diagnostic
    |> List.toArray

/// Ordered degradation chain as plain steps: { kind: "compiler" | "warm" | "local" | "escalate", ... }.
let chain (languageId: string) (compilerId: string) : obj[] =
    Fallback.chain (langOf languageId) compilerId
    |> List.map (function
        | TryCompiler c -> createObj [ "kind" ==> "compiler"; "compiler" ==> compiler c ]
        | WarmResult -> createObj [ "kind" ==> "warm" ]
        | LocalChecks -> createObj [ "kind" ==> "local" ]
        | Escalate m -> createObj [ "kind" ==> "escalate"; "message" ==> m ])
    |> List.toArray

let private report (r: Report) : obj =
    createObj
        [ "ok" ==> r.Ok
          "phase"
          ==> (match r.Phase with
               | Build -> "build"
               | Run -> "run"
               | Infrastructure -> "infrastructure")
          "summary" ==> r.Summary
          "stdout" ==> r.Stdout
          "stderr" ==> r.Stderr
          "exitCode" ==> r.ExitCode
          "diagnostics" ==> (r.Diagnostics |> List.map diagnostic |> List.toArray) ]

/// raw: { engine, buildFailed, buildOutput, stdout, stderr, exitCode, timedOut, durationMs, cached }
let interpret (languageId: string) (raw: obj) : obj =
    report (
        Protocol.interpret
            (langOf languageId)
            { Engine = unbox<string> raw?engine
              BuildFailed = unbox<bool> raw?buildFailed
              BuildOutput = unbox<string> raw?buildOutput
              Stdout = unbox<string> raw?stdout
              Stderr = unbox<string> raw?stderr
              ExitCode = unbox<int> raw?exitCode
              TimedOut = unbox<bool> raw?timedOut
              DurationMs = unbox<int> raw?durationMs
              Cached = unbox<bool> raw?cached }
    )

let infrastructure (message: string) (localDiagnostics: obj[]) : obj =
    report (Protocol.infrastructure message [])
    |> fun r ->
        r?diagnostics <- localDiagnostics
        r

// ---- workspace <-> plain object ------------------------------------------------------------------
let private workspaceOf (o: obj) : Workspace =
    let buffers =
        Language.all
        |> List.fold
            (fun w l ->
                let t: string = o?buffers?(Language.id l)

                if isNull (box t) then w else Workspace.withText l t w)
            Workspace.initial

    buffers
    |> Workspace.activate (langOf (unbox<string> o?active))
    |> Workspace.withStdin (let s: string = o?stdin in if isNull (box s) then "" else s)

let private workspaceObj (w: Workspace) : obj =
    let buffers = createObj []

    for l in Language.all do
        buffers?(Language.id l) <- Workspace.text l w

    createObj
        [ "active" ==> Language.id w.Active
          "buffers" ==> buffers
          "stdin" ==> w.Stdin ]

let initialWorkspace () : obj = workspaceObj Workspace.initial

let diffSummary (before: string) (after: string) : obj[] =
    Diff.lines before after
    |> List.map (fun l ->
        createObj
            [ "kind"
              ==> (match l.Kind with
                   | Same -> "same"
                   | Added -> "added"
                   | Removed -> "removed")
              "text" ==> l.Text ])
    |> List.toArray

// ---- projects (ADR-0021) -------------------------------------------------------------------------
// A project crosses the boundary as { name, files: [{ path, text }], entry: string | null, pipeline: [path] }.
let private projectOf (o: obj) : Project =
    let files: obj[] = o?files
    let entry: string = o?entry
    let pipeline: string[] = o?pipeline

    { Name = unbox<string> o?name
      Files =
        files
        |> Array.map (fun f ->
            { Path = unbox<string> f?path
              Text = unbox<string> f?text })
        |> Array.toList
      Entry = if isNull (box entry) then None else Some entry
      Pipeline = if isNull (box pipeline) then [] else Array.toList pipeline }

let private projectObj (p: Project) : obj =
    createObj
        [ "name" ==> p.Name
          "files"
          ==> (p.Files
               |> List.map (fun f -> createObj [ "path" ==> f.Path; "text" ==> f.Text ])
               |> List.toArray)
          "entry" ==> Option.toObj p.Entry
          "pipeline" ==> List.toArray p.Pipeline ]

let private projectResult (r: Result<Project, string>) : obj =
    match r with
    | Ok p -> createObj [ "ok" ==> true; "project" ==> projectObj p; "error" ==> "" ]
    | Result.Error e -> createObj [ "ok" ==> false; "project" ==> null; "error" ==> e ]

let projectCreate (name: string) : obj = projectObj (Project.create name)

/// The project with its name cleaned the way every project name is: trimmed, no path separators, at most 80 characters.
let projectRename (project: obj) (name: string) : obj =
    projectObj
        { projectOf project with
            Name = Project.cleanName name }

/// The limits the model enforces, so the page can warn before an edit would be refused: { files, fileChars, totalChars, pipeline }.
let projectLimits () : obj =
    createObj
        [ "files" ==> Project.maxFiles
          "fileChars" ==> Project.maxFileChars
          "totalChars" ==> Project.maxTotalChars
          "pipeline" ==> Project.maxPipeline ]

let projectAddFile (project: obj) (path: string) (text: string) : obj =
    projectResult (Project.addFile path text (projectOf project))

let projectSetText (project: obj) (path: string) (text: string) : obj =
    projectResult (Project.setText path text (projectOf project))

let projectRemoveFile (project: obj) (path: string) : obj =
    projectResult (Project.removeFile path (projectOf project))

let projectRemoveFolder (project: obj) (path: string) : obj =
    projectResult (Project.removeFolder path (projectOf project))

let projectMove (project: obj) (fromPath: string) (toPath: string) : obj =
    projectResult (Project.move fromPath toPath (projectOf project))

let projectSetEntry (project: obj) (path: string) : obj =
    projectResult (Project.setEntry path (projectOf project))

let projectSetPipeline (project: obj) (stages: string[]) : obj =
    projectResult (Project.setPipeline (Array.toList stages) (projectOf project))

/// Rows of the file tree in display order: { path, name, depth, isFolder, language } (language is "" for text files).
let projectListing (project: obj) : obj[] =
    Project.listing (projectOf project)
    |> List.map (fun r ->
        createObj
            [ "path" ==> r.Path
              "name" ==> r.Name
              "depth" ==> r.Depth
              "isFolder" ==> r.IsFolder
              "language" ==> (r.Language |> Option.map Language.id |> Option.defaultValue "") ])
    |> List.toArray

let projectIssues (project: obj) : string[] =
    Project.issues (projectOf project) |> List.toArray

/// The path a plain Run uses, or "" for an empty project.
let projectEntry (project: obj) : string =
    Project.entryFile (projectOf project)
    |> Option.map (fun f -> f.Path)
    |> Option.defaultValue ""

let projectLanguages (project: obj) : string[] =
    Project.languages (projectOf project) |> List.map Language.id |> List.toArray

/// buffers: { languageId: text }. Used once, to carry the single-language buffers of an older workspace into a project.
let projectFromBuffers (name: string) (active: string) (buffers: obj) : obj =
    let pairs =
        Language.all
        |> List.choose (fun l ->
            let t: string = buffers?(Language.id l)
            if isNull (box t) then None else Some(l, t))

    projectObj (Project.ofBuffers name (langOf active) pairs)

/// entries: [{ path, text }]. Returns { project, skipped: [{ path, reason }] }.
let projectImport (name: string) (entries: obj[]) (entry: string) (pipeline: string[]) : obj =
    let r =
        Project.ofEntries
            name
            (entries
             |> Array.map (fun e -> unbox<string> e?path, unbox<string> e?text)
             |> Array.toList)
            (if isNull (box entry) || entry = "" then
                 None
             else
                 Some entry)
            (if isNull (box pipeline) then [] else Array.toList pipeline)

    createObj
        [ "project" ==> projectObj r.Project
          "skipped"
          ==> (r.Skipped
               |> List.map (fun (p, why) -> createObj [ "path" ==> p; "reason" ==> why ])
               |> List.toArray) ]

/// { ok, path, error }: the one spelling the project uses for a path, or why it was refused.
let pathNormalize (raw: string) : obj =
    match ProjectPath.normalize raw with
    | Ok p -> createObj [ "ok" ==> true; "path" ==> p; "error" ==> "" ]
    | Result.Error e -> createObj [ "ok" ==> false; "path" ==> ""; "error" ==> e ]

/// The language id for a file name, or "" for a text or data file.
let pathLanguage (path: string) : string =
    ProjectPath.languageOf path |> Option.map Language.id |> Option.defaultValue ""

let manifestName = ProjectPath.manifestName

// ---- editor features (ADR-0022) -------------------------------------------------------------------
// Everything here takes and returns plain objects, like the rest of this module. A value that is missing or of the
// wrong type falls back to a default instead of throwing: the page may hand over data it read from storage or a file.
let private asString (v: obj) (fallback: string) : string =
    if jsTypeof v = "string" then unbox<string> v else fallback

let private asBool (v: obj) (fallback: bool) : bool =
    if jsTypeof v = "boolean" then unbox<bool> v else fallback

let private asInt (v: obj) (fallback: int) : int =
    if jsTypeof v = "number" then
        int (unbox<float> v)
    else
        fallback

let private asStrings (v: obj) : string list =
    if isNull v then
        []
    else
        unbox<obj[]> v
        |> Array.filter (fun x -> jsTypeof x = "string")
        |> Array.map unbox<string>
        |> Array.toList

let private asObjects (v: obj) : obj[] =
    if isNull v then [||] else unbox<obj[]> v

/// [{ index, text, score, positions: [int] }], best first. An empty query keeps the order.
let fuzzyRank (query: string) (items: string[]) (limit: int) : obj[] =
    Fuzzy.rank query (Array.toList items) limit
    |> List.map (fun r ->
        createObj
            [ "index" ==> r.Index
              "text" ==> r.Text
              "score" ==> r.Score
              "positions" ==> List.toArray r.Positions ])
    |> List.toArray

/// null when the query does not match, else { score, positions }.
let fuzzyScore (query: string) (text: string) : obj =
    match Fuzzy.score query text with
    | Some m -> createObj [ "score" ==> m.Score; "positions" ==> List.toArray m.Positions ]
    | None -> null

/// { mode: "file" | "line" | "symbol" | "command", text, line, column }
let quickParse (raw: string) : obj =
    let q = Quick.parse raw

    createObj
        [ "mode" ==> q.Mode
          "text" ==> q.Text
          "line" ==> q.Line
          "column" ==> q.Column ]

let private searchOf (o: obj) : SearchOptions =
    { Query = asString o?query ""
      CaseSensitive = asBool o?caseSensitive false
      WholeWord = asBool o?wholeWord false
      Regex = asBool o?regex false }

let private hitObj (h: SearchHit) : obj =
    createObj
        [ "line" ==> h.Line
          "column" ==> h.Column
          "length" ==> h.Length
          "preview" ==> h.Preview
          "previewStart" ==> h.PreviewStart ]

/// Searches a project. options: { query, caseSensitive, wholeWord, regex }. Returns
/// { files: [{ path, more, hits: [{ line, column, length, preview, previewStart }] }], total, filesSearched, truncated, error }.
let searchRun (project: obj) (options: obj) (includes: string) (excludes: string) : obj =
    let p = projectOf project

    let r =
        Search.run
            (searchOf options)
            (if isNull (box includes) then "" else includes)
            (if isNull (box excludes) then "" else excludes)
            (p.Files |> List.map (fun f -> f.Path, f.Text))

    createObj
        [ "files"
          ==> (r.Files
               |> List.map (fun f ->
                   createObj
                       [ "path" ==> f.Path
                         "more" ==> f.More
                         "hits" ==> (f.Hits |> List.map hitObj |> List.toArray) ])
               |> List.toArray)
          "total" ==> r.Total
          "filesSearched" ==> r.FilesSearched
          "truncated" ==> r.Truncated
          "error" ==> r.Error ]

/// { ok, text, count, error }
let searchReplaceText (options: obj) (replacement: string) (text: string) : obj =
    match Search.replaceInText (searchOf options) replacement text with
    | Ok(t, n) -> createObj [ "ok" ==> true; "text" ==> t; "count" ==> n; "error" ==> "" ]
    | Result.Error e -> createObj [ "ok" ==> false; "text" ==> text; "count" ==> 0; "error" ==> e ]

/// { ok, text, error }: only the match that starts at (line, column) is replaced.
let searchReplaceOne (options: obj) (replacement: string) (text: string) (line: int) (column: int) : obj =
    match Search.replaceOne (searchOf options) replacement text line column with
    | Ok t -> createObj [ "ok" ==> true; "text" ==> t; "error" ==> "" ]
    | Result.Error e -> createObj [ "ok" ==> false; "text" ==> text; "error" ==> e ]

/// Replaces in the listed files. Returns { project, changed: [{ path, count }], failed: [{ path, reason }] }.
let searchReplaceProject (project: obj) (options: obj) (replacement: string) (paths: string[]) : obj =
    let r =
        Search.replaceInProject (searchOf options) replacement (Array.toList paths) (projectOf project)

    createObj
        [ "project" ==> projectObj r.Project
          "changed"
          ==> (r.Changed
               |> List.map (fun (p, n) -> createObj [ "path" ==> p; "count" ==> n ])
               |> List.toArray)
          "failed"
          ==> (r.Failed
               |> List.map (fun (p, why) -> createObj [ "path" ==> p; "reason" ==> why ])
               |> List.toArray) ]

let private symbolObj (s: Symbol) : obj =
    createObj
        [ "name" ==> s.Name
          "kind" ==> s.Kind
          "line" ==> s.Line
          "column" ==> s.Column
          "endLine" ==> s.EndLine
          "depth" ==> s.Depth
          "container" ==> s.Container ]

let private symbolOf (o: obj) : Symbol =
    { Name = asString o?name ""
      Kind = asString o?kind ""
      Line = asInt o?line 0
      Column = asInt o?column 1
      EndLine = asInt o?endLine 0
      Depth = asInt o?depth 0
      Container = asString o?container "" }

/// The outline of a file, by its extension: [{ name, kind, line, column, endLine, depth, container }].
let symbolsOutline (path: string) (text: string) : obj[] =
    Symbols.outlineOfPath path text |> List.map symbolObj |> List.toArray

/// The symbols (from `symbolsOutline`) that contain a line, outermost first.
let symbolsEnclosing (symbols: obj[]) (line: int) : obj[] =
    Symbols.enclosing (symbols |> Array.map symbolOf |> Array.toList) line
    |> List.map symbolObj
    |> List.toArray

/// Where a name is declared in a project: [{ path, name, kind, line, column }].
let symbolsDefinitions (project: obj) (name: string) : obj[] =
    let p = projectOf project

    Symbols.definitions name (p.Files |> List.map (fun f -> f.Path, f.Text))
    |> List.map (fun d ->
        createObj
            [ "path" ==> d.Path
              "name" ==> d.Symbol.Name
              "kind" ==> d.Symbol.Kind
              "line" ==> d.Symbol.Line
              "column" ==> d.Symbol.Column ])
    |> List.toArray

/// The identifier under a 1-based column of a line, or "".
let wordAt (line: string) (column: int) : string = Symbols.wordAt line column

let private snippetObj (s: Snippet) : obj =
    createObj
        [ "label" ==> s.Label
          "detail" ==> s.Detail
          "body" ==> s.Body
          "languages" ==> List.toArray s.Languages ]

let private snippetOf (o: obj) : Snippet =
    { Label = asString o?label ""
      Detail = asString o?detail ""
      Body = asString o?body ""
      Languages = asStrings o?languages }

/// Reads snippets from VS Code's format. defs: [{ name, prefixes: [string], body: string, description, scope: [string] }].
let snippetsImport (defs: obj[]) : obj[] =
    defs
    |> Array.map (fun d ->
        { Name = asString d?name ""
          Prefixes = asStrings d?prefixes
          Body = asString d?body ""
          Description = asString d?description ""
          Scope = asStrings d?scope })
    |> Array.toList
    |> Snippets.ofDefs
    |> List.map snippetObj
    |> List.toArray

/// The snippets offered in a language: the built-in ones plus the imported ones (from `snippetsImport`) that apply.
let snippetsFor (languageId: string) (imported: obj[]) : obj[] =
    Snippets.forLanguage languageId (imported |> Array.map snippetOf |> Array.toList)
    |> List.map snippetObj
    |> List.toArray

/// A snippet body as an editor template: { template, warnings: [string] }. context: { fileName, filePath, folder,
/// selected, projectName, year, month, day, lineComment }.
let snippetTemplate (body: string) (context: obj) : obj =
    let r =
        Snippets.template
            body
            { FileName = asString context?fileName ""
              FilePath = asString context?filePath ""
              Folder = asString context?folder ""
              Selected = asString context?selected ""
              ProjectName = asString context?projectName ""
              Year = asInt context?year 1970
              Month = asInt context?month 1
              Day = asInt context?day 1
              LineComment = asString context?lineComment "//" }

    createObj [ "template" ==> r.Template; "warnings" ==> List.toArray r.Warnings ]

let private themeObj (t: ThemeSpec) : obj =
    createObj
        [ "id" ==> t.Id
          "name" ==> t.Name
          "scheme" ==> t.Scheme
          "contrast" ==> t.Contrast
          "vars" ==> createObj (t.Vars |> List.map (fun (k, v) -> k ==> v)) ]

/// The built-in themes: [{ id, name, scheme, contrast, vars: { name: "#rrggbb" } }]. The first, "auto", sets nothing.
let themes () : obj[] =
    Themes.builtin |> List.map themeObj |> List.toArray

/// Converts a VS Code theme. vs: { name, type, colors: [{ key, value }], tokenColors: [{ scopes: [string], foreground }] }.
/// Returns { theme, warnings: [string] }.
let themeImport (vs: obj) : obj =
    let r =
        Themes.ofVsCode
            { Name = asString vs?name ""
              Type = asString vs?``type`` ""
              Colors =
                asObjects vs?colors
                |> Array.map (fun c -> asString c?key "", asString c?value "")
                |> Array.toList
              TokenColors =
                asObjects vs?tokenColors
                |> Array.map (fun t ->
                    { Scopes = asStrings t?scopes
                      Foreground = asString t?foreground "" })
                |> Array.toList }

    createObj [ "theme" ==> themeObj r.Theme; "warnings" ==> List.toArray r.Warnings ]

/// WCAG contrast ratio of two #rrggbb colours (0 when either is not a valid colour).
let themeContrast (a: string) (b: string) : float =
    match Themes.normalizeColor a, Themes.normalizeColor b with
    | Some x, Some y -> Themes.contrast x y
    | _ -> 0.0

/// `#rrggbb` for any VS Code colour spelling, or "" when it is not hex.
let themeColor (raw: string) : string =
    Themes.normalizeColor raw |> Option.defaultValue ""

let private settingsObj (s: EditorSettings) : obj =
    createObj
        [ "theme" ==> s.Theme
          "fontSize" ==> s.FontSize
          "tabSize" ==> s.TabSize
          "insertSpaces" ==> s.InsertSpaces
          "wordWrap" ==> s.WordWrap
          "lineNumbers" ==> s.LineNumbers
          "renderWhitespace" ==> s.RenderWhitespace
          "autoCloseBrackets" ==> s.AutoCloseBrackets
          "breadcrumbs" ==> s.Breadcrumbs ]

// Each field that is missing or has the wrong type keeps its default.
let private settingsOf (o: obj) : EditorSettings =
    let d = Settings.defaults

    if isNull o then
        d
    else
        Settings.sanitize
            { Theme = asString o?theme d.Theme
              FontSize = asInt o?fontSize d.FontSize
              TabSize = asInt o?tabSize d.TabSize
              InsertSpaces = asBool o?insertSpaces d.InsertSpaces
              WordWrap = asBool o?wordWrap d.WordWrap
              LineNumbers = asBool o?lineNumbers d.LineNumbers
              RenderWhitespace = asBool o?renderWhitespace d.RenderWhitespace
              AutoCloseBrackets = asBool o?autoCloseBrackets d.AutoCloseBrackets
              Breadcrumbs = asBool o?breadcrumbs d.Breadcrumbs }

let settingsDefaults () : obj = settingsObj Settings.defaults

/// Brings any object into a valid settings object: wrong types fall back, numbers are clamped, the theme id is cleaned.
let settingsSanitize (o: obj) : obj = settingsObj (settingsOf o)

/// { fontMin, fontMax, tabMin, tabMax }
let settingsLimits () : obj =
    createObj
        [ "fontMin" ==> Settings.fontMin
          "fontMax" ==> Settings.fontMax
          "tabMin" ==> Settings.tabMin
          "tabMax" ==> Settings.tabMax ]

/// Reads a parsed VS Code settings.json. pairs: [{ key, value }]. Returns { settings, applied: [key], ignored: [key] }.
let settingsFromVsCode (current: obj) (pairs: obj[]) : obj =
    let values =
        pairs
        |> Array.choose (fun p ->
            let key = asString p?key ""
            let v: obj = p?value

            match jsTypeof v with
            | "number" -> Some(key, VNum(unbox<float> v))
            | "string" -> Some(key, VText(unbox<string> v))
            | "boolean" -> Some(key, VFlag(unbox<bool> v))
            | _ -> None)
        |> Array.toList

    let r = Settings.ofVsCode (settingsOf current) values

    createObj
        [ "settings" ==> settingsObj r.Settings
          "applied" ==> List.toArray r.Applied
          "ignored" ==> List.toArray r.Ignored ]

/// The extension ids VS Code is told to recommend for a project, Copilot last.
let vsCodeRecommendations (project: obj) : string[] =
    VsCode.recommendations (projectOf project) |> List.toArray

/// The files to add to a project so it opens well in VS Code: [{ path, text }]. Files the project has are not listed.
let vsCodeFiles (project: obj) (settings: obj) : obj[] =
    VsCode.files (projectOf project) (settingsOf settings)
    |> List.map (fun (p, t) -> createObj [ "path" ==> p; "text" ==> t ])
    |> List.toArray

/// Statepoint-backed history. One per browser tab. AttachMembers keeps method names intact for JavaScript callers.
[<AttachMembers>]
type Session(initial: obj) =
    let timeline = Timeline(workspaceOf initial)

    member _.snapshot(label: string, ws: obj) : string =
        timeline.Snapshot(label, workspaceOf ws)

    member _.restore(id: string) : obj =
        match timeline.Restore id with
        | Some w -> workspaceObj w
        | None -> null

    member _.read(id: string) : obj =
        match timeline.Read id with
        | Some w -> workspaceObj w
        | None -> null

    member _.fork(id: string, name: string) : string =
        match timeline.Fork(id, name) with
        | Some i -> i
        | None -> ""

    member _.headId: string = timeline.HeadId

    member _.entries() : obj[] =
        timeline.Entries
        |> List.map (fun e ->
            createObj
                [ "id" ==> e.Id
                  "label" ==> e.Label
                  "parent" ==> (e.Parent |> Option.defaultValue "")
                  "branch" ==> e.Branch
                  "createdMs" ==> e.CreatedMs
                  "isHead" ==> e.IsHead ])
        |> List.toArray

// ---- whole-project history (ADR-0021) ---------------------------------------------------------------
let private stateOf (o: obj) : ProjectState =
    let active: string = o?active
    let stdin: string = o?stdin

    { Project = projectOf o?project
      Active = if isNull (box active) then "" else active
      Stdin = if isNull (box stdin) then "" else stdin }

let private stateObj (s: ProjectState) : obj =
    createObj
        [ "project" ==> projectObj s.Project
          "active" ==> s.Active
          "stdin" ==> s.Stdin ]

/// The same statepoint history as `Session`, but each statepoint holds a whole project: every file, the entry, the
/// pipeline, the open file and the input. The state crosses the boundary as { project, active, stdin }.
[<AttachMembers>]
type ProjectSession(initial: obj) =
    let timeline = ProjectTimeline(stateOf initial)

    member _.snapshot(label: string, state: obj) : string = timeline.Snapshot(label, stateOf state)

    member _.restore(id: string) : obj =
        match timeline.Restore id with
        | Some s -> stateObj s
        | None -> null

    member _.read(id: string) : obj =
        match timeline.Read id with
        | Some s -> stateObj s
        | None -> null

    member _.fork(id: string, name: string) : string =
        match timeline.Fork(id, name) with
        | Some i -> i
        | None -> ""

    member _.headId: string = timeline.HeadId

    member _.entries() : obj[] =
        timeline.Entries
        |> List.map (fun e ->
            createObj
                [ "id" ==> e.Id
                  "label" ==> e.Label
                  "parent" ==> (e.Parent |> Option.defaultValue "")
                  "branch" ==> e.Branch
                  "createdMs" ==> e.CreatedMs
                  "isHead" ==> e.IsHead ])
        |> List.toArray
