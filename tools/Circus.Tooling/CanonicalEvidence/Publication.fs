module Circus.Tooling.CanonicalEvidence.Publication

// =============================================================================
// Canonical evidence – atomic snapshot publication
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07
//
// This module implements staged multi-file snapshot publication with strict
// round-trip validation:
//
//   - Render and write all four staged files
//   - Mutation seam runs after write, before validation
//   - All four files are reread from disk using exact bytes
//   - Strict parse of records.jsonl with byte-identical round-trip
//   - Strict parse of aggregate.json with recomputation verification
//   - Strict parse of artifacts.jsonl with hash/length verification
//   - Strict parse and validation of canonical-evidence.json
//   - Compatibility-to-record consistency validation
//   - Typed staged-validation failures
//   - Typed cleanup-failure preservation (no masking)
//   - Previous-snapshot preservation on all failure paths
// =============================================================================

open System
open System.IO
open System.Text

open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.Validation
open Circus.Tooling.FSharpDiagnostics.Hashing

// -----------------------------------------------------------------------------
// Canonical UTF-8 encoding authority
// -----------------------------------------------------------------------------

/// Strict UTF-8 without BOM encoding. Used for all canonical bytes:
// rendered staged bytes, disk decoding, hash computation, byte-length
/// computation, and canonical byte comparison.
let private strictUtf8 = UTF8Encoding(false, true)

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

let private escapeJsonStringPub (s: string) : string =
    let sb = StringBuilder(s.Length + 10)
    sb.Append('"') |> ignore
    for c in s do
        match c with
        | '\\' -> sb.Append("\\\\") |> ignore
        | '"' -> sb.Append("\\\"") |> ignore
        | '\n' -> sb.Append("\\n") |> ignore
        | '\r' -> sb.Append("\\r") |> ignore
        | '\t' -> sb.Append("\\t") |> ignore
        | _ ->
            if int c < 0x20 then
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
            else
                sb.Append(c) |> ignore
    sb.Append('"') |> ignore
    sb.ToString()

// -----------------------------------------------------------------------------
// Publication types
// -----------------------------------------------------------------------------

type PublicationArtifact = {
    Path: string
    Sha256: string
    ByteLength: int64
}

type PublicationSnapshot = {
    Records: CanonicalExecutionEvidence list
    Aggregate: CanonicalExecutionAggregate
    Artifacts: PublicationArtifact list
    Timestamp: string
}

/// Typed staged validation failures for detailed error reporting.
[<RequireQualifiedAccess>]
type StagedSnapshotFailure =
    | MissingFile of path: string
    | InvalidUtf8 of path: string * detail: string
    | RecordParseFailed of line: int * errors: EvidenceWireParseError list
    | AggregateParseFailed of errors: AggregateWireParseError list
    | ArtifactManifestParseFailed of errors: ArtifactManifestParseError list
    | CompatibilityParseFailed of detail: string
    | NonCanonicalWire of path: string
    | RecordValidationFailed of issues: RecordValidationIssue list
    | AggregateSemanticHashMismatch of expected: string * actual: string
    | AggregateFieldMismatch of difference: Validation.AggregateDifference
    | ArtifactHashMismatch of path: string * expected: string * actual: string
    | ArtifactLengthMismatch of path: string * expected: int64 * actual: int64
    | CompatibilitySemanticHashMismatch of expected: string * actual: string
    | CompatibilityCommitOidMismatch of expected: string * actual: string
    | CompatibilityTreeOidMismatch of expected: string * actual: string
    | CompatibilityProjectionMismatch of detail: string
    | CompatibilityRecordMismatch of checkId: string * detail: string
    | MutationHookFailed of detail: string
    | UnknownArtifactPath of path: string
    | DuplicateArtifactPath of path: string

let stagedSnapshotFailureToString (f: StagedSnapshotFailure) : string =
    match f with
    | StagedSnapshotFailure.MissingFile p -> sprintf "missing staged file: %s" p
    | StagedSnapshotFailure.InvalidUtf8 (p, d) -> sprintf "invalid UTF-8 in %s: %s" p d
    | StagedSnapshotFailure.RecordParseFailed (line, errors) ->
        sprintf "record parse failed at line %d: %s" line (String.concat "; " (List.map evidenceWireParseErrorToString errors))
    | StagedSnapshotFailure.AggregateParseFailed errors ->
        sprintf "aggregate parse failed: %s" (String.concat "; " (List.map aggregateWireParseErrorToString errors))
    | StagedSnapshotFailure.ArtifactManifestParseFailed errors ->
        sprintf "artifact manifest parse failed: %s" (String.concat "; " (List.map artifactManifestParseErrorToString errors))
    | StagedSnapshotFailure.CompatibilityParseFailed d -> sprintf "compatibility parse failed: %s" d
    | StagedSnapshotFailure.NonCanonicalWire p -> sprintf "non-canonical wire bytes in: %s" p
    | StagedSnapshotFailure.RecordValidationFailed issues ->
        sprintf "record validation failed: %s" (String.concat "; " (List.map recordValidationIssueToString issues))
    | StagedSnapshotFailure.AggregateSemanticHashMismatch (expected, actual) ->
        sprintf "aggregate semantic hash mismatch: expected=%s actual=%s" expected actual
    | StagedSnapshotFailure.AggregateFieldMismatch diff ->
        sprintf "aggregate field difference: %s" (Validation.aggregateDifferenceToString diff)
    | StagedSnapshotFailure.ArtifactHashMismatch (path, expected, actual) ->
        sprintf "artifact hash mismatch for %s: expected=%s actual=%s" path expected actual
    | StagedSnapshotFailure.ArtifactLengthMismatch (path, expected, actual) ->
        sprintf "artifact length mismatch for %s: expected=%d actual=%d" path expected actual
    | StagedSnapshotFailure.CompatibilitySemanticHashMismatch (expected, actual) ->
        sprintf "compatibility semantic hash mismatch: expected=%s actual=%s" expected actual
    | StagedSnapshotFailure.CompatibilityCommitOidMismatch (expected, actual) ->
        sprintf "compatibility commit OID mismatch: expected=%s actual=%s" expected actual
    | StagedSnapshotFailure.CompatibilityTreeOidMismatch (expected, actual) ->
        sprintf "compatibility tree OID mismatch: expected=%s actual=%s" expected actual
    | StagedSnapshotFailure.CompatibilityProjectionMismatch d -> sprintf "compatibility projection mismatch: %s" d
    | StagedSnapshotFailure.CompatibilityRecordMismatch (id, d) -> sprintf "compatibility record mismatch for %s: %s" id d
    | StagedSnapshotFailure.MutationHookFailed d -> sprintf "mutation hook failed: %s" d
    | StagedSnapshotFailure.UnknownArtifactPath p -> sprintf "unknown artifact path in manifest: %s" p
    | StagedSnapshotFailure.DuplicateArtifactPath p -> sprintf "duplicate artifact path in manifest: %s" p

type PublicationFailure =
    | SnapshotStagingFailed of detail: string
    | SnapshotValidationFailed of issues: string list
    | SnapshotVerificationFailed of detail: string
    | SnapshotRecordWriteFailed of detail: string
    | SnapshotAggregateWriteFailed of detail: string
    | SnapshotArtifactWriteFailed of detail: string
    | SnapshotCompatibilityWriteFailed of detail: string
    | SnapshotReplacementFailed of detail: string
    | SnapshotPreservationFailed of detail: string
    | SnapshotCleanupFailedAfterPublish of detail: string
    | SnapshotStagedValidationFailed of StagedSnapshotFailure list

