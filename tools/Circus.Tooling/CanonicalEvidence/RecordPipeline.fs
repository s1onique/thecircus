module Circus.Tooling.CanonicalEvidence.RecordPipeline

// =============================================================================
// Canonical evidence – real record pipeline
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01
//
// This module implements the real per-check execution record pipeline:
//
//   - Definition/result bijection validation
//   - Per-check record conversion
//   - Record validation
//   - Aggregate derivation
//   - Compatibility projection construction
//
// The pipeline is built on top of the existing bounded check execution
// and does NOT introduce new process or Git authorities.
//
// This module compiles BEFORE Provider.fs and defines all required types.
// =============================================================================

open System
open System.Globalization

open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.Serialization
open Circus.Tooling.ScopeAuthority.Domain

// -----------------------------------------------------------------------------
// Record validation issues
// Note: RecordValidationIssue and recordValidationIssueToString are
// re-exported from EvidenceRecords module above.
// -----------------------------------------------------------------------------

type RecordValidationResult = {
    Valid: bool
    Issues: RecordValidationIssue list
}

// -----------------------------------------------------------------------------
// Record pipeline failures
// -----------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type RecordPipelineFailure =
    | DefinitionsEmpty
    | ResultsEmpty
    | DuplicateDefinitionId of checkId: string
    | DuplicateResultId of checkId: string
    | DefinitionMissingResult of checkId: string
    | ResultMissingDefinition of checkId: string
    | EmptyCommand of checkId: string
    | RecordIdentityMismatch of checkId: string * expected: string * actual: string
    | RecordValidationFailed of RecordValidationIssue list

let recordPipelineFailureToString (f: RecordPipelineFailure) : string =
    match f with
    | RecordPipelineFailure.DefinitionsEmpty -> "canonical check definitions list is empty"
    | RecordPipelineFailure.ResultsEmpty -> "check results list is empty"
    | RecordPipelineFailure.DuplicateDefinitionId id -> sprintf "duplicate definition check ID: %s" id
    | RecordPipelineFailure.DuplicateResultId id -> sprintf "duplicate result check ID: %s" id
    | RecordPipelineFailure.DefinitionMissingResult id -> sprintf "definition has no matching result: %s" id
    | RecordPipelineFailure.ResultMissingDefinition id -> sprintf "result has no matching definition: %s" id
    | RecordPipelineFailure.EmptyCommand id -> sprintf "empty command for check: %s" id
    | RecordPipelineFailure.RecordIdentityMismatch (id, expected, actual) ->
        sprintf "record identity mismatch for %s: expected=%s actual=%s" id expected actual
    | RecordPipelineFailure.RecordValidationFailed issues ->
        sprintf "record validation failed: %s" (String.concat "; " (List.map recordValidationIssueToString issues))

// -----------------------------------------------------------------------------
// Definition/result bijection validation
// -----------------------------------------------------------------------------

