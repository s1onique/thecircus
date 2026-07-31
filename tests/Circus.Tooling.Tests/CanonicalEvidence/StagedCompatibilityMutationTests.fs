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
    try
        Directory.Delete(dir, true)
    with _ ->
        ()

// Snapshot file names
let private snapshotFiles =
    [ "records.jsonl"
      "aggregate.json"
      "artifacts.jsonl"
      "canonical-evidence.json" ]

/// Read all four snapshot files and return their bytes
let private readSnapshot (dir: string) : Map<string, byte array option> =
    snapshotFiles
    |> List.map (fun f ->
        let path = Path.Combine(dir, f)

        let bytes =
            if File.Exists path then
                Some(File.ReadAllBytes path)
            else
                None

        f, bytes)
    |> Map.ofList

/// Compare two snapshots, returning list of differences
let private compareSnapshots
    (before: Map<string, byte array option>)
    (after: Map<string, byte array option>)
    : string list =
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
[<Tests>]
let rehashedProviderNameTests =
    testList
        "RehashedProviderNameMutation"
        [ testCase "rejects rehashed provider_name mutation with CompatibilityProjectionMismatch"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  // Mutation: change provider_name in domain model, re-render with correct hash
                  let mutatedProjection =
                      fixture.CompatibilityProjection
                      |> (fun p ->
                          { p with
                              ProviderName = "malicious-provider" })
                      |> withSemanticHash

                  let mutatedJson = renderWireJson mutatedProjection

                  // Mutation function writes re-rendered, correctly-hashed document
                  let mutation: string -> Result<unit, string> =
                      fun stagingDir ->
                          let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                          writeCanonicalBytes compatPath mutatedJson
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "publication should fail after rehashed provider_name mutation"

                  match outcome.Failure with
                  | Some(SnapshotStagedValidationFailed failures) ->
                      let hasProviderMismatch =
                          failures
                          |> List.exists (function
                              | StagedSnapshotFailure.CompatibilityProjectionMismatch d -> d.Contains("provider_name")
                              | _ -> false)

                      Expect.isTrue hasProviderMismatch "should detect provider_name projection mismatch"
                  | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
              finally
                  cleanupDir workDir ]

/// Test: Mutate overall_status, rehash, re-render - requires CompatibilityProjectionMismatch
[<Tests>]
let rehashedOverallStatusTests =
    testList
        "RehashedOverallStatusMutation"
        [ testCase "rejects rehashed overall_status mutation with CompatibilityProjectionMismatch"
          <| fun () ->
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

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "publication should fail after rehashed overall_status mutation"

                  match outcome.Failure with
                  | Some(SnapshotStagedValidationFailed failures) ->
                      let hasStatusMismatch =
                          failures
                          |> List.exists (function
                              | StagedSnapshotFailure.CompatibilityProjectionMismatch d -> d.Contains("overall_status")
                              | _ -> false)

                      Expect.isTrue hasStatusMismatch "should detect overall_status projection mismatch"
                  | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
              finally
                  cleanupDir workDir ]