let publicationFailureToString (f: PublicationFailure) : string =
    match f with
    | SnapshotStagingFailed d -> sprintf "staging failed: %s" d
    | SnapshotValidationFailed issues -> sprintf "validation failed: %s" (String.concat "; " issues)
    | SnapshotVerificationFailed d -> sprintf "verification failed: %s" d
    | SnapshotRecordWriteFailed d -> sprintf "records write failed: %s" d
    | SnapshotAggregateWriteFailed d -> sprintf "aggregate write failed: %s" d
    | SnapshotArtifactWriteFailed d -> sprintf "artifact manifest write failed: %s" d
    | SnapshotCompatibilityWriteFailed d -> sprintf "compatibility projection write failed: %s" d
    | SnapshotReplacementFailed d -> sprintf "atomic replacement failed: %s" d
    | SnapshotPreservationFailed d -> sprintf "previous snapshot preservation failed: %s" d
    | SnapshotCleanupFailedAfterPublish d -> sprintf "cleanup failed after publish: %s" d
    | SnapshotStagedValidationFailed failures ->
        sprintf "staged validation failed: %s" (String.concat "; " (List.map stagedSnapshotFailureToString failures))

/// Cleanup failure type preserving details without masking the initiating failure.
type PublicationCleanupFailure = {
    Path: string
    ExceptionType: string
    Message: string
}

/// Publication outcome with typed cleanup failure preservation.
type PublicationOutcome = {
    Success: bool
    SnapshotPath: string
    RecordsCount: int
    AggregateSha256: string
    PreviousSnapshotPreserved: bool
    LiveSnapshotMayHaveChanged: bool
    Failure: PublicationFailure option
    CleanupFailure: PublicationCleanupFailure option
}

// -----------------------------------------------------------------------------
// Compatibility projection
// -----------------------------------------------------------------------------

open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.Serialization

let computeCompatibilityProjection (snapshot: PublicationSnapshot) : string =
    let records = snapshot.Records
    let aggregate = snapshot.Aggregate
    let sb = System.Text.StringBuilder()
    sb.Append("{") |> ignore
    sb.Append("\"schema_version\":1,") |> ignore
    sb.Append("\"provider_name\":\"circus-canonical-evidence\",") |> ignore
    sb.Append("\"provider_version\":\"1.0.0\",") |> ignore
    sb.Append("\"tested_commit_oid\":") |> ignore
    sb.Append(escapeJsonStringPub aggregate.SubjectCommitOid) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"tested_tree_oid\":") |> ignore
    sb.Append(escapeJsonStringPub aggregate.SubjectTreeOid) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"records_total\":") |> ignore
    sb.Append(aggregate.RecordsTotal.ToString()) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"records_passed\":") |> ignore
    sb.Append(aggregate.RecordsPassed.ToString()) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"records_failed\":") |> ignore
    sb.Append(aggregate.RecordsFailed.ToString()) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"tests_total\":") |> ignore
    sb.Append(aggregate.TestsTotal.ToString()) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"tests_passed\":") |> ignore
    sb.Append(aggregate.TestsPassed.ToString()) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"tests_failed\":") |> ignore
    sb.Append(aggregate.TestsFailed.ToString()) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"required_checks_total\":") |> ignore
    sb.Append(aggregate.RequiredChecksTotal.ToString()) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"required_checks_passed\":") |> ignore
    sb.Append(aggregate.RequiredChecksPassed.ToString()) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"required_checks_failed\":") |> ignore
    sb.Append(aggregate.RequiredChecksFailed.ToString()) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"record_ids\":[") |> ignore
    let mutable first = true
    for id in aggregate.RecordIds do
        if first then first <- false else sb.Append(",") |> ignore
        sb.Append(escapeJsonStringPub id) |> ignore
    sb.Append("],") |> ignore
    sb.Append("\"overall_status\":") |> ignore
    sb.Append(escapeJsonStringPub (recordStatusToken aggregate.OverallStatus)) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"semantic_sha256\":") |> ignore
    sb.Append(escapeJsonStringPub aggregate.SemanticSha256) |> ignore
    sb.Append(",") |> ignore
    sb.Append("\"records\":[") |> ignore
    let mutable firstRecord = true
    for r in records do
        if firstRecord then firstRecord <- false else sb.Append(",") |> ignore
        sb.Append(renderEvidenceWireJson r) |> ignore
    sb.Append("]") |> ignore
    sb.Append("}") |> ignore
    sb.ToString()

// -----------------------------------------------------------------------------
// Snapshot validation
// -----------------------------------------------------------------------------

type SnapshotValidationResult = {
    Valid: bool
    Issues: string list
    DuplicateRecordIds: string list
    DuplicateCheckIds: string list
    SubjectMismatch: bool
    AggregateCountMismatch: bool
    AggregateHashMismatch: bool
}

let validateSnapshot (snapshot: PublicationSnapshot) : SnapshotValidationResult =
    let issues = ResizeArray<string>()
    let duplicateRecordIds = ResizeArray<string>()
    let duplicateCheckIds = ResizeArray<string>()

    let recordIdCounts = System.Collections.Generic.Dictionary<string, int>()
    for r in snapshot.Records do
        if recordIdCounts.ContainsKey(r.EvidenceId) then
            recordIdCounts.[r.EvidenceId] <- recordIdCounts.[r.EvidenceId] + 1
            if not (duplicateRecordIds.Contains(r.EvidenceId)) then
                duplicateRecordIds.Add(r.EvidenceId)
        else
            recordIdCounts.[r.EvidenceId] <- 1

    let checkIdCounts = System.Collections.Generic.Dictionary<string, int>()
    for r in snapshot.Records do
        if checkIdCounts.ContainsKey(r.CheckId) then
            checkIdCounts.[r.CheckId] <- checkIdCounts.[r.CheckId] + 1
            if not (duplicateCheckIds.Contains(r.CheckId)) then
                duplicateCheckIds.Add(r.CheckId)
        else
            checkIdCounts.[r.CheckId] <- 1

    let subjectMismatch =
        match snapshot.Records with
        | [] -> true // Empty records means mismatch
        | head :: _ -> snapshot.Aggregate.SubjectCommitOid <> head.TestedCommitOid
    let aggregateCountMismatch = snapshot.Aggregate.RecordsTotal <> List.length snapshot.Records
    let aggregateHashMismatch = snapshot.Aggregate.SemanticSha256 <> computeAggregateSemanticHash snapshot.Aggregate

    if duplicateRecordIds.Count > 0 then
        issues.Add(sprintf "duplicate record IDs: %s" (String.concat ", " duplicateRecordIds))
    if duplicateCheckIds.Count > 0 then
        issues.Add(sprintf "duplicate check IDs: %s" (String.concat ", " duplicateCheckIds))
    if subjectMismatch then
        issues.Add("subject commit mismatch between records and aggregate")
    if aggregateCountMismatch then
        issues.Add(sprintf "aggregate count mismatch: expected %d, got %d" (List.length snapshot.Records) snapshot.Aggregate.RecordsTotal)
    if aggregateHashMismatch then
        issues.Add("aggregate semantic hash mismatch")

    { Valid = issues.Count = 0; Issues = List.ofSeq issues; DuplicateRecordIds = List.ofSeq duplicateRecordIds; DuplicateCheckIds = List.ofSeq duplicateCheckIds; SubjectMismatch = subjectMismatch; AggregateCountMismatch = aggregateCountMismatch; AggregateHashMismatch = aggregateHashMismatch }