/// Validate that definitions and results form an exact bijection:
/// - Every definition has exactly one result
/// - Every result has exactly one definition
/// - All IDs are unique within each list
let validateBijection
    (definitions: EvidenceCheckDefinition list)
    (results: EvidenceCheckResult list)
    : Result<unit, RecordPipelineFailure> =
    // Check for empty inputs
    if List.isEmpty definitions then
        Result.Error RecordPipelineFailure.DefinitionsEmpty
    elif List.isEmpty results then
        Result.Error RecordPipelineFailure.ResultsEmpty
    else
        // Check for duplicate definition IDs
        let defIds = definitions |> List.map (fun d -> d.Id)
        let defIdCounts = defIds |> List.countBy id |> List.filter (fun (_, count) -> count > 1)
        match defIdCounts with
        | (id, _) :: _ -> Result.Error(RecordPipelineFailure.DuplicateDefinitionId id)
        | [] ->
            // Check for duplicate result IDs
            let resIds = results |> List.map (fun r -> r.Id)
            let resIdCounts = resIds |> List.countBy id |> List.filter (fun (_, count) -> count > 1)
            match resIdCounts with
            | (id, _) :: _ -> Result.Error(RecordPipelineFailure.DuplicateResultId id)
            | [] ->
                // Build ID sets for membership tests
                let defIdSet = Set.ofList defIds
                let resIdSet = Set.ofList resIds

                // Check each definition has a result
                let missingResult =
                    definitions
                    |> List.tryFind (fun d -> not (Set.contains d.Id resIdSet))
                match missingResult with
                | Some d -> Result.Error(RecordPipelineFailure.DefinitionMissingResult d.Id)
                | None ->
                    // Check each result has a definition
                    let missingDef =
                        results
                        |> List.tryFind (fun r -> not (Set.contains r.Id defIdSet))
                    match missingDef with
                    | Some r -> Result.Error(RecordPipelineFailure.ResultMissingDefinition r.Id)
                    | None ->
                        // Check counts match
                        if defIds.Length <> resIds.Length then
                            Result.Error(RecordPipelineFailure.DefinitionMissingResult
                                (sprintf "count mismatch: %d definitions, %d results" defIds.Length resIds.Length))
                        else
                            Result.Ok()

// -----------------------------------------------------------------------------
// Status mapping
// -----------------------------------------------------------------------------

/// Map EvidenceStatus to RecordStatus
let mapStatusToRecordStatus (status: EvidenceStatus) : RecordStatus =
    match status with
    | EvidenceStatus.Pass -> RecordPass
    | EvidenceStatus.Fail -> RecordFail
    | EvidenceStatus.Unavailable -> RecordUnavailable

// -----------------------------------------------------------------------------
// CORRECTION02: Per-check execution context
// -----------------------------------------------------------------------------

/// Captures the full execution context for a single check, including
/// its own pre-execution timestamp. The clock is sampled immediately
/// before RunCheck, not after.
type ExecutedCanonicalCheck = {
    /// The check definition that was executed.
    Definition: EvidenceCheckDefinition
    /// The result produced by RunCheck.
    Result: EvidenceCheckResult
    /// The UTC timestamp sampled immediately before RunCheck was called.
    /// This is the actual start time of this specific check.
    StartedAt: DateTimeOffset
}

// -----------------------------------------------------------------------------
// Per-check record conversion
// -----------------------------------------------------------------------------

/// Convert an ExecutedCanonicalCheck to a CanonicalExecutionEvidence record.
/// Each record receives its own StartedAt timestamp from the execution context.
let convertExecutedCheckToRecord
    (executed: ExecutedCanonicalCheck)
    (subjectCommitOid: string)
    (subjectTreeOid: string)
    (workingTreeClean: bool)
    : Result<CanonicalExecutionEvidence, RecordPipelineFailure> =
    let definition = executed.Definition
    let result = executed.Result
    let startedAt = executed.StartedAt

    // Validate command is non-empty
    match result.CommandArgv with
    | [] ->
        Result.Error(RecordPipelineFailure.EmptyCommand result.Id)
    | executable :: arguments ->
        let record =
            createEvidenceRecord
                result.Id
                definition.Required
                executable
                arguments
                result.WorkingDirectory
                (startedAt.ToString("O", CultureInfo.InvariantCulture))
                result.DurationMilliseconds
                result.ExitCode
                (mapStatusToRecordStatus result.Status)
                None // testsTotal - not available from bounded result
                None // testsPassed
                None // testsIgnored
                None // testsFailed
                None // testsErrored
                result.StdoutSha256
                result.StderrSha256
                None // stdoutByteLength - not available from bounded result
                None // stderrByteLength
                result.FailureKind // Preserve FailureKind from bounded execution
                subjectCommitOid
                subjectTreeOid
                workingTreeClean
                None // providerBinarySha256
                None // toolingBinarySha256
                None // testBinarySha256

        // Verify record identity recomputes correctly
        let recomputedId = computeEvidenceId record
        if recomputedId <> record.EvidenceId then
            Result.Error(RecordPipelineFailure.RecordIdentityMismatch(
                result.Id, record.EvidenceId, recomputedId))
        else
            Result.Ok record

