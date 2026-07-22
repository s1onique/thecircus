module Circus.Tooling.NoForcePush.Rendering

open System
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

/// Render a single diagnostic as a human-readable line.
let renderDiagnosticHuman (d: Types.Diagnostic) : string =
    sprintf "[%s] %s:%d:%d %s: %s"
        d.RuleId
        d.Path
        d.Line
        d.Column
        d.RuleId
        d.Detail

/// Render the static policy result as human-readable text.
let renderStaticPolicyHuman (result: Types.StaticPolicyResult) : string =
    let sb = StringBuilder()
    
    sb.AppendLine(sprintf "No-force-push static policy verification") |> ignore
    sb.AppendLine(sprintf "Repository: %s" result.RepositoryRoot) |> ignore
    sb.AppendLine(sprintf "Files examined: %d" result.FilesExamined) |> ignore
    
    if not (List.isEmpty result.OperationalErrors) then
        sb.AppendLine("Operational errors:") |> ignore
        for err in result.OperationalErrors do
            sb.AppendLine(sprintf "  - %s" err) |> ignore
    
    if List.isEmpty result.Diagnostics then
        sb.AppendLine("No violations detected.") |> ignore
    else
        sb.AppendLine(sprintf "Violations (%d):" (List.length result.Diagnostics)) |> ignore
        for d in result.Diagnostics do
            sb.AppendLine(sprintf "  %s" (renderDiagnosticHuman d)) |> ignore
    
    sb.ToString()

/// Render a diagnostic as a structured JSON object.
type DiagnosticJson = {
    [<JsonPropertyName("rule_id")>]
    RuleId: string
    [<JsonPropertyName("path")>]
    Path: string
    [<JsonPropertyName("line")>]
    Line: int
    [<JsonPropertyName("column")>]
    Column: int
    [<JsonPropertyName("detail")>]
    Detail: string
    [<JsonPropertyName("normalized_command")>]
    NormalizedCommand: string
}

let diagnosticToJson (d: Types.Diagnostic) : DiagnosticJson = {
    RuleId = d.RuleId
    Path = d.Path
    Line = d.Line
    Column = d.Column
    Detail = d.Detail
    NormalizedCommand = d.NormalizedCommand
}

/// Render the static policy result as JSON.
type StaticPolicyJson = {
    [<JsonPropertyName("schema_version")>]
    SchemaVersion: int
    [<JsonPropertyName("repository_root")>]
    RepositoryRoot: string
    [<JsonPropertyName("files_examined")>]
    FilesExamined: int
    [<JsonPropertyName("violation_count")>]
    ViolationCount: int
    [<JsonPropertyName("operational_errors")>]
    OperationalErrors: string list
    [<JsonPropertyName("diagnostics")>]
    Diagnostics: DiagnosticJson list
}

let renderStaticPolicyJson (result: Types.StaticPolicyResult) : string =
    let json = {
        SchemaVersion = 1
        RepositoryRoot = result.RepositoryRoot
        FilesExamined = result.FilesExamined
        ViolationCount = List.length result.Diagnostics
        OperationalErrors = result.OperationalErrors
        Diagnostics = result.Diagnostics |> List.map diagnosticToJson
    }
    
    let opts = JsonSerializerOptions()
    opts.WriteIndented <- true
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
    
    JsonSerializer.Serialize(json, opts)

/// Render the pre-push outcome as human-readable text.
let renderPrePushOutcomeHuman (outcome: Types.PrePushOutcome) : string =
    match outcome with
    | Types.Allowed update ->
        sprintf "ALLOWED %s: %s -> %s" update.RemoteRef update.RemoteOid update.LocalOid
    | Types.Rejected(update, reason) ->
        sprintf "REJECTED %s: %s -> %s (%s)" update.RemoteRef update.RemoteOid update.LocalOid reason
    | Types.OperationalFailure(update, detail) ->
        sprintf "ERROR %s: %s -> %s (%s)" update.RemoteRef update.RemoteOid update.LocalOid detail

/// Render pre-push outcomes as a summary.
let renderPrePushSummaryHuman (outcomes: Types.PrePushOutcome list) : string =
    let allowed = outcomes |> List.filter (function Types.Allowed _ -> true | _ -> false) |> List.length
    let rejected = outcomes |> List.filter (function Types.Rejected _ -> true | _ -> false) |> List.length
    let failed = outcomes |> List.filter (function Types.OperationalFailure _ -> true | _ -> false) |> List.length
    
    let sb = StringBuilder()
    sb.AppendLine(sprintf "Pre-push verification summary:") |> ignore
    sb.AppendLine(sprintf "  allowed: %d" allowed) |> ignore
    sb.AppendLine(sprintf "  rejected: %d" rejected) |> ignore
    sb.AppendLine(sprintf "  errors: %d" failed) |> ignore
    
    if not (List.isEmpty outcomes) then
        sb.AppendLine("Details:") |> ignore
        for outcome in outcomes do
            sb.AppendLine(sprintf "  %s" (renderPrePushOutcomeHuman outcome)) |> ignore
    
    sb.ToString()
