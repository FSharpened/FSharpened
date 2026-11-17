namespace FSharpened.Ide.Host

open System
open System.Collections.Generic

/// What probing found for one tool the packs declare.
type ToolInfo =
    {
        Id: string
        Label: string
        Found: bool
        /// The argument vector that starts it (for example `["py"; "-3"]`), empty when not found.
        Exe: string list
        Version: string
        Install: string
    }

module Tools =
    let private firstLine (s: string) =
        s.Split('\n')
        |> Array.map (fun l -> l.Trim())
        |> Array.tryFind (fun l -> l <> "")
        |> Option.defaultValue ""
        |> fun l -> if l.Length > 100 then l.Substring(0, 100) else l

    /// Try each candidate's version command; the first that exits 0 is the tool.
    let probeOne (spec: ToolSpec) : ToolInfo =
        let cwd = Environment.CurrentDirectory

        let found =
            spec.Candidates
            |> List.tryPick (fun c ->
                match Proc.run (c @ spec.Version) cwd None (TimeSpan.FromSeconds 6.0) with
                | Ok r when r.ExitCode = Some 0 ->
                    Some(c, firstLine (if r.Stdout.Trim() <> "" then r.Stdout else r.Stderr))
                | _ -> None)

        match found with
        | Some(exe, v) ->
            { Id = spec.Id
              Label = spec.Label
              Found = true
              Exe = exe
              Version = v
              Install = spec.Install }
        | None ->
            { Id = spec.Id
              Label = spec.Label
              Found = false
              Exe = []
              Version = ""
              Install = spec.Install }

    let probeAll (specs: ToolSpec list) : ToolInfo list =
        specs
        |> List.map (fun s -> async { return probeOne s })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.toList

/// The tools of the loaded packs and what is installed. Probing starts programs, so it is done once and on request.
type ToolBox(specs: ToolSpec list) =
    let gate = obj ()
    let found = Dictionary<string, ToolInfo>()

    member _.Specs = specs

    /// Probe every tool (in parallel) and remember the answers.
    member _.Refresh() : ToolInfo list =
        let all = Tools.probeAll specs

        lock gate (fun () ->
            found.Clear()
            all |> List.iter (fun t -> found[t.Id] <- t))

        all

    member this.Get(id: string) : ToolInfo option =
        let cached =
            lock gate (fun () ->
                match found.TryGetValue id with
                | true, t -> Some t
                | _ -> None)

        match cached with
        | Some t -> Some t
        | None ->
            specs
            |> List.tryFind (fun s -> s.Id = id)
            |> Option.map (fun s ->
                let t = Tools.probeOne s
                lock gate (fun () -> found[id] <- t)
                t)

    member _.All = lock gate (fun () -> found.Values |> Seq.toList)
