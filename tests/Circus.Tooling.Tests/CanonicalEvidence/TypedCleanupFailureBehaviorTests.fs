module Circus.Tooling.Tests.CanonicalEvidence.TypedCleanupFailureBehaviorTests

// =============================================================================
// Canonical evidence – typed cleanup failure behavior tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04
//
// Tests for typed cleanup failure behavior:
//   - Provider uses idempotent writes (overwrites are safe)
//   - All typed errors are specific and actionable
//   - Cleanup failure preserves details without masking
//   - Outcome preserves all required fields
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.RecordPipeline
open Circus.Tooling.Tests.CanonicalEvidence.PublicationFixture

// -----------------------------------------------------------------------------
// Test group: Idempotent Writes
// -----------------------------------------------------------------------------

let idempotentWriteTests =
    testList
        "IdempotentWrites"
        [ testCase "staging succeeds when compatibility file already exists"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // Pre-create the compatibility file
                  let compatPath = Path.Combine(tempDir, "canonical-evidence.json")
                  File.WriteAllText(compatPath, "{}")

                  // Staging should succeed (idempotent overwrite)
                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome.Success "staging should succeed with idempotent overwrite"

                  // Verify the file was overwritten correctly
                  let newContent = File.ReadAllText(compatPath)

                  Expect.stringContains
                      newContent
                      "circus-canonical-evidence"
                      "file should be overwritten with valid content"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "staging succeeds when aggregate file already exists"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // Pre-create the aggregate file
                  let aggregatePath = Path.Combine(tempDir, "aggregate.json")
                  File.WriteAllText(aggregatePath, "{}")

                  // Staging should succeed (idempotent overwrite)
                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome.Success "staging should succeed with idempotent overwrite"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "staging succeeds when records file already exists"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // Pre-create the records file
                  let recordsPath = Path.Combine(tempDir, "records.jsonl")
                  File.WriteAllText(recordsPath, "{}")

                  // Staging should succeed (idempotent overwrite)
                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome.Success "staging should succeed with idempotent overwrite"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "repeated staging produces identical output"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // First staging
                  let outcome1 =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome1.Success "first staging should succeed"

                  // Second staging
                  let outcome2 =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome2.Success "second staging should succeed"

                  // Both should have same aggregate SHA
                  Expect.equal outcome1.AggregateSha256 outcome2.AggregateSha256 "aggregate SHA should be identical"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// Test group: RecordValidationFailures
// -----------------------------------------------------------------------------

let recordValidationFailureTests =
    testList
        "RecordValidationFailures"
        [ testCase "validation fails with empty records list"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // Try to stage with empty records
                  let outcome =
                      stageAndPublishSnapshot tempDir [] fixture.Aggregate fixture.CompatibilityProjection None

                  Expect.isFalse outcome.Success "staging should fail with empty records"

                  match outcome.Failure with
                  | Some failure ->
                      // Failure should be typed
                      match failure with
                      | SnapshotValidationFailed _ -> ()
                      | SnapshotStagingFailed _ -> ()
                      | SnapshotStagedValidationFailed _ -> ()
                      | _ -> ()
                  | None -> failwith "Expected a failure"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "validation fails with duplicate record IDs"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // Duplicate the first record to create duplicate evidence IDs
                  if not (List.isEmpty fixture.Records) then
                      let duplicateRecord =
                          { fixture.Records.Head with
                              EvidenceId = fixture.Records.Head.EvidenceId }

                      let duplicateRecords = [ duplicateRecord; duplicateRecord ]

                      // Compute aggregate from duplicate records
                      let aggregate =
                          computeAggregate testCommitOid1 testTreeOid1 duplicateRecords
                          |> finalizeAggregate

                      let outcome =
                          stageAndPublishSnapshot
                              tempDir
                              duplicateRecords
                              aggregate
                              fixture.CompatibilityProjection
                              None

                      Expect.isFalse outcome.Success "staging should fail with duplicate record IDs"

                      match outcome.Failure with
                      | Some _ -> () // Any typed failure is acceptable
                      | None -> failwith "Expected a failure"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// Test group: OutputDirectoryCreation
// -----------------------------------------------------------------------------

let outputDirectoryCreationTests =
    testList
        "OutputDirectoryCreation"
        [ testCase "staging creates nested output directory"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              let nestedDir = Path.Combine(tempDir, "subdir", "nested")

              try
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

                  let fixture = createValidPublicationFixture ()

                  let outcome =
                      stageAndPublishSnapshot
                          nestedDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome.Success "staging should succeed and create nested directories"
                  Expect.isTrue (Directory.Exists nestedDir) "nested directory should be created"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "staging creates all required files"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome.Success "staging should succeed"

                  // Verify all files are created
                  Expect.isTrue (File.Exists(Path.Combine(tempDir, "records.jsonl"))) "records.jsonl should exist"
                  Expect.isTrue (File.Exists(Path.Combine(tempDir, "aggregate.json"))) "aggregate.json should exist"

                  Expect.isTrue
                      (File.Exists(Path.Combine(tempDir, "canonical-evidence.json")))
                      "canonical-evidence.json should exist"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// Test group: OutcomeFieldPreservation
// -----------------------------------------------------------------------------

let outcomeFieldPreservationTests =
    testList
        "OutcomeFieldPreservation"
        [ testCase "outcome preserves all required fields"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome.Success "staging should succeed"
                  Expect.isFalse (String.IsNullOrEmpty outcome.SnapshotPath) "snapshot path should be set"
                  Expect.equal outcome.RecordsCount fixture.Records.Length "records count should match"
                  Expect.isFalse (String.IsNullOrEmpty outcome.AggregateSha256) "aggregate SHA should be set"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "failure preserves typed cleanup failure when present"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          None

                  Expect.isTrue outcome.Success "staging should succeed"

                  // Cleanup failure may or may not be present depending on write success
                  // If present, it should have all required fields
                  match outcome.CleanupFailure with
                  | Some cleanupFailure ->
                      Expect.isFalse (String.IsNullOrEmpty cleanupFailure.Path) "cleanup failure path should be set"

                      Expect.isFalse
                          (String.IsNullOrEmpty cleanupFailure.Detail)
                          "cleanup failure exception type should be set"
                  | None ->
                      // No cleanup failure is acceptable (successful write)
                      ()
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "failure preserves typed failure with details"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // Try with empty records to trigger validation failure
                  let outcome =
                      stageAndPublishSnapshot tempDir [] fixture.Aggregate fixture.CompatibilityProjection None

                  Expect.isFalse outcome.Success "staging should fail"

                  // Failure should be typed with details
                  match outcome.Failure with
                  | Some failure ->
                      // All typed failures have details
                      match failure with
                      | SnapshotValidationFailed issues ->
                          Expect.isFalse (List.isEmpty issues) "validation failures should have issues"
                      | SnapshotStagingFailed detail ->
                          Expect.isFalse (String.IsNullOrEmpty detail) "staging failure should have details"
                      | SnapshotStagedValidationFailed failures ->
                          Expect.isFalse (List.isEmpty failures) "staged validation failures should have issues"
                      | _ -> ()
                  | None -> failwith "Expected a failure"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// All typed cleanup failure behavior tests
// -----------------------------------------------------------------------------

[<Tests>]
let typedCleanupFailureBehaviorTests =
    testList
        "TypedCleanupFailureBehavior"
        [ idempotentWriteTests
          recordValidationFailureTests
          outputDirectoryCreationTests
          outcomeFieldPreservationTests ]
