namespace FSharpened.Ide.Host

open System
open System.IO

/// What git says about one path.
type GitChange =
    | Modified
    | Added
    | Deleted
    | Renamed
    | Untracked
    | Conflicted

/// The state of the opened folder's repository, read by running `git` (a program found on the PATH, started with an
/// argument list, never through a shell). A folder that is not a repository, or a computer without git, gives None.
module Git =
    /// `--no-optional-locks` keeps a status refresh from fighting a git command the person runs in a terminal at the
    /// same time; `core.fsmonitor=false` keeps the repository's own configuration from starting a program of its own.
    let internal baseArgs =
        [ "git"
          "--no-optional-locks"
          "-c"
          "core.quotepath=false"
          "-c"
          "core.fsmonitor=false" ]

    let private run (root: string) (args: string list) : string option =
        match Proc.run (baseArgs @ args) root None (TimeSpan.FromSeconds 10.0) with
        | Ok r when r.ExitCode = Some 0 -> Some r.Stdout
        | _ -> None

    /// Runs git and gives its output, or the reason in git's own words.
    let internal exec
        (root: string)
        (args: string list)
        (stdin: string option)
        (seconds: float)
        : Result<string, string> =
        match Proc.run (baseArgs @ args) root stdin (TimeSpan.FromSeconds seconds) with
        | Ok r when r.ExitCode = Some 0 -> Ok r.Stdout
        | Ok r when r.TimedOut -> Error "git took too long and was stopped"
        | Ok r ->
            let why = (if r.Stderr.Trim() <> "" then r.Stderr else r.Stdout).Trim()
            Error(if why = "" then "git failed" else why)
        | Error e -> Error e

    /// The current branch, or the short commit when HEAD is detached.
    let branch (root: string) : string option =
        match run root [ "rev-parse"; "--abbrev-ref"; "HEAD" ] with
        | Some "HEAD\n" ->
            run root [ "rev-parse"; "--short"; "HEAD" ]
            |> Option.map (fun s -> "(" + s.Trim() + ")")
        | Some s when s.Trim() <> "" -> Some(s.Trim())
        | _ -> None

    let private classify (x: char) (y: char) : GitChange =
        match x, y with
        | 'U', _
        | _, 'U'
        | 'A', 'A'
        | 'D', 'D' -> Conflicted
        | '?', _ -> Untracked
        | 'R', _
        | _, 'R' -> Renamed
        | 'A', _ -> Added
        | 'D', _
        | _, 'D' -> Deleted
        | _ -> Modified

    /// Parses `git status --porcelain=v1 -z`: entries are "XY path", separated by NUL; a rename or copy is followed by
    /// one more entry holding the old path, which is skipped.
    let parseStatus (output: string) : (string * GitChange) list =
        let parts = output.Split('\000', StringSplitOptions.RemoveEmptyEntries)
        let result = Collections.Generic.List<string * GitChange>()
        let mutable i = 0

        while i < parts.Length do
            let p = parts[i]

            if p.Length > 3 && p[2] = ' ' then
                let x, y = p[0], p[1]
                result.Add((p.Substring 3).Replace('\\', '/').TrimEnd('/'), classify x y)

                if x = 'R' || x = 'C' || y = 'R' || y = 'C' then
                    i <- i + 1

            i <- i + 1

        List.ofSeq result

    /// The changed paths, relative to the folder and with forward slashes.
    let status (root: string) : (string * GitChange) list =
        match run root [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=normal" ] with
        | Some out -> parseStatus out
        | None -> []

    /// The text of a file as of the last commit, for a diff against the editor; None when it is new or untracked.
    let head (root: string) (rel: string) : string option = run root [ "show"; "HEAD:" + rel ]
