namespace FSharpened.Ide

/// A fuzzy match: how good it is and which characters of the text matched, so the page can highlight them.
type FuzzyMatch = { Score: int; Positions: int list }

/// One ranked candidate: its place in the list that was searched, the text, its score and the matched positions.
type Ranked =
    { Index: int
      Text: string
      Score: int
      Positions: int list }

/// What a person typed into quick open or the command palette, split into its mode and its parts.
/// Mode is "file", "line", "symbol" or "command".
type QuickQuery =
    {
        Mode: string
        /// The text to match (a file name, a symbol, a command), without the mode prefix or the ":line" suffix.
        Text: string
        /// 1-based line from ":12" or "main.rs:12", or 0 when none was given.
        Line: int
        Column: int
    }

/// Fuzzy matching for quick open, the command palette and symbol search (the way VS Code ranks them). A match is a
/// subsequence of the text, case-insensitive; the best alignment wins, favouring word starts, runs of consecutive
/// characters, and the file-name part of a path.
module Fuzzy =
    let private maxText = 400
    let private none = -1000000

    let private lower (c: char) = System.Char.ToLowerInvariant c
    let private isUpper (c: char) = c >= 'A' && c <= 'Z'
    let private isLower (c: char) = c >= 'a' && c <= 'z'

    let private isSeparator (c: char) =
        c = '/' || c = '\\' || c = '.' || c = '_' || c = '-' || c = ' ' || c = ':'

    let private squeeze (s: string) =
        s
        |> Seq.filter (fun c -> not (System.Char.IsWhiteSpace c))
        |> Seq.toArray
        |> System.String

    /// The best alignment of `query` inside `text`, or None when the query is not a subsequence of it (ignoring case and
    /// ignoring spaces in the query). An empty query matches everything with score 0.
    let score (query: string) (text: string) : FuzzyMatch option =
        let q = squeeze query

        let t =
            if text.Length > maxText then
                text.Substring(0, maxText)
            else
                text

        let m = q.Length
        let n = t.Length

        if m = 0 then
            Some { Score = 0; Positions = [] }
        elif m > n then
            None
        else
            let ql = Array.init m (fun i -> lower q.[i])
            let tl = Array.init n (fun i -> lower t.[i])
            let nameStart = t.LastIndexOf '/'

            let bonus (j: int) =
                let cur = t.[j]

                let boundary =
                    if j = 0 then 14
                    elif isSeparator t.[j - 1] then 12
                    elif isLower t.[j - 1] && isUpper cur then 10
                    else 0

                boundary + (if j > nameStart then 4 else 0)

            // sc.[i].[j]: the best score with query character i matched at text position j. from.[i].[j]: where i-1 matched.
            let sc = Array.init m (fun _ -> Array.create n none)
            let from = Array.init m (fun _ -> Array.create n -1)

            for i in 0 .. m - 1 do
                // g: the best way to have matched i-1 characters ending at least two before here, less a gap penalty that
                // grows by one for every skipped character. gk is where that match ended.
                let mutable g = none
                let mutable gk = -1

                for j in 0 .. n - 1 do
                    if i > 0 then
                        if g > none then
                            g <- g - 1

                        if j >= 2 && sc.[i - 1].[j - 2] > none then
                            let cand = sc.[i - 1].[j - 2] - 3

                            if cand > g then
                                g <- cand
                                gk <- j - 2

                    if ql.[i] = tl.[j] then
                        let b = 16 + bonus j + (if q.[i] = t.[j] then 1 else 0)

                        if i = 0 then
                            sc.[0].[j] <- b - (min j 5)
                        else
                            let consecutive =
                                if j >= 1 && sc.[i - 1].[j - 1] > none then
                                    sc.[i - 1].[j - 1] + 10
                                else
                                    none

                            if consecutive > none && consecutive >= g then
                                sc.[i].[j] <- consecutive + b
                                from.[i].[j] <- j - 1
                            elif g > none then
                                sc.[i].[j] <- g + b
                                from.[i].[j] <- gk

            let last = m - 1
            let mutable best = none
            let mutable bestJ = -1

            for j in 0 .. n - 1 do
                if sc.[last].[j] > best then
                    best <- sc.[last].[j]
                    bestJ <- j

            if bestJ < 0 then
                None
            else
                let positions = ResizeArray<int>()
                let mutable j = bestJ
                let mutable i = last

                while i >= 0 do
                    positions.Add j
                    j <- from.[i].[j]
                    i <- i - 1

                Some
                    { Score = best
                      Positions = positions |> Seq.rev |> Seq.toList }

    /// Every item that matches, best first (ties: the shorter text, then the original order). An empty query keeps the
    /// original order. At most `limit` are returned.
    let rank (query: string) (items: string list) (limit: int) : Ranked list =
        if (squeeze query) = "" then
            items
            |> List.mapi (fun i s ->
                { Index = i
                  Text = s
                  Score = 0
                  Positions = [] })
            |> List.truncate limit
        else
            items
            |> List.mapi (fun i s -> i, s)
            |> List.choose (fun (i, s) ->
                score query s
                |> Option.map (fun r ->
                    { Index = i
                      Text = s
                      Score = r.Score
                      Positions = r.Positions }))
            |> List.sortBy (fun r -> -r.Score, r.Text.Length, r.Index)
            |> List.truncate limit

/// Splits what was typed into quick open: ">" is the command palette, "@" a symbol in this file, ":12" a line, and
/// "main.rs:12:5" a file with a position.
module Quick =
    let private digits (s: string) =
        s.Length > 0 && s.Length <= 7 && s |> Seq.forall (fun c -> c >= '0' && c <= '9')

    let private num (s: string) = if digits s then int s else 0

    let parse (raw: string) : QuickQuery =
        let s = raw.TrimStart()

        if s.StartsWith ">" then
            { Mode = "command"
              Text = s.Substring(1).Trim()
              Line = 0
              Column = 0 }
        elif s.StartsWith "@" then
            { Mode = "symbol"
              Text = s.Substring(1).Trim()
              Line = 0
              Column = 0 }
        elif s.StartsWith ":" then
            let parts = s.Substring(1).Trim().Split(':')

            { Mode = "line"
              Text = ""
              Line = num (parts.[0].Trim())
              Column = if parts.Length > 1 then num (parts.[1].Trim()) else 0 }
        else
            let parts = s.TrimEnd().Split(':')
            let n = parts.Length

            if n >= 3 && digits parts.[n - 1] && digits parts.[n - 2] then
                { Mode = "file"
                  Text = String.concat ":" (parts |> Array.take (n - 2))
                  Line = num parts.[n - 2]
                  Column = num parts.[n - 1] }
            elif n >= 2 && digits parts.[n - 1] then
                { Mode = "file"
                  Text = String.concat ":" (parts |> Array.take (n - 1))
                  Line = num parts.[n - 1]
                  Column = 0 }
            else
                { Mode = "file"
                  Text = s.TrimEnd()
                  Line = 0
                  Column = 0 }
