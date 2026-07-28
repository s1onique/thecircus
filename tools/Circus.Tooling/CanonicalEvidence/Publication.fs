module Circus.Tooling.CanonicalEvidence.Publication

// =============================================================================
// Canonical evidence – atomic snapshot publication
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01
// =============================================================================

open System
open System.IO

open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.FSharpDiagnostics.Hashing

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

let private escapeJsonStringPub (s: string) : string =
    let sb = System.Text.StringBuilder(s.Length + 10)
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

type PublicationOutcome = {
    Success: bool
    SnapshotPath: string
    RecordsCount: int
    AggregateSha256: string
    PreviousSnapshotPreserved: bool
    Failure: PublicationFailure option
}

// -----------------------------------------------------------------------------
// Compatibility projection
// -----------------------------------------------------------------------------

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

let private safeDeletePub (path: string) : unit =
    if File.Exists path then File.Delete path

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
let publishSnapshot (outputRoot: string) (records: CanonicalExecutionEvidence list) (aggregate: CanonicalExecutionAggregate) : PublicationOutcome =
    let snapshotFiles = ["records.jsonl"; "aggregate.json"; "artifacts.jsonl"; "canonical-evidence.json"]
    let recordsCount = List.length records
    let semanticSha = aggregate.SemanticSha256
    
    let ensureOutputDir dir =
        if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
        Directory.Exists dir
    
    if not (ensureOutputDir outputRoot) then
        { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = true; Failure = Some(SnapshotStagingFailed "cannot create output directory") }
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
                safeDeletePub stagingDir
                { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = true; Failure = Some(SnapshotValidationFailed validation.Issues) }
            else
                try
                    for f in snapshotFiles do
                        let src = Path.Combine(stagingDir, f)
                        let dst = Path.Combine(outputRoot, f)
                        if File.Exists src then
                            if File.Exists dst then File.Delete dst
                            File.Move(src, dst)
                    safeDeletePub stagingDir
                    { Success = true; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = semanticSha; PreviousSnapshotPreserved = true; Failure = None }
                with ex ->
                    let restored = restoreSnapshot outputRoot previousSnapshot
                    safeDeletePub stagingDir
                    { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = restored; Failure = Some(SnapshotReplacementFailed (sprintf "%s: %s" (ex.GetType().Name) ex.Message)) }
        with ex ->
            let restored = restoreSnapshot outputRoot previousSnapshot
            if Directory.Exists stagingDir then safeDeletePub stagingDir
            { Success = false; SnapshotPath = outputRoot; RecordsCount = recordsCount; AggregateSha256 = ""; PreviousSnapshotPreserved = restored; Failure = Some(SnapshotStagingFailed (sprintf "%s: %s" (ex.GetType().Name) ex.Message)) }