/// Test: Change tested_commit_oid, rehash - requires commit OID mismatch detection
/// The cross-check between aggregate and staged file produces CompatibilityCommitOidMismatch
/// because the aggregate.SubjectCommitOid differs from staged file's TestedCommitOid.
[<Tests>]
let rehashedCommitOidTests =
    testList
        "RehashedCommitOidMutation"
        [ testCase "rejects rehashed tested_commit_oid mutation with commit OID mismatch"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  // Mutation: change commit OID, rehash
                  let mutatedCommitOid = "1111111111111111111111111111111111111111"

                  let mutatedProjection =
                      fixture.CompatibilityProjection
                      |> (fun p ->
                          { p with
                              TestedCommitOid = mutatedCommitOid })
                      |> withSemanticHash

                  let mutatedJson = renderWireJson mutatedProjection

                  let mutation: string -> Result<unit, string> =
                      fun stagingDir ->
                          let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                          writeCanonicalBytes compatPath mutatedJson
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "publication should fail after rehashed commit OID mutation"

                  match outcome.Failure with
                  | Some(SnapshotStagedValidationFailed failures) ->
                      // Cross-check failure: aggregate.SubjectCommitOid vs staged file's TestedCommitOid
                      // The production cross-check uses CompatibilityCommitOidMismatch for this comparison
                      // (not CompatibilitySemanticHashMismatch which is reserved for semantic hash comparisons)
                      let hasCommitOidMismatch =
                          failures
                          |> List.exists (function
                              | StagedSnapshotFailure.CompatibilityCommitOidMismatch(expected, actual) ->
                                  // Cross-check: expected = aggregate.SubjectCommitOid, actual = diskCompat.TestedCommitOid
                                  expected = fixture.Aggregate.SubjectCommitOid && actual = mutatedCommitOid
                              | _ -> false)

                      Expect.isTrue
                          hasCommitOidMismatch
                          (sprintf
                              "MUST detect commit OID mismatch: expected=%s actual=%s"
                              fixture.Aggregate.SubjectCommitOid
                              mutatedCommitOid)

                      // Prove no CompatibilitySemanticHashMismatch contains commit OIDs
                      // (semantic hash mismatches are for semantic hash values, not commit OIDs)
                      // Use exact comparison of actual values to avoid shape-based heuristics
                      let forbiddenCommitValues =
                          Set.ofList
                              [ fixture.Aggregate.SubjectCommitOid
                                fixture.CompatibilityProjection.TestedCommitOid
                                mutatedCommitOid ]

                      let misclassifiedCommitValues =
                          failures
                          |> List.collect (function
                              | StagedSnapshotFailure.CompatibilitySemanticHashMismatch(expected, actual) ->
                                  [ expected; actual ]
                              | _ -> [])
                          |> List.filter forbiddenCommitValues.Contains

                      Expect.isEmpty
                          misclassifiedCommitValues
                          "commit OIDs must never be carried by CompatibilitySemanticHashMismatch"
                  | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
              finally
                  cleanupDir workDir ]

/// Test: Change tested_tree_oid, rehash - requires tree OID mismatch detection
/// The cross-check between aggregate and staged file produces CompatibilityTreeOidMismatch
/// because the aggregate.SubjectTreeOid differs from staged file's TestedTreeOid.
[<Tests>]
let rehashedTreeOidTests =
    testList
        "RehashedTreeOidMutation"
        [ testCase "rejects rehashed tested_tree_oid mutation with tree OID mismatch"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  // Mutation: change tree OID, rehash
                  let mutatedTreeOid = "2222222222222222222222222222222222222222"

                  let mutatedProjection =
                      fixture.CompatibilityProjection
                      |> (fun p ->
                          { p with
                              TestedTreeOid = mutatedTreeOid })
                      |> withSemanticHash

                  let mutatedJson = renderWireJson mutatedProjection

                  let mutation: string -> Result<unit, string> =
                      fun stagingDir ->
                          let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                          writeCanonicalBytes compatPath mutatedJson
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "publication should fail after rehashed tree OID mutation"

                  match outcome.Failure with
                  | Some(SnapshotStagedValidationFailed failures) ->
                      // Cross-check failure: aggregate.SubjectTreeOid vs staged file's TestedTreeOid
                      // The production cross-check uses CompatibilityTreeOidMismatch for this comparison
                      // (not CompatibilitySemanticHashMismatch which is reserved for semantic hash comparisons)
                      let hasTreeOidMismatch =
                          failures
                          |> List.exists (function
                              | StagedSnapshotFailure.CompatibilityTreeOidMismatch(expected, actual) ->
                                  // Cross-check: expected = aggregate.SubjectTreeOid, actual = diskCompat.TestedTreeOid
                                  expected = fixture.Aggregate.SubjectTreeOid && actual = mutatedTreeOid
                              | _ -> false)

                      Expect.isTrue
                          hasTreeOidMismatch
                          (sprintf
                              "MUST detect tree OID mismatch: expected=%s actual=%s"
                              fixture.Aggregate.SubjectTreeOid
                              mutatedTreeOid)

                      // Prove no CompatibilitySemanticHashMismatch contains tree OIDs
                      // (semantic hash mismatches are for semantic hash values, not tree OIDs)
                      // Use exact comparison of actual values to avoid shape-based heuristics
                      let forbiddenTreeValues =
                          Set.ofList
                              [ fixture.Aggregate.SubjectTreeOid
                                fixture.CompatibilityProjection.TestedTreeOid
                                mutatedTreeOid ]

                      let misclassifiedTreeValues =
                          failures
                          |> List.collect (function
                              | StagedSnapshotFailure.CompatibilitySemanticHashMismatch(expected, actual) ->
                                  [ expected; actual ]
                              | _ -> [])
                          |> List.filter forbiddenTreeValues.Contains

                      Expect.isEmpty
                          misclassifiedTreeValues
                          "tree OIDs must never be carried by CompatibilitySemanticHashMismatch"
                  | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
              finally
                  cleanupDir workDir ]

