namespace FSharpened.Ide

type DiffKind =
    | Same
    | Added
    | Removed

type DiffLine = { Kind: DiffKind; Text: string }

module Diff =
    let private split (s: string) =
        if s = "" then [||] else s.Replace("\r\n", "\n").Split('\n')

    /// Line diff by longest common subsequence. Inputs are editor buffers, so O(n*m) is fine; huge inputs fall back to a block replace.
    let lines (before: string) (after: string) : DiffLine list =
        let a = split before
        let b = split after

        if a.Length * b.Length > 4_000_000 then
            [ for l in a -> { Kind = Removed; Text = l } ]
            @ [ for l in b -> { Kind = Added; Text = l } ]
        else
            let n = a.Length
            let m = b.Length
            let w = m + 1
            let t: int[] = Array.zeroCreate ((n + 1) * w)

            for i in n - 1 .. -1 .. 0 do
                for j in m - 1 .. -1 .. 0 do
                    t.[i * w + j] <-
                        if a.[i] = b.[j] then
                            t.[(i + 1) * w + j + 1] + 1
                        else
                            max t.[(i + 1) * w + j] t.[i * w + j + 1]

            let out = ResizeArray<DiffLine>()
            let mutable i = 0
            let mutable j = 0

            while i < n || j < m do
                if i < n && j < m && a.[i] = b.[j] then
                    out.Add { Kind = Same; Text = a.[i] }
                    i <- i + 1
                    j <- j + 1
                elif j < m && (i = n || t.[i * w + j + 1] >= t.[(i + 1) * w + j]) then
                    out.Add { Kind = Added; Text = b.[j] }
                    j <- j + 1
                else
                    out.Add { Kind = Removed; Text = a.[i] }
                    i <- i + 1

            List.ofSeq out

    let changed (d: DiffLine list) =
        d |> List.filter (fun l -> l.Kind <> Same) |> List.length