/// Convert a list of ExecutedCanonicalCheck to evidence records.
/// Each record receives its own StartedAt from its execution context.
let convertExecutedChecksToRecords
    (executedChecks: ExecutedCanonicalCheck list)
    (subjectCommitOid: string)
    (subjectTreeOid: string)
    (workingTreeClean: bool)
    : Result<CanonicalExecutionEvidence list, RecordPipelineFailure> =
    // Convert each executed check to a record
    let mutable records : CanonicalExecutionEvidence list = []
    let mutable failure : RecordPipelineFailure option = None

    for executed in executedChecks do
        match failure with
        | Some _ -> ()
        | None ->
            match convertExecutedCheckToRecord executed subjectCommitOid subjectTreeOid workingTreeClean with
            | Ok record ->
                records <- record :: records
            | Error err ->
                failure <- Some err

    match failure with
    | Some f -> Result.Error f
    | None -> Result.Ok(List.rev records)

/// Legacy conversion function for backward compatibility.
/// Converts definitions and results to ExecutedCanonicalCheck list
/// where each check uses the same startedAt timestamp.
/// DEPRECATED: Use convertExecutedChecksToRecords with per-check timestamps.
let convertCheckResultsToRecords
    (definitions: EvidenceCheckDefinition list)
    (results: EvidenceCheckResult list)
    (subjectCommitOid: string)
    (subjectTreeOid: string)
    (workingTreeClean: bool)
    (startedAt: DateTimeOffset)
    : Result<CanonicalExecutionEvidence list, RecordPipelineFailure> =
    // Build executed checks with shared timestamp (legacy behavior)
    let executedChecks =
        List.map2 (fun def res -> { Definition = def; Result = res; StartedAt = startedAt })
            definitions
            results
    convertExecutedChecksToRecords executedChecks subjectCommitOid subjectTreeOid workingTreeClean

// -----------------------------------------------------------------------------
// Record validation
// -----------------------------------------------------------------------------

/// Validate a list of evidence records against expected subject identity.
let validateRecords
    (records: CanonicalExecutionEvidence list)
    (expectedCommitOid: string)
    (expectedTreeOid: string)
    : RecordValidationResult =
    let issues = ResizeArray<RecordValidationIssue>()

    // Check for empty record list
    if List.isEmpty records then
        issues.Add(RecordValidationIssue.RecordsEmpty)
    else
        // Check each record
        let evidenceIdCounts = System.Collections.Generic.Dictionary<string, int>()
        let checkIdCounts = System.Collections.Generic.Dictionary<string, int>()

        for r in records do
            // Count evidence IDs for duplicate detection
            if evidenceIdCounts.ContainsKey(r.EvidenceId) then
                evidenceIdCounts.[r.EvidenceId] <- evidenceIdCounts.[r.EvidenceId] + 1
            else
                evidenceIdCounts.[r.EvidenceId] <- 1

            // Count check IDs for duplicate detection
            if checkIdCounts.ContainsKey(r.CheckId) then
                checkIdCounts.[r.CheckId] <- checkIdCounts.[r.CheckId] + 1
            else
                checkIdCounts.[r.CheckId] <- 1

            // Check for empty evidence ID
            if String.IsNullOrEmpty(r.EvidenceId) then
                issues.Add(RecordValidationIssue.EvidenceIdEmpty r.CheckId)

            // Verify evidence ID recomputes
            let recomputedId = computeEvidenceId r
            if recomputedId <> r.EvidenceId then
                issues.Add(RecordValidationIssue.EvidenceIdMismatch(r.CheckId, r.EvidenceId, recomputedId))

            // Check subject commit matches
            if r.TestedCommitOid <> expectedCommitOid then
                issues.Add(RecordValidationIssue.SubjectMismatch(r.CheckId, expectedCommitOid, r.TestedCommitOid))

            // Check tree OID matches
            if r.TestedTreeOid <> expectedTreeOid then
                issues.Add(RecordValidationIssue.TreeMismatch(r.CheckId, expectedTreeOid, r.TestedTreeOid))

        // Check for duplicate evidence IDs
        for kv in evidenceIdCounts do
            if kv.Value > 1 then
                issues.Add(RecordValidationIssue.DuplicateEvidenceId kv.Key)

        // Check for duplicate check IDs
        for kv in checkIdCounts do
            if kv.Value > 1 then
                issues.Add(RecordValidationIssue.DuplicateCheckId kv.Key)

    { Valid = issues.Count = 0; Issues = List.ofSeq issues }

