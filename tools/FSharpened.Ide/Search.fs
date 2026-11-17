namespace FSharpened.Ide

open System.Text.RegularExpressions

type SearchOptions =
    {
        Query: string
        CaseSensitive: bool
        WholeWord: bool
        /// The pattern is a regular expression, in the syntax JavaScript and .NET share (classes, groups, lookahead,
        /// quantifiers, backreferences). It is matched against one line at a time.
        Regex: bool
    }

/// One match: where it is (1-based line and column), how long, and the part of the line to show.
type SearchHit =
    {
        Line: int
        Column: int
        Length: int
        /// A slice of the line around the match, for the results list.
        Preview: string
        /// Where the preview starts in the line, so the page can highlight `Column - 1 - PreviewStart` for `Length`.
        PreviewStart: int
    }

type FileHits =
    {
        Path: string
        Hits: SearchHit list
        /// True when the file has more matches than are listed.
        More: bool
    }

type SearchResult =
    {
        Files: FileHits list
        Total: int
        FilesSearched: int
        Truncated: bool
        /// Why the search could not run (an invalid regular expression), or "".
        Error: string
    }

/// What a replace did to a project.
type ReplaceResult =
    { Project: Project
      Changed: (string * int) list
      Failed: (string * string) list }

/// File-name patterns for "files to include" and "files to exclude": `*` and `?` inside a name, `**` across folders,
/// comma-separated. A pattern with no slash matches any file or folder of that name, at any depth.
module Glob =
    let private lower (c: char) = System.Char.ToLowerInvariant c

    let private segment (pat: string) (s: string) : bool =
        let mutable pi = 0
        let mutable si = 0
        let mutable star = -1
        let mutable mark = 0
        let mutable ok = true

        while ok && si < s.Length do
            if
                pi < pat.Length
                && (pat.[pi] = '?' || (pat.[pi] <> '*' && lower pat.[pi] = lower s.[si]))
            then
                pi <- pi + 1
                si <- si + 1
            elif pi < pat.Length && pat.[pi] = '*' then
                star <- pi
                mark <- si
                pi <- pi + 1
            elif star >= 0 then
                pi <- star + 1
                mark <- mark + 1
                si <- mark
            else
                ok <- false

        if not ok then
            false
        else
            while pi < pat.Length && pat.[pi] = '*' do
                pi <- pi + 1

            pi = pat.Length

    let private parts (s: string) =
        s.Split('/') |> Array.filter (fun x -> x <> "") |> Array.toList

    let private single (pattern: string) (path: string) : bool =
        let pat = pattern.Trim().Replace('\\', '/')
        let ps = parts pat
        let ss = parts path

        match ps with
        | [] -> false
        | [ one ] when not (pat.Contains "/") -> ss |> List.exists (fun s -> segment one s)
        | _ ->
            let rec go (ps: string list) (ss: string list) =
                match ps, ss with
                | [], _ -> true
                | "**" :: rest, _ ->
                    go rest ss
                    || (match ss with
                        | [] -> false
                        | _ :: tail -> go ps tail)
                | p :: prest, s :: stail -> segment p s && go prest stail
                | _, [] -> false

            go ps ss

    /// True when any of the comma-separated patterns matches the path (or a folder it is inside).
    let matchesAny (patterns: string) (path: string) : bool =
        patterns.Split(',') |> Array.exists (fun p -> p.Trim() <> "" && single p path)

    /// Included by `include` (empty means everything) and not excluded by `exclude` (empty means nothing).
    let allowed (includes: string) (excludes: string) (path: string) : bool =
        (includes.Trim() = "" || matchesAny includes path)
        && not (excludes.Trim() <> "" && matchesAny excludes path)

