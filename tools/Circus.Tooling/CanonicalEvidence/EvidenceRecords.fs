module Circus.Tooling.CanonicalEvidence.EvidenceRecords

// =============================================================================
// Canonical evidence – per-check evidence records
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01
//
// This module defines the evidence record types for the provider's canonical
// execution evidence. Each check produces one record with deterministic
// identity derived from canonical semantic content.
//
// Evidence record schema:
//   - schema_version: int (1)
//   - evidence_id: string (SHA-256 of canonical content)
//   - check_id: string
//   - required: bool
//   - provider_id: string ("circus-canonical-evidence")
//   - provider_version: string
//   - command: string
//   - arguments: string list
//   - working_directory: string
//   - started_at: string (ISO 8601)
//   - duration_ms: int64
//   - exit_code: int option
//   - result: string ("pass" | "fail" | "unavailable")
//   - tests_total: int option
//   - tests_passed: int option
//   - tests_ignored: int option
//   - tests_failed: int option
//   - tests_errored: int option
//   - stdout_sha256: string option
//   - stderr_sha256: string option
//   - stdout_byte_length: int64 option
//   - stderr_byte_length: int64 option
//   - tested_commit_oid: string
//   - tested_tree_oid: string
//   - working_tree_clean: bool
//   - provider_binary_sha256: string option
//   - tooling_binary_sha256: string option
//   - test_binary_sha256: string option
// =============================================================================

open System
open System.Globalization
open System.Text

open Circus.Tooling.FSharpDiagnostics.Hashing

// -----------------------------------------------------------------------------
// Status
// -----------------------------------------------------------------------------

type RecordStatus =
    | RecordPass
    | RecordFail
    | RecordUnavailable

let recordStatusToken (s: RecordStatus) : string =
    match s with
    | RecordPass -> "pass"
    | RecordFail -> "fail"
    | RecordUnavailable -> "unavailable"

let tryParseRecordStatus (token: string) : RecordStatus option =
    match token with
    | "pass" -> Some RecordPass
    | "fail" -> Some RecordFail
    | "unavailable" -> Some RecordUnavailable
    | _ -> None

// -----------------------------------------------------------------------------
// Evidence record
// -----------------------------------------------------------------------------

type CanonicalExecutionEvidence = {
    SchemaVersion: int
    EvidenceId: string
    CheckId: string
    Required: bool
    ProviderId: string
    ProviderVersion: string
    Command: string
    Arguments: string list
    WorkingDirectory: string
    StartedAt: string
    DurationMs: int64
    ExitCode: int option
    Result: RecordStatus
    TestsTotal: int option
    TestsPassed: int option
    TestsIgnored: int option
    TestsFailed: int option
    TestsErrored: int option
    StdoutSha256: string option
    StderrSha256: string option
    StdoutByteLength: int64 option
    StderrByteLength: int64 option
    TestedCommitOid: string
    TestedTreeOid: string
    WorkingTreeClean: bool
    ProviderBinarySha256: string option
    ToolingBinarySha256: string option
    TestBinarySha256: string option
}

[<Literal>]
let EvidenceSchemaVersion = 1

[<Literal>]
let ProviderIdValue = "circus-canonical-evidence"

[<Literal>]
let ProviderVersionEvidence = "1.0.0"

// -----------------------------------------------------------------------------
// Deterministic evidence ID
//
// The evidence_id is derived from canonical semantic content, excluding:
// - absolute temporary roots
// - nondeterministic collection order
// - ANSI formatting
// - execution timestamps (where not semantically required)
// - close-report formatting
// -----------------------------------------------------------------------------

let internal escapeJsonString (s: string) : string =
    let sb = StringBuilder(s.Length + 2)
    sb.Append '"' |> ignore
    for c in s do
        if c = '\\' then sb.Append("\\\\") |> ignore
        elif c = '"' then sb.Append("\\\"") |> ignore
        elif c = '\n' then sb.Append("\\n") |> ignore
        elif c = '\r' then sb.Append("\\r") |> ignore
        elif c = '\t' then sb.Append("\\t") |> ignore
        elif int c < 0x20 then
            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
        else sb.Append c |> ignore
    sb.Append '"' |> ignore
    sb.ToString()

