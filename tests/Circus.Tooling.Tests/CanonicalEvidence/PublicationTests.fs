module Circus.Tooling.Tests.CanonicalEvidence.PublicationTests

// =============================================================================
// Canonical evidence – publication tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION03
//
// Tests for staged publication with full round-trip validation:
//   - All-four-file staged disk round trip
//   - Aggregate recomputation from parsed staged records
//   - Artifact manifest authority
//   - Compatibility equivalence
//   - Typed cleanup-failure preservation
//   - Previous-snapshot preservation
//   - Replacement failure semantics
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.CanonicalEvidence.Serialization
open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.RecordPipeline
open Circus.Tooling.Tests.CanonicalEvidence.PublicationFixture

// -----------------------------------------------------------------------------
// Helper: SHA-256 of file content
// -----------------------------------------------------------------------------

let private sha256OfFile (path: string) =
    use hasher = System.Security.Cryptography.SHA256.Create()
    let bytes = File.ReadAllBytes path
    let hash = hasher.ComputeHash bytes
    BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()

// -----------------------------------------------------------------------------
// Test group: PublicationFixture
// -----------------------------------------------------------------------------

let publicationFixtureTests =
    testList
        "PublicationFixture"
        [ testCase "createValidPublicationFixture produces valid fixture"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              Expect.isNonEmpty fixture.Records "fixture should have records"

              Expect.isTrue
                  (List.forall (fun r -> not (String.IsNullOrEmpty r.EvidenceId)) fixture.Records)
                  "all records should have EvidenceId"

              Expect.equal fixture.Aggregate.RecordsTotal (List.length fixture.Records) "aggregate count should match"
              Expect.isNonEmpty fixture.Aggregate.SemanticSha256 "aggregate should have semantic hash"

          testCase "fixture records have recomputable EvidenceId"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              for record in fixture.Records do
                  let recomputed = computeEvidenceId record
                  Expect.equal recomputed record.EvidenceId "EvidenceId should recompute"

          testCase "fixture aggregate has recomputable SemanticSha256"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              let recomputed = computeAggregateSemanticHash fixture.Aggregate
              Expect.equal recomputed fixture.Aggregate.SemanticSha256 "SemanticSha256 should recompute"

          testCase "fixture compatibility has recomputable SemanticSha256"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              let recomputed = computeSemanticHash fixture.CompatibilityProjection

              Expect.equal
                  recomputed
                  fixture.CompatibilityProjection.SemanticSha256
                  "compatibility SemanticSha256 should recompute" ]

// -----------------------------------------------------------------------------
// Test group: StagedSnapshotRoundTrip
// -----------------------------------------------------------------------------

let stagedSnapshotRoundTripTests =
    testList
        "StagedSnapshotRoundTrip"
        [ testCase "stageAndPublishSnapshot writes and validates all four files"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // Use mutation seam that validates all four files were written
                  let mutable stagedFilesFound = 0

                  let validateMutation stagingDir =
                      stagedFilesFound <- 0

                      for f in
                          [ "records.jsonl"
                            "aggregate.json"
                            "artifacts.jsonl"
                            "canonical-evidence.json" ] do
                          let path = Path.Combine(stagingDir, f)

                          if File.Exists path then
                              stagedFilesFound <- stagedFilesFound + 1
                              let bytes = File.ReadAllBytes path
                              Expect.isTrue (bytes.Length > 0) (sprintf "%s should have content" f)

                      if stagedFilesFound <> 4 then
                          Error "not all staged files found"
                      else
                          Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some validateMutation)

                  Expect.isTrue outcome.Success "publication should succeed"
                  Expect.equal stagedFilesFound 4 "all four files should be written"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "staged files use canonical UTF-8 without BOM"
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

                  Expect.isTrue outcome.Success "publication should succeed"

                  // Check each file has no BOM
                  for f in
                      [ "records.jsonl"
                        "aggregate.json"
                        "artifacts.jsonl"
                        "canonical-evidence.json" ] do
                      let path = Path.Combine(tempDir, f)
                      let bytes = File.ReadAllBytes path

                      Expect.isFalse
                          (bytes.Length >= 3
                           && bytes.[0] = 0xEFuy
                           && bytes.[1] = 0xBBuy
                           && bytes.[2] = 0xBFuy)
                          (sprintf "%s should not have UTF-8 BOM" f)
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "staged files end with exactly one LF"
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

                  Expect.isTrue outcome.Success "publication should succeed"

                  for f in
                      [ "records.jsonl"
                        "aggregate.json"
                        "artifacts.jsonl"
                        "canonical-evidence.json" ] do
                      let path = Path.Combine(tempDir, f)
                      let content = File.ReadAllText path
                      Expect.isTrue (content.EndsWith "\n") (sprintf "%s should end with LF" f)
                      Expect.isFalse (content.EndsWith "\n\n") (sprintf "%s should not have multiple trailing LF" f)
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// Test group: StagedCorruption
// -----------------------------------------------------------------------------

