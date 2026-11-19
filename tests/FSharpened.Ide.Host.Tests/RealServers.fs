/// Checks of the language-server client against REAL servers (gopls, rust-analyzer, pyright, ...), opt-in.
///
/// Every test here is SKIPPED, not passed, unless `FSH_REAL_SERVERS=1` is set and the server is installed, so the
/// normal test run never depends on a toolchain. Run them with `python tools/ci/fsh_ci.py run --profile verify`
/// (docs/operations/desktop.md). Each test starts the server the way the app does (`Lsp.start` with the server of the
/// language pack), on a copy of `tests/fixtures/realservers/<id>`, and checks: initialize, a published diagnostic for a
/// deliberately wrong file, completion, go to definition and hover at positions the fixture names.
module RealServers

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading
open Xunit
open FSharpened.Ide.Host

/// Real-server checks are on only when asked for.
let enabled = Environment.GetEnvironmentVariable "FSH_REAL_SERVERS" = "1"

let repoRoot =
    let rec up (d: DirectoryInfo) =
        if isNull d then
            failwith "FSharpened.slnx not found above the test folder"
        elif File.Exists(Path.Combine(d.FullName, "FSharpened.slnx")) then
            d.FullName
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let fixturesDir = Path.Combine(repoRoot, "tests", "fixtures", "realservers")

/// `FSH_REALSERVER_CMD_<NAME>`: a JSON array [command, args...] that replaces the pack's server command. The
/// verification script sets it where the server is a Windows `.cmd` shim, which the host cannot start (known limit).
let overrideFor (server: string) : string list option =
    let name = "FSH_REALSERVER_CMD_" + server.ToUpperInvariant().Replace('-', '_')

    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | json -> Some(JsonSerializer.Deserialize<string list> json)