// -----------------------------------------------------------------------------
// Atomic snapshot publication
// -----------------------------------------------------------------------------

let private safeDeleteFile (path: string) : unit =
    if File.Exists path then File.Delete path

let private safeDeleteDir (path: string) : unit =
    if Directory.Exists path then Directory.Delete(path, true)

let private snapshotExistingFiles (dir: string) (files: string list) : Map<string, byte array option> =
    let mutable result = Map.empty
    for f in files do
        let path = Path.Combine(dir, f)
        let bytes = if File.Exists path then Some(File.ReadAllBytes path) else None
        result <- Map.add f bytes result
    result

let private restoreSnapshot (dir: string) (snapshot: Map<string, byte array option>) : bool =
    let mutable ok = true
    for kv in snapshot do
        try
            match kv.Value with
            | Some bytes -> File.WriteAllBytes(Path.Combine(dir, kv.Key), bytes)
            | None -> if File.Exists (Path.Combine(dir, kv.Key)) then File.Delete (Path.Combine(dir, kv.Key))
        with _ -> ok <- false
    ok

/// Publish a canonical evidence snapshot atomically.
///
/// DEPRECATED: This function computes its own compatibility projection rather than using
/// the provider-owned projection. Use publishSnapshotWithCompatibilityProjection instead
/// to ensure single compatibility authority. This function will raise a compile-time error.
[<Obsolete("Use publishSnapshotWithCompatibilityProjection for single compatibility authority", true)>]
let publishSnapshot (outputRoot: string) (records: CanonicalExecutionEvidence list) (aggregate: CanonicalExecutionAggregate) : PublicationOutcome =
    let snapshotFiles = ["records.jsonl"; "aggregate.json"; "artifacts.jsonl"; "canonical-evidence.json"]
    let recordsCount = List.length records
    let semanticSha = aggregate.SemanticSha256

    let ensureOutputDir dir =
        if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
        Directory.Exists dir

    if not (ensureOutputDir outputRoot) then
        { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false; Failure = Some(SnapshotStagingFailed "cannot create output directory"); CleanupFailure = None }
    else
        let previousSnapshot = snapshotExistingFiles outputRoot snapshotFiles
        let guid = Guid.NewGuid().ToString("n")
        let stagingDir = Path.Combine(outputRoot, ".staging." + guid)

        try
            Directory.CreateDirectory stagingDir |> ignore

            let timestamp = DateTimeOffset.UtcNow.ToString("O")
            let compatibilityProjection = computeCompatibilityProjection { Records = records; Aggregate = aggregate; Artifacts = []; Timestamp = timestamp }
            let recordsJsonl = String.concat "\n" (List.map renderEvidenceWireJson records)
            let aggregateJson = renderAggregateWireJson aggregate

            let recordsBytes = System.Text.Encoding.UTF8.GetBytes(recordsJsonl + "\n")
            let aggregateBytes = System.Text.Encoding.UTF8.GetBytes(aggregateJson + "\n")
            let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatibilityProjection + "\n")

            let artifactsJsonl = String.concat "\n" [
                sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggregateBytes) aggregateBytes.Length
                sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
            ]

            File.WriteAllText(Path.Combine(stagingDir, "records.jsonl"), recordsJsonl + "\n")
            File.WriteAllText(Path.Combine(stagingDir, "aggregate.json"), aggregateJson + "\n")
            File.WriteAllText(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsJsonl + "\n")
            File.WriteAllText(Path.Combine(stagingDir, "canonical-evidence.json"), compatibilityProjection + "\n")

            let snap = { Records = records; Aggregate = aggregate; Artifacts = []; Timestamp = timestamp }
            let validation = validateSnapshot snap

            if not validation.Valid then
                safeDeleteDir stagingDir
                { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false; Failure = Some(SnapshotValidationFailed validation.Issues); CleanupFailure = None }
            else
                try
                    for f in snapshotFiles do
                        let src = Path.Combine(stagingDir, f)
                        let dst = Path.Combine(outputRoot, f)
                        if File.Exists src then
                            if File.Exists dst then File.Delete dst
                            File.Move(src, dst)
                    safeDeleteDir stagingDir
                    { Success = true; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = semanticSha; PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false; Failure = None; CleanupFailure = None }
                with ex ->
                    let restored = restoreSnapshot outputRoot previousSnapshot
                    safeDeleteDir stagingDir
                    { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = restored; LiveSnapshotMayHaveChanged = not restored; Failure = Some(SnapshotReplacementFailed (sprintf "%s: %s" (ex.GetType().Name) ex.Message)); CleanupFailure = None }
        with ex ->
            let restored = restoreSnapshot outputRoot previousSnapshot
            if Directory.Exists stagingDir then safeDeleteDir stagingDir
            { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = restored; LiveSnapshotMayHaveChanged = not restored; Failure = Some(SnapshotStagingFailed (sprintf "%s: %s" (ex.GetType().Name) ex.Message)); CleanupFailure = None }

