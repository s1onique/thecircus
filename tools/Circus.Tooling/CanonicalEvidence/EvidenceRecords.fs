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
    FailureKind: string option
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

/// Canonical representation of FailureKind for wire format.
/// When None, uses the unambiguous token "<NONE>".
let internal failureKindCanonicalToken (fk: string option) : string =
    match fk with
    | None -> "<NONE>"
    | Some v -> v

/// Parse the canonical FailureKind token from wire format.
let internal parseFailureKindCanonicalToken (token: string) : string option =
    if token = "<NONE>" then None else Some token

/// Render the canonicalisation form for an evidence record. This form
/// is used to derive the deterministic evidence_id. Excludes the
/// evidence_id field itself. Includes FailureKind for semantic identity.
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
    sb.Append ",\"failure_kind\":" |> ignore
    sb.Append(escapeJsonString (failureKindCanonicalToken e.FailureKind)) |> ignore
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
    (failureKind: string option)
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
        FailureKind = failureKind
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
    // Required unavailable checks count as failures per ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION01
    let requiredChecksFailed =
        requiredRecords
        |> List.filter (fun r -> r.Result = RecordFail || r.Result = RecordUnavailable)
        |> List.length
    
    let recordIds = records |> List.map (fun r -> r.EvidenceId) |> List.sort
    
    // Overall status: any required failure or unavailability means overall fail
    let overallStatus =
        if requiredChecksFailed > 0 then RecordFail
        elif requiredChecksTotal > 0 && requiredChecksPassed = requiredChecksTotal then RecordPass
        elif requiredChecksTotal = 0 then RecordPass  // No required checks
        else RecordFail  // Some required checks unavailable = fail
    
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

/// Render the evidence record to wire JSON format including all fields.
/// The wire format includes FailureKind for compatibility.
let renderEvidenceWireJson (e: CanonicalExecutionEvidence) : string =
    let sb = StringBuilder()
    sb.Append "{" |> ignore
    sb.Append "\"schema_version\":" |> ignore
    sb.Append(e.SchemaVersion.ToString(CultureInfo.InvariantCulture)) |> ignore
    sb.Append ",\"evidence_id\":" |> ignore
    sb.Append(escapeJsonString e.EvidenceId) |> ignore
    sb.Append ",\"check_id\":" |> ignore
    sb.Append(escapeJsonString e.CheckId) |> ignore
    sb.Append ",\"required\":" |> ignore
    sb.Append(if e.Required then "true" else "false") |> ignore
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
    sb.Append ",\"started_at\":" |> ignore
    sb.Append(escapeJsonString e.StartedAt) |> ignore
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
    sb.Append ",\"failure_kind\":" |> ignore
    sb.Append(escapeJsonString (failureKindCanonicalToken e.FailureKind)) |> ignore
    sb.Append ",\"tested_commit_oid\":" |> ignore
    sb.Append(escapeJsonString e.TestedCommitOid) |> ignore
    sb.Append ",\"tested_tree_oid\":" |> ignore
    sb.Append(escapeJsonString e.TestedTreeOid) |> ignore
    sb.Append ",\"working_tree_clean\":" |> ignore
    sb.Append(if e.WorkingTreeClean then "true" else "false") |> ignore
    sb.Append ",\"provider_binary_sha256\":" |> ignore
    match e.ProviderBinarySha256 with
    | None -> sb.Append "null" |> ignore
    | Some h -> sb.Append(escapeJsonString h) |> ignore
    sb.Append ",\"tooling_binary_sha256\":" |> ignore
    match e.ToolingBinarySha256 with
    | None -> sb.Append "null" |> ignore
    | Some h -> sb.Append(escapeJsonString h) |> ignore
    sb.Append ",\"test_binary_sha256\":" |> ignore
    match e.TestBinarySha256 with
    | None -> sb.Append "null" |> ignore
    | Some h -> sb.Append(escapeJsonString h) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

/// Render the aggregate to wire JSON format.
let renderAggregateWireJson (a: CanonicalExecutionAggregate) : string =
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
    sb.Append "]," |> ignore
    sb.Append "\"overall_status\":" |> ignore
    sb.Append(escapeJsonString (recordStatusToken a.OverallStatus)) |> ignore
    sb.Append ",\"semantic_sha256\":" |> ignore
    sb.Append(escapeJsonString a.SemanticSha256) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

// -----------------------------------------------------------------------------
// Strict evidence wire parser
// -----------------------------------------------------------------------------

open System.Text.Json

