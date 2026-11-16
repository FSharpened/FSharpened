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
              "file" ==> Language.fileName l ])
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
