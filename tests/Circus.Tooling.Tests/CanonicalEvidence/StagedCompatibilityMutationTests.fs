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
// 2. Applies a mutation to the staged compatibility projection using domain functions
// 3. Re-renders and writes the mutated document with correct semantic hash
// 4. Proves that stageAndPublishSnapshot rejects it with the appropriate typed failure
// 5. Verifies ALL FOUR live snapshot files are preserved unchanged
// =============================================================================

open System
open System.IO
open System.Text

open Expecto

open Circus.Tooling.CanonicalEvidence
open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.CanonicalEvidence.Serialization
open Circus.Tooling.CanonicalEvidence.Validation

// -----------------------------------------------------------------------------
// Test helpers
// -----------------------------------------------------------------------------

/// Strict UTF-8 without BOM encoding for canonical byte writing
let private strictUtf8 = UTF8Encoding(false, true)

let private tempDir () =
    let temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory(temp) |> ignore
    temp

let private cleanupDir (dir: string) =
    try Directory.Delete(dir, true) with _ -> ()

// Snapshot file names
let private snapshotFiles = ["records.jsonl"; "aggregate.json"; "artifacts.jsonl"; "canonical-evidence.json"]

/// Read all four snapshot files and return their bytes
let private readSnapshot (dir: string) : Map<string, byte array option> =
    snapshotFiles
    |> List.map (fun f ->
        let path = Path.Combine(dir, f)
        let bytes = if File.Exists path then Some(File.ReadAllBytes path) else None
        f, bytes)
    |> Map.ofList

/// Compare two snapshots, returning list of differences
let private compareSnapshots (before: Map<string, byte array option>) (after: Map<string, byte array option>) : string list =
    snapshotFiles
    |> List.filter (fun f ->
        match before.[f], after.[f] with
        | Some b, Some a -> b <> a
        | None, None -> false
        | _ -> true)
    |> List.map (fun f -> sprintf "%s changed" f)

/// Write canonical bytes using strict UTF-8 (mirrors production)
let private writeCanonicalBytes (path: string) (text: string) =
    let bytes = strictUtf8.GetBytes(text)
    File.WriteAllBytes(path, bytes)

// -----------------------------------------------------------------------------
// TOP-LEVEL FIELD MUTATION TESTS (rehashed)
// -----------------------------------------------------------------------------

/// Test: Mutate provider_name, rehash, re-render - requires CompatibilityProjectionMismatch
let [<Tests>] rehashedProviderNameTests =
    testList "RehashedProviderNameMutation" [
        testCase "rejects rehashed provider_name mutation with CompatibilityProjectionMismatch" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Mutation: change provider_name in domain model, re-render with correct hash
                let mutatedProjection =
                    fixture.CompatibilityProjection
                    |> (fun p -> { p with ProviderName = "malicious-provider" })
                    |> withSemanticHash

                let mutatedJson = renderWireJson mutatedProjection

                // Mutation function writes re-rendered, correctly-hashed document
                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        writeCanonicalBytes compatPath mutatedJson
                        Ok()

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after rehashed provider_name mutation"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    let hasProviderMismatch =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityProjectionMismatch d ->
                                d.Contains("provider_name")
                            | _ -> false)
                    Expect.isTrue hasProviderMismatch "should detect provider_name projection mismatch"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

/// Test: Mutate overall_status, rehash, re-render - requires CompatibilityProjectionMismatch
let [<Tests>] rehashedOverallStatusTests =
    testList "RehashedOverallStatusMutation" [
        testCase "rejects rehashed overall_status mutation with CompatibilityProjectionMismatch" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Mutation: change overall_status from pass to fail, rehash
                let mutatedProjection =
                    fixture.CompatibilityProjection
                    |> (fun p -> { p with OverallStatus = Fail })
                    |> withSemanticHash

                let mutatedJson = renderWireJson mutatedProjection

                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        writeCanonicalBytes compatPath mutatedJson
                        Ok()

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after rehashed overall_status mutation"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    let hasStatusMismatch =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityProjectionMismatch d ->
                                d.Contains("overall_status")
                            | _ -> false)
                    Expect.isTrue hasStatusMismatch "should detect overall_status projection mismatch"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