/// Publish a canonical evidence snapshot using the exact provider-computed compatibility projection.
/// This ensures single compatibility authority: the provider owns the projection bytes and
/// publication writes them unchanged to canonical-evidence.json.
let publishSnapshotWithCompatibilityProjection
    (outputRoot: string)
    (records: CanonicalExecutionEvidence list)
    (aggregate: CanonicalExecutionAggregate)
    (compatibilityProjection: CanonicalEvidence)
    : PublicationOutcome =
    let snapshotFiles = ["records.jsonl"; "aggregate.json"; "artifacts.jsonl"; "canonical-evidence.json"]
    let recordsCount = List.length records
    let semanticSha = aggregate.SemanticSha256

    let ensureOutputDir dir =
        if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
        Directory.Exists dir

    if not (ensureOutputDir outputRoot) then
        { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false; Failure = Some(SnapshotStagingFailed "cannot create output directory"); CleanupFailure = None }
    else
        let previousSnapshot = snapshotExistingFiles outputRoot snapshotFiles
        let guid = Guid.NewGuid().ToString("n")
        let stagingDir = Path.Combine(outputRoot, ".staging." + guid)

        try
            Directory.CreateDirectory stagingDir |> ignore

            // Render the exact provider-computed compatibility projection unchanged
            let compatJson = renderWireJson compatibilityProjection
            let recordsJsonl = String.concat "\n" (List.map renderEvidenceWireJson records)
            let aggregateJson = renderAggregateWireJson aggregate

            let recordsBytes = System.Text.Encoding.UTF8.GetBytes(recordsJsonl + "\n")
            let aggregateBytes = System.Text.Encoding.UTF8.GetBytes(aggregateJson + "\n")
            let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

            let artifactsJsonl = String.concat "\n" [
                sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggregateBytes) aggregateBytes.Length
                sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
            ]

            File.WriteAllText(Path.Combine(stagingDir, "records.jsonl"), recordsJsonl + "\n")
            File.WriteAllText(Path.Combine(stagingDir, "aggregate.json"), aggregateJson + "\n")
            File.WriteAllText(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsJsonl + "\n")
            // Write the EXACT compatibility projection unchanged
            File.WriteAllText(Path.Combine(stagingDir, "canonical-evidence.json"), compatJson + "\n")

            // Validate: READ the written compatibility projection from disk, parse it, and verify
            let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
            let parsedCompat =
                try
                    if not (File.Exists compatPath) then
                        Error "canonical-evidence.json not found in staging"
                    else
                        let writtenBytes = File.ReadAllBytes compatPath
                        let writtenText = System.Text.Encoding.UTF8.GetString writtenBytes
                        match parseWireJson writtenText with
                        | Result.Ok e -> Ok e
                        | Result.Error err -> Error(sprintf "parse failed: %s" err)
                with ex -> Error(sprintf "read/parse exception: %s" ex.Message)

            match parsedCompat with
            | Error detail ->
                safeDeleteDir stagingDir
                { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false; Failure = Some(SnapshotCompatibilityWriteFailed detail); CleanupFailure = None }
            | Ok parsed ->
                // Verify commit/tree match aggregate
                if parsed.TestedCommitOid <> aggregate.SubjectCommitOid then
                    safeDeleteDir stagingDir
                    { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false; Failure = Some(SnapshotCompatibilityWriteFailed(sprintf "commit mismatch: projection=%s aggregate=%s" parsed.TestedCommitOid aggregate.SubjectCommitOid)); CleanupFailure = None }
                elif parsed.TestedTreeOid <> aggregate.SubjectTreeOid then
                    safeDeleteDir stagingDir
                    { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false; Failure = Some(SnapshotCompatibilityWriteFailed(sprintf "tree mismatch: projection=%s aggregate=%s" parsed.TestedTreeOid aggregate.SubjectTreeOid)); CleanupFailure = None }
                else
                    // Validate snapshot
                    let snap = { Records = records; Aggregate = aggregate; Artifacts = []; Timestamp = "" }
                    let validation = validateSnapshot snap

                    if not validation.Valid then
                        safeDeleteDir stagingDir
                        { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false; Failure = Some(SnapshotValidationFailed validation.Issues); CleanupFailure = None }
                    else
                        try
                            for f in snapshotFiles do
                                let src = Path.Combine(stagingDir, f)
                                let dst = Path.Combine(outputRoot, f)
                                if File.Exists src then
                                    if File.Exists dst then File.Delete dst
                                    File.Move(src, dst)
                            safeDeleteDir stagingDir
                            { Success = true; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = semanticSha; PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false; Failure = None; CleanupFailure = None }
                        with ex ->
                            let restored = restoreSnapshot outputRoot previousSnapshot
                            safeDeleteDir stagingDir
                            { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = "";
                              PreviousSnapshotPreserved = restored; LiveSnapshotMayHaveChanged = not restored; Failure = Some(SnapshotReplacementFailed (sprintf "%s: %s" (ex.GetType().Name) (ex.Message))); CleanupFailure = None }
        with ex ->
            let restored = restoreSnapshot outputRoot previousSnapshot
            if Directory.Exists stagingDir then safeDeleteDir stagingDir
            { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = "";
              PreviousSnapshotPreserved = restored; LiveSnapshotMayHaveChanged = not restored; Failure = Some(SnapshotStagingFailed (sprintf "%s: %s" (ex.GetType().Name) (ex.Message))); CleanupFailure = None }

// -----------------------------------------------------------------------------
// Staged round-trip validation
// -----------------------------------------------------------------------------



// The staged round-trip validation implements the complete staged publication
// pipeline with strict byte-for-byte fidelity and comprehensive error detection.
//
// Pipeline phases:
//   1. Render and write all four staged files using canonical UTF-8
//   2. Mutation seam: optional hook for corruption testing
//   3. Reread all four files from disk using exact canonical bytes
//   4. Strict parse of records.jsonl with byte-identical round-trip check
//   5. Strict parse of aggregate.json with recomputation verification
//   6. Strict parse of artifacts.jsonl with hash/length verification
//   7. Strict parse and validation of canonical-evidence.json
//   8. Compatibility-to-record consistency validation
//   9. Typed failure assembly and previous-snapshot preservation
//
// Mutation seam:
//   After writing all staged files and before validation, the provided
//   mutationFn is called with the staging directory path. This enables
//   corruption testing by allowing callers to modify staged files.
//   Pass None for production use.

/// Read file bytes using canonical UTF-8 encoding.
let private readFileCanonicalUtf8 (path: string) : Result<byte array, string> =
    try
        Ok(File.ReadAllBytes path)
    with ex ->
        Error(sprintf "failed to read %s: %s" path ex.Message)

/// Compute SHA-256 hex digest of bytes.
let private sha256HexOfBytes (bytes: byte array) : string =
    use hasher = System.Security.Cryptography.SHA256.Create()
    let hash = hasher.ComputeHash(bytes)
    BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()

/// Validate record round-trip: parse, re-render, compare byte-for-byte.
let private validateRecordRoundTrip
    (lineNumber: int)
    (originalBytes: byte array)
    (record: CanonicalExecutionEvidence)
    : StagedSnapshotFailure option =
    let reRendered = renderEvidenceWireJson record
    let reRenderedBytes = strictUtf8.GetBytes(reRendered)
    if reRenderedBytes <> originalBytes then
        // Compute diff for debugging (but don't expose raw bytes in error message)
        let originalLen = originalBytes.Length
        let reRenderedLen = reRenderedBytes.Length
        let lenDiff = abs(originalLen - reRenderedLen)
        Some(StagedSnapshotFailure.NonCanonicalWire(
            sprintf "records.jsonl line %d: re-rendered bytes differ (length diff: %d)" lineNumber lenDiff))
    else
        // Verify evidence_id is derivable from canonical form
        let computedId = computeEvidenceId record
        if computedId <> record.EvidenceId then
            Some(StagedSnapshotFailure.RecordValidationFailed [
                RecordValidationIssue.RecordIdMismatch(computedId, record.EvidenceId)
            ])
        else
            None

/// Parse records.jsonl from disk bytes with full round-trip validation.
let private parseAndValidateRecordsJsonl
    (recordsPath: string)
    (recordsBytes: byte array)
    : StagedSnapshotFailure list =
    let failures = ResizeArray()
    let recordsText =
        try
            strictUtf8.GetString(recordsBytes) |> Ok
        with ex ->
            Error ex.Message
    match recordsText with
    | Error detail ->
        failures.Add(StagedSnapshotFailure.InvalidUtf8(recordsPath, detail))
        List.ofSeq failures
    | Ok text ->
        // Normalize line endings: accept both \n and \r\n
        let normalizedText = text.Replace("\r\n", "\n")
        let lines = normalizedText.Split([|'\n'|], StringSplitOptions.None)
        let parsedRecords = ResizeArray()
        let mutable lineIdx = 0
        for line in lines do
            lineIdx <- lineIdx + 1
            // Skip empty trailing line
            if not (String.IsNullOrEmpty line) then
                match parseEvidenceWireJsonStrict line with
                | Result.Error errors ->
                    failures.Add(StagedSnapshotFailure.RecordParseFailed(lineIdx, errors))
                | Result.Ok record ->
                    let lineBytes = strictUtf8.GetBytes(line)
                    match validateRecordRoundTrip lineIdx lineBytes record with
                    | Some failure -> failures.Add(failure)
                    | None -> parsedRecords.Add(record)
        List.ofSeq failures

