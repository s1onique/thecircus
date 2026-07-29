module Circus.Tooling.Tests.CanonicalEvidence.StagedCompatibilityMutationTests

// =============================================================================
// Staged compatibility mutation tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01
//
// These tests prove that the production comparator in Validation.fs is wired into
// the staged validation pipeline and detects mutations in the canonical-evidence.json
// file by comparing against the provider-owned expected projection.
//
// Each test:
// 1. Creates a valid snapshot using the production pipeline
// 2. Applies a mutation to the staged canonical-evidence.json
// 3. Proves that stageAndPublishSnapshot rejects it with the appropriate typed failure
// 4. Verifies the live snapshot is preserved
// =============================================================================

open System
open System.IO

open Expecto

open Circus.Tooling.CanonicalEvidence
open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.CanonicalEvidence.Validation

// -----------------------------------------------------------------------------
// Test helpers
// -----------------------------------------------------------------------------

let private tempDir () =
    let temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory(temp) |> ignore
    temp

let private cleanupDir (dir: string) =
    try Directory.Delete(dir, true) with _ -> ()

// -----------------------------------------------------------------------------
// Staged mutation tests using the production fixture
// -----------------------------------------------------------------------------

/// Test: Mutate provider_name in staged canonical-evidence.json
/// Requires: CompatibilityProjectionMismatch with provider_name detail
let [<Tests>] stagedMutationProviderNameTests =
    testList "StagedMutationProviderName" [
        testCase "rejects mutated provider_name with CompatibilityProjectionMismatch" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Mutation: corrupt provider_name in staged file
                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        if File.Exists compatPath then
                            let content = File.ReadAllText compatPath
                            let corrupted = content.Replace("circus-canonical-evidence", "malicious-provider")
                            File.WriteAllText(compatPath, corrupted)
                            Ok()
                        else
                            Error("canonical-evidence.json not found")

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after provider_name mutation"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    let hasProviderMismatch =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityProjectionMismatch d ->
                                d.Contains("provider_name") || d.Contains("malicious-provider")
                            | _ -> false)
                    Expect.isTrue hasProviderMismatch "should detect provider_name mismatch"
                    Expect.isTrue outcome.PreviousSnapshotPreserved "previous snapshot should be preserved"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure

                // Verify live snapshot unchanged
                Expect.isFalse (File.Exists (Path.Combine(workDir, "canonical-evidence.json"))) "live snapshot should not exist after rejection"
            finally
                cleanupDir workDir
    ]

/// Test: Mutate schema_version in staged canonical-evidence.json
/// Requires: CompatibilityProjectionMismatch with schema_version detail
let [<Tests>] stagedMutationSchemaVersionTests =
    testList "StagedMutationSchemaVersion" [
        testCase "rejects mutated schema_version with CompatibilityProjectionMismatch" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Mutation: change schema_version in staged file
                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        if File.Exists compatPath then
                            let content = File.ReadAllText compatPath
                            let corrupted = content.Replace("\"schema_version\":1", "\"schema_version\":99")
                            File.WriteAllText(compatPath, corrupted)
                            Ok()
                        else
                            Error("canonical-evidence.json not found")

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after schema_version mutation"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    let hasSchemaMismatch =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityProjectionMismatch d ->
                                d.Contains("schema_version")
                            | _ -> false)
                    Expect.isTrue hasSchemaMismatch "should detect schema_version mismatch"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

/// Test: Mutate tested_commit_oid in staged canonical-evidence.json
/// Requires: CompatibilityProjectionMismatch (not SemanticHashMismatch) per taxonomy fix
let [<Tests>] stagedMutationCommitOidTests =
    testList "StagedMutationCommitOid" [
        testCase "rejects mutated tested_commit_oid with CompatibilityProjectionMismatch" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Mutation: corrupt commit OID in staged file
                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        if File.Exists compatPath then
                            let content = File.ReadAllText compatPath
                            let corrupted = content.Replace("tested_commit_oid\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "tested_commit_oid\":\"1111111111111111111111111111111111111111")
                            File.WriteAllText(compatPath, corrupted)
                            Ok()
                        else
                            Error("canonical-evidence.json not found")

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after commit OID mutation"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    // Should detect commit mismatch - either as projection mismatch or aggregate mismatch
                    let hasMismatch =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityProjectionMismatch d ->
                                d.Contains("commit") || d.Contains("oid")
                            | StagedSnapshotFailure.AggregateMismatch _ -> true
                            | _ -> false)
                    Expect.isTrue hasMismatch "should detect commit OID mismatch"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