/// Strict typed errors for evidence wire parsing.
[<RequireQualifiedAccess>]
type EvidenceWireParseError =
    | InvalidJson of detail: string
    | DuplicateProperty of propertyName: string
    | MissingField of fieldName: string
    | WrongFieldType of fieldName: string * expected: string * actual: string
    | UnsupportedSchemaVersion of actual: int
    | UnknownResult of value: string
    | InvalidInteger of fieldName: string * detail: string
    | InvalidSha256 of fieldName: string * value: string
    | InvalidEvidenceId of value: string
    | InvalidTimestamp of value: string
    | InconsistentTestCounts of detail: string
    | TrailingContent

/// Location info for evidence wire parse errors.
type LocatedEvidenceWireError = {
    SourcePath: string
    Line: int
    Error: EvidenceWireParseError
}

let evidenceWireParseErrorToString (e: EvidenceWireParseError) : string =
    match e with
    | EvidenceWireParseError.InvalidJson d -> sprintf "invalid JSON: %s" d
    | EvidenceWireParseError.DuplicateProperty p -> sprintf "duplicate property: %s" p
    | EvidenceWireParseError.MissingField f -> sprintf "missing required field: %s" f
    | EvidenceWireParseError.WrongFieldType (f, exp, act) -> sprintf "wrong type for %s: expected %s, got %s" f exp act
    | EvidenceWireParseError.UnsupportedSchemaVersion v -> sprintf "unsupported schema_version: %d" v
    | EvidenceWireParseError.UnknownResult v -> sprintf "unknown result value: %s" v
    | EvidenceWireParseError.InvalidInteger (f, d) -> sprintf "invalid integer for %s: %s" f d
    | EvidenceWireParseError.InvalidSha256 (f, v) -> sprintf "invalid SHA-256 for %s: %s" f v
    | EvidenceWireParseError.InvalidEvidenceId v -> sprintf "invalid evidence_id: %s" v
    | EvidenceWireParseError.InvalidTimestamp v -> sprintf "invalid timestamp: %s" v
    | EvidenceWireParseError.InconsistentTestCounts d -> sprintf "inconsistent test counts: %s" d
    | EvidenceWireParseError.TrailingContent -> "trailing content after JSON object"

let locatedEvidenceWireErrorToString (e: LocatedEvidenceWireError) : string =
    sprintf "%s:%d: %s" e.SourcePath e.Line (evidenceWireParseErrorToString e.Error)

/// Check if a string is a valid SHA-256 hex value (64 lowercase hex chars).
let private isValidSha256 (s: string) : bool =
    if isNull s || s.Length <> 64 then false
    else
        let mutable ok = true
        for c in s do
            if not ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) then ok <- false
        ok

/// Parse a JSON string value, reporting MissingField and WrongFieldType.
let private parseRequiredJsonString (el: JsonElement) (name: string) (errors: ResizeArray<EvidenceWireParseError>) : string =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then
        if found.ValueKind = JsonValueKind.String then
            found.GetString()
        elif found.ValueKind = JsonValueKind.Null then
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "string", "null"))
            ""
        else
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "string", string found.ValueKind))
            ""
    else
        errors.Add(EvidenceWireParseError.MissingField name)
        ""

/// Parse a JSON number value as int64, rejecting non-integers.
let private parseRequiredJsonInt64 (el: JsonElement) (name: string) (errors: ResizeArray<EvidenceWireParseError>) : int64 =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then
        if found.ValueKind = JsonValueKind.Number then
            let mutable intValue = 0L
            if found.TryGetInt64(&intValue) then
                intValue
            else
                errors.Add(EvidenceWireParseError.InvalidInteger(name, "not an integer or out of range"))
                0L
        elif found.ValueKind = JsonValueKind.Null then
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "int64", "null"))
            0L
        else
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "int64", string found.ValueKind))
            0L
    else
        errors.Add(EvidenceWireParseError.MissingField name)
        0L

/// Parse a JSON number value as int, rejecting non-integers and negative values.
let private parseRequiredJsonInt (el: JsonElement) (name: string) (errors: ResizeArray<EvidenceWireParseError>) : int =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then
        if found.ValueKind = JsonValueKind.Number then
            let mutable intValue = 0
            if found.TryGetInt32(&intValue) then
                intValue
            else
                errors.Add(EvidenceWireParseError.InvalidInteger(name, "not an integer or out of range"))
                0
        elif found.ValueKind = JsonValueKind.Null then
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "int", "null"))
            0
        else
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "int", string found.ValueKind))
            0
    else
        errors.Add(EvidenceWireParseError.MissingField name)
        0