// -----------------------------------------------------------------------------
// Compatibility projection
// -----------------------------------------------------------------------------

/// Build the compatibility projection from records and scope binding.
let buildCompatibilityProjection
    (records: CanonicalExecutionEvidence list)
    (aggregate: CanonicalExecutionAggregate)
    (scope: ScopeBinding)
    (objectFormat: string)
    : CanonicalEvidence =
    let checks =
        records
        |> List.map (fun r ->
            {
                Id = r.CheckId
                CommandArgv = r.Command :: r.Arguments
                WorkingDirectory = r.WorkingDirectory
                DurationMilliseconds = r.DurationMs
                ExitCode = r.ExitCode
                Status =
                    match r.Result with
                    | RecordPass -> EvidenceStatus.Pass
                    | RecordFail -> EvidenceStatus.Fail
                    | RecordUnavailable -> EvidenceStatus.Unavailable
                StdoutSha256 = r.StdoutSha256
                StderrSha256 = r.StderrSha256
                FailureKind = r.FailureKind
            })
        |> sortChecksDeterministic

    let overallStatus =
        if aggregate.RequiredChecksFailed > 0 then
            EvidenceStatus.Fail
        elif aggregate.RequiredChecksTotal > 0 && aggregate.RequiredChecksPassed = aggregate.RequiredChecksTotal then
            EvidenceStatus.Pass
        elif aggregate.RequiredChecksTotal = 0 then
            EvidenceStatus.Pass
        else
            EvidenceStatus.Fail

    let doc = {
        SchemaVersion = SchemaVersionValue
        ProviderName = ProviderNameValue
        ProviderVersion = ProviderVersionValue
        TestedCommitOid = aggregate.SubjectCommitOid
        TestedTreeOid = aggregate.SubjectTreeOid
        ObjectFormat = objectFormat
        ActiveScopeActId = scope.ActId
        ActiveScopePointerBlobOid = scope.PointerBlobOid
        ScopeDeclarationPath = scope.DeclarationPath
        DeclarationBlobOid = scope.DeclarationBlobOid
        BaselineCommitOid = scope.BaselineCommitOid
        Checks = checks
        OverallStatus = overallStatus
        SemanticSha256 = ""
    }
    withSemanticHash doc

// -----------------------------------------------------------------------------
// Provider result type (for use by Provider.fs after compilation)
// -----------------------------------------------------------------------------

type CanonicalEvidenceProviderResult = {
    SubjectCommitOid: string
    SubjectTreeOid: string
    ObjectFormat: string
    Records: CanonicalExecutionEvidence list
    Aggregate: CanonicalExecutionAggregate
    CompatibilityProjection: CanonicalEvidence
}

/// Internal execution stage record for the provider
type CanonicalCheckExecution = {
    CommitOid: string
    TreeOid: string
    ObjectFormat: string
    WorkingTreeClean: bool
    StartedAt: DateTimeOffset
    Definitions: EvidenceCheckDefinition list
    Results: EvidenceCheckResult list
}
