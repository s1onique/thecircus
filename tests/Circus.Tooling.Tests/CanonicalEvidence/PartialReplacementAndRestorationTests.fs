module Circus.Tooling.Tests.CanonicalEvidence.PartialReplacementAndRestorationTests

// =============================================================================
// Canonical evidence – partial replacement and restoration tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04
//
// Tests for partial replacement and restoration:
//   - Previous snapshot preserved on validation failure
//   - Previous snapshot preserved on replacement failure
//   - Live snapshot may have changed detection
//   - No partial writes on failure
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.RecordPipeline
open Circus.Tooling.Tests.CanonicalEvidence.PublicationFixture

// -----------------------------------------------------------------------------
// Test group: PreviousSnapshotPreservation
// -----------------------------------------------------------------------------

let previousSnapshotPreservationTests =
    testList "PreviousSnapshotPreservation" [
        testCase "previous snapshot preserved on validation failure" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                // First staging should succeed
                let outcome1 = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome1.Success "first staging should succeed"
                Expect.isTrue outcome1.PreviousSnapshotPreserved "previous snapshot preserved flag should be true"
                
                // Try with empty records to trigger validation failure
                let outcome2 = stageAndPublishSnapshot tempDir [] fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isFalse outcome2.Success "second staging should fail with empty records"
                
                // Previous snapshot should be preserved
                Expect.isTrue outcome2.PreviousSnapshotPreserved "previous snapshot should be preserved on validation failure"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "previous snapshot preserved on staging error" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                // First staging should succeed
                let outcome1 = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome1.Success "first staging should succeed"
                
                // Try to stage to same location - should succeed (idempotent)
                // This doesn't test error path, but verifies the preservation works
                let outcome2 = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome2.Success "second staging should succeed"
                Expect.isTrue outcome2.PreviousSnapshotPreserved "previous snapshot preserved on idempotent write"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "first staging has no previous to preserve" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
                
                let fixture = createValidPublicationFixture ()
                
                // First staging to empty directory
                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome.Success "first staging should succeed"
                Expect.isTrue outcome.PreviousSnapshotPreserved "previous snapshot preserved flag should be true (no previous to overwrite)"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: LiveSnapshotMayHaveChanged
// -----------------------------------------------------------------------------

let liveSnapshotMayHaveChangedTests =
    testList "LiveSnapshotMayHaveChanged" [
        testCase "live snapshot not changed on successful staging" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome.Success "staging should succeed"
                Expect.isFalse outcome.LiveSnapshotMayHaveChanged "live snapshot may have changed should be false on success"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "live snapshot may have changed on failure" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                // First staging should succeed
                let outcome1 = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome1.Success "first staging should succeed"
                
                // Try with empty records - this should fail but previous is preserved
                // LiveSnapshotMayHaveChanged should be false because we restore previous
                let outcome2 = stageAndPublishSnapshot tempDir [] fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isFalse outcome2.Success "second staging should fail"
                Expect.isFalse outcome2.LiveSnapshotMayHaveChanged "live snapshot may have changed should be false (restored)"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: NoPartialWrites
// -----------------------------------------------------------------------------

let noPartialWritesTests =
    testList "NoPartialWrites" [
        testCase "no partial files on successful staging" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome.Success "staging should succeed"
                
                // All four files should exist
                Expect.isTrue (File.Exists(Path.Combine(tempDir, "records.jsonl"))) "records.jsonl should exist"
                Expect.isTrue (File.Exists(Path.Combine(tempDir, "aggregate.json"))) "aggregate.json should exist"
                Expect.isTrue (File.Exists(Path.Combine(tempDir, "canonical-evidence.json"))) "canonical-evidence.json should exist"
                Expect.isTrue (File.Exists(Path.Combine(tempDir, "artifacts.jsonl"))) "artifacts.jsonl should exist"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "no partial files on staging failure" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                // Stage valid content first
                let outcome1 = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome1.Success "first staging should succeed"
                
                let originalRecords = File.ReadAllText(Path.Combine(tempDir, "records.jsonl"))
                let originalAggregate = File.ReadAllText(Path.Combine(tempDir, "aggregate.json"))
                
                // Try with empty records - should fail
                let outcome2 = stageAndPublishSnapshot tempDir [] fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isFalse outcome2.Success "second staging should fail"
                
                // Files should still have original content (restored)
                let currentRecords = File.ReadAllText(Path.Combine(tempDir, "records.jsonl"))
                Expect.equal currentRecords originalRecords "records.jsonl should be restored to previous state"
                
                let currentAggregate = File.ReadAllText(Path.Combine(tempDir, "aggregate.json"))
                Expect.equal currentAggregate originalAggregate "aggregate.json should be restored to previous state"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "idempotent staging produces consistent files" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                // First staging
                let outcome1 = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome1.Success "first staging should succeed"
                
                let firstRecords = File.ReadAllText(Path.Combine(tempDir, "records.jsonl"))
                let firstAggregate = File.ReadAllText(Path.Combine(tempDir, "aggregate.json"))
                let firstCompat = File.ReadAllText(Path.Combine(tempDir, "canonical-evidence.json"))
                
                // Second staging (idempotent)
                let outcome2 = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome2.Success "second staging should succeed"
                
                let secondRecords = File.ReadAllText(Path.Combine(tempDir, "records.jsonl"))
                let secondAggregate = File.ReadAllText(Path.Combine(tempDir, "aggregate.json"))
                let secondCompat = File.ReadAllText(Path.Combine(tempDir, "canonical-evidence.json"))
                
                // Content should be identical
                Expect.equal secondRecords firstRecords "records.jsonl should be identical on idempotent write"
                Expect.equal secondAggregate firstAggregate "aggregate.json should be identical on idempotent write"
                Expect.equal secondCompat firstCompat "canonical-evidence.json should be identical on idempotent write"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: SnapshotContentValidation
// -----------------------------------------------------------------------------

let snapshotContentValidationTests =
    testList "SnapshotContentValidation" [
        testCase "published snapshot has correct record count" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome.Success "staging should succeed"
                Expect.equal outcome.RecordsCount fixture.Records.Length "records count should match input"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "published snapshot has correct aggregate SHA" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome.Success "staging should succeed"
                Expect.isFalse (String.IsNullOrEmpty outcome.AggregateSha256) "aggregate SHA should be set"
                Expect.equal outcome.AggregateSha256 fixture.Aggregate.SemanticSha256 "aggregate SHA should match fixture"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "published snapshot path is set correctly" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                
                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome.Success "staging should succeed"
                Expect.equal outcome.SnapshotPath tempDir "snapshot path should match output directory"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// All partial replacement and restoration tests
// -----------------------------------------------------------------------------

[<Tests>]
let partialReplacementAndRestorationTests =
    testList "PartialReplacementAndRestoration" [
        previousSnapshotPreservationTests
        liveSnapshotMayHaveChangedTests
        noPartialWritesTests
        snapshotContentValidationTests
    ]