/// Parse a JSON bool value.
let private parseRequiredJsonBool (el: JsonElement) (name: string) (errors: ResizeArray<EvidenceWireParseError>) : bool =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then
        if found.ValueKind = JsonValueKind.True || found.ValueKind = JsonValueKind.False then
            found.GetBoolean()
        elif found.ValueKind = JsonValueKind.Null then
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "bool", "null"))
            false
        else
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "bool", string found.ValueKind))
            false
    else
        errors.Add(EvidenceWireParseError.MissingField name)
        false

/// Parse a JSON string array.
let private parseRequiredJsonStringArray (el: JsonElement) (name: string) (errors: ResizeArray<EvidenceWireParseError>) : string list =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then
        if found.ValueKind = JsonValueKind.Array then
            let mutable items = []
            for item in found.EnumerateArray() do
                if item.ValueKind = JsonValueKind.String then
                    items <- (item.GetString()) :: items
                elif item.ValueKind = JsonValueKind.Null then
                    errors.Add(EvidenceWireParseError.WrongFieldType(name + "[*]", "string", "null"))
                    items <- "" :: items
                else
                    errors.Add(EvidenceWireParseError.WrongFieldType(name + "[*]", "string", string item.ValueKind))
                    items <- "" :: items
            List.rev items
        elif found.ValueKind = JsonValueKind.Null then
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "array", "null"))
            []
        else
            errors.Add(EvidenceWireParseError.WrongFieldType(name, "array", string found.ValueKind))
            []
    else
        errors.Add(EvidenceWireParseError.MissingField name)
        []

/// Parse an optional JSON string that may be null.
let private parseOptionalJsonString (el: JsonElement) (name: string) : string option =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then
        if found.ValueKind = JsonValueKind.String then Some(found.GetString())
        elif found.ValueKind = JsonValueKind.Null then None
        else None
    else None

/// Parse an optional JSON integer that may be null.
let private parseOptionalJsonInt (el: JsonElement) (name: string) : int option =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then
        if found.ValueKind = JsonValueKind.Number then
            let mutable v = 0
            if found.TryGetInt32(&v) then Some v else None
        elif found.ValueKind = JsonValueKind.Null then None
        else None
    else None

/// Parse an optional JSON int64 that may be null.
let private parseOptionalJsonInt64 (el: JsonElement) (name: string) : int64 option =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then
        if found.ValueKind = JsonValueKind.Number then
            let mutable v = 0L
            if found.TryGetInt64(&v) then Some v else None
        elif found.ValueKind = JsonValueKind.Null then None
        else None
    else None

/// Check for duplicate properties in a JSON object.
let private checkNoDuplicateProperties (el: JsonElement) (seen: System.Collections.Generic.Dictionary<string, bool>) : ResizeArray<EvidenceWireParseError> =
    let errors = ResizeArray()
    for prop in el.EnumerateObject() do
        if seen.ContainsKey(prop.Name) then
            errors.Add(EvidenceWireParseError.DuplicateProperty prop.Name)
        else
            seen.[prop.Name] <- true
    errors

