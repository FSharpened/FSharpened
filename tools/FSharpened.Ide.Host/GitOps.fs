namespace FSharpened.Ide.Host

open System
open System.Globalization

/// One path as git's status sees it: what is staged for the next commit, and what is changed beyond that.
type GitEntry =
    {
        /// Relative to the repository folder, with forward slashes.
        Path: string
        Staged: GitChange option
        Unstaged: GitChange option
    }

/// Who last changed a line, from `git blame`.
type BlameLine =
    {
        /// 1-based line in the text that was blamed.
        Line: int
        Commit: string
        Author: string
        Time: DateTimeOffset option
        Summary: string
        /// False for a line that is not in any commit yet (new or edited and not committed).
        Committed: bool
    }

/// What a person does with git in the IDE beyond reading the status: stage, commit, branches and blame. Every call runs
/// `git` as a program with an argument list (never through a shell) in the repository folder, and a failure comes back
/// as git's own words. Whether a folder may run git at all (trust) is decided by the caller.
module GitOps =
    let private oneChange (c: char) : GitChange option =
        match c with
        | ' ' -> None
        | '?' -> Some Untracked
        | 'A' -> Some Added
        | 'D' -> Some Deleted
        | 'R'
        | 'C' -> Some Renamed
        | 'U' -> Some Conflicted
        | _ -> Some Modified // M, T

    /// Parses `git status --porcelain=v1 -z` keeping the two columns apart: the index (X) and the work tree (Y).
    let parseEntries (output: string) : GitEntry list =
        let parts = output.Split('\000', StringSplitOptions.RemoveEmptyEntries)
        let result = Collections.Generic.List<GitEntry>()
        let mutable i = 0

        while i < parts.Length do
            let p = parts[i]

            if p.Length > 3 && p[2] = ' ' then
                let x, y = p[0], p[1]
                let path = (p.Substring 3).Replace('\\', '/').TrimEnd('/')

                let conflicted = x = 'U' || y = 'U' || (x = 'A' && y = 'A') || (x = 'D' && y = 'D')

                result.Add(
                    if conflicted then
                        { Path = path
                          Staged = None
                          Unstaged = Some Conflicted }
                    else
                        { Path = path
                          Staged = (if x = '?' then None else oneChange x)
                          Unstaged = oneChange y }
                )

                // a rename or copy is followed by an entry holding the old path
                if x = 'R' || x = 'C' || y = 'R' || y = 'C' then
                    i <- i + 1

            i <- i + 1

        List.ofSeq result

    let entries (root: string) : Result<GitEntry list, string> =
        Git.exec root [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=normal" ] None 15.0
        |> Result.map parseEntries

    let private hasHead (root: string) =
        Git.exec root [ "rev-parse"; "--verify"; "-q"; "HEAD" ] None 10.0 |> Result.isOk

    /// Stages paths (relative to the folder) for the next commit; a deleted file's removal is staged too.
    let stage (root: string) (paths: string list) : Result<unit, string> =
        if paths.IsEmpty then
            Ok()
        else
            Git.exec root ("add" :: "--" :: paths) None 30.0 |> Result.map ignore

    /// Takes paths out of the next commit again, leaving the files as they are. Works before the first commit too.
    let unstage (root: string) (paths: string list) : Result<unit, string> =
        if paths.IsEmpty then
            Ok()
        elif hasHead root then
            Git.exec root ("reset" :: "-q" :: "--" :: paths) None 30.0 |> Result.map ignore
        else
            Git.exec root ("rm" :: "--cached" :: "-r" :: "-q" :: "--" :: paths) None 30.0
            |> Result.map ignore

    /// Commits what is staged. The message is one argument, so it cannot be read as an option or by a shell; git's hooks
    /// run (they are part of the repository) and may take a while.
    let commit (root: string) (message: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace message then
            Error "Write a commit message first"
        else
            match entries root with
            | Error e -> Error e
            | Ok list when list |> List.forall (fun e -> e.Staged.IsNone) -> Error "Nothing is staged to commit"
            | Ok _ ->
                Git.exec root [ "commit"; "-m"; message.Trim() ] None 120.0
                |> Result.map (fun out -> (out.Split('\n') |> Array.tryHead |> Option.defaultValue "Committed").Trim())

    /// The local branches, by name.
    let branches (root: string) : string list =
        match Git.exec root [ "for-each-ref"; "--format=%(refname:short)"; "refs/heads" ] None 10.0 with
        | Ok out ->
            out.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.toList
            |> List.sort
        | Error _ -> []

    /// Whether git accepts the name for a new branch. A name that starts with `-` or holds a space, `..`, `~`, `^`, `:` ...
    /// is refused with git's own reason.
    let checkBranchName (root: string) (name: string) : Result<string, string> =
        let n = name.Trim()

        if n = "" then
            Error "Give the branch a name"
        elif n.StartsWith "-" then
            Error "A branch name cannot start with a dash"
        else
            match Git.exec root [ "check-ref-format"; "--branch"; n ] None 10.0 with
            | Ok _ -> Ok n
            | Error _ -> Error(sprintf "“%s” is not a valid branch name" n)

    /// Moves to an existing local branch. Git refuses (and says why) when changes in the work tree would be lost.
    let switchTo (root: string) (name: string) : Result<unit, string> =
        if not (branches root |> List.contains name) then
            Error(sprintf "There is no branch named “%s”" name)
        else
            Git.exec root [ "checkout"; name; "--" ] None 60.0 |> Result.map ignore

    /// Creates a branch at the current commit and moves to it.
    let createBranch (root: string) (name: string) : Result<string, string> =
        match checkBranchName root name with
        | Error e -> Error e
        | Ok n when branches root |> List.contains n -> Error(sprintf "A branch named “%s” already exists" n)
        | Ok n -> Git.exec root [ "checkout"; "-b"; n ] None 60.0 |> Result.map (fun _ -> n)

    // ---- blame ------------------------------------------------------------------------------------

    /// Parses `git blame --line-porcelain`: per line a header (`<commit> <old line> <line> [<count>]`), a few `key value`
    /// lines, and the line's text after a tab.
    let parseBlame (output: string) : BlameLine list =
        let result = Collections.Generic.List<BlameLine>()
        let mutable commit = ""
        let mutable line = 0
        let mutable author = ""
        let mutable time: DateTimeOffset option = None
        let mutable summary = ""

        let isHeader (l: string) =
            let p = l.Split(' ')

            p.Length >= 3
            && p[0].Length >= 40
            && (p[0] |> Seq.forall Uri.IsHexDigit)
            && p[2] <> ""
            && Seq.forall Char.IsDigit p[2]

        for raw in output.Split('\n') do
            let l = raw.TrimEnd('\r')

            if l.StartsWith "\t" then
                result.Add
                    { Line = line
                      Commit = commit
                      Author = author
                      Time = time
                      Summary = summary
                      Committed = not (commit |> Seq.forall (fun c -> c = '0')) }
            elif isHeader l then
                let p = l.Split(' ')
                commit <- p[0]
                line <- int p[2]
                author <- ""
                time <- None
                summary <- ""
            elif l.StartsWith "author " then
                author <- l.Substring 7
            elif l.StartsWith "author-time " then
                match Int64.TryParse(l.Substring 12, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, t -> time <- Some(DateTimeOffset.FromUnixTimeSeconds t)
                | _ -> ()
            elif l.StartsWith "summary " then
                summary <- l.Substring 8

        List.ofSeq result

    /// Who changed each line of `text` (the editor's text, not only what is on disk), as `rel` is in the repository.
    /// Fails for a file git does not know.
    let blame (root: string) (rel: string) (text: string) : Result<BlameLine list, string> =
        Git.exec root [ "blame"; "--line-porcelain"; "--contents"; "-"; "--"; rel ] (Some text) 30.0
        |> Result.map parseBlame

    /// "3 days ago", "just now", "2 years ago".
    let ago (now: DateTimeOffset) (t: DateTimeOffset) : string =
        let s = (now - t).TotalSeconds

        let plural (n: int) (unit': string) =
            if n = 1 then
                sprintf "1 %s ago" unit'
            else
                sprintf "%d %ss ago" n unit'

        if s < 60.0 then
            "just now"
        elif s < 3600.0 then
            plural (int (s / 60.0)) "minute"
        elif s < 86400.0 then
            plural (int (s / 3600.0)) "hour"
        elif s < 86400.0 * 30.0 then
            plural (int (s / 86400.0)) "day"
        elif s < 86400.0 * 365.0 then
            plural (int (s / (86400.0 * 30.0))) "month"
        else
            plural (int (s / (86400.0 * 365.0))) "year"

    /// The short text beside a line: "Ada, 3 days ago", or "Not committed yet".
    let blameLabel (now: DateTimeOffset) (b: BlameLine) : string =
        if not b.Committed then
            "Not committed yet"
        else
            match b.Time with
            | Some t -> sprintf "%s, %s" b.Author (ago now t)
            | None -> b.Author

    /// The longer text of a tooltip: the commit, the author, the date and the summary.
    let blameDetail (now: DateTimeOffset) (b: BlameLine) : string =
        if not b.Committed then
            "Not committed yet"
        else
            sprintf
                "%s  %s\n%s\n%s"
                (b.Commit.Substring(0, min 8 b.Commit.Length))
                (blameLabel now b)
                (match b.Time with
                 | Some t -> t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                 | None -> "")
                b.Summary