/// Find and replace over text and projects (Ctrl+Shift+F). Pure and bounded: the number of hits, their previews and the
/// length of the query are capped, so a pathological regular expression cannot flood the page with results.
module Search =
    let maxHits = 2000
    let maxPerFile = 200
    let maxQuery = 500
    let private previewLength = 200

    type private LineMatch =
        { Start: int
          Length: int
          Groups: string list }

    let private isWordChar (c: char) =
        System.Char.IsLetterOrDigit c || c = '_'

    let private lower (c: char) = System.Char.ToLowerInvariant c

    // A matcher finds every match in one line. Empty matches (such as `^`) are kept; the scan steps over them.
    let private build (o: SearchOptions) : Result<string -> LineMatch list, string> =
        if o.Query = "" then
            Result.Error ""
        elif o.Query.Length > maxQuery then
            Result.Error "the search text is longer than 500 characters"
        elif o.Regex then
            try
                let pattern = if o.WholeWord then "\\b(?:" + o.Query + ")\\b" else o.Query

                let opts =
                    if o.CaseSensitive then
                        RegexOptions.None
                    else
                        RegexOptions.IgnoreCase

                let re = Regex(pattern, opts)

                Ok(fun (line: string) ->
                    let found = ResizeArray<LineMatch>()
                    let mutable pos = 0
                    let mutable go = true

                    while go && pos <= line.Length && found.Count <= maxPerFile do
                        let m = re.Match(line, pos)

                        if not m.Success then
                            go <- false
                        else
                            found.Add
                                { Start = m.Index
                                  Length = m.Length
                                  Groups = [ for g in 0 .. m.Groups.Count - 1 -> m.Groups.[g].Value ] }

                            pos <- if m.Length = 0 then m.Index + 1 else m.Index + m.Length

                    found |> Seq.toList)
            with e ->
                Result.Error("not a valid regular expression: " + e.Message)
        else
            let q = o.Query
            let n = q.Length

            let same (a: char) (b: char) =
                if o.CaseSensitive then a = b else lower a = lower b

            Ok(fun (line: string) ->
                let found = ResizeArray<LineMatch>()
                let mutable i = 0

                while i <= line.Length - n && found.Count <= maxPerFile do
                    let mutable k = 0

                    while k < n && same line.[i + k] q.[k] do
                        k <- k + 1

                    let fits =
                        k = n
                        && (not o.WholeWord
                            || ((i = 0 || not (isWordChar line.[i - 1]))
                                && (i + n >= line.Length || not (isWordChar line.[i + n]))))

                    if fits then
                        found.Add
                            { Start = i
                              Length = n
                              Groups = [ line.Substring(i, n) ] }

                        i <- i + n
                    else
                        i <- i + 1

                found |> Seq.toList)

    let private splitLines (text: string) = text.Split('\n')

    let private withoutCr (line: string) =
        if line.EndsWith "\r" then
            line.Substring(0, line.Length - 1)
        else
            line

    let private preview (line: string) (start: int) =
        let first =
            let lead = line.Length - line.TrimStart().Length

            if start <= 40 then min lead start else start - 30

        let len = min previewLength (line.Length - first)
        first, line.Substring(first, len)

    /// Every match of the query in one text, in order.
    let findInText (o: SearchOptions) (path: string) (text: string) : Result<FileHits, string> =
        match build o with
        | Result.Error e -> Result.Error e
        | Ok find ->
            let hits = ResizeArray<SearchHit>()
            let mutable more = false
            let lines = splitLines text

            for i in 0 .. lines.Length - 1 do
                if not more then
                    let line = withoutCr lines.[i]

                    for m in find line do
                        if hits.Count >= maxPerFile then
                            more <- true
                        else
                            let first, shown = preview line m.Start

                            hits.Add
                                { Line = i + 1
                                  Column = m.Start + 1
                                  Length = m.Length
                                  Preview = shown
                                  PreviewStart = first }

            Ok
                { Path = path
                  Hits = hits |> Seq.toList
                  More = more }

    /// Searches the files whose path passes the include and exclude patterns, in the order given.
    let run (o: SearchOptions) (includes: string) (excludes: string) (files: (string * string) list) : SearchResult =
        match build o with
        | Result.Error e ->
            { Files = []
              Total = 0
              FilesSearched = 0
              Truncated = false
              Error = e }
        | Ok _ ->
            let found = ResizeArray<FileHits>()
            let mutable total = 0
            let mutable searched = 0
            let mutable truncated = false

            for (path, text) in files do
                if not truncated && Glob.allowed includes excludes path then
                    searched <- searched + 1

                    match findInText o path text with
                    | Result.Error _ -> ()
                    | Ok fh when fh.Hits.IsEmpty -> ()
                    | Ok fh ->
                        let room = maxHits - total

                        if fh.Hits.Length > room then
                            found.Add
                                { fh with
                                    Hits = List.truncate room fh.Hits
                                    More = true }

                            total <- maxHits
                            truncated <- true
                        else
                            found.Add fh
                            total <- total + fh.Hits.Length

                            if total >= maxHits then
                                truncated <- true

            { Files = found |> Seq.toList
              Total = total
              FilesSearched = searched
              Truncated = truncated
              Error = "" }

    // Replacement text: in a regular expression `$1`..`$99` are groups, `$&` and `$0` the whole match, `$$` a dollar
    // sign, and `\n`, `\t`, `\\` the usual escapes. In a plain search the replacement is taken literally.
    let private expand (o: SearchOptions) (replacement: string) (groups: string list) : string =
        if not o.Regex then
            replacement
        else
            let groups = List.toArray groups
            let sb = System.Text.StringBuilder()
            let mutable i = 0

            while i < replacement.Length do
                let c = replacement.[i]

                let next =
                    if i + 1 < replacement.Length then
                        replacement.[i + 1]
                    else
                        ' '

                if c = '$' && next = '$' then
                    sb.Append '$' |> ignore
                    i <- i + 2
                elif c = '$' && next = '&' then
                    sb.Append groups.[0] |> ignore
                    i <- i + 2
                elif c = '$' && next >= '0' && next <= '9' then
                    let two =
                        if
                            i + 2 < replacement.Length
                            && replacement.[i + 2] >= '0'
                            && replacement.[i + 2] <= '9'
                        then
                            int (replacement.Substring(i + 1, 2))
                        else
                            -1

                    if two > 0 && two < groups.Length then
                        sb.Append groups.[two] |> ignore
                        i <- i + 3
                    else
                        let one = int next - int '0'

                        if one < groups.Length then
                            sb.Append groups.[one] |> ignore
                        else
                            sb.Append('$').Append(next) |> ignore

                        i <- i + 2
                elif c = '\\' && next = 'n' then
                    sb.Append '\n' |> ignore
                    i <- i + 2
                elif c = '\\' && next = 't' then
                    sb.Append '\t' |> ignore
                    i <- i + 2
                elif c = '\\' && next = '\\' then
                    sb.Append '\\' |> ignore
                    i <- i + 2
                else
                    sb.Append c |> ignore
                    i <- i + 1

            sb.ToString()

    /// Replaces every match in a text. Returns the new text and how many replacements were made.
    let replaceInText (o: SearchOptions) (replacement: string) (text: string) : Result<string * int, string> =
        match build o with
        | Result.Error e -> Result.Error e
        | Ok find ->
            let count = ref 0
            let lines = splitLines text

            let rewritten =
                lines
                |> Array.map (fun raw ->
                    let line = withoutCr raw
                    let cr = if raw.Length > line.Length then "\r" else ""
                    let ms = find line

                    if ms.IsEmpty then
                        raw
                    else
                        let sb = System.Text.StringBuilder()
                        let mutable at = 0

                        for m in ms do
                            sb.Append(line.Substring(at, m.Start - at)).Append(expand o replacement m.Groups)
                            |> ignore

                            at <- m.Start + m.Length
                            count.Value <- count.Value + 1

                        sb.Append(line.Substring at).Append(cr).ToString())

            Ok(String.concat "\n" rewritten, count.Value)

    /// Replaces the one match that starts at (line, column), as the per-result replace button does.
    let replaceOne
        (o: SearchOptions)
        (replacement: string)
        (text: string)
        (line: int)
        (column: int)
        : Result<string, string> =
        match build o with
        | Result.Error e -> Result.Error e
        | Ok find ->
            let lines = splitLines text

            if line < 1 || line > lines.Length then
                Result.Error "that line is not in the file"
            else
                let raw = lines.[line - 1]
                let body = withoutCr raw
                let cr = if raw.Length > body.Length then "\r" else ""

                match find body |> List.tryFind (fun m -> m.Start = column - 1) with
                | None -> Result.Error "the match is no longer there"
                | Some m ->
                    lines.[line - 1] <-
                        body.Substring(0, m.Start)
                        + expand o replacement m.Groups
                        + body.Substring(m.Start + m.Length)
                        + cr

                    Ok(String.concat "\n" lines)

    /// Replaces in the listed files of a project. A file the project cannot hold afterwards (too large) is reported and
    /// left as it was.
    let replaceInProject (o: SearchOptions) (replacement: string) (paths: string list) (p: Project) : ReplaceResult =
        let mutable current = p
        let changed = ResizeArray<string * int>()
        let failed = ResizeArray<string * string>()

        for path in paths do
            match Project.tryFind path current with
            | None -> failed.Add(path, "the file is not in the project")
            | Some f ->
                match replaceInText o replacement f.Text with
                | Result.Error e -> failed.Add(path, e)
                | Ok(_, 0) -> ()
                | Ok(updated, n) ->
                    match Project.setText f.Path updated current with
                    | Ok q ->
                        current <- q
                        changed.Add(f.Path, n)
                    | Result.Error e -> failed.Add(f.Path, e)

        { Project = current
          Changed = changed |> Seq.toList
          Failed = failed |> Seq.toList }