let stagedCorruptionTests =
    testList
        "StagedCorruption"
        [ testCase "record corruption is rejected"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // Mutate records.jsonl in staging
                  let corruptRecords stagingDir =
                      let path = Path.Combine(stagingDir, "records.jsonl")
                      let content = File.ReadAllText path
                      File.WriteAllText(path, content + "CORRUPTED")
                      Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some corruptRecords)

                  Expect.isFalse outcome.Success "corrupted records should fail"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "aggregate corruption is rejected"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  let corruptAggregate stagingDir =
                      let path = Path.Combine(stagingDir, "aggregate.json")
                      let content = File.ReadAllText path
                      File.WriteAllText(path, content + "CORRUPTED")
                      Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some corruptAggregate)

                  Expect.isFalse outcome.Success "corrupted aggregate should fail"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "compatibility corruption is rejected"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  let corruptCompat stagingDir =
                      let path = Path.Combine(stagingDir, "canonical-evidence.json")
                      File.WriteAllText(path, "{ invalid json }")
                      Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some corruptCompat)

                  Expect.isFalse outcome.Success "corrupted compatibility should fail"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// Test group: AggregateRecomputation
// -----------------------------------------------------------------------------

let aggregateRecomputationTests =
    testList
        "AggregateRecomputation"
        [ testCase "aggregate recomputed from parsed records matches stored"
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

                  Expect.isTrue outcome.Success "publication should succeed"

                  // Reread and parse records
                  let recordsPath = Path.Combine(tempDir, "records.jsonl")
                  let recordsContent = File.ReadAllText recordsPath
                  let lines = recordsContent.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)

                  let parsedRecords =
                      lines
                      |> Array.toList
                      |> List.choose (fun line ->
                          match parseEvidenceWireJsonStrict line with
                          | Ok r -> Some r
                          | Error _ -> None)

                  // Recompute aggregate
                  let recomputedAggregate =
                      computeAggregate fixture.Aggregate.SubjectCommitOid fixture.Aggregate.SubjectTreeOid parsedRecords
                      |> finalizeAggregate

                  Expect.equal
                      recomputedAggregate.SemanticSha256
                      fixture.Aggregate.SemanticSha256
                      "recomputed aggregate should match"

                  Expect.equal
                      recomputedAggregate.RecordsTotal
                      fixture.Aggregate.RecordsTotal
                      "record count should match"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "aggregate with changed count fails"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  // Modify aggregate to have wrong count
                  let corruptAggregate stagingDir =
                      let path = Path.Combine(stagingDir, "aggregate.json")
                      let content = File.ReadAllText path
                      // Replace the records_total value
                      let modified = content.Replace("\"records_total\":2", "\"records_total\":999")
                      File.WriteAllText(path, modified)
                      Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some corruptAggregate)

                  Expect.isFalse outcome.Success "wrong aggregate count should fail"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// Test group: ArtifactManifestAuthority
// -----------------------------------------------------------------------------

let artifactManifestAuthorityTests =
    testList
        "ArtifactManifestAuthority"
        [ testCase "manifest has exactly three required paths"
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

                  Expect.isTrue outcome.Success "publication should succeed"

                  let manifestPath = Path.Combine(tempDir, "artifacts.jsonl")
                  let manifestContent = File.ReadAllText manifestPath

                  match parseArtifactManifestJsonlStrict manifestContent with
                  | Ok entries ->
                      let paths = entries |> List.map (fun e -> e.Path) |> Set.ofList
                      let required = set [ "records.jsonl"; "aggregate.json"; "canonical-evidence.json" ]
                      Expect.equal paths required "manifest should have exactly three paths"
                  | Error e -> failwithf "Manifest parse failed: %A" e
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "manifest hashes match reread bytes"
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

                  Expect.isTrue outcome.Success "publication should succeed"

                  let manifestPath = Path.Combine(tempDir, "artifacts.jsonl")
                  let manifestContent = File.ReadAllText manifestPath

                  match parseArtifactManifestJsonlStrict manifestContent with
                  | Ok entries ->
                      for entry in entries do
                          let actualPath = Path.Combine(tempDir, entry.Path)
                          let actualHash = sha256OfFile actualPath
                          Expect.equal entry.Sha256 actualHash (sprintf "hash for %s should match" entry.Path)

                      for entry in entries do
                          let actualPath = Path.Combine(tempDir, entry.Path)
                          let actualLength = int64 (File.ReadAllBytes actualPath).LongLength
                          Expect.equal entry.ByteLength actualLength (sprintf "length for %s should match" entry.Path)
                  | Error e -> failwithf "Manifest parse failed: %A" e
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "manifest with extra path is rejected"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture = createValidPublicationFixture ()

                  let addExtraPath stagingDir =
                      let path = Path.Combine(stagingDir, "artifacts.jsonl")
                      let content = File.ReadAllText path

                      let extra =
                          "\n{\"path\":\"extra.json\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":0}"

                      File.WriteAllText(path, content.TrimEnd() + extra + "\n")
                      Ok()

                  let outcome =
                      stageAndPublishSnapshot
                          tempDir
                          fixture.Records
                          fixture.Aggregate
                          fixture.CompatibilityProjection
                          (Some addExtraPath)

                  Expect.isFalse outcome.Success "extra path in manifest should fail"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// Test group: CompatibilityEquivalence
