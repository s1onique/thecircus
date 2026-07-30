module Circus.Tooling.Tests.CanonicalEvidence.StagedAggregateMutationTests

// =============================================================================
// Canonical evidence – staged aggregate mutation integration tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01
//
// Tests that prove stageAndPublishSnapshot correctly:
//   - Parses mutated aggregate bytes from disk
//   - Rejects staged aggregate mutations before replacement
//   - Preserves all four live files on rejection
//
// The validation compares the recomputed aggregate (derived from records.jsonl)
// against the disk aggregate. Any structural mutation should be detected.
//
// For SemanticSha256, the self-integrity check should trigger
// AggregateSemanticHashMismatch, not a structural field mismatch.
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.CanonicalEvidence
open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.CanonicalEvidence.RecordPipeline
open Circus.Tooling.CanonicalEvidence.Validation
open Circus.Tooling.CanonicalEvidence.Serialization
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.Tests.CanonicalEvidence.PublicationFixture

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

/// Check if failures contain a specific AggregateFieldMismatch
let private containsStagedFieldMismatch (failures: StagedSnapshotFailure list) (expected: AggregateDifference) : bool =
    failures |> List.exists (function
        | StagedSnapshotFailure.AggregateFieldMismatch diff ->
            match expected, diff with
            | AggregateDifference.SchemaVersion (e0, a0), AggregateDifference.SchemaVersion (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.SubjectCommitOid (e0, a0), AggregateDifference.SubjectCommitOid (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.SubjectTreeOid (e0, a0), AggregateDifference.SubjectTreeOid (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.RecordsTotal (e0, a0), AggregateDifference.RecordsTotal (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.RecordsPassed (e0, a0), AggregateDifference.RecordsPassed (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.RecordsFailed (e0, a0), AggregateDifference.RecordsFailed (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.RecordsUnavailable (e0, a0), AggregateDifference.RecordsUnavailable (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.TestsTotal (e0, a0), AggregateDifference.TestsTotal (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.TestsPassed (e0, a0), AggregateDifference.TestsPassed (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.TestsIgnored (e0, a0), AggregateDifference.TestsIgnored (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.TestsFailed (e0, a0), AggregateDifference.TestsFailed (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.TestsErrored (e0, a0), AggregateDifference.TestsErrored (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.RequiredChecksTotal (e0, a0), AggregateDifference.RequiredChecksTotal (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.RequiredChecksPassed (e0, a0), AggregateDifference.RequiredChecksPassed (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.RequiredChecksFailed (e0, a0), AggregateDifference.RequiredChecksFailed (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.RecordIds (e0, a0), AggregateDifference.RecordIds (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.OverallStatus (e0, a0), AggregateDifference.OverallStatus (e1, a1) -> e0 = e1 && a0 = a1
            | AggregateDifference.SemanticSha256 (e0, a0), AggregateDifference.SemanticSha256 (e1, a1) -> e0 = e1 && a0 = a1
            | _ -> false
        | _ -> false)

/// Check if failures contain AggregateSemanticHashMismatch with expected values
let private containsSemanticHashMismatch (failures: StagedSnapshotFailure list) (expectedHash: string) (corruptedHash: string) : bool =
    failures |> List.exists (function
        | StagedSnapshotFailure.AggregateSemanticHashMismatch(exp, act) ->
            exp = expectedHash && act = corruptedHash
        | _ -> false)

/// Check that failures do NOT contain any AggregateFieldMismatch with SemanticSha256
let private noSemanticSha256FieldMismatch (failures: StagedSnapshotFailure list) : bool =
    failures |> List.forall (function
        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.SemanticSha256 _) -> false
        | _ -> true)

/// Check that failures do NOT contain any AggregateFieldMismatch at all
let private noAggregateFieldMismatch (failures: StagedSnapshotFailure list) : bool =
    failures |> List.forall (function
        | StagedSnapshotFailure.AggregateFieldMismatch _ -> false
        | _ -> true)

/// Check that failures contain exactly one RecordParseFailure (malformed records)
let private hasRecordParseFailure (failures: StagedSnapshotFailure list) : bool =
    failures |> List.exists (function
        | StagedSnapshotFailure.RecordParseFailed _ -> true
        | _ -> false)

/// Read existing snapshot files (returns Map of filename -> bytes option)
let private readSnapshotFiles (dir: string) : Map<string, byte array option> =
    let files = ["records.jsonl"; "aggregate.json"; "artifacts.jsonl"; "canonical-evidence.json"]
    files |> List.map (fun f ->
        let path = Path.Combine(dir, f)
        let bytes = if File.Exists path then Some(File.ReadAllBytes path) else None
        f, bytes
    ) |> Map.ofList

/// Check that all four files are byte-identical to their original state.
/// This compares exact option values: None stays None, Some bytes stays identical bytes.
let private verifyFilesPreserved (original: Map<string, byte array option>) (current: Map<string, byte array option>) : bool =
    // First verify both maps have the same keys
    let originalKeys = original |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let currentKeys = current |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    if originalKeys <> currentKeys then false
    else
        // Now verify each file's bytes are exactly the same
        original |> Map.forall (fun filename origBytes ->
            match origBytes with
            | None ->
                // Original was absent, current must also be absent
                match Map.tryFind filename current with
                | None -> true
                | Some None -> true
                | Some (Some _) -> false
            | Some orig ->
                // Original had bytes, current must have identical bytes
                match Map.tryFind filename current with
                | Some (Some curr) -> orig = curr
                | _ -> false)

// -----------------------------------------------------------------------------
// Test group: four-file preservation with real initial snapshot
//
// The previous tests captured originalFiles before ANY file was written,
// making the preservation check vacuous. We now publish a valid snapshot
// first, then attempt a mutated publication and verify byte-identical preservation.
// -----------------------------------------------------------------------------

let fourFilePreservationTests =
    testList "FourFilePreservation" [
        testCase "aggregate mutation preserves all four live files byte-identically" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                // Step 1: Publish a valid initial snapshot
                let initialOutcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue initialOutcome.Success "initial snapshot should publish successfully"

                // Step 2: Capture exact bytes of all four live files
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Verify we actually captured bytes (not all None)
                let anyFileExists = liveSnapshotBefore |> Map.exists (fun _ v -> v.IsSome)
                Expect.isTrue anyFileExists "at least one live file should exist after initial publish"

                // Step 3: Create a mutated aggregate
                let mutatedAggregate = { fixture.Aggregate with RecordsTotal = fixture.Aggregate.RecordsTotal + 1 }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                // Step 4: Attempt publication with mutation
                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (Circus.Tooling.FSharpDiagnostics.Hashing.sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (Circus.Tooling.FSharpDiagnostics.Hashing.sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (Circus.Tooling.FSharpDiagnostics.Hashing.sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                // Step 5: Expect rejection
                Expect.isFalse outcome.Success "mutated snapshot should be rejected"

                // Step 6: Verify all four live files are byte-identical to before mutation
                let liveSnapshotAfter = readSnapshotFiles tempDir
                Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                    "all four live files should be byte-identical after rejected publication"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "semantic hash corruption preserves all four live files byte-identically" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                // Step 1: Publish a valid initial snapshot
                let initialOutcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue initialOutcome.Success "initial snapshot should publish successfully"

                // Step 2: Capture exact bytes of all four live files
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Step 3: Attempt publication with corrupted semantic hash
                let corruptedHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                let corruptedAggregate = { fixture.Aggregate with SemanticSha256 = corruptedHash }

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson corruptedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (Circus.Tooling.FSharpDiagnostics.Hashing.sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (Circus.Tooling.FSharpDiagnostics.Hashing.sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (Circus.Tooling.FSharpDiagnostics.Hashing.sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                // Expect rejection
                Expect.isFalse outcome.Success "corrupted snapshot should be rejected"

                // Verify all four live files are byte-identical
                let liveSnapshotAfter = readSnapshotFiles tempDir
                Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                    "all four live files should be byte-identical after rejected publication"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: complete derived-field mutation matrix
//
// Each test proves the record-derived aggregate authority by:
//   1. Mutating a field in the aggregate
//   2. Finalizing (rehashing) the mutated aggregate
//   3. Publishing with mutation
//   4. Expecting exact AggregateFieldMismatch (NOT AggregateSemanticHashMismatch)
//   5. Verifying no previous snapshot bytes changed
//
// The validation recomputes expectedAggregate from the unchanged records,
// parses actualAggregate from the mutated disk file, and compares them.
// Since finalizeAggregate recomputes the hash from the new values, but the
// records remain unchanged, the field mismatch is detected while the semantic
// hash mismatch is avoided.
// -----------------------------------------------------------------------------

let derivedFieldMutationTests =
    testList "DerivedFieldMutation" [
        testCase "records_total mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                // Publish valid initial snapshot
                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: add 5 to RecordsTotal and RecordsPassed to maintain consistency
                let increment = 5
                let mutatedAggregate = {
                    fixture.Aggregate with
                        RecordsTotal = fixture.Aggregate.RecordsTotal + increment
                        RecordsPassed = fixture.Aggregate.RecordsPassed + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordsTotal _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "RecordsTotal field mismatch should be detected. Failures: %A" failures)

                    // Should NOT have AggregateSemanticHashMismatch
                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "RecordsTotal mutation should NOT produce AggregateSemanticHashMismatch"

                    // Verify previous snapshot preserved
                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "records_passed mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment both RecordsTotal and RecordsPassed to maintain consistency
                let increment = 3
                let mutatedAggregate = {
                    fixture.Aggregate with
                        RecordsTotal = fixture.Aggregate.RecordsTotal + increment
                        RecordsPassed = fixture.Aggregate.RecordsPassed + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordsPassed _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "RecordsPassed field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "RecordsPassed mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "records_failed mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment RecordsFailed and RecordsTotal
                let increment = 2
                let mutatedAggregate = {
                    fixture.Aggregate with
                        RecordsTotal = fixture.Aggregate.RecordsTotal + increment
                        RecordsFailed = fixture.Aggregate.RecordsFailed + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordsFailed _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "RecordsFailed field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "RecordsFailed mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "records_unavailable mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment RecordsUnavailable and RecordsTotal
                let increment = 1
                let mutatedAggregate = {
                    fixture.Aggregate with
                        RecordsTotal = fixture.Aggregate.RecordsTotal + increment
                        RecordsUnavailable = fixture.Aggregate.RecordsUnavailable + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordsUnavailable _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "RecordsUnavailable field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "RecordsUnavailable mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "tests_total mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment TestsTotal and TestsPassed
                let increment = 10
                let mutatedAggregate = {
                    fixture.Aggregate with
                        TestsTotal = fixture.Aggregate.TestsTotal + increment
                        TestsPassed = fixture.Aggregate.TestsPassed + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.TestsTotal _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "TestsTotal field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "TestsTotal mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "tests_passed mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment TestsPassed and TestsTotal
                let increment = 5
                let mutatedAggregate = {
                    fixture.Aggregate with
                        TestsTotal = fixture.Aggregate.TestsTotal + increment
                        TestsPassed = fixture.Aggregate.TestsPassed + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.TestsPassed _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "TestsPassed field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "TestsPassed mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "tests_failed mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment TestsFailed and TestsTotal
                let increment = 3
                let mutatedAggregate = {
                    fixture.Aggregate with
                        TestsTotal = fixture.Aggregate.TestsTotal + increment
                        TestsFailed = fixture.Aggregate.TestsFailed + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.TestsFailed _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "TestsFailed field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "TestsFailed mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "tests_ignored mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment TestsIgnored and TestsTotal
                let increment = 2
                let mutatedAggregate = {
                    fixture.Aggregate with
                        TestsTotal = fixture.Aggregate.TestsTotal + increment
                        TestsIgnored = fixture.Aggregate.TestsIgnored + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.TestsIgnored _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "TestsIgnored field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "TestsIgnored mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "tests_errored mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment TestsErrored and TestsTotal
                let increment = 1
                let mutatedAggregate = {
                    fixture.Aggregate with
                        TestsTotal = fixture.Aggregate.TestsTotal + increment
                        TestsErrored = fixture.Aggregate.TestsErrored + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.TestsErrored _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "TestsErrored field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "TestsErrored mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "required_checks_total mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment RequiredChecksTotal and RequiredChecksPassed
                let increment = 1
                let mutatedAggregate = {
                    fixture.Aggregate with
                        RequiredChecksTotal = fixture.Aggregate.RequiredChecksTotal + increment
                        RequiredChecksPassed = fixture.Aggregate.RequiredChecksPassed + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RequiredChecksTotal _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "RequiredChecksTotal field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "RequiredChecksTotal mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "required_checks_passed mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment BOTH RequiredChecksTotal AND RequiredChecksPassed to keep aggregate valid
                let increment = 1
                let mutatedAggregate = {
                    fixture.Aggregate with
                        RequiredChecksTotal = fixture.Aggregate.RequiredChecksTotal + increment
                        RequiredChecksPassed = fixture.Aggregate.RequiredChecksPassed + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RequiredChecksPassed _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "RequiredChecksPassed field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "RequiredChecksPassed mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "required_checks_failed mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: increment RequiredChecksTotal and RequiredChecksFailed to keep aggregate valid
                let increment = 1
                let mutatedAggregate = {
                    fixture.Aggregate with
                        RequiredChecksTotal = fixture.Aggregate.RequiredChecksTotal + increment
                        RequiredChecksFailed = fixture.Aggregate.RequiredChecksFailed + increment
                }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RequiredChecksFailed _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "RequiredChecksFailed field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "RequiredChecksFailed mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "record_ids mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: change the last character of the SECOND record ID from 'e' to 'f'
                // This keeps the list sorted: "eeee...eeee" < "eeee...eeef" < "ffff...ffff"
                // If there's only one record, we need at least 2 to test this mutation
                // We replace the first ID with a new valid one
                let mutatedRecordIds = 
                    match fixture.Aggregate.RecordIds with
                    | [] -> [String.replicate 64 "c"]
                    | [single] -> [String.replicate 64 "a"; String.replicate 64 "c"]
                    | first :: second :: rest ->
                        // Mutate second ID: change last char from 'e' to 'f'
                        let mutatedSecond = second.Substring(0, 63) + "f"
                        first :: mutatedSecond :: rest
                let mutatedAggregate = { fixture.Aggregate with RecordIds = mutatedRecordIds }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordIds _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "RecordIds field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "RecordIds mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "overall_status mutation is detected via field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutate: change overall status from pass to fail (or vice versa)
                let newStatus =
                    match fixture.Aggregate.OverallStatus with
                    | RecordPass -> RecordFail
                    | RecordFail -> RecordPass
                    | RecordUnavailable -> RecordPass
                let mutatedAggregate = { fixture.Aggregate with OverallStatus = newStatus }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)
                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasFieldMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.OverallStatus _) -> true
                        | _ -> false)
                    Expect.isTrue hasFieldMismatch
                        (sprintf "OverallStatus field mismatch should be detected. Failures: %A" failures)

                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "OverallStatus mutation should NOT produce AggregateSemanticHashMismatch"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: staged record divergence (decisive test for record-derived authority)
//
// This test mutates records.jsonl while leaving aggregate.json unchanged.
// The validation recomputes aggregate from the mutated records and detects
// the mismatch with the unchanged aggregate.json. This proves the new
// record-derived aggregate authority.
// -----------------------------------------------------------------------------

let stagedRecordDivergenceTests =
    testList "StagedRecordDivergence" [
        testCase "mutated records.jsonl without aggregate update is rejected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                // Publish valid initial snapshot
                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutation: modify records.jsonl to add extra records without updating aggregate.json
                // This simulates someone adding test results without incrementing the aggregate
                let mutatedRecords = fixture.Records @ [
                    // Add an extra record with different evidence_id
                    { fixture.Records.Head with
                        EvidenceId = String.replicate 64 "f"
                        CheckId = "extra-check"
                        TestsTotal = Some 5
                        TestsPassed = Some 5
                        TestsFailed = Some 0
                        TestsIgnored = Some 0
                        TestsErrored = Some 0
                    }
                ]

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        // Write MUTATED records.jsonl to disk
                        let recordsJsonl = String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson mutatedRecords) + "\n"
                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes recordsJsonl
                        File.WriteAllBytes(Path.Combine(stagingDir, "records.jsonl"), recordsBytes)

                        // Write UNCHANGED aggregate.json (this is the key: records changed but aggregate didn't)
                        let aggJson = EvidenceRecords.renderAggregateWireJson fixture.Aggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        // Write UNCHANGED canonical-evidence.json
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "canonical-evidence.json"), compatBytes)

                        // Update artifacts.jsonl with new records hash but correct aggregate/compat hashes
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                // Expect rejection because:
                // 1. Validation recomputes aggregate from mutated records.jsonl
                // 2. Recomputed aggregate has different RecordsTotal (3 instead of 2)
                // 3. Recomputed aggregate has different TestsTotal, TestsPassed, etc.
                // 4. Recomputed aggregate has different RecordIds
                // 5. These don't match the unchanged aggregate.json
                Expect.isFalse outcome.Success "mutated records without aggregate update should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    // Should have multiple field mismatches
                    let hasRecordsTotalMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordsTotal _) -> true
                        | _ -> false)
                    Expect.isTrue hasRecordsTotalMismatch
                        (sprintf "RecordsTotal mismatch should be detected. Failures: %A" failures)

                    let hasTestsTotalMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.TestsTotal _) -> true
                        | _ -> false)
                    Expect.isTrue hasTestsTotalMismatch
                        (sprintf "TestsTotal mismatch should be detected. Failures: %A" failures)

                    let hasRecordIdsMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordIds _) -> true
                        | _ -> false)
                    Expect.isTrue hasRecordIdsMismatch
                        (sprintf "RecordIds mismatch should be detected. Failures: %A" failures)

                    // Previous snapshot should be preserved
                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "mutated status in records without aggregate update is rejected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                // Publish valid initial snapshot
                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutation: change a record's status from pass to fail WITHOUT updating aggregate
                let mutatedRecords =
                    match fixture.Records with
                    | [] -> []
                    | head :: tail ->
                        // Change first record from pass to fail
                        { head with Result = RecordFail } :: tail

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        // Write MUTATED records.jsonl
                        let recordsJsonl = String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson mutatedRecords) + "\n"
                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes recordsJsonl
                        File.WriteAllBytes(Path.Combine(stagingDir, "records.jsonl"), recordsBytes)

                        // Write UNCHANGED aggregate.json
                        let aggJson = EvidenceRecords.renderAggregateWireJson fixture.Aggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "canonical-evidence.json"), compatBytes)

                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                // Expect rejection because recomputed aggregate from mutated records
                // has different RecordsPassed/RecordsFailed/OverallStatus
                Expect.isFalse outcome.Success "mutated record status without aggregate update should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    // Should detect the status-related mismatches
                    let hasRecordsPassedMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordsPassed _) -> true
                        | _ -> false)
                    let hasRecordsFailedMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordsFailed _) -> true
                        | _ -> false)
                    let hasOverallStatusMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.OverallStatus _) -> true
                        | _ -> false)

                    Expect.isTrue (hasRecordsPassedMismatch || hasRecordsFailedMismatch || hasOverallStatusMismatch)
                        (sprintf "Status-related mismatch should be detected. Failures: %A" failures)

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: malformed records isolation
//
// This proves that an unparseable records.jsonl is treated as a failure,
// NOT as an empty record collection. The aggregate is NOT recomputed from
// an empty records list.
// -----------------------------------------------------------------------------

let malformedRecordIsolationTests =
    testList "MalformedRecordIsolation" [
        testCase "malformed records.jsonl triggers parse failure, not aggregate authority" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                // Publish valid initial snapshot
                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutation: write invalid JSON to records.jsonl
                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        // Write MALFORMED records.jsonl (invalid JSON)
                        let malformedJson = "this is not valid JSON {"
                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes malformedJson
                        File.WriteAllBytes(Path.Combine(stagingDir, "records.jsonl"), recordsBytes)

                        // Write valid aggregate.json and canonical-evidence.json
                        let aggJson = EvidenceRecords.renderAggregateWireJson fixture.Aggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "canonical-evidence.json"), compatBytes)

                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "malformed records should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    // MUST have RecordParseFailure
                    let hasRecordParseFailure = hasRecordParseFailure failures
                    Expect.isTrue hasRecordParseFailure
                        (sprintf "RecordParseFailure should be present. Failures: %A" failures)

                    // Should NOT have AggregateFieldMismatch for records-derived fields
                    // (the aggregate is NOT recomputed from empty records)
                    let hasRecordsTotalMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateFieldMismatch(AggregateDifference.RecordsTotal _) -> true
                        | _ -> false)
                    Expect.isFalse hasRecordsTotalMismatch
                        "Should NOT have RecordsTotal field mismatch (aggregate not recomputed from empty records)"

                    // Should NOT have AggregateSemanticHashMismatch
                    let hasHashMismatch = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateSemanticHashMismatch _ -> true
                        | _ -> false)
                    Expect.isFalse hasHashMismatch
                        "Should NOT have AggregateSemanticHashMismatch"

                    // Previous snapshot should be preserved
                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "records.jsonl with invalid line triggers parse failure" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                // Publish valid initial snapshot
                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Mutation: write valid JSON with one invalid line
                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        // Write records.jsonl with first line valid, second line malformed
                        let validLine = EvidenceRecords.renderEvidenceWireJson fixture.Records.Head
                        let malformedLine = "this line is not JSON"
                        let recordsContent = validLine + "\n" + malformedLine + "\n"
                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes recordsContent
                        File.WriteAllBytes(Path.Combine(stagingDir, "records.jsonl"), recordsBytes)

                        let aggJson = EvidenceRecords.renderAggregateWireJson fixture.Aggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "canonical-evidence.json"), compatBytes)

                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "malformed record line should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let hasRecordParseFailure = hasRecordParseFailure failures
                    Expect.isTrue hasRecordParseFailure
                        (sprintf "RecordParseFailure should be present. Failures: %A" failures)

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: semantic hash self-integrity
// For SemanticSha256, we do NOT recompute the hash. We corrupt it directly
// and require the self-integrity failure, not a structural field mismatch.
// -----------------------------------------------------------------------------

let semanticHashSelfIntegrityTests =
    testList "SemanticHashSelfIntegrity" [
        testCase "corrupted semantic_sha256 triggers self-integrity failure, not field mismatch" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                // Publish valid initial snapshot
                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Corrupt the semantic hash directly (do NOT recompute)
                let corruptedHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                let corruptedAggregate = { fixture.Aggregate with SemanticSha256 = corruptedHash }

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson corruptedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "corrupted snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    // Should have AggregateSemanticHashMismatch, not AggregateFieldMismatch
                    Expect.isTrue (containsSemanticHashMismatch failures fixture.Aggregate.SemanticSha256 corruptedHash)
                        "corrupted semantic hash should trigger AggregateSemanticHashMismatch"

                    // Should NOT have AggregateFieldMismatch for SemanticSha256
                    Expect.isTrue (noSemanticSha256FieldMismatch failures)
                        "corrupted semantic hash should NOT produce AggregateFieldMismatch(SemanticSha256)"

                    // Should have no aggregate field mismatches at all (since other fields are unchanged)
                    Expect.isTrue (noAggregateFieldMismatch failures)
                        "corrupted semantic hash should not produce other aggregate field mismatches"

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | _ -> failwithf "Expected SnapshotStagedValidationFailed"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: subject OID mutations
// -----------------------------------------------------------------------------

let subjectOidMutationTests =
    testList "SubjectOidMutation" [
        testCase "subject_commit_oid mutation is rejected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                let mutatedAggregate = { fixture.Aggregate with SubjectCommitOid = testCommitOid2 }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let expectedDiff = AggregateDifference.SubjectCommitOid(fixture.Aggregate.SubjectCommitOid, testCommitOid2)
                    Expect.isTrue (containsStagedFieldMismatch failures expectedDiff)
                        "SubjectCommitOid field mismatch should be reported"
                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | _ -> failwithf "Expected SnapshotStagedValidationFailed"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "subject_tree_oid mutation is rejected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                let mutatedAggregate = { fixture.Aggregate with SubjectTreeOid = testTreeOid2 }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"
                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    let expectedDiff = AggregateDifference.SubjectTreeOid(fixture.Aggregate.SubjectTreeOid, testTreeOid2)
                    Expect.isTrue (containsStagedFieldMismatch failures expectedDiff)
                        "SubjectTreeOid field mismatch should be reported"
                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved"
                | _ -> failwithf "Expected SnapshotStagedValidationFailed"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: schema version mutation (parser-level rejection)
// -----------------------------------------------------------------------------

let schemaVersionMutationTests =
    testList "SchemaVersionMutation" [
        testCase "schema_version mutation is rejected with parse failure" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                let _ = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                let liveSnapshotBefore = readSnapshotFiles tempDir

                // Create mutated aggregate with invalid schema version
                let mutatedAggregate = { fixture.Aggregate with SchemaVersion = 999 }
                let rehashedAggregate = finalizeAggregate mutatedAggregate

                let mutationFn (stagingDir: string) : Result<unit, string> =
                    try
                        let aggJson = EvidenceRecords.renderAggregateWireJson rehashedAggregate
                        let aggBytes = System.Text.Encoding.UTF8.GetBytes(aggJson + "\n")
                        File.WriteAllBytes(Path.Combine(stagingDir, "aggregate.json"), aggBytes)

                        let recordsBytes = System.Text.Encoding.UTF8.GetBytes(
                            (String.concat "\n" (List.map EvidenceRecords.renderEvidenceWireJson fixture.Records)) + "\n")
                        let compatJson = Serialization.renderWireJson fixture.CompatibilityProjection
                        let compatBytes = System.Text.Encoding.UTF8.GetBytes(compatJson + "\n")

                        let artifactsLine = sprintf """{"path":"records.jsonl","sha256":"%s","byte_length":%d}""" (sha256Hex recordsBytes) recordsBytes.Length
                        let aggArtifactLine = sprintf """{"path":"aggregate.json","sha256":"%s","byte_length":%d}""" (sha256Hex aggBytes) aggBytes.Length
                        let compatArtifactLine = sprintf """{"path":"canonical-evidence.json","sha256":"%s","byte_length":%d}""" (sha256Hex compatBytes) compatBytes.Length
                        let artifactsJson = String.concat "\n" [artifactsLine; aggArtifactLine; compatArtifactLine] + "\n"
                        let artifactsBytes = System.Text.Encoding.UTF8.GetBytes(artifactsJson)
                        File.WriteAllBytes(Path.Combine(stagingDir, "artifacts.jsonl"), artifactsBytes)

                        Ok ()
                    with ex -> Error(sprintf "mutation failed: %s" ex.Message)

                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection (Some mutationFn)

                Expect.isFalse outcome.Success "mutated snapshot should be rejected"

                match outcome.Failure with
                | Some(PublicationFailure.SnapshotStagedValidationFailed failures) ->
                    // Should have aggregate parse failure (schema version 999 is invalid)
                    let hasParseFailure = failures |> List.exists (function
                        | StagedSnapshotFailure.AggregateParseFailed _ -> true
                        | _ -> false)
                    Expect.isTrue hasParseFailure
                        (sprintf "aggregate parse failure should be reported. Failures: %A" failures)

                    let liveSnapshotAfter = readSnapshotFiles tempDir
                    Expect.isTrue (verifyFilesPreserved liveSnapshotBefore liveSnapshotAfter)
                        "all four live files should be preserved after rejection"
                | Some other -> 
                    failwithf "Expected SnapshotStagedValidationFailed, got %A" other
                | None ->
                    failwith "Expected failure but got Success"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// Test group: successful publication (baseline)
// -----------------------------------------------------------------------------

let baselinePublicationTests =
    testList "BaselinePublication" [
        testCase "valid snapshot publishes successfully" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            try
                Directory.CreateDirectory(tempDir) |> ignore

                // Publish without mutation
                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None

                Expect.isTrue outcome.Success "valid snapshot should publish successfully"
                Expect.equal outcome.RecordsCount (List.length fixture.Records) "records count should match"
                Expect.isTrue outcome.PreviousSnapshotPreserved "previous snapshot should be preserved"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// All staged aggregate mutation tests
// -----------------------------------------------------------------------------

[<Tests>]
let stagedAggregateMutationTests =
    testList "StagedAggregateMutation" [
        baselinePublicationTests
        fourFilePreservationTests
        schemaVersionMutationTests
        subjectOidMutationTests
        derivedFieldMutationTests
        stagedRecordDivergenceTests
        malformedRecordIsolationTests
        semanticHashSelfIntegrityTests
    ]