// -----------------------------------------------------------------------------
// PER-CHECK FIELD MUTATION TESTS (rehashed)
// -----------------------------------------------------------------------------

/// Test: Mutate per-check FailureKind, rehash - requires CompatibilityRecordMismatch with failure_kind detail
[<Tests>]
let rehashedCheckFailureKindTests =
    testList
        "RehashedCheckFailureKindMutation"
        [ testCase "rejects rehashed FailureKind mutation with CompatibilityRecordMismatch"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  // Find a check with a FailureKind and bind its exact identity
                  let target =
                      fixture.CompatibilityProjection.Checks
                      |> List.tryFind (fun check -> check.FailureKind.IsSome)
                      |> Option.defaultWith (fun () -> failtest "fixture must contain a check with FailureKind")

                  let originalFailureKind = target.FailureKind
                  let mutatedFailureKind = Some "assertion_failure"

                  Expect.notEqual mutatedFailureKind originalFailureKind "test must actually change FailureKind"

                  // Mutate the target check
                  let mutatedChecks =
                      fixture.CompatibilityProjection.Checks
                      |> List.map (fun check ->
                          if check.Id = target.Id then
                              { check with
                                  FailureKind = mutatedFailureKind }
                          else
                              check)

                  let mutatedProjection =
                      { fixture.CompatibilityProjection with
                          Checks = mutatedChecks }
                      |> withSemanticHash

                  let mutatedJson = renderWireJson mutatedProjection

                  let mutation: string -> Result<unit, string> =
                      fun stagingDir ->
                          let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                          writeCanonicalBytes compatPath mutatedJson
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "publication should fail after rehashed FailureKind mutation"

                  match outcome.Failure with
                  | Some(SnapshotStagedValidationFailed failures) ->
                      // Must report FailureKind mismatch for the exact check ID
                      let hasCheckMismatch =
                          failures
                          |> List.exists (function
                              | StagedSnapshotFailure.CompatibilityRecordMismatch(id, detail) ->
                                  id = target.Id && detail.Contains("failure_kind")
                              | _ -> false)

                      Expect.isTrue
                          hasCheckMismatch
                          (sprintf "should detect FailureKind mismatch for check '%s'" target.Id)
                  | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
              finally
                  cleanupDir workDir ]

// -----------------------------------------------------------------------------
// BIJECTION MUTATION TESTS (rehashed)
// -----------------------------------------------------------------------------

/// Test: Remove a check, rehash - requires CheckCount + MissingCheck
[<Tests>]
let rehashedRemovedCheckTests =
    testList
        "RehashedRemovedCheckMutation"
        [ testCase "rejects rehashed removed check with CheckCount and MissingCheck"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  // Remove first check
                  if fixture.CompatibilityProjection.Checks.IsEmpty then
                      failtest "fixture must contain a check to remove"

                  let removedCheckId = fixture.CompatibilityProjection.Checks.Head.Id
                  let mutatedChecks = fixture.CompatibilityProjection.Checks.Tail

                  let mutatedProjection =
                      { fixture.CompatibilityProjection with
                          Checks = mutatedChecks }
                      |> withSemanticHash

                  let mutatedJson = renderWireJson mutatedProjection

                  let mutation: string -> Result<unit, string> =
                      fun stagingDir ->
                          let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                          writeCanonicalBytes compatPath mutatedJson
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "publication should fail after removing check"

                  match outcome.Failure with
                  | Some(SnapshotStagedValidationFailed failures) ->
                      // Must have check count mismatch
                      let hasCountMismatch =
                          failures
                          |> List.exists (function
                              | StagedSnapshotFailure.CompatibilityRecordMismatch(id, detail) ->
                                  id = "(all)" && detail.Contains("check count")
                              | _ -> false)

                      Expect.isTrue hasCountMismatch "should detect check count mismatch"

                      // Must have missing check report for the exact removed check ID
                      let hasMissingCheck =
                          failures
                          |> List.exists (function
                              | StagedSnapshotFailure.CompatibilityRecordMismatch(id, detail) ->
                                  id = removedCheckId && detail.Contains("missing")
                              | _ -> false)

                      Expect.isTrue hasMissingCheck (sprintf "should detect missing check '%s'" removedCheckId)
                  | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
              finally
                  cleanupDir workDir ]

