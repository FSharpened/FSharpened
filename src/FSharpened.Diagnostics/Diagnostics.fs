namespace FSharpened.Diagnostics

type Severity =
    | Hidden
    | Info
    | Warning
    | Error

type SourceRange =
    { File: string
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int }

/// Every compiler or runtime diagnostic is projected into this machine-readable shape.
type StructuredDiagnostic =
    { Code: string
      Severity: Severity
      Range: SourceRange option
      Message: string
      OffendingConstruct: string option
      SuggestedFix: string option
      DocsUrl: string option
      Context: Map<string, string> }

module StructuredDiagnostic =
    let create code severity message =
        { Code = code
          Severity = severity
          Range = None
          Message = message
          OffendingConstruct = None
          SuggestedFix = None
          DocsUrl = None
          Context = Map.empty }

    let withContext key value (d: StructuredDiagnostic) =
        { d with Context = Map.add key value d.Context }

    let withFix fix (d: StructuredDiagnostic) = { d with SuggestedFix = Some fix }

module Pillar =
    [<Literal>]
    let Id = "Diagnostics"

    [<Literal>]
    let Stability = "Stable"

    [<Literal>]
    let Status = "Skeleton"
