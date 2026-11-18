namespace FSharpened.Ide.Host

open System

/// What the gutter shows for a line of the edited text, compared with the committed text.
type LineMark =
    /// The line is new.
    | AddedLine
    /// The line replaces one or more committed lines.
    | ModifiedLine
    /// Committed lines were removed just above this line (the mark is on the line after the gap).
    | RemovedAbove

/// A line diff between the committed text of a file and what is in the editor, for the gutter. Pure.
module GitDiff =
    /// Lines of a text, without their line breaks; a final break does not make an extra empty line.
    let lines (text: string) : string[] =
        if text = "" then
            [||]
        else
            let parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')

            if parts[parts.Length - 1] = "" then
                parts[.. parts.Length - 2]
            else
                parts

    /// The most cells of the comparison table that are worked out; a bigger change inside one file is marked as one
    /// modified block instead, which is what a person sees anyway.
    let private maxCells = 2_000_000L

    /// The marks for lines of `current`, 1-based and in order. Everything is `AddedLine` when `committed` is None.
    let marks (committed: string option) (current: string) : (int * LineMark) list =
        let b = lines current

        match committed with
        | None -> [ for i in 1 .. b.Length -> i, AddedLine ]
        | Some old ->
            let a = lines old
            // lines that both texts start and end with are the same: only the middle needs comparing
            let mutable pre = 0

            while pre < a.Length && pre < b.Length && a[pre] = b[pre] do
                pre <- pre + 1

            let mutable suf = 0

            while suf < a.Length - pre
                  && suf < b.Length - pre
                  && a[a.Length - 1 - suf] = b[b.Length - 1 - suf] do
                suf <- suf + 1

            let n = a.Length - pre - suf
            let m = b.Length - pre - suf
            let result = Collections.Generic.List<int * LineMark>()

            /// A run of `del` committed lines replaced by `ins` new ones, the new ones starting at 0-based `at`.
            let hunk (del: int) (ins: int) (at: int) =
                let common = min del ins

                for k in 0 .. ins - 1 do
                    result.Add((at + k + 1), (if k < common then ModifiedLine else AddedLine))

                // fewer new lines than old ones: the new ones read as modified; none at all is a removal
                if ins = 0 && del > 0 then
                    result.Add((max 1 (min b.Length (at + 1))), RemovedAbove)

            if n = 0 && m = 0 then
                ()
            elif n = 0 || m = 0 || int64 n * int64 m > maxCells then
                hunk n m pre
            else
                // longest common subsequence of the middle, from the back so the walk below goes forwards
                let t = Array2D.zeroCreate<int> (n + 1) (m + 1)

                for i in n - 1 .. -1 .. 0 do
                    for j in m - 1 .. -1 .. 0 do
                        t[i, j] <-
                            if a[pre + i] = b[pre + j] then
                                t[i + 1, j + 1] + 1
                            else
                                max t[i + 1, j] t[i, j + 1]

                let mutable i = 0
                let mutable j = 0
                let mutable delRun = 0
                let mutable insRun = 0
                let mutable insStart = 0

                let flush () =
                    if delRun > 0 || insRun > 0 then
                        hunk delRun insRun insStart
                        delRun <- 0
                        insRun <- 0

                while i < n || j < m do
                    if i < n && j < m && a[pre + i] = b[pre + j] then
                        flush ()
                        i <- i + 1
                        j <- j + 1
                    elif j < m && (i >= n || t[i, j + 1] >= t[i + 1, j]) then
                        if insRun = 0 then
                            insStart <- pre + j

                        insRun <- insRun + 1
                        j <- j + 1
                    else
                        if insRun = 0 && delRun = 0 then
                            insStart <- pre + j

                        delRun <- delRun + 1
                        i <- i + 1

                flush ()

            // a removal mark never covers a line that already has a mark of its own
            let own =
                result
                |> Seq.filter (fun (_, m) -> m <> RemovedAbove)
                |> Seq.map fst
                |> Set.ofSeq

            result
            |> Seq.filter (fun (l, m) -> m <> RemovedAbove || not (own.Contains l))
            |> Seq.distinctBy fst
            |> Seq.sortBy fst
            |> List.ofSeq