/// Parse one evidence record JSON object strictly.
let private parseEvidenceRecordObject (el: JsonElement) : EvidenceWireParseError list =
    let errors = ResizeArray()
    let seen = System.Collections.Generic.Dictionary()
    errors.AddRange(checkNoDuplicateProperties el seen)

    // Required fields
    let schemaVersion = parseRequiredJsonInt el "schema_version" errors
    let evidenceId = parseRequiredJsonString el "evidence_id" errors
    let checkId = parseRequiredJsonString el "check_id" errors
    let required = parseRequiredJsonBool el "required" errors
    let providerId = parseRequiredJsonString el "provider_id" errors
    let providerVersion = parseRequiredJsonString el "provider_version" errors
    let command = parseRequiredJsonString el "command" errors
    let arguments = parseRequiredJsonStringArray el "arguments" errors
    let workingDirectory = parseRequiredJsonString el "working_directory" errors
    let startedAt = parseRequiredJsonString el "started_at" errors
    let durationMs = parseRequiredJsonInt64 el "duration_ms" errors
    let exitCode = parseOptionalJsonInt el "exit_code"
    let resultStr = parseRequiredJsonString el "result" errors
    let testsTotal = parseOptionalJsonInt el "tests_total"
    let testsPassed = parseOptionalJsonInt el "tests_passed"
    let testsIgnored = parseOptionalJsonInt el "tests_ignored"
    let testsFailed = parseOptionalJsonInt el "tests_failed"
    let testsErrored = parseOptionalJsonInt el "tests_errored"
    let stdoutSha256 = parseOptionalJsonString el "stdout_sha256"
    let stderrSha256 = parseOptionalJsonString el "stderr_sha256"
    let stdoutByteLength = parseOptionalJsonInt64 el "stdout_byte_length"
    let stderrByteLength = parseOptionalJsonInt64 el "stderr_byte_length"
    let failureKindStr = parseOptionalJsonString el "failure_kind"
    let testedCommitOid = parseRequiredJsonString el "tested_commit_oid" errors
    let testedTreeOid = parseRequiredJsonString el "tested_tree_oid" errors
    let workingTreeClean = parseRequiredJsonBool el "working_tree_clean" errors
    let providerBinarySha256 = parseOptionalJsonString el "provider_binary_sha256"
    let toolingBinarySha256 = parseOptionalJsonString el "tooling_binary_sha256"
    let testBinarySha256 = parseOptionalJsonString el "test_binary_sha256"

    // Validate schema version
    if schemaVersion <> EvidenceSchemaVersion then
        errors.Add(EvidenceWireParseError.UnsupportedSchemaVersion schemaVersion)

    // Validate evidence_id format (64 hex chars)
    if not (String.IsNullOrEmpty evidenceId) && not (System.Text.RegularExpressions.Regex.IsMatch(evidenceId, "^[0-9a-f]{64}$")) then
        errors.Add(EvidenceWireParseError.InvalidEvidenceId evidenceId)

    // Validate SHA-256 fields
    match stdoutSha256 with
    | Some v when not (isValidSha256 v) -> errors.Add(EvidenceWireParseError.InvalidSha256("stdout_sha256", v))
    | _ -> ()
    match stderrSha256 with
    | Some v when not (isValidSha256 v) -> errors.Add(EvidenceWireParseError.InvalidSha256("stderr_sha256", v))
    | _ -> ()
    match providerBinarySha256 with
    | Some v when not (isValidSha256 v) -> errors.Add(EvidenceWireParseError.InvalidSha256("provider_binary_sha256", v))
    | _ -> ()
    match toolingBinarySha256 with
    | Some v when not (isValidSha256 v) -> errors.Add(EvidenceWireParseError.InvalidSha256("tooling_binary_sha256", v))
    | _ -> ()
    match testBinarySha256 with
    | Some v when not (isValidSha256 v) -> errors.Add(EvidenceWireParseError.InvalidSha256("test_binary_sha256", v))
    | _ -> ()

    // Validate result token
    let result =
        match resultStr with
        | "pass" -> RecordPass
        | "fail" -> RecordFail
        | "unavailable" -> RecordUnavailable
        | v ->
            errors.Add(EvidenceWireParseError.UnknownResult v)
            RecordUnavailable

    // Parse FailureKind from canonical token
    let failureKind = Option.bind parseFailureKindCanonicalToken failureKindStr

    // Validate negative values
    if durationMs < 0L then
        errors.Add(EvidenceWireParseError.InvalidInteger("duration_ms", "negative value"))
    match stdoutByteLength with
    | Some v when v < 0L -> errors.Add(EvidenceWireParseError.InvalidInteger("stdout_byte_length", "negative value"))
    | _ -> ()
    match stderrByteLength with
    | Some v when v < 0L -> errors.Add(EvidenceWireParseError.InvalidInteger("stderr_byte_length", "negative value"))
    | _ -> ()
    match testsTotal with
    | Some v when v < 0 -> errors.Add(EvidenceWireParseError.InvalidInteger("tests_total", "negative value"))
    | _ -> ()
    match testsPassed with
    | Some v when v < 0 -> errors.Add(EvidenceWireParseError.InvalidInteger("tests_passed", "negative value"))
    | _ -> ()
    match testsIgnored with
    | Some v when v < 0 -> errors.Add(EvidenceWireParseError.InvalidInteger("tests_ignored", "negative value"))
    | _ -> ()
    match testsFailed with
    | Some v when v < 0 -> errors.Add(EvidenceWireParseError.InvalidInteger("tests_failed", "negative value"))
    | _ -> ()
    match testsErrored with
    | Some v when v < 0 -> errors.Add(EvidenceWireParseError.InvalidInteger("tests_errored", "negative value"))
    | _ -> ()

    // Validate test counts consistency
    match testsTotal, testsPassed, testsIgnored, testsFailed, testsErrored with
    | Some total, Some passed, Some ignored, Some failed, Some errored ->
        if total <> passed + ignored + failed + errored then
            errors.Add(EvidenceWireParseError.InconsistentTestCounts(
                sprintf "tests_total=%d but passed+ignored+failed+errored=%d" total (passed + ignored + failed + errored)))
    | _ -> ()

    List.ofSeq errors