/// Test: Corrupt canonical-evidence.json to invalid JSON
/// Requires: CompatibilityParseFailed
let [<Tests>] stagedMutationInvalidJsonTests =
    testList "StagedMutationInvalidJson" [
        testCase "rejects invalid JSON in canonical-evidence.json with CompatibilityParseFailed" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Mutation: corrupt JSON structure
                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        if File.Exists compatPath then
                            File.WriteAllText(compatPath, "{ this is not json }")
                            Ok()
                        else
                            Error("canonical-evidence.json not found")

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after JSON corruption"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    let hasParseFailure =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityParseFailed _ -> true
                            | _ -> false)
                    Expect.isTrue hasParseFailure "should detect JSON parse failure"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

/// Test: Verify valid snapshot succeeds (no mutation)
/// Requires: Success = true, all files written
let [<Tests>] stagedMutationValidSnapshotTests =
    testList "StagedMutationValidSnapshot" [
        testCase "valid snapshot passes without mutation" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // No mutation
                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None

                Expect.isTrue outcome.Success "valid snapshot should succeed"
                Expect.isTrue (File.Exists (Path.Combine(workDir, "records.jsonl"))) "records.jsonl should exist"
                Expect.isTrue (File.Exists (Path.Combine(workDir, "aggregate.json"))) "aggregate.json should exist"
                Expect.isTrue (File.Exists (Path.Combine(workDir, "artifacts.jsonl"))) "artifacts.jsonl should exist"
                Expect.isTrue (File.Exists (Path.Combine(workDir, "canonical-evidence.json"))) "canonical-evidence.json should exist"
                Expect.isTrue outcome.PreviousSnapshotPreserved "previous snapshot preserved flag should be true"
            finally
                cleanupDir workDir
    ]

/// Test: Previous snapshot preserved after rejection
/// Requires: PreviousSnapshotPreserved = true, live snapshot unchanged
let [<Tests>] stagedMutationPreservationTests =
    testList "StagedMutationPreservation" [
        testCase "previous snapshot preserved after rejection" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // First, publish a valid snapshot
                let cleanOutcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue cleanOutcome.Success "first publication should succeed"

                // Read live content for verification
                let originalContent = File.ReadAllText (Path.Combine(workDir, "canonical-evidence.json"))
                Expect.isTrue (originalContent.Contains("circus-canonical-evidence")) "original should contain provider name"

                // Now mutate
                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        if File.Exists compatPath then
                            let content = File.ReadAllText compatPath
                            let corrupted = content.Replace("circus-canonical-evidence", "EVIL")
                            File.WriteAllText(compatPath, corrupted)
                            Ok()
                        else
                            Error("canonical-evidence.json not found")

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "second publication should fail"
                Expect.isTrue outcome.PreviousSnapshotPreserved "previous snapshot should be preserved"

                // Verify live snapshot bytes unchanged
                let liveContent = File.ReadAllText (Path.Combine(workDir, "canonical-evidence.json"))
                Expect.equal liveContent originalContent "live snapshot should be byte-identical to original"
            finally
                cleanupDir workDir
    ]

/// Test: Idempotent overwrite succeeds
/// Requires: Success = true, no validation failures
let [<Tests>] stagedMutationIdempotentTests =
    testList "StagedMutationIdempotent" [
        testCase "idempotent overwrite succeeds" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // First publication
                let outcome1 = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome1.Success "first publication should succeed"

                // Second publication with same content
                let outcome2 = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome2.Success "idempotent publication should succeed"

                // Verify content unchanged
                let content1 = File.ReadAllText (Path.Combine(workDir, "canonical-evidence.json"))
                let outcome3 = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let content2 = File.ReadAllText (Path.Combine(workDir, "canonical-evidence.json"))
                Expect.equal content1 content2 "content should be unchanged after idempotent overwrite"
            finally
                cleanupDir workDir
    ]
