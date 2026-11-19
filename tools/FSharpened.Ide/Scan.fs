namespace FSharpened.Ide

open System.Collections.Generic

/// The lexical families the bundler reads. Languages in one family share their comment and string rules.
type Dialect =
    | JsLike
    | PyLike
    | CLike
    | JavaLike
    | GoLike
    | RustLike

/// A small lexical scanner for the bundler (ADR-0024). It does not parse: it only finds where comments and string
/// literals are, so that `import`, `mod`, `#include` and braces inside them are never mistaken for code.
module Scan =
    let private isWord (c: char) =
        System.Char.IsLetterOrDigit c || c = '_' || c = '$'

    // Words after which a `/` starts a regular expression and not a division.
    let private regexAfter =
        set
            [ "return"
              "typeof"
              "case"
              "in"
              "of"
              "delete"
              "void"
              "throw"
              "new"
              "else"
              "do"
              "yield"
              "await" ]

    /// The same text with comments and the inside of string literals replaced by spaces. Line breaks stay, so the line
    /// and column of everything that remains are unchanged. Quote marks stay, so `"  "` still reads as a string.
    let mask (d: Dialect) (text: string) : string =
        let n = text.Length
        let a = text.ToCharArray()

        let at k =
            if k >= 0 && k < n then text.[k] else ' '

        let blank (s: int) (e: int) =
            for k in s .. (min e n) - 1 do
                if a.[k] <> '\n' && a.[k] <> '\r' then
                    a.[k] <- ' '

        // The position of the closing quote of a string whose text starts at k. A newline ends a single-line string.
        let quoted (k: int) (q: char) (multiline: bool) =
            let mutable j = k
            let mutable fin = false

            while not fin && j < n do
                let ch = text.[j]

                if ch = '\\' then j <- j + 2
                elif ch = q then fin <- true
                elif ch = '\n' && not multiline then fin <- true
                else j <- j + 1

            min j n

        // The position of the closing run of three quotes (Python triple quotes, Java text blocks).
        let triple (k: int) (q: char) =
            let mutable j = k
            let mutable fin = false

            while not fin && j < n do
                if text.[j] = '\\' then
                    j <- j + 2
                elif text.[j] = q && at (j + 1) = q && at (j + 2) = q then
                    fin <- true
                else
                    j <- j + 1

            min j n

        let indexFrom (s: string) (k: int) =
            if k >= n then
                -1
            else
                text.IndexOf(s, k, System.StringComparison.Ordinal)

        let lineComment = (d <> PyLike)
        let nestedBlocks = (d = RustLike)
        let tmpl = Stack<int>()
        let mutable brace = 0
        let mutable prev = ' '
        let mutable lastWord = ""
        let mutable i = 0

        // Template literal text starting at k: blank it up to the closing backtick or to a `${`. Returns the position
        // of whichever came first, and whether it was an expression.
        let template (k: int) =
            let mutable j = k
            let mutable fin = false
            let mutable expr = false

            while not fin && j < n do
                let ch = text.[j]

                if ch = '\\' then
                    j <- j + 2
                elif ch = '`' then
                    fin <- true
                elif ch = '$' && at (j + 1) = '{' then
                    fin <- true
                    expr <- true
                else
                    j <- j + 1

            let e = min j n
            blank k e
            (e, expr)

        while i < n do
            let c = text.[i]
            let c1 = at (i + 1)

            if lineComment && c = '/' && c1 = '/' then
                let e = (let j = indexFrom "\n" i in if j < 0 then n else j)
                blank i e
                i <- e
            elif d = PyLike && c = '#' then
                let e = (let j = indexFrom "\n" i in if j < 0 then n else j)
                blank i e
                i <- e
            elif d <> PyLike && c = '/' && c1 = '*' then
                if nestedBlocks then
                    let mutable depth = 1
                    let mutable j = i + 2

                    while depth > 0 && j < n do
                        if text.[j] = '/' && at (j + 1) = '*' then
                            depth <- depth + 1
                            j <- j + 2
                        elif text.[j] = '*' && at (j + 1) = '/' then
                            depth <- depth - 1
                            j <- j + 2
                        else
                            j <- j + 1

                    blank i j
                    i <- j
                else
                    let e = (let j = indexFrom "*/" (i + 2) in if j < 0 then n else j + 2)
                    blank i e
                    i <- e
            elif c = '"' || (c = '\'' && (d = JsLike || d = PyLike)) then
                if
                    (d = PyLike || d = JavaLike) && c = '"' && at (i + 1) = '"' && at (i + 2) = '"'
                    || (d = PyLike && c = '\'' && at (i + 1) = '\'' && at (i + 2) = '\'')
                then
                    let e = triple (i + 3) c
                    blank (i + 3) e
                    i <- min n (e + 3)
                elif d = RustLike && c = '"' then
                    // r"..." and r#"..."#: a raw string has no escapes and ends at a quote followed by as many hashes
                    let mutable h = 0

                    while at (i - 1 - h) = '#' do
                        h <- h + 1

                    let r = at (i - 1 - h)

                    let raw =
                        r = 'r'
                        && (not (isWord (at (i - 2 - h)))
                            || (at (i - 2 - h) = 'b' && not (isWord (at (i - 3 - h)))))

                    if raw then
                        let close = "\"" + String.replicate h "#"
                        let e = (let j = indexFrom close (i + 1) in if j < 0 then n else j)
                        blank (i + 1) e
                        i <- min n (e + close.Length)
                    else
                        let e = quoted (i + 1) '"' true
                        blank (i + 1) e
                        i <- min n (e + 1)
                elif
                    d = CLike
                    && c = '"'
                    && at (i - 1) = 'R'
                    && not (isWord (at (i - 2)) && at (i - 2) <> '8')
                then
                    // C++ raw string: R"delim( ... )delim"
                    let open' = indexFrom "(" (i + 1)

                    if open' < 0 then
                        i <- i + 1
                    else
                        let delim = text.Substring(i + 1, open' - i - 1)
                        let close = ")" + delim + "\""
                        let e = (let j = indexFrom close (open' + 1) in if j < 0 then n else j)
                        blank (i + 1) e
                        i <- min n (e + close.Length)
                else
                    let e = quoted (i + 1) c false
                    blank (i + 1) e
                    i <- min n (e + 1)

                prev <- '"'
            elif c = '\'' && d = RustLike then
                // a char literal ('a', '\n') or a lifetime ('a): only the first is a string
                if c1 = '\\' then
                    let e = quoted (i + 1) '\'' false
                    blank (i + 1) e
                    i <- min n (e + 1)
                elif at (i + 2) = '\'' then
                    blank (i + 1) (i + 2)
                    i <- i + 3
                else
                    i <- i + 1
            elif c = '\'' && (d = CLike || d = JavaLike || d = GoLike) then
                // C++14 digit separators (1'000'000) are not quotes
                if System.Char.IsDigit(at (i - 1)) && d = CLike then
                    i <- i + 1
                else
                    let e = quoted (i + 1) '\'' false
                    blank (i + 1) e
                    i <- min n (e + 1)
            elif c = '`' && d = GoLike then
                let e = (let j = indexFrom "`" (i + 1) in if j < 0 then n else j)
                blank (i + 1) e
                i <- min n (e + 1)
            elif c = '`' && d = JsLike then
                let (e, expr) = template (i + 1)

                if expr then
                    tmpl.Push brace
                    i <- e + 2
                    prev <- '{'
                else
                    i <- min n (e + 1)
                    prev <- '"'
            elif d = JsLike && c = '{' then
                brace <- brace + 1
                prev <- c
                i <- i + 1
            elif d = JsLike && c = '}' then
                if tmpl.Count > 0 && tmpl.Peek() = brace then
                    tmpl.Pop() |> ignore
                    let (e, expr) = template (i + 1)

                    if expr then
                        tmpl.Push brace
                        i <- e + 2
                        prev <- '{'
                    else
                        i <- min n (e + 1)
                        prev <- '"'
                else
                    brace <- max 0 (brace - 1)
                    prev <- c
                    i <- i + 1
            elif d = JsLike && c = '/' then
                let regexOk =
                    "(,=:[!&|?{};+-*%<>~^".IndexOf prev >= 0
                    || prev = ' '
                    || (prev = 'a' && regexAfter.Contains lastWord)

                if regexOk then
                    // a regular expression literal: skip to the closing slash, outside a [...] class
                    let mutable j = i + 1
                    let mutable inClass = false
                    let mutable fin = false

                    while not fin && j < n do
                        let ch = text.[j]

                        if ch = '\\' then
                            j <- j + 2
                        elif ch = '\n' then
                            fin <- true
                        elif ch = '[' then
                            inClass <- true
                            j <- j + 1
                        elif ch = ']' then
                            inClass <- false
                            j <- j + 1
                        elif ch = '/' && not inClass then
                            fin <- true
                        else
                            j <- j + 1

                    let e = min j n
                    blank (i + 1) e
                    i <- min n (e + 1)
                    prev <- '"'
                else
                    prev <- c
                    i <- i + 1
            elif isWord c then
                let mutable j = i

                while j < n && isWord text.[j] do
                    j <- j + 1

                lastWord <- text.Substring(i, j - i)
                prev <- 'a'
                i <- j
            else
                if not (System.Char.IsWhiteSpace c) then
                    prev <- c

                i <- i + 1

        System.String(a)

    /// The text split into lines, with CRLF and CR read as a line break. The last element is the text after the last
    /// break, so `lines "a\n"` is `["a"; ""]`.
    let lines (text: string) : string list =
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n') |> Array.toList

    /// For each position of `text`, nothing is stored; this returns the brace depth at the start of every line of the
    /// masked text (so a declaration is at top level when its line starts at depth 0 and nothing opened before it).
    let depthAtLineStarts (masked: string) : int[] =
        let ls = masked.Split('\n')
        let res = Array.zeroCreate ls.Length
        let mutable depth = 0

        for li in 0 .. ls.Length - 1 do
            res.[li] <- depth

            for ch in ls.[li] do
                if ch = '{' then
                    depth <- depth + 1
                elif ch = '}' then
                    depth <- max 0 (depth - 1)

        res