/// Parse evidence wire JSON strictly.
let parseEvidenceWireJsonStrict (source: string) : Result<CanonicalExecutionEvidence, EvidenceWireParseError list> =
    try
        let doc = JsonDocument.Parse(source)
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then
            Result.Error [ EvidenceWireParseError.InvalidJson "root must be an object" ]
        else
            let errors = parseEvidenceRecordObject root
            if not (List.isEmpty errors) then
                Result.Error errors
            else
                // Build the record
                let evidenceId = root.GetProperty("evidence_id").GetString()
                let checkId = root.GetProperty("check_id").GetString()
                let required = root.GetProperty("required").GetBoolean()
                let providerId = root.GetProperty("provider_id").GetString()
                let providerVersion = root.GetProperty("provider_version").GetString()
                let command = root.GetProperty("command").GetString()
                let arguments =
                    [ for a in root.GetProperty("arguments").EnumerateArray() -> a.GetString() ]
                let workingDirectory = root.GetProperty("working_directory").GetString()
                let startedAt = root.GetProperty("started_at").GetString()
                let durationMs = root.GetProperty("duration_ms").GetInt64()
                let exitCode = parseOptionalJsonInt root "exit_code"
                let resultStr = root.GetProperty("result").GetString()
                let result =
                    match resultStr with
                    | "pass" -> RecordPass
                    | "fail" -> RecordFail
                    | "unavailable" -> RecordUnavailable
                    | _ -> RecordUnavailable
                let testsTotal = parseOptionalJsonInt root "tests_total"
                let testsPassed = parseOptionalJsonInt root "tests_passed"
                let testsIgnored = parseOptionalJsonInt root "tests_ignored"
                let testsFailed = parseOptionalJsonInt root "tests_failed"
                let testsErrored = parseOptionalJsonInt root "tests_errored"
                let stdoutSha256 = parseOptionalJsonString root "stdout_sha256"
                let stderrSha256 = parseOptionalJsonString root "stderr_sha256"
                let stdoutByteLength = parseOptionalJsonInt64 root "stdout_byte_length"
                let stderrByteLength = parseOptionalJsonInt64 root "stderr_byte_length"
                let failureKindStr = parseOptionalJsonString root "failure_kind"
                let failureKind = Option.bind parseFailureKindCanonicalToken failureKindStr
                let testedCommitOid = root.GetProperty("tested_commit_oid").GetString()
                let testedTreeOid = root.GetProperty("tested_tree_oid").GetString()
                let workingTreeClean = root.GetProperty("working_tree_clean").GetBoolean()
                let providerBinarySha256 = parseOptionalJsonString root "provider_binary_sha256"
                let toolingBinarySha256 = parseOptionalJsonString root "tooling_binary_sha256"
                let testBinarySha256 = parseOptionalJsonString root "test_binary_sha256"

                Result.Ok {
                    SchemaVersion = 1
                    EvidenceId = evidenceId
                    CheckId = checkId
                    Required = required
                    ProviderId = providerId
                    ProviderVersion = providerVersion
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
                    FailureKind = failureKind
                    TestedCommitOid = testedCommitOid
                    TestedTreeOid = testedTreeOid
                    WorkingTreeClean = workingTreeClean
                    ProviderBinarySha256 = providerBinarySha256
                    ToolingBinarySha256 = toolingBinarySha256
                    TestBinarySha256 = testBinarySha256
                }
    with ex ->
        Result.Error [ EvidenceWireParseError.InvalidJson ex.Message ]

// -----------------------------------------------------------------------------
// Strict aggregate wire parser
// -----------------------------------------------------------------------------

/// Strict typed errors for aggregate wire parsing.
[<RequireQualifiedAccess>]
type AggregateWireParseError =
    | InvalidJson of detail: string
    | DuplicateProperty of propertyName: string
    | MissingField of fieldName: string
    | WrongFieldType of fieldName: string * expected: string * actual: string
    | UnsupportedSchemaVersion of actual: int
    | InvalidInteger of fieldName: string * detail: string
    | InvalidRecordIds of detail: string
    | UnknownStatus of value: string
    | InconsistentTotals of detail: string
    | DuplicateRecordId of id: string
    | UnsortedRecordIds
    | InvalidSemanticSha256 of value: string
    | TrailingContent