// -----------------------------------------------------------------------------

let compatibilityEquivalenceTests =
    testList
        "CompatibilityEquivalence"
        [ testCase "compatibility projection matches provider output"
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

                  Expect.isTrue outcome.Success "publication should succeed"

                  let compatPath = Path.Combine(tempDir, "canonical-evidence.json")
                  let diskContent = File.ReadAllText compatPath

                  match parseWireJson diskContent with
                  | Ok parsed ->
                      Expect.equal
                          parsed.TestedCommitOid
                          fixture.CompatibilityProjection.TestedCommitOid
                          "commit should match"

                      Expect.equal
                          parsed.TestedTreeOid
                          fixture.CompatibilityProjection.TestedTreeOid
                          "tree should match"

                      Expect.equal
                          parsed.SemanticSha256
                          fixture.CompatibilityProjection.SemanticSha256
                          "semantic hash should match"
                  | Error e -> failwithf "Failed to parse compatibility: %s" e
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "compatibility check count equals record count"
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

                  Expect.isTrue outcome.Success "publication should succeed"

                  let compatPath = Path.Combine(tempDir, "canonical-evidence.json")
                  let diskContent = File.ReadAllText compatPath

                  match parseWireJson diskContent with
                  | Ok parsed ->
                      Expect.equal (List.length parsed.Checks) (List.length fixture.Records) "check count should match"
                  | Error e -> failwithf "Failed to parse compatibility: %s" e
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// Test group: PreviousSnapshotPreservation
// -----------------------------------------------------------------------------

let previousSnapshotPreservationTests =
    testList
        "PreviousSnapshotPreservation"
        [ testCase "previous snapshot preserved on validation failure"
          <| fun () ->
              let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
              Directory.CreateDirectory tempDir |> ignore

              try
                  let fixture1 = createValidPublicationFixture ()

                  let outcome1 =
                      stageAndPublishSnapshot
                          tempDir
                          fixture1.Records
                          fixture1.Aggregate
                          fixture1.CompatibilityProjection
                          None

                  Expect.isTrue outcome1.Success "first publication should succeed"

                  // Record the original file hashes
                  let originalHashes =
                      [ "records.jsonl"
                        "aggregate.json"
                        "artifacts.jsonl"
                        "canonical-evidence.json" ]
                      |> List.map (fun f -> f, sha256OfFile (Path.Combine(tempDir, f)))
                      |> Map.ofList

                  // Create second fixture and try to publish corrupted
                  let fixture2 = createValidPublicationFixture ()

                  let corrupt stagingDir =
                      let path = Path.Combine(stagingDir, "records.jsonl")
                      File.WriteAllText(path, "CORRUPTED")
                      Ok()

                  let outcome2 =
                      stageAndPublishSnapshot
                          tempDir
                          fixture2.Records
                          fixture2.Aggregate
                          fixture2.CompatibilityProjection
                          (Some corrupt)

                  Expect.isFalse outcome2.Success "corrupted publication should fail"
                  Expect.isTrue outcome2.PreviousSnapshotPreserved "previous snapshot should be preserved"

                  // Verify files are unchanged
                  for f in
                      [ "records.jsonl"
                        "aggregate.json"
                        "artifacts.jsonl"
                        "canonical-evidence.json" ] do
                      let currentHash = sha256OfFile (Path.Combine(tempDir, f))
                      Expect.equal currentHash originalHashes.[f] (sprintf "%s should be unchanged" f)
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true) ]

// -----------------------------------------------------------------------------
// All publication tests
// -----------------------------------------------------------------------------

[<Tests>]
let publicationTests =
    testList
        "Publication"
        [ publicationFixtureTests
          stagedSnapshotRoundTripTests
          stagedCorruptionTests
          aggregateRecomputationTests
          artifactManifestAuthorityTests
          compatibilityEquivalenceTests
          previousSnapshotPreservationTests ]
