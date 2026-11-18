namespace FSharpened.Tooling.Completion

open System
open System.Text

type JsonValue =
    | JNull
    | JBool of bool
    | JNum of float
    | JStr of string
    | JArr of JsonValue list
    | JObj of (string * JsonValue) list

/// A small JSON reader and writer, enough for the Workers AI wire format, with the same behaviour under .NET and Fable.
/// It refuses what it does not need: nesting deeper than 16, input over 256 KB.
module MiniJson =
    [<Literal>]
    let MaxChars = 262144

    [<Literal>]
    let MaxDepth = 16

    let private hex (c: char) =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
        else -1

    exception private Bad of string

    let parse (text: string) : Result<JsonValue, string> =
        if text.Length > MaxChars then
            Error "too large"
        else
            let mutable i = 0
            let fail (m: string) = raise (Bad m)

            let skip () =
                while i < text.Length
                      && (text.[i] = ' ' || text.[i] = '\t' || text.[i] = '\n' || text.[i] = '\r') do
                    i <- i + 1

            let expect (c: char) =
                if i < text.Length && text.[i] = c then
                    i <- i + 1
                else
                    fail ("expected " + string c)

            let literal (word: string) (v: JsonValue) =
                if i + word.Length <= text.Length && text.Substring(i, word.Length) = word then
                    i <- i + word.Length
                    v
                else
                    fail "bad literal"

            let str () =
                expect '"'
                let sb = StringBuilder()
                let mutable fin = false

                while not fin do
                    if i >= text.Length then
                        fail "unterminated string"

                    let c = text.[i]
                    i <- i + 1

                    if c = '"' then
                        fin <- true
                    elif c = '\\' then
                        if i >= text.Length then
                            fail "bad escape"

                        let e = text.[i]
                        i <- i + 1

                        match e with
                        | '"' -> sb.Append '"' |> ignore
                        | '\\' -> sb.Append '\\' |> ignore
                        | '/' -> sb.Append '/' |> ignore
                        | 'b' -> sb.Append '\b' |> ignore
                        | 'f' -> sb.Append '\012' |> ignore
                        | 'n' -> sb.Append '\n' |> ignore
                        | 'r' -> sb.Append '\r' |> ignore
                        | 't' -> sb.Append '\t' |> ignore
                        | 'u' ->
                            if i + 4 > text.Length then
                                fail "bad escape"

                            let d = [ hex text.[i]; hex text.[i + 1]; hex text.[i + 2]; hex text.[i + 3] ]

                            if List.exists (fun x -> x < 0) d then
                                fail "bad escape"

                            sb.Append(char (d.[0] * 4096 + d.[1] * 256 + d.[2] * 16 + d.[3])) |> ignore
                            i <- i + 4
                        | _ -> fail "bad escape"
                    elif c < ' ' then
                        fail "control character in string"
                    else
                        sb.Append c |> ignore

                sb.ToString()

            let number () =
                let start = i

                while i < text.Length
                      && (Char.IsDigit text.[i]
                          || text.[i] = '-'
                          || text.[i] = '+'
                          || text.[i] = '.'
                          || text.[i] = 'e'
                          || text.[i] = 'E') do
                    i <- i + 1

                let raw = text.Substring(start, i - start)

                // `float` reads with the invariant culture on .NET and with parseFloat under Fable.
                let n =
                    try
                        float raw
                    with _ ->
                        nan

                if Double.IsNaN n || Double.IsInfinity n then
                    fail "bad number"
                else
                    JNum n

            let rec value (depth: int) : JsonValue =
                if depth > MaxDepth then
                    fail "too deep"

                skip ()

                if i >= text.Length then
                    fail "unexpected end"

                match text.[i] with
                | '{' ->
                    i <- i + 1
                    skip ()

                    if i < text.Length && text.[i] = '}' then
                        i <- i + 1
                        JObj []
                    else
                        let items = ResizeArray<string * JsonValue>()
                        let mutable more = true

                        while more do
                            skip ()
                            let k = str ()
                            skip ()
                            expect ':'
                            items.Add((k, value (depth + 1)))
                            skip ()

                            if i < text.Length && text.[i] = ',' then
                                i <- i + 1
                            else
                                expect '}'
                                more <- false

                        JObj(List.ofSeq items)
                | '[' ->
                    i <- i + 1
                    skip ()

                    if i < text.Length && text.[i] = ']' then
                        i <- i + 1
                        JArr []
                    else
                        let items = ResizeArray<JsonValue>()
                        let mutable more = true

                        while more do
                            items.Add(value (depth + 1))
                            skip ()

                            if i < text.Length && text.[i] = ',' then
                                i <- i + 1
                            else
                                expect ']'
                                more <- false

                        JArr(List.ofSeq items)
                | '"' -> JStr(str ())
                | 't' -> literal "true" (JBool true)
                | 'f' -> literal "false" (JBool false)
                | 'n' -> literal "null" JNull
                | _ -> number ()

            try
                let v = value 0
                skip ()

                if i <> text.Length then
                    Error "trailing characters"
                else
                    Ok v
            with
            | Bad m -> Error m
            | _ -> Error "invalid json"

    let private escape (s: string) =
        let sb = StringBuilder()

        for c in s do
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.Append("\\u").Append((int c).ToString("x4")) |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()

    let rec render (v: JsonValue) : string =
        match v with
        | JNull -> "null"
        | JBool b -> if b then "true" else "false"
        | JNum n ->
            if n = Math.Floor n && abs n < 1e15 then
                string (int64 n)
            else
                n.ToString(Globalization.CultureInfo.InvariantCulture)
        | JStr s -> "\"" + escape s + "\""
        | JArr items -> "[" + String.Join(",", items |> List.map render) + "]"
        | JObj fields ->
            "{"
            + String.Join(",", fields |> List.map (fun (k, x) -> "\"" + escape k + "\":" + render x))
            + "}"

    let tryField (name: string) (v: JsonValue) : JsonValue option =
        match v with
        | JObj fields -> fields |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd
        | _ -> None