let aggregateWireParseErrorToString (e: AggregateWireParseError) : string =
    match e with
    | AggregateWireParseError.InvalidJson d -> sprintf "invalid JSON: %s" d
    | AggregateWireParseError.DuplicateProperty p -> sprintf "duplicate property: %s" p
    | AggregateWireParseError.MissingField f -> sprintf "missing required field: %s" f
    | AggregateWireParseError.WrongFieldType (f, exp, act) -> sprintf "wrong type for %s: expected %s, got %s" f exp act
    | AggregateWireParseError.UnsupportedSchemaVersion v -> sprintf "unsupported schema_version: %d" v
    | AggregateWireParseError.InvalidInteger (f, d) -> sprintf "invalid integer for %s: %s" f d
    | AggregateWireParseError.InvalidRecordIds d -> sprintf "invalid record_ids: %s" d
    | AggregateWireParseError.UnknownStatus v -> sprintf "unknown status: %s" v
    | AggregateWireParseError.InconsistentTotals d -> sprintf "inconsistent totals: %s" d
    | AggregateWireParseError.DuplicateRecordId id -> sprintf "duplicate record ID: %s" id
    | AggregateWireParseError.UnsortedRecordIds -> "record_ids are not sorted"
    | AggregateWireParseError.InvalidSemanticSha256 v -> sprintf "invalid semantic_sha256: %s" v
    | AggregateWireParseError.TrailingContent -> "trailing content after JSON object"

/// Parse aggregate wire JSON strictly.
let parseAggregateWireJsonStrict (source: string) : Result<CanonicalExecutionAggregate, AggregateWireParseError list> =
    try
        let doc = JsonDocument.Parse(source)
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then
            Result.Error [ AggregateWireParseError.InvalidJson "root must be an object" ]
        else
            let errors = ResizeArray()
            let seen = System.Collections.Generic.Dictionary()
            for prop in root.EnumerateObject() do
                if seen.ContainsKey(prop.Name) then
                    errors.Add(AggregateWireParseError.DuplicateProperty prop.Name)
                else
                    seen.[prop.Name] <- true

            let schemaVersion =
                let mutable found = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("schema_version", &found) then
                    if found.ValueKind = JsonValueKind.Number then
                        let mutable v = 0
                        if found.TryGetInt32(&v) then v else 0
                    else 0
                else 0

            if schemaVersion <> AggregateSchemaVersion then
                errors.Add(AggregateWireParseError.UnsupportedSchemaVersion schemaVersion)

            // Build the aggregate
            let subjectCommitOid = root.GetProperty("subject_commit_oid").GetString()
            let subjectTreeOid = root.GetProperty("subject_tree_oid").GetString()
            let recordsTotal = root.GetProperty("records_total").GetInt32()
            let recordsPassed = root.GetProperty("records_passed").GetInt32()
            let recordsFailed = root.GetProperty("records_failed").GetInt32()
            let recordsUnavailable = root.GetProperty("records_unavailable").GetInt32()
            let testsTotal = root.GetProperty("tests_total").GetInt32()
            let testsPassed = root.GetProperty("tests_passed").GetInt32()
            let testsIgnored = root.GetProperty("tests_ignored").GetInt32()
            let testsFailed = root.GetProperty("tests_failed").GetInt32()
            let testsErrored = root.GetProperty("tests_errored").GetInt32()
            let requiredChecksTotal = root.GetProperty("required_checks_total").GetInt32()
            let requiredChecksPassed = root.GetProperty("required_checks_passed").GetInt32()
            let requiredChecksFailed = root.GetProperty("required_checks_failed").GetInt32()
            let recordIds =
                [ for id in root.GetProperty("record_ids").EnumerateArray() -> id.GetString() ]
            let overallStatusStr = root.GetProperty("overall_status").GetString()
            let semanticSha256 = root.GetProperty("semantic_sha256").GetString()

            // Validate status
            let overallStatus =
                match overallStatusStr with
                | "pass" -> RecordPass
                | "fail" -> RecordFail
                | "unavailable" -> RecordUnavailable
                | v ->
                    errors.Add(AggregateWireParseError.UnknownStatus v)
                    RecordUnavailable

            // Validate semantic_sha256 format
            if not (isValidSha256 semanticSha256) then
                errors.Add(AggregateWireParseError.InvalidSemanticSha256 semanticSha256)

            // Validate totals consistency
            if recordsTotal <> recordsPassed + recordsFailed + recordsUnavailable then
                errors.Add(AggregateWireParseError.InconsistentTotals(
                    sprintf "records_total=%d but passed+failed+unavailable=%d" recordsTotal (recordsPassed + recordsFailed + recordsUnavailable)))

            if requiredChecksTotal <> requiredChecksPassed + requiredChecksFailed then
                errors.Add(AggregateWireParseError.InconsistentTotals(
                    sprintf "required_checks_total=%d but passed+failed=%d" requiredChecksTotal (requiredChecksPassed + requiredChecksFailed)))

            // Validate record IDs are unique and sorted
            let seenIds = System.Collections.Generic.HashSet()
            let mutable prevId = ""
            let mutable idsOk = true
            for id in recordIds do
                if not (seenIds.Add id) then
                    errors.Add(AggregateWireParseError.DuplicateRecordId id)
                    idsOk <- false
                if id < prevId && idsOk then
                    errors.Add(AggregateWireParseError.UnsortedRecordIds)
                    idsOk <- false
                prevId <- id

            if errors.Count > 0 then
                Result.Error (List.ofSeq errors)
            else
                Result.Ok {
                    SchemaVersion = 1
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
                    SemanticSha256 = semanticSha256
                }
    with ex ->
        Result.Error [ AggregateWireParseError.InvalidJson ex.Message ]

