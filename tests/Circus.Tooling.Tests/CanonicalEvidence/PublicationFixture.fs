module Circus.Tooling.Tests.CanonicalEvidence.PublicationFixture

// =============================================================================
// Canonical evidence – publication fixture builder
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION03
//
// This module provides constructor-derived publication fixtures for testing.
// All fixtures use valid OIDs and are built through the production pipeline.
// =============================================================================

open System
open System.Globalization

open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.RecordPipeline
open Circus.Tooling.CanonicalEvidence.Serialization

// -----------------------------------------------------------------------------
// Valid test OIDs (40-char SHA-1 for commits/trees, 64-char SHA-256 for hashes)
// -----------------------------------------------------------------------------

/// Valid 40-char SHA-1 OID for testing.
let testCommitOid1 = "a".PadRight(40, 'a')  // "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
let testCommitOid2 = "b".PadRight(40, 'b')  // "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
let testTreeOid1 = "c".PadRight(40, 'c')     // "cccccccccccccccccccccccccccccccccccccccc"
let testTreeOid2 = "d".PadRight(40, 'd')     // "dddddddddddddddddddddddddddddddddddddddd"

/// Valid 64-char SHA-256 OID for testing.
let testEvidenceId1 = "e".PadRight(64, 'e')
let testEvidenceId2 = "f".PadRight(64, 'f')

// -----------------------------------------------------------------------------
// ExecutedCanonicalCheck builder
// -----------------------------------------------------------------------------

/// Build an ExecutedCanonicalCheck with valid test data.
let createExecutedCheck
    (id: string)
    (required: bool)
    (status: EvidenceStatus)
    (startedAt: DateTimeOffset)
    : ExecutedCanonicalCheck =
    {
        Definition = {
            Id = id
            Executable = "dotnet"
            Arguments = [ "build"; "--no-restore" ]
            WorkingDirectory = "/repo"
            Required = required
            Timeout = TimeSpan.FromMinutes(5.0)
            StdoutLimitBytes = 32 * 1024 * 1024
            StderrLimitBytes = 32 * 1024 * 1024
        }
        Result = {
            Id = id
            CommandArgv = [ "dotnet"; "build"; "--no-restore" ]
            WorkingDirectory = "/repo"
            DurationMilliseconds = 1500L
            ExitCode = Some(if status = Pass then 0 else 1)
            Status = status
            StdoutSha256 = Some("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
            StderrSha256 = Some("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
            FailureKind = if status = Fail then Some("non_zero_exit:1") else None
        }
        StartedAt = startedAt
    }

// -----------------------------------------------------------------------------
// ValidPublicationFixture type and builder
// -----------------------------------------------------------------------------

type ValidPublicationFixture = {
    /// Executed checks with per-check timestamps.
    ExecutedChecks: ExecutedCanonicalCheck list
    /// Canonical execution evidence records.
    Records: CanonicalExecutionEvidence list
    /// Canonical execution aggregate.
    Aggregate: CanonicalExecutionAggregate
    /// Provider-owned compatibility projection.
    CompatibilityProjection: CanonicalEvidence
}

/// Build a valid publication fixture through the production pipeline.
/// Uses valid 40-char SHA-1 OIDs for commits/trees and produces
/// proper evidence IDs and semantic hashes.
let createValidPublicationFixture (): ValidPublicationFixture =
    // Create two executed checks: one required pass, one optional fail
    let check1Start = DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero)
    let check2Start = check1Start.AddSeconds(2.0)

    let executedChecks = [
        createExecutedCheck "tooling-build" true Pass check1Start
        createExecutedCheck "tooling-tests-build" false Fail check2Start
    ]

    // Convert to records using the production pipeline
    let recordsResult = convertExecutedChecksToRecords executedChecks testCommitOid1 testTreeOid1 true

    match recordsResult with
    | Error e -> failwithf "Fixture creation failed: %A" e
    | Ok records ->
        // Validate records
        let validation = validateRecords records testCommitOid1 testTreeOid1
        if not validation.Valid then
            failwithf "Fixture records validation failed: %A" validation.Issues

        // Compute aggregate
        let aggregate =
            computeAggregate testCommitOid1 testTreeOid1 records
            |> finalizeAggregate

        // Verify aggregate semantic hash recomputes
        let recomputedHash = computeAggregateSemanticHash aggregate
        if recomputedHash <> aggregate.SemanticSha256 then
            failwith "Aggregate semantic hash does not recompute"

        // Build compatibility projection
        let compatChecks = records |> List.map (fun r ->
            {
                Id = r.EvidenceId
                CommandArgv = r.Command :: r.Arguments
                WorkingDirectory = r.WorkingDirectory
                DurationMilliseconds = r.DurationMs
                ExitCode = r.ExitCode
                Status = match r.Result with | RecordPass -> Pass | RecordFail -> Fail | RecordUnavailable -> Unavailable
                StdoutSha256 = r.StdoutSha256
                StderrSha256 = r.StderrSha256
                FailureKind = r.FailureKind
            }
        )

        let compatibilityProjectionBase = {
            SchemaVersion = 1
            ProviderName = "circus-canonical-evidence"
            ProviderVersion = "1.0.0"
            TestedCommitOid = testCommitOid1
            TestedTreeOid = testTreeOid1
            ObjectFormat = "sha1"
            ActiveScopeActId = ""
            ActiveScopePointerBlobOid = ""
            ScopeDeclarationPath = "/.circus/scope.yaml"
            DeclarationBlobOid = ""
            BaselineCommitOid = ""
            Checks = compatChecks
            OverallStatus = 
                match aggregate.OverallStatus with
                | RecordPass -> Pass
                | RecordFail -> Fail
                | RecordUnavailable -> Unavailable
            SemanticSha256 = ""  // Will be computed
        }

        let compatibilityProjection = withSemanticHash compatibilityProjectionBase

        // Verify compatibility semantic hash recomputes
        let compatRecomputedHash = computeSemanticHash compatibilityProjection
        if compatRecomputedHash <> compatibilityProjection.SemanticSha256 then
            failwith "Compatibility semantic hash does not recompute"

        {
            ExecutedChecks = executedChecks
            Records = records
            Aggregate = aggregate
            CompatibilityProjection = compatibilityProjection
        }