/// Test: Add unknown check, rehash - requires UnknownCheck
[<Tests>]
let rehashedUnknownCheckTests =
    testList
        "RehashedUnknownCheckMutation"
        [ testCase "rejects rehashed unknown check with UnknownCheck"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  // Add an extra check
                  let extraCheck =
                      { Id = "extra-evidence-999"
                        CommandArgv = [ "echo"; "extra" ]
                        WorkingDirectory = "/tmp"
                        DurationMilliseconds = 10L
                        ExitCode = Some 0
                        Status = Pass
                        StdoutSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                        StderrSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                        FailureKind = None }

                  let mutatedChecks = fixture.CompatibilityProjection.Checks @ [ extraCheck ]

                  let mutatedProjection =
                      { fixture.CompatibilityProjection with
                          Checks = mutatedChecks }
                      |> withSemanticHash

                  let mutatedJson = renderWireJson mutatedProjection

                  let mutation: string -> Result<unit, string> =
                      fun stagingDir ->
                          let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                          writeCanonicalBytes compatPath mutatedJson
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "publication should fail after adding unknown check"

                  match outcome.Failure with
                  | Some(SnapshotStagedValidationFailed failures) ->
                      // Must have unknown check report for the exact extra check ID
                      let hasUnknownCheck =
                          failures
                          |> List.exists (function
                              | StagedSnapshotFailure.CompatibilityRecordMismatch(id, detail) ->
                                  id = "extra-evidence-999" && detail.Contains("unknown")
                              | _ -> false)

                      Expect.isTrue hasUnknownCheck "should detect unknown check 'extra-evidence-999'"
                  | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
              finally
                  cleanupDir workDir ]

/// Test: Duplicate check ID, rehash - requires DuplicateActualCheckId
[<Tests>]
let rehashedDuplicateCheckIdTests =
    testList
        "RehashedDuplicateCheckIdMutation"
        [ testCase "rejects rehashed duplicate check ID with DuplicateActualCheckId"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  // Duplicate the first check ID
                  let originalChecks = fixture.CompatibilityProjection.Checks

                  if originalChecks.IsEmpty then
                      failtest "fixture must contain a check to duplicate"

                  let first = originalChecks.Head
                  let rest = originalChecks.Tail

                  // Create duplicate with same Id
                  let duplicateCheck = { first with Id = first.Id } // Same ID = duplicate

                  let mutatedChecks = first :: duplicateCheck :: rest

                  let mutatedProjection =
                      { fixture.CompatibilityProjection with
                          Checks = mutatedChecks }
                      |> withSemanticHash

                  let mutatedJson = renderWireJson mutatedProjection

                  let mutation: string -> Result<unit, string> =
                      fun stagingDir ->
                          let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                          writeCanonicalBytes compatPath mutatedJson
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "publication should fail after duplicating check ID"

                  match outcome.Failure with
                  | Some(SnapshotStagedValidationFailed failures) ->
                      // Must have DuplicateActualCheckId for the duplicated check
                      let duplicateFailures =
                          failures
                          |> List.choose (function
                              | StagedSnapshotFailure.CompatibilityRecordMismatch(id, detail) when
                                  id = first.Id && detail.Contains("duplicate")
                                  ->
                                  Some detail
                              | _ -> None)

                      Expect.hasLength
                          duplicateFailures
                          1
                          (sprintf "must report DuplicateActualCheckId for check '%s'" first.Id)
                  | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
              finally
                  cleanupDir workDir ]

