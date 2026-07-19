module Circus.DevHost.Evidence

open System
open System.Text.Json

open Domain
open Circus.DevHost.Adapters

/// Stable JSON shape used by both `doctor --json` and `verify all --json`.
///
/// Status mapping:
///   pass    -> "pass"
///   fail    -> "fail"
///   skip    -> "skip"
///   unavailable -> "unavailable"
type EvidenceCheck = {
    mutable name: string
    mutable status: string
    mutable detail: string
}

type EvidenceReport = {
    mutable schema_version: int
    mutable generated_at: string
    mutable tool_version: string
    mutable repository_commit: string
    mutable repository_tree: string
    mutable overall_status: string
    mutable checks_total: int
    mutable checks_passed: int
    mutable checks_failed: int
    mutable checks_skipped: int
    mutable checks: EvidenceCheck list
}

let schemaVersion = 1

let mapStatus (s: CheckStatus) : string =
    match s with
    | Passed -> "pass"
    | Skipped _ -> "skip"
    | Failed _ -> "fail"

let mapOverall (results: CheckResult list) : string =
    let anyFailed =
        results
        |> List.exists (fun r -> match r.Status with Failed _ -> true | _ -> false)
    let anySkipped =
        results
        |> List.exists (fun r -> match r.Status with Skipped _ -> true | _ -> false)
    if anyFailed then "fail"
    elif anySkipped then "unavailable"
    else "pass"

let build (commit: string) (tree: string) (toolVersion: string) (results: CheckResult list) : EvidenceReport =
    let passed = results |> List.filter (fun r -> match r.Status with Passed -> true | _ -> false) |> List.length
    let failed = results |> List.filter (fun r -> match r.Status with Failed _ -> true | _ -> false) |> List.length
    let skipped = results |> List.filter (fun r -> match r.Status with Skipped _ -> true | _ -> false) |> List.length
    let checks =
        results
        |> List.map (fun r ->
            { name = r.Name
              status = mapStatus r.Status
              detail = r.Detail |> Option.defaultValue (renderFailureSafe r.Status) })
    {
        schema_version = schemaVersion
        generated_at = DateTimeOffset.UtcNow.ToString("o")
        tool_version = toolVersion
        repository_commit = commit
        repository_tree = tree
        overall_status = mapOverall results
        checks_total = results.Length
        checks_passed = passed
        checks_failed = failed
        checks_skipped = skipped
        checks = checks
    }

let private renderFailureSafe (s: CheckStatus) =
    match s with
    | Failed f -> renderFailure f
    | _ -> ""

/// Render the report as JSON. Indented for legibility.
let toJson (report: EvidenceReport) : string =
    let opts = JsonSerializerOptions(WriteIndented = true)
    JsonSerializer.Serialize(report, opts)

/// Persist the report to a file. The path is `$toolRoot/evidence/devhost-summary.json`.
let persist
    (fs: IFilesystem)
    (path: string)
    (report: EvidenceReport)
    : Result<string, DevHostFailure> =
    try
        let dir = System.IO.Path.GetDirectoryName path
        if not (fs.IsDirectory dir) then fs.CreateDirectory dir
        let text = toJson report
        fs.WriteAllText (path, text)
        Ok path
    with ex ->
        Error(DownloadFailure("evidence", ex.Message))