/// The file a bare program name resolves to on PATH, the way `ProcessStartInfo` resolves it (on Windows only
/// `.exe` and `.com` start without a shell, so a `.cmd` shim does not count).
let onPath (program: string) : string option =
    let suffixes =
        if OperatingSystem.IsWindows() then
            [ ".exe"; ".com" ]
        else
            [ "" ]

    let dirs =
        (Environment.GetEnvironmentVariable "PATH"
         |> Option.ofObj
         |> Option.defaultValue "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)

    [ for d in dirs do
          for s in suffixes do
              Path.Combine(d, program + s) ]
    |> List.tryFind File.Exists

/// Why a server cannot be tested here, or None when it can.
let missing (program: string) (hint: string) : string option =
    match overrideFor program with
    | Some _ -> None
    | None ->
        match onPath program with
        | Some _ -> None
        | None ->
            let shim =
                OperatingSystem.IsWindows()
                && (Environment.GetEnvironmentVariable "PATH"
                    |> Option.ofObj
                    |> Option.defaultValue "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                   |> Array.exists (fun d -> File.Exists(Path.Combine(d, program + ".cmd")))

            Some(
                if shim then
                    sprintf "%s is only a .cmd shim on PATH, which the host cannot start (known limit). %s" program hint
                else
                    sprintf "%s is not installed. %s" program hint
            )

/// A test that is skipped unless real-server checks are enabled and the server is installed.
type RealServerFactAttribute(program: string, hint: string) as this =
    inherit FactAttribute()

    do
        if not enabled then
            this.Skip <-
                "real-server checks are opt-in: set FSH_REAL_SERVERS=1 (python tools/ci/fsh_ci.py run --profile verify)"
        else
            match missing program hint with
            | Some why -> this.Skip <- why
            | None -> ()

type Check =
    { Name: string
      Ok: bool
      Detail: string }

let str (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> ""

let private strings (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Array -> [ for x in v.EnumerateArray() -> x.GetString() ]
    | _ -> []

/// `text|more` in a fixture means: the cursor sits at the bar in the first place the text without the bar occurs.
let positionOf (content: string) (at: string) : LspPos option =
    let bar = at.IndexOf '|'
    let needle = at.Remove(bar, 1)
    let idx = content.IndexOf(needle, StringComparison.Ordinal)

    if idx < 0 then
        None
    else
        let upTo = content.Substring(0, idx + bar)
        let line = upTo |> Seq.filter ((=) '\n') |> Seq.length
        let lastNl = upTo.LastIndexOf '\n'

        Some
            { Line = line
              Character = upTo.Length - (lastNl + 1) }

let private copyDir (src: string) (dst: string) =
    for f in Directory.GetFiles(src, "*", SearchOption.AllDirectories) do
        let target = Path.Combine(dst, Path.GetRelativePath(src, f))
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        File.Copy(f, target, true)

/// Polls `probe` until it gives a value or the time is up (servers answer empty while they still index).
let private eventually (seconds: int) (probe: unit -> 'a option) : 'a option =
    let until = DateTime.UtcNow.AddSeconds(float seconds)
    let mutable result = None

    while result.IsNone && DateTime.UtcNow < until do
        result <- probe ()

        if result.IsNone then
            Thread.Sleep 1000

    result

let private report (id: string) (checks: Check list) =
    match Environment.GetEnvironmentVariable "FSH_REAL_REPORT" with
    | null
    | "" -> ()
    | dir ->
        Directory.CreateDirectory dir |> ignore

        let json =
            JsonSerializer.Serialize(
                {| id = id
                   checks =
                    [ for c in checks ->
                          {| name = c.Name
                             ok = c.Ok
                             detail = c.Detail |} ] |},
                JsonSerializerOptions(WriteIndented = true)
            )

        File.WriteAllText(Path.Combine(dir, id + ".json"), json)

/// Runs the five checks of a fixture against its server and returns each one's outcome.
let runFixture (id: string) : Check list =
    let fixDir = Path.Combine(fixturesDir, id)

    use fixture =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(fixDir, "fixture.json")))

    let fx = fixture.RootElement
    let server = str fx "server"

    let spec =
        match overrideFor server with
        | Some(cmd :: args) ->
            { Command = cmd
              Args = args
              Install = "" }
        | _ ->
            let packFile = Path.Combine(repoRoot, "languages", str fx "pack" + ".json")

            match LanguagePack.parse packFile (File.ReadAllText packFile) with
            | Ok p when p.Server.IsSome -> p.Server.Value
            | Ok _ -> failwithf "the %s pack names no server" (str fx "pack")
            | Error e -> failwithf "the %s pack did not parse: %s" (str fx "pack") e

    let root =
        Path.Combine(Path.GetTempPath(), "fsh-real-" + id + "-" + Guid.NewGuid().ToString("N"))

    copyDir fixDir root
    let checks = ResizeArray<Check>()

    let add name ok detail =
        checks.Add
            { Name = name
              Ok = ok
              Detail = detail }

    let diagnostics = ConcurrentDictionary<string, LspDiagnostic list>()

    let onDiag (path: string) (ds: LspDiagnostic list) =
        diagnostics[Path.GetFullPath path] <- ds

    try
        match Lsp.start spec onDiag with
        | Error e -> add "start" false e
        | Ok session ->
            try
                let client = session.Client
                add "start" true (sprintf "%s %s" spec.Command (String.Join(' ', spec.Args)))

                // the client waits at most 15 s for each answer; a server that needs longer fails here, which is a finding
                let init = client.Initialize root
                let answered = init.Wait(TimeSpan.FromSeconds 60.0) && init.Result

                add
                    "initialize"
                    answered
                    (if answered then
                         "the server answered `initialize`"
                     else
                         "no answer to `initialize`")

                for rel in strings fx "files" do
                    let path = Path.Combine(root, rel)
                    client.DidOpen(path, str fx "languageId", File.ReadAllText path)
                    client.DidSave path

                // 1. a diagnostic published for the deliberately wrong file
                let bad = fx.GetProperty "diagnostic"
                let badPath = Path.GetFullPath(Path.Combine(root, str bad "file"))

                let diag =
                    eventually 90 (fun () ->
                        match diagnostics.TryGetValue badPath with
                        | true, (d :: _ as ds) when ds |> List.exists (fun x -> x.Severity = 1) -> Some ds
                        | _ -> None)

                match diag with
                | Some ds ->
                    let e = ds |> List.find (fun x -> x.Severity = 1)
                    add "diagnostics" true (sprintf "%d at line %d: %s" ds.Length (e.Range.Start.Line + 1) e.Message)
                | None -> add "diagnostics" false (sprintf "no error published for %s within 90 s" (str bad "file"))

                // 2. completion, 3. go to definition, 4. hover: each retried while the server is still indexing
                let at (section: string) =
                    let s = fx.GetProperty section
                    let path = Path.Combine(root, str s "file")
                    s, path, positionOf (File.ReadAllText path) (str s "at")

                let comp, compPath, compPos = at "completion"

                match compPos with
                | None -> add "completion" false "fixture position not found"
                | Some pos ->
                    let want = str comp "expect"

                    let got =
                        eventually 60 (fun () ->
                            let items = client.Completion(compPath, pos).Result

                            if items |> List.exists (fun i -> i.Label.StartsWith want) then
                                Some items
                            else
                                None)

                    match got with
                    | Some items -> add "completion" true (sprintf "%d items, includes %s" items.Length want)
                    | None -> add "completion" false (sprintf "no item starting with %s within 60 s" want)

                let def, defPath, defPos = at "definition"

                match defPos with
                | None -> add "definition" false "fixture position not found"
                | Some pos ->
                    let wants = strings def "expectFile"

                    let got =
                        eventually 60 (fun () ->
                            client.Definition(defPath, pos).Result
                            |> List.tryFind (fun l ->
                                wants
                                |> List.exists (fun w ->
                                    l.Path.Replace('\\', '/').EndsWith("/" + w)
                                    || l.Path.Replace('\\', '/').EndsWith w)))

                    match got with
                    | Some l ->
                        add "definition" true (sprintf "%s line %d" (Path.GetFileName l.Path) (l.Range.Start.Line + 1))
                    | None ->
                        add "definition" false (sprintf "no location in %s within 60 s" (String.Join(", ", wants)))

                let hov, hovPath, hovPos = at "hover"

                match hovPos with
                | None -> add "hover" false "fixture position not found"
                | Some pos ->
                    let want = str hov "contains"

                    let got =
                        eventually 60 (fun () ->
                            match client.Hover(hovPath, pos).Result with
                            | Some t when t.Contains(want, StringComparison.OrdinalIgnoreCase) -> Some t
                            | _ -> None)

                    match got with
                    | Some t -> add "hover" true (t.Replace('\n', ' ').Substring(0, min 80 t.Length))
                    | None -> add "hover" false (sprintf "no hover containing %s within 60 s" want)

                (try
                    client.Shutdown().Wait(TimeSpan.FromSeconds 10.0) |> ignore
                 with _ ->
                     ())
            finally
                session.Stop()
    finally
        try
            Directory.Delete(root, true)
        with _ ->
            ()

    let result = List.ofSeq checks
    report id result
    result

let private assertAll (id: string) =
    let checks = runFixture id

    let text =
        checks
        |> List.map (fun c -> sprintf "%-12s %s  %s" c.Name (if c.Ok then "ok  " else "FAIL") c.Detail)
        |> String.concat "\n"

    Assert.True(checks |> List.forall (fun c -> c.Ok), "\n" + text)

[<RealServerFact("gopls", "go install golang.org/x/tools/gopls@latest")>]
let ``gopls: initialize, diagnostics, completion, definition, hover`` () = assertAll "go"

[<RealServerFact("rust-analyzer", "rustup component add rust-analyzer rust-src")>]
let ``rust-analyzer: initialize, diagnostics, completion, definition, hover`` () = assertAll "rust"

[<RealServerFact("pyright-langserver", "npm install -g pyright")>]
let ``pyright-langserver: initialize, diagnostics, completion, definition, hover`` () = assertAll "python"

[<RealServerFact("typescript-language-server", "npm install -g typescript@5 typescript-language-server")>]
let ``typescript-language-server: initialize, diagnostics, completion, definition, hover`` () = assertAll "typescript"

[<RealServerFact("clangd", "install clangd (LLVM)")>]
let ``clangd: initialize, diagnostics, completion, definition, hover`` () = assertAll "c"

[<RealServerFact("lua-language-server", "https://luals.github.io")>]
let ``lua-language-server: initialize, diagnostics, completion, definition, hover`` () = assertAll "lua"

[<RealServerFact("fsautocomplete", "dotnet tool install -g fsautocomplete")>]
let ``fsautocomplete: initialize, diagnostics, completion, definition, hover`` () = assertAll "fsharp"