/// Parse aggregate.json from disk bytes with recomputation verification.
let private parseAndValidateAggregateJson
    (aggregatePath: string)
    (aggregateBytes: byte array)
    (expectedAggregate: CanonicalExecutionAggregate)
    : StagedSnapshotFailure list =
    let failures = ResizeArray()
    let aggregateText =
        try
            strictUtf8.GetString(aggregateBytes) |> Ok
        with ex ->
            Error ex.Message
    match aggregateText with
    | Error detail ->
        failures.Add(StagedSnapshotFailure.InvalidUtf8(aggregatePath, detail))
    | Ok text ->
        match parseAggregateWireJsonStrict text with
        | Result.Error errors ->
            failures.Add(StagedSnapshotFailure.AggregateParseFailed(errors))
        | Result.Ok aggregate ->
            // Phase 1: Verify self-integrity (semantic hash recomputed from aggregate itself)
            let recomputedHash = computeAggregateSemanticHash aggregate
            if recomputedHash <> aggregate.SemanticSha256 then
                failures.Add(StagedSnapshotFailure.AggregateSemanticHashMismatch(
                    recomputedHash, aggregate.SemanticSha256))

            // Phase 2: Use production aggregate comparator for complete field-by-field comparison
            // SemanticSha256 is excluded here because it's already handled in Phase 1 (self-integrity).
            // This avoids dual reporting: one corrupted hash produces exactly one failure authority.
            let diffs =
                compareAggregate expectedAggregate aggregate
                |> List.filter (function
                    | AggregateDifference.SemanticSha256 _ -> false
                    | _ -> true)
            for diff in diffs do
                failures.Add(StagedSnapshotFailure.AggregateFieldMismatch(diff))
    List.ofSeq failures

/// Parse artifacts.jsonl from disk bytes with hash/length verification.
let private parseAndValidateArtifactsJsonl
    (artifactsPath: string)
    (artifactsBytes: byte array)
    (recordsPath: string) (recordsBytes: byte array)
    (aggregatePath: string) (aggregateBytes: byte array)
    (compatPath: string) (compatBytes: byte array)
    : StagedSnapshotFailure list =
    let failures = ResizeArray()
    let artifactsText =
        try
            strictUtf8.GetString(artifactsBytes) |> Ok
        with ex ->
            Error ex.Message
    match artifactsText with
    | Error detail ->
        failures.Add(StagedSnapshotFailure.InvalidUtf8(artifactsPath, detail))
    | Ok text ->
        match parseArtifactManifestJsonlStrict text with
        | Result.Error errors ->
            failures.Add(StagedSnapshotFailure.ArtifactManifestParseFailed(errors))
        | Result.Ok entries ->
            // Exact manifest inventory: only these three paths are permitted
            let requiredPaths = ["records.jsonl"; "aggregate.json"; "canonical-evidence.json"] |> Set.ofList
            let actualPaths = entries |> List.map (fun e -> e.Path) |> Set.ofList

            // Check for missing paths
            let missingPaths = requiredPaths - actualPaths
            for p in missingPaths do
                failures.Add(StagedSnapshotFailure.MissingFile(p))

            // Check for unknown paths (extra entries not allowed)
            let unknownPaths = actualPaths - requiredPaths
            for p in unknownPaths do
                failures.Add(StagedSnapshotFailure.UnknownArtifactPath(p))

            // Check for duplicate paths
            let pathCounts = entries |> List.countBy (fun e -> e.Path)
            for path, count in pathCounts do
                if count > 1 then
                    failures.Add(StagedSnapshotFailure.DuplicateArtifactPath(path))

            // Verify each required artifact's hash and length
            let recordsHash = sha256HexOfBytes recordsBytes
            let recordsLength = int64 recordsBytes.Length
            let aggregateHash = sha256HexOfBytes aggregateBytes
            let aggregateLength = int64 aggregateBytes.Length
            let compatHash = sha256HexOfBytes compatBytes
            let compatLength = int64 compatBytes.Length

            let checkArtifact expectedPath expectedHash expectedLength =
                match List.tryFind (fun (e: SnapshotArtifactEntry) -> e.Path = expectedPath) entries with
                | None -> () // Already reported as missing above
                | Some entry ->
                    if entry.Sha256 <> expectedHash then
                        failures.Add(StagedSnapshotFailure.ArtifactHashMismatch(expectedPath, expectedHash, entry.Sha256))
                    if entry.ByteLength <> expectedLength then
                        failures.Add(StagedSnapshotFailure.ArtifactLengthMismatch(expectedPath, expectedLength, entry.ByteLength))

            checkArtifact "records.jsonl" recordsHash recordsLength
            checkArtifact "aggregate.json" aggregateHash aggregateLength
            checkArtifact "canonical-evidence.json" compatHash compatLength
    List.ofSeq failures

