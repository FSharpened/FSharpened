namespace FSharpened.Ide

open System.Text
open FSharpened.Diagnostics

/// Raw outcome from the gateway (already normalised server-side; the client only extracts these fields).
type RawResult =
    { Engine: string
      BuildFailed: bool
      BuildOutput: string
      Stdout: string
      Stderr: string
      ExitCode: int
      TimedOut: bool
      DurationMs: int
      Cached: bool }

type Phase =
    | Build
    | Run
    | Infrastructure

type Report =
    { Ok: bool
      Phase: Phase
      Summary: string
      Diagnostics: StructuredDiagnostic list
      Stdout: string
      Stderr: string
      ExitCode: int }

module Protocol =
    let private jsonString (s: string) =
        let sb = StringBuilder()
        sb.Append '"' |> ignore

        for c in s do
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when int c < 0x20 -> sb.Append("\\u" + (int c).ToString("x4")) |> ignore
            | c -> sb.Append c |> ignore

        sb.Append '"' |> ignore
        sb.ToString()

    /// Body for POST /api/run on the gateway.
    let requestJson (compiler: Compiler) (options: string) (source: string) (stdin: string) =
        "{\"compiler\":"
        + jsonString compiler.Id
        + ",\"language\":"
        + jsonString (Language.id compiler.Language)
        + ",\"options\":"
        + jsonString options
        + ",\"source\":"
        + jsonString source
        + ",\"stdin\":"
        + jsonString stdin
        + "}"

    let private seconds (ms: int) = (float ms / 1000.0).ToString("0.0#")

    let interpret (lang: Language) (raw: RawResult) : Report =
        let buildDiags = Problems.parse lang raw.BuildOutput

        // PHP reports its errors on stdout, not stderr, so the run output is read as a whole for it.
        let runOutput =
            if lang = Php then
                raw.Stdout + "\n" + raw.Stderr
            else
                raw.Stderr

        let runDiags =
            if raw.BuildFailed then
                []
            else
                Problems.parse lang runOutput

        let errors = Problems.countOf Severity.Error buildDiags
        let warnings = Problems.countOf Severity.Warning buildDiags

        if raw.BuildFailed then
            { Ok = false
              Phase = Build
              Summary =
                "Build failed: "
                + string (max errors 1)
                + (if max errors 1 = 1 then " error" else " errors")
                + (if warnings > 0 then
                       ", " + string warnings + " warning(s)"
                   else
                       "")
              Diagnostics = buildDiags
              Stdout = raw.BuildOutput |> Problems.readable
              Stderr = ""
              ExitCode = raw.ExitCode }
        elif raw.TimedOut then
            { Ok = false
              Phase = Run
              Summary = "Timed out: the program was stopped by the sandbox."
              Diagnostics = buildDiags @ runDiags
              Stdout = raw.Stdout
              Stderr = raw.Stderr
              ExitCode = raw.ExitCode }
        else
            let ok = raw.ExitCode = 0

            { Ok = ok
              Phase = Run
              Summary =
                (if ok then
                     "Finished"
                 else
                     "Exited with code " + string raw.ExitCode)
                + " in "
                + seconds raw.DurationMs
                + "s on "
                + raw.Engine
                + (if raw.Cached then " (cached)" else "")
                + (if warnings > 0 then
                       ", " + string warnings + " warning(s)"
                   else
                       "")
              Diagnostics = buildDiags @ runDiags
              Stdout = raw.Stdout
              Stderr = Problems.readable raw.Stderr
              ExitCode = raw.ExitCode }

    let infrastructure (message: string) (diags: StructuredDiagnostic list) : Report =
        { Ok = false
          Phase = Infrastructure
          Summary = message
          Diagnostics = diags
          Stdout = ""
          Stderr = ""
          ExitCode = -1 }

    /// FNV-1a 64-bit over UTF-16 code units. 64-bit (not 32-bit) on purpose: Fable evaluates uint64 exactly, whereas a
    /// uint32 multiply loses precision in JavaScript. Pinned by a test so .NET and the browser agree on warm-cache keys.
    let fingerprint (parts: string list) : string =
        let mutable h = 14695981039346656037UL

        for p in parts do
            for c in p + "" do
                h <- (h ^^^ uint64 c) * 1099511628211UL

        h.ToString("x16")