let internal strListJson (vs: string list) : string =
    "[" + (vs |> List.map escapeJsonString |> String.concat ",") + "]"

let internal optIntStr (v: int option) : string =
    match v with
    | None -> "null"
    | Some n -> n.ToString(CultureInfo.InvariantCulture)

let internal optInt64Str (v: int64 option) : string =
    match v with
    | None -> "null"
    | Some n -> n.ToString(CultureInfo.InvariantCulture)

let internal boolStr (v: bool) : string =
    if v then "true" else "false"

/// Render the canonicalisation form for an evidence record. This form
/// is used to derive the deterministic evidence_id. Excludes the
/// evidence_id field itself.
let renderEvidenceCanonicalisationForm (e: CanonicalExecutionEvidence) : string =
    let sb = StringBuilder()
    sb.Append "{" |> ignore
    sb.Append "\"schema_version\":" |> ignore
    sb.Append(e.SchemaVersion.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"check_id\":" |> ignore
    sb.Append(escapeJsonString e.CheckId) |> ignore
    sb.Append ",\"provider_id\":" |> ignore
    sb.Append(escapeJsonString e.ProviderId) |> ignore
    sb.Append ",\"provider_version\":" |> ignore
    sb.Append(escapeJsonString e.ProviderVersion) |> ignore
    sb.Append ",\"command\":" |> ignore
    sb.Append(escapeJsonString e.Command) |> ignore
    sb.Append ",\"arguments\":" |> ignore
    sb.Append(strListJson e.Arguments) |> ignore
    sb.Append ",\"working_directory\":" |> ignore
    sb.Append(escapeJsonString e.WorkingDirectory) |> ignore
    sb.Append ",\"duration_ms\":" |> ignore
    sb.Append(e.DurationMs.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"exit_code\":" |> ignore
    sb.Append(optIntStr e.ExitCode) |> ignore
    sb.Append ",\"result\":" |> ignore
    sb.Append(escapeJsonString (recordStatusToken e.Result)) |> ignore
    sb.Append ",\"tests_total\":" |> ignore
    sb.Append(optIntStr e.TestsTotal) |> ignore
    sb.Append ",\"tests_passed\":" |> ignore
    sb.Append(optIntStr e.TestsPassed) |> ignore
    sb.Append ",\"tests_ignored\":" |> ignore
    sb.Append(optIntStr e.TestsIgnored) |> ignore
    sb.Append ",\"tests_failed\":" |> ignore
    sb.Append(optIntStr e.TestsFailed) |> ignore
    sb.Append ",\"tests_errored\":" |> ignore
    sb.Append(optIntStr e.TestsErrored) |> ignore
    sb.Append ",\"stdout_sha256\":" |> ignore
    match e.StdoutSha256 with
    | None -> sb.Append "null" |> ignore
    | Some h -> sb.Append(escapeJsonString h) |> ignore
    sb.Append ",\"stderr_sha256\":" |> ignore
    match e.StderrSha256 with
    | None -> sb.Append "null" |> ignore
    | Some h -> sb.Append(escapeJsonString h) |> ignore
    sb.Append ",\"stdout_byte_length\":" |> ignore
    sb.Append(optInt64Str e.StdoutByteLength) |> ignore
    sb.Append ",\"stderr_byte_length\":" |> ignore
    sb.Append(optInt64Str e.StderrByteLength) |> ignore
    sb.Append ",\"tested_commit_oid\":" |> ignore
    sb.Append(escapeJsonString e.TestedCommitOid) |> ignore
    sb.Append ",\"tested_tree_oid\":" |> ignore
    sb.Append(escapeJsonString e.TestedTreeOid) |> ignore
    sb.Append ",\"working_tree_clean\":" |> ignore
    sb.Append(boolStr e.WorkingTreeClean) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

/// Compute the deterministic evidence_id from canonical content.
let computeEvidenceId (e: CanonicalExecutionEvidence) : string =
    let canon = renderEvidenceCanonicalisationForm e
    sha256OfUtf8 canon

/// Create an evidence record with deterministic identity.
let createEvidenceRecord
    (checkId: string)
    (required: bool)
    (command: string)
    (arguments: string list)
    (workingDirectory: string)
    (startedAt: string)
    (durationMs: int64)
    (exitCode: int option)
    (result: RecordStatus)
    (testsTotal: int option)
    (testsPassed: int option)
    (testsIgnored: int option)
    (testsFailed: int option)
    (testsErrored: int option)
    (stdoutSha256: string option)
    (stderrSha256: string option)
    (stdoutByteLength: int64 option)
    (stderrByteLength: int64 option)
    (testedCommitOid: string)
    (testedTreeOid: string)
    (workingTreeClean: bool)
    (providerBinarySha256: string option)
    (toolingBinarySha256: string option)
    (testBinarySha256: string option)
    : CanonicalExecutionEvidence =
    let record = {
        SchemaVersion = EvidenceSchemaVersion
        EvidenceId = "" // Will be computed
        CheckId = checkId
        Required = required
        ProviderId = ProviderIdValue
        ProviderVersion = ProviderVersionEvidence
        Command = command
        Arguments = arguments
        WorkingDirectory = workingDirectory
        StartedAt = startedAt
        DurationMs = durationMs
        ExitCode = exitCode
        Result = result
        TestsTotal = testsTotal
        TestsPassed = testsPassed
        TestsIgnored = testsIgnored
        TestsFailed = testsFailed
        TestsErrored = testsErrored
        StdoutSha256 = stdoutSha256
        StderrSha256 = stderrSha256
        StdoutByteLength = stdoutByteLength
        StderrByteLength = stderrByteLength
        TestedCommitOid = testedCommitOid
        TestedTreeOid = testedTreeOid
        WorkingTreeClean = workingTreeClean
        ProviderBinarySha256 = providerBinarySha256
        ToolingBinarySha256 = toolingBinarySha256
        TestBinarySha256 = testBinarySha256
    }
    let evidenceId = computeEvidenceId record
    { record with EvidenceId = evidenceId }

// -----------------------------------------------------------------------------
// Aggregate
// -----------------------------------------------------------------------------

type CanonicalExecutionAggregate = {
    SchemaVersion: int
    SubjectCommitOid: string
    SubjectTreeOid: string
    RecordsTotal: int
    RecordsPassed: int
    RecordsFailed: int
    RecordsUnavailable: int
    TestsTotal: int
    TestsPassed: int
    TestsIgnored: int
    TestsFailed: int
    TestsErrored: int
    RequiredChecksTotal: int
    RequiredChecksPassed: int
    RequiredChecksFailed: int
    RecordIds: string list
    OverallStatus: RecordStatus
    SemanticSha256: string
}

[<Literal>]
let AggregateSchemaVersion = 1

/// Compute aggregate from evidence records.
let computeAggregate
    (subjectCommitOid: string)
    (subjectTreeOid: string)
    (records: CanonicalExecutionEvidence list)
    : CanonicalExecutionAggregate =
    let recordsTotal = List.length records
    let recordsPassed = records |> List.filter (fun r -> r.Result = RecordPass) |> List.length
    let recordsFailed = records |> List.filter (fun r -> r.Result = RecordFail) |> List.length
    let recordsUnavailable = records |> List.filter (fun r -> r.Result = RecordUnavailable) |> List.length
    
    let testsTotal = records |> List.choose (fun r -> r.TestsTotal) |> List.sum
    let testsPassed = records |> List.choose (fun r -> r.TestsPassed) |> List.sum
    let testsIgnored = records |> List.choose (fun r -> r.TestsIgnored) |> List.sum
    let testsFailed = records |> List.choose (fun r -> r.TestsFailed) |> List.sum
    let testsErrored = records |> List.choose (fun r -> r.TestsErrored) |> List.sum
    
    // Required checks only - filter to required=true records
    let requiredRecords = records |> List.filter (fun r -> r.Required)
    let requiredChecksTotal = List.length requiredRecords
    let requiredChecksPassed = requiredRecords |> List.filter (fun r -> r.Result = RecordPass) |> List.length
    let requiredChecksFailed = requiredRecords |> List.filter (fun r -> r.Result = RecordFail) |> List.length
    
    let recordIds = records |> List.map (fun r -> r.EvidenceId) |> List.sort
    
    // Overall status considers required check failures/unavailability as failures
    let overallStatus =
        if requiredChecksFailed > 0 then RecordFail
        elif requiredChecksTotal > 0 && requiredChecksPassed = requiredChecksTotal then RecordPass
        elif requiredChecksTotal = 0 then RecordPass  // No required checks
        else RecordUnavailable  // Some required checks unavailable
    
    { SchemaVersion = AggregateSchemaVersion
      SubjectCommitOid = subjectCommitOid
      SubjectTreeOid = subjectTreeOid
      RecordsTotal = recordsTotal
      RecordsPassed = recordsPassed
      RecordsFailed = recordsFailed
      RecordsUnavailable = recordsUnavailable
      TestsTotal = testsTotal
      TestsPassed = testsPassed
      TestsIgnored = testsIgnored
      TestsFailed = testsFailed
      TestsErrored = testsErrored
      RequiredChecksTotal = requiredChecksTotal
      RequiredChecksPassed = requiredChecksPassed
      RequiredChecksFailed = requiredChecksFailed
      RecordIds = recordIds
      OverallStatus = overallStatus
      SemanticSha256 = "" } // Will be computed

/// Render aggregate canonicalisation form.
let renderAggregateCanonicalisationForm (a: CanonicalExecutionAggregate) : string =
    let sb = StringBuilder()
    sb.Append "{" |> ignore
    sb.Append "\"schema_version\":" |> ignore
    sb.Append(a.SchemaVersion.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"subject_commit_oid\":" |> ignore
    sb.Append(escapeJsonString a.SubjectCommitOid) |> ignore
    sb.Append ",\"subject_tree_oid\":" |> ignore
    sb.Append(escapeJsonString a.SubjectTreeOid) |> ignore
    sb.Append ",\"records_total\":" |> ignore
    sb.Append(a.RecordsTotal.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"records_passed\":" |> ignore
    sb.Append(a.RecordsPassed.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"records_failed\":" |> ignore
    sb.Append(a.RecordsFailed.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"records_unavailable\":" |> ignore
    sb.Append(a.RecordsUnavailable.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"tests_total\":" |> ignore
    sb.Append(a.TestsTotal.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"tests_passed\":" |> ignore
    sb.Append(a.TestsPassed.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"tests_ignored\":" |> ignore
    sb.Append(a.TestsIgnored.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"tests_failed\":" |> ignore
    sb.Append(a.TestsFailed.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"tests_errored\":" |> ignore
    sb.Append(a.TestsErrored.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"required_checks_total\":" |> ignore
    sb.Append(a.RequiredChecksTotal.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"required_checks_passed\":" |> ignore
    sb.Append(a.RequiredChecksPassed.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"required_checks_failed\":" |> ignore
    sb.Append(a.RequiredChecksFailed.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"record_ids\":[" |> ignore
    let mutable first = true
    for id in a.RecordIds do
        if first then first <- false else sb.Append "," |> ignore
        sb.Append(escapeJsonString id) |> ignore
    sb.Append "]" |> ignore
    sb.Append ",\"overall_status\":" |> ignore
    sb.Append(escapeJsonString (recordStatusToken a.OverallStatus)) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

/// Compute aggregate semantic hash.
let computeAggregateSemanticHash (a: CanonicalExecutionAggregate) : string =
    let canon = renderAggregateCanonicalisationForm a
    sha256OfUtf8 canon

/// Finalize aggregate with semantic hash.
let finalizeAggregate (a: CanonicalExecutionAggregate) : CanonicalExecutionAggregate =
    let hash = computeAggregateSemanticHash a
    { a with SemanticSha256 = hash }

// -----------------------------------------------------------------------------
// Serialization
// -----------------------------------------------------------------------------

let renderEvidenceWireJson (e: CanonicalExecutionEvidence) : string =
    renderEvidenceCanonicalisationForm e

let renderAggregateWireJson (a: CanonicalExecutionAggregate) : string =
    renderAggregateCanonicalisationForm a