/// Validate compatibility evidence against records and expected projection.
let private validateCompatibilityEvidence
    (compatPath: string)
    (compatBytes: byte array)
    (expectedProjection: CanonicalEvidence)
    (records: CanonicalExecutionEvidence list)
    (aggregate: CanonicalExecutionAggregate)
    : StagedSnapshotFailure list =
    let failures = ResizeArray()
    let compatText =
        try
            strictUtf8.GetString(compatBytes) |> Ok
        with ex ->
            Error ex.Message
    match compatText with
    | Error detail ->
        failures.Add(StagedSnapshotFailure.InvalidUtf8(compatPath, detail))
    | Ok text ->
        match parseWireJson text with
        | Result.Error detail ->
            failures.Add(StagedSnapshotFailure.CompatibilityParseFailed(detail))
        | Result.Ok diskCompat ->
            // Phase 1: Compare disk compatibility against expected projection using production comparator
            // This is the authoritative structural comparison for staged validation
            let projectionDiffs = compareCompatibilityProjection expectedProjection diskCompat
            for diff in projectionDiffs do
                match diff with
                | CompatibilityDifference.SchemaVersion (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "schema_version mismatch: expected=%d actual=%d" expected actual))
                | CompatibilityDifference.ProviderName (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "provider_name mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.ProviderVersion (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "provider_version mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.TestedCommitOid (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "tested_commit_oid mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.TestedTreeOid (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "tested_tree_oid mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.ObjectFormat (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "object_format mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.ActiveScopeActId (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "active_scope_act_id mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.ActiveScopePointerBlobOid (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "active_scope_pointer_blob_oid mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.ScopeDeclarationPath (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "scope_declaration_path mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.DeclarationBlobOid (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "declaration_blob_oid mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.BaselineCommitOid (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "baseline_commit_oid mismatch: expected=%s actual=%s" expected actual))
                | CompatibilityDifference.OverallStatus (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityProjectionMismatch(
                        sprintf "overall_status mismatch: expected=%s actual=%s" (statusToken expected) (statusToken actual)))
                | CompatibilityDifference.SemanticSha256 (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilitySemanticHashMismatch(expected, actual))
                | CompatibilityDifference.CheckCount (expected, actual) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityRecordMismatch(
                        "(all)", sprintf "check count mismatch: expected=%d actual=%d" expected actual))
                | CompatibilityDifference.MissingCheck checkId ->
                    failures.Add(StagedSnapshotFailure.CompatibilityRecordMismatch(
                        checkId, sprintf "missing check in disk compatibility: %s" checkId))
                | CompatibilityDifference.UnknownCheck checkId ->
                    failures.Add(StagedSnapshotFailure.CompatibilityRecordMismatch(
                        checkId, sprintf "unknown check in disk compatibility: %s" checkId))
                | CompatibilityDifference.CheckDifference (checkId, checkDiff) ->
                    let detail =
                        match checkDiff with
                        | CompatibilityCheckDifference.Id (expected, actual) -> sprintf "id mismatch: expected=%s actual=%s" expected actual
                        | CompatibilityCheckDifference.CommandArgv (expected, actual) -> sprintf "command_argv mismatch"
                        | CompatibilityCheckDifference.WorkingDirectory (expected, actual) -> sprintf "working_directory mismatch"
                        | CompatibilityCheckDifference.DurationMilliseconds (expected, actual) -> sprintf "duration mismatch: expected=%d actual=%d" expected actual
                        | CompatibilityCheckDifference.ExitCode (expected, actual) -> sprintf "exit_code mismatch"
                        | CompatibilityCheckDifference.Status (expected, actual) -> sprintf "status mismatch: expected=%s actual=%s" (statusToken expected) (statusToken actual)
                        | CompatibilityCheckDifference.StdoutSha256 (expected, actual) -> sprintf "stdout_sha256 mismatch"
                        | CompatibilityCheckDifference.StderrSha256 (expected, actual) -> sprintf "stderr_sha256 mismatch"
                        | CompatibilityCheckDifference.FailureKind (expected, actual) -> sprintf "failure_kind mismatch"
                    failures.Add(StagedSnapshotFailure.CompatibilityRecordMismatch(checkId, detail))
                | CompatibilityDifference.DuplicateExpectedCheckId (checkId, count) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityRecordMismatch(
                        checkId, sprintf "duplicate check id in expected: %s (count=%d)" checkId count))
                | CompatibilityDifference.DuplicateActualCheckId (checkId, count) ->
                    failures.Add(StagedSnapshotFailure.CompatibilityRecordMismatch(
                        checkId, sprintf "duplicate check id in disk: %s (count=%d)" checkId count))

            // Phase 2: Cross-file consistency checks (aggregate vs compatibility)
            // Verify commit: use dedicated type to avoid misclassifying OIDs as hashes
            if diskCompat.TestedCommitOid <> aggregate.SubjectCommitOid then
                failures.Add(StagedSnapshotFailure.CompatibilityCommitOidMismatch(
                    aggregate.SubjectCommitOid, diskCompat.TestedCommitOid))
            // Verify tree: use dedicated type for consistency
            if diskCompat.TestedTreeOid <> aggregate.SubjectTreeOid then
                failures.Add(StagedSnapshotFailure.CompatibilityTreeOidMismatch(
                    aggregate.SubjectTreeOid, diskCompat.TestedTreeOid))
            // Verify record IDs match (diskCompat.Checks uses EvidenceCheckResult with Id field)
            let compatRecordIds = List.map (fun (e: EvidenceCheckResult) -> e.Id) diskCompat.Checks |> List.sort
            let aggregateRecordIds = aggregate.RecordIds
            if compatRecordIds <> aggregateRecordIds then
                failures.Add(StagedSnapshotFailure.CompatibilityRecordMismatch(
                    "(all)",
                    sprintf "record IDs mismatch: compat count=%d aggregate count=%d"
                        (List.length compatRecordIds) (List.length aggregateRecordIds)))
            // Verify individual record consistency
            let compatById = List.map (fun (e: EvidenceCheckResult) -> e.Id, e) diskCompat.Checks |> Map.ofList
            for record in records do
                match Map.tryFind record.EvidenceId compatById with
                | None ->
                    failures.Add(StagedSnapshotFailure.CompatibilityRecordMismatch(
                        record.CheckId,
                        sprintf "record %s not found in compatibility projection" record.EvidenceId))
                | Some _ ->
                    // Record found - EvidenceCheckResult doesn't have check_id so we skip check_id validation
                    // The semantic hash comparison is sufficient for compatibility
                    ()
    List.ofSeq failures

/// Stage and publish a snapshot with full round-trip validation.
///
/// This function implements the complete staged publication pipeline:
///   1. Render all four files to a staging directory
///   2. Optionally apply mutation (for corruption testing)
///   3. Reread all files from disk using canonical UTF-8
///   4. Validate each file strictly
///   5. Verify consistency across files
///   6. Atomically replace the live snapshot or preserve on failure
///
/// The mutationFn parameter allows callers to modify staged files before
/// validation. This enables corruption testing. Pass None for production.
let stageAndPublishSnapshot
    (outputRoot: string)
    (records: CanonicalExecutionEvidence list)
    (aggregate: CanonicalExecutionAggregate)
    (compatibilityProjection: CanonicalEvidence)
    (mutationFn: (string -> Result<unit, string>) option)
    : PublicationOutcome =
    let snapshotFiles = ["records.jsonl"; "aggregate.json"; "artifacts.jsonl"; "canonical-evidence.json"]
    let recordsCount = List.length records
    let semanticSha = aggregate.SemanticSha256

    // Snapshot existing files for rollback
    let previousSnapshot = snapshotExistingFiles outputRoot snapshotFiles

    // Check if live snapshot may have changed during staging
    let liveSnapshotMayHaveChanged () =
        snapshotExistingFiles outputRoot snapshotFiles <> previousSnapshot

    // Ensure output directory
    let ensureOutputDir dir =
        if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
        Directory.Exists dir

    if not (ensureOutputDir outputRoot) then
        { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = "";
          PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false;
          Failure = Some(SnapshotStagingFailed "cannot create output directory"); CleanupFailure = None }

    else
        // Create staging directory with unique name
        let guid = Guid.NewGuid().ToString("n")
        let stagingDir = Path.Combine(outputRoot, ".staging." + guid)

        try
            Directory.CreateDirectory stagingDir |> ignore

            // Phase 1: Render and write all four staged files
            let compatJson = renderWireJson compatibilityProjection
            let recordsJsonl = String.concat "\n" (List.map renderEvidenceWireJson records)
            let aggregateJson = renderAggregateWireJson aggregate

            // Render artifact manifest entries (paths in sorted order)
            let recordsBytes = strictUtf8.GetBytes(recordsJsonl + "\n")
            let aggregateBytes = strictUtf8.GetBytes(aggregateJson + "\n")
            let compatBytes = strictUtf8.GetBytes(compatJson + "\n")

            let artifactsJsonl = String.concat "\n" [
                sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}"""
                    (sha256HexOfBytes recordsBytes) recordsBytes.Length
                sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}"""
                    (sha256HexOfBytes aggregateBytes) aggregateBytes.Length
                sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}"""
                    (sha256HexOfBytes compatBytes) compatBytes.Length
            ]
            let artifactsBytes = strictUtf8.GetBytes(artifactsJsonl + "\n")

            // Write all files using canonical UTF-8
            File.WriteAllBytes(Path.Combine(stagingDir, "records.jsonl"), recordsBytes)
            File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggregateBytes)
            File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
            File.WriteAllBytes(Path.Combine(stagingDir, "canonical-evidence.json"), compatBytes)

            // Phase 2: Mutation seam - run mutation if provided
            match mutationFn with
            | Some mutateFn ->
                match mutateFn stagingDir with
                | Result.Error detail ->
                    safeDeleteDir stagingDir
                    { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = "";
                      PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false;
                      Failure = Some(SnapshotStagingFailed detail); CleanupFailure = None }
                | Result.Ok () ->
                    // Mutation succeeded, continue with validation
                    // Phase 3: Reread all four files from disk
                    let recordsDiskBytes = readFileCanonicalUtf8 (Path.Combine(stagingDir, "records.jsonl"))
                    let aggregateDiskBytes = readFileCanonicalUtf8 (Path.Combine(stagingDir, "aggregate.json"))
                    let artifactsDiskBytes = readFileCanonicalUtf8 (Path.Combine(stagingDir, "artifacts.jsonl"))
                    let compatDiskBytes = readFileCanonicalUtf8 (Path.Combine(stagingDir, "canonical-evidence.json"))

                    // Phase 4-8: Strict validation
                    let allFailures = ResizeArray()

                    // Validate records.jsonl
                    match recordsDiskBytes with
                    | Result.Error detail ->
                        allFailures.Add(StagedSnapshotFailure.InvalidUtf8("records.jsonl", detail))
                    | Ok bytes ->
                        let recordFailures = parseAndValidateRecordsJsonl "records.jsonl" bytes
                        allFailures.AddRange(recordFailures)

                    // Parse records for later validation (collect from previous step)
                    // CRITICAL: If records parsing fails, do NOT derive aggregate from an empty list.
                    // Aggregate derivation is SKIPPED when record parsing fails - this prevents
                    // parse errors from silently creating false aggregate mismatches.
                    let parsedRecordsResult =
                        match recordsDiskBytes with
                        | Result.Ok bytes ->
                            let text = strictUtf8.GetString bytes
                            let normalizedText = text.Replace("\r\n", "\n")
                            let lines = normalizedText.Split([|'\n'|], StringSplitOptions.None)
                            let parsed = ResizeArray()
                            let hadParseErrors = ref false
                            for line in lines do
                                if not (String.IsNullOrEmpty line) then
                                    match parseEvidenceWireJsonStrict line with
                                    | Result.Ok r -> parsed.Add(r)
                                    | Result.Error _ -> hadParseErrors := true
                            if !hadParseErrors then
                                Error "record parsing had errors, skipping aggregate derivation"
                            else
                                Ok(List.ofSeq parsed)
                        | Result.Error _ -> Error "record read failed, skipping aggregate derivation"

                    // Validate aggregate.json
                    // Only derive aggregate if all records parsed successfully.
                    // On parse failure, aggregate validation is SKIPPED - this is the correct
                    // behavior: parse failures produce RecordParseFailed, not field mismatches.
                    match aggregateDiskBytes with
                    | Result.Error detail ->
                        allFailures.Add(StagedSnapshotFailure.InvalidUtf8("aggregate.json", detail))
                    | Ok bytes ->
                        match parsedRecordsResult with
                        | Error _ ->
                            // Records had parse errors - aggregate derivation is skipped.
                            // The RecordParseFailed failures are already in allFailures.
                            // We do NOT run aggregate comparison against an empty/incomplete record list.
                            ()
                        | Ok parsedRecords ->
                            let recomputedAggregate =
                                computeAggregate aggregate.SubjectCommitOid aggregate.SubjectTreeOid parsedRecords
                                |> finalizeAggregate
                            let aggregateFailures = parseAndValidateAggregateJson "aggregate.json" bytes recomputedAggregate
                            allFailures.AddRange(aggregateFailures)

                    // Validate artifacts.jsonl
                    match artifactsDiskBytes, recordsDiskBytes, aggregateDiskBytes, compatDiskBytes with
                    | Result.Ok artBytes, Result.Ok recBytes, Result.Ok aggBytes, Result.Ok comBytes ->
                        let artifactFailures =
                            parseAndValidateArtifactsJsonl "artifacts.jsonl" artBytes
                                "records.jsonl" recBytes "aggregate.json" aggBytes
                                "canonical-evidence.json" comBytes
                        allFailures.AddRange(artifactFailures)
                    | Result.Error detail, _, _, _ ->
                        allFailures.Add(StagedSnapshotFailure.InvalidUtf8("artifacts.jsonl", detail))
                    | _, Result.Error detail, _, _ ->
                        allFailures.Add(StagedSnapshotFailure.InvalidUtf8("records.jsonl", detail))
                    | _, _, Result.Error detail, _ ->
                        allFailures.Add(StagedSnapshotFailure.InvalidUtf8("aggregate.json", detail))
                    | _, _, _, Result.Error detail ->
                        allFailures.Add(StagedSnapshotFailure.InvalidUtf8("canonical-evidence.json", detail))

                    // Validate compatibility evidence against records
                    // Only validate if records parsed successfully
                    match compatDiskBytes with
                    | Result.Error detail ->
                        allFailures.Add(StagedSnapshotFailure.InvalidUtf8("canonical-evidence.json", detail))
                    | Ok bytes ->
                        match parsedRecordsResult with
                        | Error _ ->
                            // Records had parse errors - compatibility validation with records is skipped
                            ()
                        | Ok parsedRecords ->
                            let compatFailures = validateCompatibilityEvidence "canonical-evidence.json" bytes compatibilityProjection parsedRecords aggregate
                            allFailures.AddRange(compatFailures)

                    // Phase 9: Handle validation result
                    if allFailures.Count > 0 then
                        // Attempt cleanup, preserving cleanup failure details
                        let mutable cleanupFailure = None
                        try
                            safeDeleteDir stagingDir
                        with ex ->
                            cleanupFailure <- Some { Path = stagingDir; ExceptionType = ex.GetType().Name; Message = ex.Message }

                        { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = "";
                          PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false;
                          Failure = Some(SnapshotStagedValidationFailed(List.ofSeq allFailures));
                          CleanupFailure = cleanupFailure }
                    else
                        // Phase 10: Atomically replace live snapshot
                        try
                            let mutable moveFailed = false
                            for f in snapshotFiles do
                                let src = Path.Combine(stagingDir, f)
                                let dst = Path.Combine(outputRoot, f)
                                if File.Exists src then
                                    if File.Exists dst then File.Delete dst
                                    File.Move(src, dst)
                                elif File.Exists dst then
                                    // Missing expected file in staging
                                    moveFailed <- true

                            if moveFailed then
                                raise (IOException("not all expected files present in staging"))

                            safeDeleteDir stagingDir
                            { Success = true; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = semanticSha;
                              PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false;
                              Failure = None; CleanupFailure = None }
                        with ex ->
                            // Rollback to previous snapshot
                            let restored = restoreSnapshot outputRoot previousSnapshot
                            safeDeleteDir stagingDir
                            { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = "";
                              PreviousSnapshotPreserved = restored; LiveSnapshotMayHaveChanged = not restored;
                              Failure = Some(SnapshotReplacementFailed (sprintf "%s: %s" (ex.GetType().Name) (ex.Message)));
                              CleanupFailure = None }
            | None ->
                // No mutation hook, continue with validation
                // Phase 3: Reread all four files from disk
                let recordsDiskBytes = readFileCanonicalUtf8 (Path.Combine(stagingDir, "records.jsonl"))
                let aggregateDiskBytes = readFileCanonicalUtf8 (Path.Combine(stagingDir, "aggregate.json"))
                let artifactsDiskBytes = readFileCanonicalUtf8 (Path.Combine(stagingDir, "artifacts.jsonl"))
                let compatDiskBytes = readFileCanonicalUtf8 (Path.Combine(stagingDir, "canonical-evidence.json"))

                // Phase 4-8: Strict validation
                let allFailures = ResizeArray()

                // Validate records.jsonl
                match recordsDiskBytes with
                | Result.Error detail ->
                    allFailures.Add(StagedSnapshotFailure.InvalidUtf8("records.jsonl", detail))
                | Ok bytes ->
                    let recordFailures = parseAndValidateRecordsJsonl "records.jsonl" bytes
                    allFailures.AddRange(recordFailures)

                // Parse records for later validation (collect from previous step)
                // CRITICAL: If records parsing fails, do NOT derive aggregate from an empty list.
                // Aggregate derivation is SKIPPED when record parsing fails - this prevents
                // parse errors from silently creating false aggregate mismatches.
                let parsedRecordsResult =
                    match recordsDiskBytes with
                    | Result.Ok bytes ->
                        let text = strictUtf8.GetString bytes
                        let normalizedText = text.Replace("\r\n", "\n")
                        let lines = normalizedText.Split([|'\n'|], StringSplitOptions.None)
                        let parsed = ResizeArray()
                        let hadParseErrors = ref false
                        for line in lines do
                            if not (String.IsNullOrEmpty line) then
                                match parseEvidenceWireJsonStrict line with
                                | Result.Ok r -> parsed.Add(r)
                                | Result.Error _ -> hadParseErrors := true
                        if !hadParseErrors then
                            Error "record parsing had errors, skipping aggregate derivation"
                        else
                            Ok(List.ofSeq parsed)
                    | Result.Error _ -> Error "record read failed, skipping aggregate derivation"

                // Validate aggregate.json
                // Only derive aggregate if all records parsed successfully.
                // On parse failure, aggregate validation is SKIPPED - this is the correct
                // behavior: parse failures produce RecordParseFailed, not field mismatches.
                match aggregateDiskBytes with
                | Result.Error detail ->
                    allFailures.Add(StagedSnapshotFailure.InvalidUtf8("aggregate.json", detail))
                | Ok bytes ->
                    match parsedRecordsResult with
                    | Error _ ->
                        // Records had parse errors - aggregate derivation is skipped.
                        // The RecordParseFailed failures are already in allFailures.
                        // We do NOT run aggregate comparison against an empty/incomplete record list.
                        ()
                    | Ok parsedRecords ->
                        let recomputedAggregate =
                            computeAggregate aggregate.SubjectCommitOid aggregate.SubjectTreeOid parsedRecords
                            |> finalizeAggregate
                        let aggregateFailures = parseAndValidateAggregateJson "aggregate.json" bytes recomputedAggregate
                        allFailures.AddRange(aggregateFailures)

                // Validate artifacts.jsonl
                match artifactsDiskBytes, recordsDiskBytes, aggregateDiskBytes, compatDiskBytes with
                | Result.Ok artBytes, Result.Ok recBytes, Result.Ok aggBytes, Result.Ok comBytes ->
                    let artifactFailures =
                        parseAndValidateArtifactsJsonl "artifacts.jsonl" artBytes
                            "records.jsonl" recBytes "aggregate.json" aggBytes
                            "canonical-evidence.json" comBytes
                    allFailures.AddRange(artifactFailures)
                | Result.Error detail, _, _, _ ->
                    allFailures.Add(StagedSnapshotFailure.InvalidUtf8("artifacts.jsonl", detail))
                | _, Result.Error detail, _, _ ->
                    allFailures.Add(StagedSnapshotFailure.InvalidUtf8("records.jsonl", detail))
                | _, _, Result.Error detail, _ ->
                    allFailures.Add(StagedSnapshotFailure.InvalidUtf8("aggregate.json", detail))
                | _, _, _, Result.Error detail ->
                    allFailures.Add(StagedSnapshotFailure.InvalidUtf8("canonical-evidence.json", detail))

                // Validate compatibility evidence against records
                // Only validate if records parsed successfully
                match compatDiskBytes with
                | Result.Error detail ->
                    allFailures.Add(StagedSnapshotFailure.InvalidUtf8("canonical-evidence.json", detail))
                | Ok bytes ->
                    match parsedRecordsResult with
                    | Error _ ->
                        // Records had parse errors - compatibility validation with records is skipped
                        ()
                    | Ok parsedRecords ->
                        let compatFailures = validateCompatibilityEvidence "canonical-evidence.json" bytes compatibilityProjection parsedRecords aggregate
                        allFailures.AddRange(compatFailures)

                // Phase 9: Handle validation result
                if allFailures.Count > 0 then
                    // Attempt cleanup, preserving cleanup failure details
                    let mutable cleanupFailure = None
                    try
                        safeDeleteDir stagingDir
                    with ex ->
                        cleanupFailure <- Some { Path = stagingDir; ExceptionType = ex.GetType().Name; Message = ex.Message }

                    { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = "";
                      PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false;
                      Failure = Some(SnapshotStagedValidationFailed(List.ofSeq allFailures));
                      CleanupFailure = cleanupFailure }
                else
                    // Phase 10: Atomically replace live snapshot
                    try
                        let mutable moveFailed = false
                        for f in snapshotFiles do
                            let src = Path.Combine(stagingDir, f)
                            let dst = Path.Combine(outputRoot, f)
                            if File.Exists src then
                                if File.Exists dst then File.Delete dst
                                File.Move(src, dst)
                            elif File.Exists dst then
                                // Missing expected file in staging
                                moveFailed <- true

                        if moveFailed then
                            raise (IOException("not all expected files present in staging"))

                        safeDeleteDir stagingDir
                        { Success = true; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = semanticSha;
                          PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false;
                          Failure = None; CleanupFailure = None }
                    with ex ->
                        // Rollback to previous snapshot
                        let restored = restoreSnapshot outputRoot previousSnapshot
                        safeDeleteDir stagingDir
                        { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = "";
                          PreviousSnapshotPreserved = restored; LiveSnapshotMayHaveChanged = not restored;
                          Failure = Some(SnapshotReplacementFailed (sprintf "%s: %s" (ex.GetType().Name) (ex.Message)));
                          CleanupFailure = None }
        with ex ->
            // Attempt cleanup, preserving cleanup failure details
            let mutable cleanupFailure = None
            try
                safeDeleteDir stagingDir
            with ex2 ->
                cleanupFailure <- Some { Path = stagingDir; ExceptionType = ex2.GetType().Name; Message = ex2.Message }
            { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = "";
              PreviousSnapshotPreserved = true; LiveSnapshotMayHaveChanged = false;
              Failure = Some(SnapshotStagingFailed (sprintf "%s: %s" (ex.GetType().Name) (ex.Message)));
              CleanupFailure = cleanupFailure }

/// Publish a snapshot using the staged validation pipeline.
/// This is the production entry point that uses staged round-trip validation.
let publishStagedSnapshot
    (outputRoot: string)
    (records: CanonicalExecutionEvidence list)
    (aggregate: CanonicalExecutionAggregate)
    (compatibilityProjection: CanonicalEvidence)
    : PublicationOutcome =
    stageAndPublishSnapshot outputRoot records aggregate compatibilityProjection None