// -----------------------------------------------------------------------------
// Artifact manifest parser
// -----------------------------------------------------------------------------

/// Artifact manifest entry.
type SnapshotArtifactEntry = {
    Path: string
    Sha256: string
    ByteLength: int64
}

// -----------------------------------------------------------------------------
// Record validation issues
// -----------------------------------------------------------------------------

/// Issue found during record validation.
/// Note: This type is also used by RecordPipeline.fs and Publication.fs.
[<RequireQualifiedAccess>]
type RecordValidationIssue =
    | RecordsEmpty
    | EvidenceIdEmpty of checkId: string
    | EvidenceIdMismatch of checkId: string * expected: string * actual: string
    | DuplicateEvidenceId of evidenceId: string
    | DuplicateCheckId of checkId: string
    | SubjectMismatch of checkId: string * expected: string * actual: string
    | TreeMismatch of checkId: string * expected: string * actual: string
    | RecordIdMismatch of expected: string * actual: string
    | CheckIdMismatch of expected: string * actual: string
    | InvalidSubjectCommit of commit: string
    | InvalidSubjectTree of tree: string
    | RequiredCheckUnavailable of checkId: string

let recordValidationIssueToString (i: RecordValidationIssue) : string =
    match i with
    | RecordValidationIssue.RecordsEmpty -> "record list is empty"
    | RecordValidationIssue.EvidenceIdEmpty id -> sprintf "empty evidence ID for check: %s" id
    | RecordValidationIssue.EvidenceIdMismatch (id, expected, actual) ->
        sprintf "evidence ID mismatch for %s: expected=%s actual=%s" id expected actual
    | RecordValidationIssue.DuplicateEvidenceId id -> sprintf "duplicate evidence ID: %s" id
    | RecordValidationIssue.DuplicateCheckId id -> sprintf "duplicate check ID: %s" id
    | RecordValidationIssue.SubjectMismatch (id, expected, actual) ->
        sprintf "subject commit mismatch for %s: expected=%s actual=%s" id expected actual
    | RecordValidationIssue.TreeMismatch (id, expected, actual) ->
        sprintf "tree OID mismatch for %s: expected=%s actual=%s" id expected actual
    | RecordValidationIssue.RecordIdMismatch (expected, actual) -> sprintf "record_id mismatch: expected=%s actual=%s" expected actual
    | RecordValidationIssue.CheckIdMismatch (expected, actual) -> sprintf "check_id mismatch: expected=%s actual=%s" expected actual
    | RecordValidationIssue.InvalidSubjectCommit c -> sprintf "invalid subject commit: %s" c
    | RecordValidationIssue.InvalidSubjectTree t -> sprintf "invalid subject tree: %s" t
    | RecordValidationIssue.RequiredCheckUnavailable id -> sprintf "required check unavailable: %s" id