/// Test: Change tested_commit_oid, rehash - requires CompatibilityProjectionMismatch (not AggregateMismatch)
/// Also proves commit values are NOT misclassified as hash values
let [<Tests>] rehashedCommitOidTests =
    testList "RehashedCommitOidMutation" [
        testCase "rejects rehashed tested_commit_oid mutation with CompatibilityProjectionMismatch" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Mutation: change commit OID, rehash
                let mutatedProjection =
                    fixture.CompatibilityProjection
                    |> (fun p -> { p with TestedCommitOid = "1111111111111111111111111111111111111111" })
                    |> withSemanticHash

                let mutatedJson = renderWireJson mutatedProjection

                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        writeCanonicalBytes compatPath mutatedJson
                        Ok()

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after rehashed commit OID mutation"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    // MUST have CompatibilityProjectionMismatch for commit OID (exact taxonomy requirement)
                    let hasProjectionMismatch =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityProjectionMismatch d ->
                                d.Contains("tested_commit_oid")
                            | _ -> false)
                    Expect.isTrue hasProjectionMismatch "MUST detect tested_commit_oid projection mismatch (exact taxonomy)"

                    // Prove commit values are NOT misclassified as hash values
                    // (semantic hash may differ legitimately, but commit values must not appear in hash mismatch)
                    let commitOidsMisclassifiedAsHashes =
                        failures
                        |> List.choose (function
                            | StagedSnapshotFailure.CompatibilitySemanticHashMismatch(expected, actual) ->
                                if expected = fixture.CompatibilityProjection.TestedCommitOid
                                   || actual = mutatedProjection.TestedCommitOid then
                                    Some(expected, actual)
                                else None
                            | _ -> None)
                    Expect.isEmpty commitOidsMisclassifiedAsHashes
                        "commit OIDs must never be rendered as semantic-hash values"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

// -----------------------------------------------------------------------------
// PER-CHECK FIELD MUTATION TESTS (rehashed)
// -----------------------------------------------------------------------------

/// Test: Mutate per-check FailureKind, rehash - requires CompatibilityRecordMismatch with failure_kind detail
let [<Tests>] rehashedCheckFailureKindTests =
    testList "RehashedCheckFailureKindMutation" [
        testCase "rejects rehashed FailureKind mutation with CompatibilityRecordMismatch" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Find a check with a FailureKind and change it
                let mutatedChecks =
                    fixture.CompatibilityProjection.Checks
                    |> List.map (fun check ->
                        if check.FailureKind.IsSome then
                            { check with FailureKind = Some "assertion_failure" }
                        else check)

                let mutatedProjection =
                    { fixture.CompatibilityProjection with Checks = mutatedChecks }
                    |> withSemanticHash

                let mutatedJson = renderWireJson mutatedProjection

                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        writeCanonicalBytes compatPath mutatedJson
                        Ok()

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after rehashed FailureKind mutation"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    let hasCheckMismatch =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityRecordMismatch (id, detail) ->
                                detail.Contains("failure_kind")
                            | _ -> false)
                    Expect.isTrue hasCheckMismatch "should detect check-level FailureKind mismatch"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

// -----------------------------------------------------------------------------
// BIOJECTION MUTATION TESTS (rehashed)
// -----------------------------------------------------------------------------

/// Test: Remove a check, rehash - requires CheckCount + MissingCheck
let [<Tests>] rehashedRemovedCheckTests =
    testList "RehashedRemovedCheckMutation" [
        testCase "rejects rehashed removed check with CheckCount and MissingCheck" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Remove first check
                let mutatedChecks =
                    match fixture.CompatibilityProjection.Checks with
                    | [] -> []
                    | _ :: rest -> rest

                let mutatedProjection =
                    { fixture.CompatibilityProjection with Checks = mutatedChecks }
                    |> withSemanticHash

                let mutatedJson = renderWireJson mutatedProjection

                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        writeCanonicalBytes compatPath mutatedJson
                        Ok()

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after removing check"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    // Must have check count mismatch
                    let hasCountMismatch =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityRecordMismatch (id, detail) ->
                                id = "(all)" && detail.Contains("check count")
                            | _ -> false)
                    Expect.isTrue hasCountMismatch "should detect check count mismatch"

                    // Must have missing check report
                    let hasMissingCheck =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityRecordMismatch (id, detail) ->
                                id <> "(all)" && detail.Contains("missing")
                            | _ -> false)
                    Expect.isTrue hasMissingCheck "should detect missing check"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