// -----------------------------------------------------------------------------
// PARSE FAILURE TEST
// -----------------------------------------------------------------------------

/// Test: Invalid JSON is rejected with CompatibilityParseFailed
[<Tests>]
let invalidJsonTests =
    testList
        "InvalidJsonMutation"
        [ testCase "rejects invalid JSON with CompatibilityParseFailed"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  let mutation: string -> Result<unit, string> =
                      fun stagingDir ->
                          let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                          File.WriteAllText(compatPath, "{ this is not json }")
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "publication should fail after JSON corruption"

                  match outcome.Failure with
                  | Some(SnapshotStagedValidationFailed failures) ->
                      let hasParseFailure =
                          failures
                          |> List.exists (function
                              | StagedSnapshotFailure.CompatibilityParseFailed _ -> true
                              | _ -> false)

                      Expect.isTrue hasParseFailure "should detect JSON parse failure"
                  | _ -> failwithf "expected SnapshotStagedValidationFailed, got %A" outcome.Failure
              finally
                  cleanupDir workDir ]

// -----------------------------------------------------------------------------
// SUCCESS AND PRESERVATION TESTS
// -----------------------------------------------------------------------------

/// Test: Valid snapshot succeeds
[<Tests>]
let validSnapshotTests =
    testList
        "ValidSnapshot"
        [ testCase "valid snapshot passes without mutation"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome.Success "valid snapshot should succeed"

                  for f in snapshotFiles do
                      let path = Path.Combine(workDir, f)
                      Expect.isTrue (File.Exists path) (sprintf "%s should exist" f)
              finally
                  cleanupDir workDir ]

/// Test: All four files preserved after rejection
[<Tests>]
let fourFilePreservationTests =
    testList
        "FourFilePreservation"
        [ testCase "all four live snapshot files preserved after rejection"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  // First, publish a valid snapshot
                  let cleanOutcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue cleanOutcome.Success "first publication should succeed"

                  // Capture all four files before mutation
                  let beforeSnapshot = readSnapshot workDir

                  // Now mutate
                  let mutatedProjection =
                      fixture.CompatibilityProjection
                      |> (fun p ->
                          { p with
                              ProviderName = "EVIL-PROVIDER" })
                      |> withSemanticHash

                  let mutatedJson = renderWireJson mutatedProjection

                  let mutation: string -> Result<unit, string> =
                      fun stagingDir ->
                          let compatPath = Path.Combine(stagingDir, "canonical-evidence.json")
                          writeCanonicalBytes compatPath mutatedJson
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some mutation)

                  Expect.isFalse outcome.Success "second publication should fail"
                  Expect.isTrue outcome.PreviousSnapshotPreserved "previous snapshot should be preserved"

                  // Capture all four files after rejection
                  let afterSnapshot = readSnapshot workDir

                  // Verify ALL FOUR files unchanged (byte-identical)
                  let changes = compareSnapshots beforeSnapshot afterSnapshot
                  Expect.isEmpty changes (sprintf "No files should change after rejection: %A" changes)
              finally
                  cleanupDir workDir ]

/// Test: Idempotent overwrite succeeds
[<Tests>]
let idempotentOverwriteTests =
    testList
        "IdempotentOverwrite"
        [ testCase "idempotent overwrite succeeds"
          <| fun () ->
              let workDir = tempDir ()

              try
                  let fixture = PublicationFixture.createValidPublicationFixture ()

                  // First publication
                  let outcome1 =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome1.Success "first publication should succeed"

                  // Capture content
                  let beforeSnapshot = readSnapshot workDir

                  // Second publication with same content
                  let outcome2 =
                      stageAndPublishSnapshot
                          workDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome2.Success "idempotent publication should succeed"

                  // Verify all files unchanged
                  let afterSnapshot = readSnapshot workDir
                  let changes = compareSnapshots beforeSnapshot afterSnapshot
                  Expect.isEmpty changes "content should be unchanged after idempotent overwrite"
              finally
                  cleanupDir workDir ]