/// Strict typed errors for artifact manifest parsing.
[<RequireQualifiedAccess>]
type ArtifactManifestParseError =
    | InvalidJson of detail: string
    | NotAnArray
    | BlankInteriorLine
    | UnknownPath of path: string
    | DuplicatePath of path: string
    | AbsolutePath of path: string
    | PathTraversal of path: string
    | InvalidSha256 of value: string
    | NegativeByteLength of length: int64
    | DuplicateEntry of path: string
    | MissingRequiredPath of path: string

let artifactManifestParseErrorToString (e: ArtifactManifestParseError) : string =
    match e with
    | ArtifactManifestParseError.InvalidJson d -> sprintf "invalid JSON: %s" d
    | ArtifactManifestParseError.NotAnArray -> "manifest must be a JSON array"
    | ArtifactManifestParseError.BlankInteriorLine -> "blank interior line in manifest"
    | ArtifactManifestParseError.UnknownPath p -> sprintf "unknown artifact path: %s" p
    | ArtifactManifestParseError.DuplicatePath p -> sprintf "duplicate artifact path: %s" p
    | ArtifactManifestParseError.AbsolutePath p -> sprintf "absolute path not allowed: %s" p
    | ArtifactManifestParseError.PathTraversal p -> sprintf "parent traversal not allowed: %s" p
    | ArtifactManifestParseError.InvalidSha256 v -> sprintf "invalid SHA-256: %s" v
    | ArtifactManifestParseError.NegativeByteLength l -> sprintf "negative byte_length: %d" l
    | ArtifactManifestParseError.DuplicateEntry p -> sprintf "duplicate entry: %s" p
    | ArtifactManifestParseError.MissingRequiredPath p -> sprintf "missing required path: %s" p

/// Parse artifact manifest JSONL strictly.
let parseArtifactManifestJsonlStrict (source: string) : Result<SnapshotArtifactEntry list, ArtifactManifestParseError list> =
    try
        let lines = source.Split([|'\n'|], StringSplitOptions.None)
        let entries = ResizeArray()
        let errors = ResizeArray()
        let seenPaths = System.Collections.Generic.HashSet()

        // Check for trailing content (non-JSON after last LF)
        let trimmedSource = source.TrimEnd([|'\n'|])
        if not (String.IsNullOrEmpty source) && not (source.EndsWith("\n")) then
            errors.Add(ArtifactManifestParseError.InvalidJson "no trailing LF")

        for i, line in Array.indexed lines do
            let lineNum = i + 1
            let trimmedLine = line.TrimEnd([|'\r'|])
            if isNull trimmedLine || trimmedLine.Length = 0 then
                if i < lines.Length - 1 then
                    errors.Add(ArtifactManifestParseError.BlankInteriorLine)
            else
                try
                    let doc = JsonDocument.Parse(trimmedLine)
                    let root = doc.RootElement
                    if root.ValueKind <> JsonValueKind.Object then
                        errors.Add(ArtifactManifestParseError.InvalidJson "entry must be an object")
                    else
                        let path = defaultArg (parseOptionalJsonString root "path") ""
                        let sha256 = defaultArg (parseOptionalJsonString root "sha256") ""
                        let byteLength = defaultArg (parseOptionalJsonInt64 root "byte_length") 0L

                        // Validate path
                        if isNull path || path.Length = 0 then
                            errors.Add(ArtifactManifestParseError.UnknownPath "(empty)")
                        elif path.Contains("..") then
                            errors.Add(ArtifactManifestParseError.PathTraversal path)
                        elif System.IO.Path.IsPathRooted path then
                            errors.Add(ArtifactManifestParseError.AbsolutePath path)
                        elif seenPaths.Contains(path) then
                            errors.Add(ArtifactManifestParseError.DuplicatePath path)
                        else
                            seenPaths.Add(path) |> ignore

                        // Validate SHA-256
                        if not (isValidSha256 sha256) then
                            errors.Add(ArtifactManifestParseError.InvalidSha256 sha256)

                        // Validate byte length
                        if byteLength < 0L then
                            errors.Add(ArtifactManifestParseError.NegativeByteLength byteLength)

                        entries.Add({ Path = path; Sha256 = sha256; ByteLength = byteLength })
                with ex ->
                    errors.Add(ArtifactManifestParseError.InvalidJson ex.Message)

        if errors.Count > 0 then
            Result.Error (List.ofSeq errors)
        else
            Result.Ok (List.ofSeq entries)
    with ex ->
        Result.Error [ ArtifactManifestParseError.InvalidJson ex.Message ]