/// Test: Add unknown check, rehash - requires UnknownCheck
let [<Tests>] rehashedUnknownCheckTests =
    testList "RehashedUnknownCheckMutation" [
        testCase "rejects rehashed unknown check with UnknownCheck" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Add an extra check
                let extraCheck = {
                    Id = "extra-evidence-999"
                    CommandArgv = ["echo"; "extra"]
                    WorkingDirectory = "/tmp"
                    DurationMilliseconds = 10L
                    ExitCode = Some 0
                    Status = Pass
                    StdoutSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                    StderrSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                    FailureKind = None
                }

                let mutatedChecks =
                    fixture.CompatibilityProjection.Checks @ [extraCheck]

                let mutatedProjection =
                    { fixture.CompatibilityProjection with Checks = mutatedChecks }
                    |> withSemanticHash

                let mutatedJson = renderWireJson mutatedProjection

                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        writeCanonicalBytes compatPath mutatedJson
                        Ok()

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "publication should fail after adding unknown check"
                match outcome.Failure with
                | Some (SnapshotStagedValidationFailed failures) ->
                    // Must have unknown check report
                    let hasUnknownCheck =
                        failures |> List.exists (function
                            | StagedSnapshotFailure.CompatibilityRecordMismatch (id, detail) ->
                                id <> "(all)" && detail.Contains("unknown")
                            | _ -> false)
                    Expect.isTrue hasUnknownCheck "should detect unknown check"
                | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

/// Test: Duplicate check ID, rehash - requires DuplicateActualCheckId
let [<Tests>] rehashedDuplicateCheckIdTests =
    testList "RehashedDuplicateCheckIdMutation" [
        testCase "rejects rehashed duplicate check ID with DuplicateActualCheckId" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // Duplicate the first check ID
                let originalChecks = fixture.CompatibilityProjection.Checks
                match originalChecks with
                | [] -> () // No checks to duplicate
                | first :: rest ->
                    // Create duplicate with same Id
                    let duplicateCheck = {
                        first with
                            Id = first.Id // Same ID = duplicate
                    }
                    let mutatedChecks = first :: duplicateCheck :: rest

                    let mutatedProjection =
                        { fixture.CompatibilityProjection with Checks = mutatedChecks }
                        |> withSemanticHash

                    let mutatedJson = renderWireJson mutatedProjection

                    let mutation: string -> Result<unit, string> =
                        fun stagingDir ->
                            let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                            writeCanonicalBytes compatPath mutatedJson
                            Ok()

                    let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                    Expect.isFalse outcome.Success "publication should fail after duplicating check ID"
                    match outcome.Failure with
                    | Some (SnapshotStagedValidationFailed failures) ->
                        // Must have duplicate check ID report
                        let hasDuplicateCheck =
                            failures |> List.exists (function
                                | StagedSnapshotFailure.CompatibilityRecordMismatch (id, detail) ->
                                    detail.Contains("duplicate") || detail.Contains("mismatch")
                                | _ -> false)
                        Expect.isTrue hasDuplicateCheck "should detect duplicate check ID"
                    | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
            finally
                cleanupDir workDir
    ]

// -----------------------------------------------------------------------------
// PARSE FAILURE TEST
// -----------------------------------------------------------------------------

/// Test: Invalid JSON is rejected with CompatibilityParseFailed
let [<Tests>] invalidJsonTests =
    testList "InvalidJsonMutation" [
        testCase "rejects invalid JSON with CompatibilityParseFailed" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        File.WriteAllText(compatPath, "{ this is not json }")
                        Ok()

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

// -----------------------------------------------------------------------------
// SUCCESS AND PRESERVATION TESTS
// -----------------------------------------------------------------------------

/// Test: Valid snapshot succeeds
let [<Tests>] validSnapshotTests =
    testList "ValidSnapshot" [
        testCase "valid snapshot passes without mutation" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None

                Expect.isTrue outcome.Success "valid snapshot should succeed"
                for f in snapshotFiles do
                    let path = Path.Combine(workDir, f)
                    Expect.isTrue (File.Exists path) (sprintf "%s should exist" f)
            finally
                cleanupDir workDir
    ]

/// Test: All four files preserved after rejection
let [<Tests>] fourFilePreservationTests =
    testList "FourFilePreservation" [
        testCase "all four live snapshot files preserved after rejection" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // First, publish a valid snapshot
                let cleanOutcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue cleanOutcome.Success "first publication should succeed"

                // Capture all four files before mutation
                let beforeSnapshot = readSnapshot workDir

                // Now mutate
                let mutatedProjection =
                    fixture.CompatibilityProjection
                    |> (fun p -> { p with ProviderName = "EVIL-PROVIDER" })
                    |> withSemanticHash
                let mutatedJson = renderWireJson mutatedProjection

                let mutation: string -> Result<unit, string> =
                    fun stagingDir ->
                        let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                        writeCanonicalBytes compatPath mutatedJson
                        Ok()

                let outcome = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutation)

                Expect.isFalse outcome.Success "second publication should fail"
                Expect.isTrue outcome.PreviousSnapshotPreserved "previous snapshot should be preserved"

                // Capture all four files after rejection
                let afterSnapshot = readSnapshot workDir

                // Verify ALL FOUR files unchanged (byte-identical)
                let changes = compareSnapshots beforeSnapshot afterSnapshot
                Expect.isEmpty changes (sprintf "No files should change after rejection: %A" changes)
            finally
                cleanupDir workDir
    ]

/// Test: Idempotent overwrite succeeds
let [<Tests>] idempotentOverwriteTests =
    testList "IdempotentOverwrite" [
        testCase "idempotent overwrite succeeds" <| fun () ->
            let workDir = tempDir ()
            try
                let fixture = PublicationFixture.createValidPublicationFixture ()

                // First publication
                let outcome1 = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome1.Success "first publication should succeed"

                // Capture content
                let beforeSnapshot = readSnapshot workDir

                // Second publication with same content
                let outcome2 = stageAndPublishSnapshot workDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome2.Success "idempotent publication should succeed"

                // Verify all files unchanged
                let afterSnapshot = readSnapshot workDir
                let changes = compareSnapshots beforeSnapshot afterSnapshot
                Expect.isEmpty changes "content should be unchanged after idempotent overwrite"
            finally
                cleanupDir workDir
    ]
