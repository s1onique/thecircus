module Circus.Tooling.Tests.CanonicalEvidence.CompatibilityStructuralEqualityTests

// =============================================================================
// Canonical evidence – compatibility structural equality tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04
//
// Tests for exact compatibility structural equality:
//   - Pure typed comparison authority covers every top-level and per-check field
//   - Check matching is by exact ID bijection, not list position
//   - Every required compatibility mutation is rejected
//   - Valid staged compatibility document compares exactly equal to provider projection
//
// NOTE: These tests call the PRODUCTION comparator in Validation module.
// All comparison authority is in Validation.compareCompatibilityProjection.
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.Serialization
open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.Tests.CanonicalEvidence.PublicationFixture

// -----------------------------------------------------------------------------
// NOTE: All comparison authority is in Validation module
// Production functions: Validation.compareCompatibilityProjection
//                      Validation.compareCompatibilityCheck
//                      Validation.CompatibilityDifference
//                      Validation.CompatibilityCheckDifference
// -----------------------------------------------------------------------------

// Use qualified calls to production functions
let compareCompatibilityProjection =
    Circus.Tooling.CanonicalEvidence.Validation.compareCompatibilityProjection

let compareCompatibilityCheck =
    Circus.Tooling.CanonicalEvidence.Validation.compareCompatibilityCheck

// -----------------------------------------------------------------------------
// Helper functions for checking difference types (qualified to Validation module)
// -----------------------------------------------------------------------------

let private hasSchemaVersionDiff
    (diffs: Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference list)
    : bool =
    diffs
    |> List.exists (function
        | Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference.SchemaVersion _ -> true
        | _ -> false)

let private hasProviderNameDiff
    (diffs: Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference list)
    : bool =
    diffs
    |> List.exists (function
        | Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference.ProviderName _ -> true
        | _ -> false)

// -----------------------------------------------------------------------------
// Test group: ExactStructuralEquality
// -----------------------------------------------------------------------------

let exactStructuralEqualityTests =
    testList
        "ExactStructuralEquality"
        [ testCase "identical documents produce empty difference list"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let diffs =
                  compareCompatibilityProjection fixture.CompatibilityProjection fixture.CompatibilityProjection

              Expect.isEmpty diffs "identical documents should have no differences"

          testCase "published compatibility equals provider projection exactly"
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

                  // Parse the published compatibility
                  let compatPath = Path.Combine(tempDir, "canonical-evidence.json")
                  let diskContent = File.ReadAllText compatPath

                  match parseWireJson diskContent with
                  | Error e -> failwithf "Failed to parse compatibility: %s" e
                  | Ok parsedCompat ->
                      let diffs =
                          compareCompatibilityProjection fixture.CompatibilityProjection parsedCompat

                      Expect.isEmpty diffs "published compatibility must equal provider projection exactly"
              finally
                  if Directory.Exists tempDir then
                      Directory.Delete(tempDir, true)

          testCase "semantic hash equality does not mask structural difference"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              // Create a mutated document with a valid (but different) semantic hash
              let mutated =
                  { fixture.CompatibilityProjection with
                      ProviderName = "different-name" }

              let withValidHash = withSemanticHash mutated
              // The semantic hash is now valid, but the structural comparison must still detect the difference
              let diffs =
                  compareCompatibilityProjection fixture.CompatibilityProjection withValidHash

              Expect.isNonEmpty diffs "structural difference must be detected even with valid hash"
              Expect.isTrue (hasProviderNameDiff diffs) "ProviderName difference should be reported" ]

// -----------------------------------------------------------------------------
// Test group: TopLevelFieldMutations
// -----------------------------------------------------------------------------

let topLevelFieldMutationsTests =
    testList
        "TopLevelFieldMutations"
        [ testCase "schema_version mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      SchemaVersion = 999 }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "schema_version mutation should be detected"
              Expect.isTrue (hasSchemaVersionDiff diffs) "SchemaVersion difference should be reported"

          testCase "provider_name mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      ProviderName = "wrong-provider" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "provider_name mutation should be detected"
              Expect.isTrue (hasProviderNameDiff diffs) "ProviderName difference should be reported"

          testCase "provider_version mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      ProviderVersion = "99.0.0" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "provider_version mutation should be detected"

          testCase "tested_commit_oid mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      TestedCommitOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "tested_commit_oid mutation should be detected"

          testCase "tested_tree_oid mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      TestedTreeOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "tested_tree_oid mutation should be detected"

          testCase "object_format mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      ObjectFormat = "sha256" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "object_format mutation should be detected"

          testCase "active_scope_act_id mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      ActiveScopeActId = "different-act-id" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "active_scope_act_id mutation should be detected"

          testCase "active_scope_pointer_blob_oid mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      ActiveScopePointerBlobOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "active_scope_pointer_blob_oid mutation should be detected"

          testCase "scope_declaration_path mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      ScopeDeclarationPath = "/different/path" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "scope_declaration_path mutation should be detected"

          testCase "declaration_blob_oid mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      DeclarationBlobOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "declaration_blob_oid mutation should be detected"

          testCase "baseline_commit_oid mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      BaselineCommitOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "baseline_commit_oid mutation should be detected"

          testCase "overall_status mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let newStatus =
                  if fixture.CompatibilityProjection.OverallStatus = Pass then
                      Fail
                  else
                      Pass

              let mutated =
                  { fixture.CompatibilityProjection with
                      OverallStatus = newStatus }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "overall_status mutation should be detected"

          testCase "semantic_sha256 mutation is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      SemanticSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "semantic_sha256 mutation should be detected" ]

// -----------------------------------------------------------------------------
// Test group: CheckCountMutations
// -----------------------------------------------------------------------------

let checkCountMutationsTests =
    testList
        "CheckCountMutations"
        [ testCase "removing one check is detected with exact MissingCheck"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              let removedCheckId = fixture.CompatibilityProjection.Checks.Head.Id

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = List.tail fixture.CompatibilityProjection.Checks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "removing check should be detected"
              // CheckCount is reported when counts differ
              let hasCheckCount =
                  diffs
                  |> List.exists (function
                      | Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference.CheckCount _ -> true
                      | _ -> false)

              Expect.isTrue hasCheckCount "CheckCount difference should be reported"
              // MissingCheck should also be reported (count-independent analysis)
              let hasMissing =
                  diffs
                  |> List.exists (function
                      | Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference.MissingCheck id ->
                          id = removedCheckId
                      | _ -> false)

              Expect.isTrue hasMissing (sprintf "MissingCheck for '%s' should be reported" removedCheckId)

          testCase "adding one check is detected with exact UnknownCheck"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let extraCheck =
                  { Id = "extra-check"
                    CommandArgv = [ "echo"; "extra" ]
                    WorkingDirectory = "/tmp"
                    DurationMilliseconds = 100L
                    ExitCode = Some 0
                    Status = Pass
                    StdoutSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                    StderrSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                    FailureKind = None }

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = extraCheck :: fixture.CompatibilityProjection.Checks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "adding check should be detected"
              // CheckCount is reported when counts differ
              let hasCheckCount =
                  diffs
                  |> List.exists (function
                      | Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference.CheckCount _ -> true
                      | _ -> false)

              Expect.isTrue hasCheckCount "CheckCount difference should be reported"
              // UnknownCheck should also be reported (count-independent analysis)
              let hasUnknown =
                  diffs
                  |> List.exists (function
                      | Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference.UnknownCheck id ->
                          id = "extra-check"
                      | _ -> false)

              Expect.isTrue hasUnknown "UnknownCheck for 'extra-check' should be reported"

          testCase "check count mismatch is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = [] }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "check count mismatch should be detected"

              let hasCheckCount =
                  diffs
                  |> List.exists (function
                      | Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference.CheckCount _ -> true
                      | _ -> false)

              Expect.isTrue hasCheckCount "CheckCount difference should be reported" ]

// -----------------------------------------------------------------------------
// Test group: BijectionEdgeCases
// -----------------------------------------------------------------------------

let bijectionEdgeCasesTests =
    testList
        "BijectionEdgeCases"
        [ testCase "duplicate check ID in expected is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              let originalCheck = fixture.CompatibilityProjection.Checks.Head

              let dupCheck =
                  { originalCheck with
                      Id = originalCheck.Id } // Same ID
              // Create expected with duplicates
              let expectedWithDup =
                  { fixture.CompatibilityProjection with
                      Checks = originalCheck :: dupCheck :: List.tail fixture.CompatibilityProjection.Checks }

              let diffs =
                  compareCompatibilityProjection expectedWithDup fixture.CompatibilityProjection

              Expect.isNonEmpty diffs "duplicate check ID should be detected"

              let hasDuplicate =
                  diffs
                  |> List.exists (function
                      | Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference.DuplicateExpectedCheckId(id,
                                                                                                                     count) ->
                          id = originalCheck.Id && count = 2
                      | _ -> false)

              Expect.isTrue
                  hasDuplicate
                  (sprintf "DuplicateExpectedCheckId for '%s' with count 2 should be reported" originalCheck.Id)

          testCase "duplicate check ID in actual is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              let originalCheck = fixture.CompatibilityProjection.Checks.Head

              let dupCheck =
                  { originalCheck with
                      Id = originalCheck.Id } // Same ID
              // Create actual with duplicates
              let actualWithDup =
                  { fixture.CompatibilityProjection with
                      Checks = dupCheck :: dupCheck :: (List.tail fixture.CompatibilityProjection.Checks) }

              let diffs =
                  compareCompatibilityProjection fixture.CompatibilityProjection actualWithDup

              Expect.isNonEmpty diffs "duplicate check ID should be detected"

              let hasDuplicate =
                  diffs
                  |> List.exists (function
                      | Circus.Tooling.CanonicalEvidence.Validation.CompatibilityDifference.DuplicateActualCheckId(id,
                                                                                                                   count) ->
                          id = originalCheck.Id && count = 2
                      | _ -> false)

              Expect.isTrue
                  hasDuplicate
                  (sprintf "DuplicateActualCheckId for '%s' with count 2 should be reported" originalCheck.Id) ]

// -----------------------------------------------------------------------------
// Test group: PerCheckFieldMutations
// -----------------------------------------------------------------------------

let perCheckFieldMutationsTests =
    testList
        "PerCheckFieldMutations"
        [ testCase "check ID change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutatedChecks =
                  fixture.CompatibilityProjection.Checks
                  |> List.mapi (fun i c -> if i = 0 then { c with Id = "changed-id" } else c)

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = mutatedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "check ID change should be detected"

          testCase "command_argv change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutatedChecks =
                  fixture.CompatibilityProjection.Checks
                  |> List.mapi (fun i c ->
                      if i = 0 then
                          { c with
                              CommandArgv = [ "wrong"; "command" ] }
                      else
                          c)

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = mutatedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "command_argv change should be detected"

          testCase "working_directory change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutatedChecks =
                  fixture.CompatibilityProjection.Checks
                  |> List.mapi (fun i c ->
                      if i = 0 then
                          { c with
                              WorkingDirectory = "/different" }
                      else
                          c)

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = mutatedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "working_directory change should be detected"

          testCase "duration change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutatedChecks =
                  fixture.CompatibilityProjection.Checks
                  |> List.mapi (fun i c -> if i = 0 then { c with DurationMilliseconds = 9999L } else c)

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = mutatedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "duration change should be detected"

          testCase "exit_code change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutatedChecks =
                  fixture.CompatibilityProjection.Checks
                  |> List.mapi (fun i c -> if i = 0 then { c with ExitCode = Some 1 } else c)

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = mutatedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "exit_code change should be detected"

          testCase "status change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutatedChecks =
                  fixture.CompatibilityProjection.Checks
                  |> List.mapi (fun i c ->
                      if i = 0 then
                          { c with
                              Status = if c.Status = Pass then Fail else Pass }
                      else
                          c)

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = mutatedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "status change should be detected"

          testCase "stdout_sha256 change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutatedChecks =
                  fixture.CompatibilityProjection.Checks
                  |> List.mapi (fun i c ->
                      if i = 0 then
                          { c with
                              StdoutSha256 = Some "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
                      else
                          c)

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = mutatedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "stdout_sha256 change should be detected"

          testCase "stderr_sha256 change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutatedChecks =
                  fixture.CompatibilityProjection.Checks
                  |> List.mapi (fun i c ->
                      if i = 0 then
                          { c with
                              StderrSha256 = Some "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
                      else
                          c)

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = mutatedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "stderr_sha256 change should be detected"

          testCase "failure_kind change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutatedChecks =
                  fixture.CompatibilityProjection.Checks
                  |> List.mapi (fun i c ->
                      if i = 0 then
                          { c with
                              FailureKind = Some "different_failure" }
                      else
                          c)

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = mutatedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isNonEmpty diffs "failure_kind change should be detected" ]

// -----------------------------------------------------------------------------
// Test group: BijectionValidation
// -----------------------------------------------------------------------------

let bijectionValidationTests =
    testList
        "BijectionValidation"
        [ testCase "checks matched by exact ID, not position"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              // Swap the order of checks - should still match since they're matched by ID
              let swappedChecks = List.rev fixture.CompatibilityProjection.Checks

              let mutated =
                  { fixture.CompatibilityProjection with
                      Checks = swappedChecks }

              let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
              Expect.isEmpty diffs "checks matched by ID should match regardless of position"

          testCase "ID swap is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              // If matching were by position only, this would match - but with ID matching, it should detect
              if List.length fixture.CompatibilityProjection.Checks >= 2 then
                  let check1 = fixture.CompatibilityProjection.Checks.[0]
                  let check2 = fixture.CompatibilityProjection.Checks.[1]
                  // Swap the IDs but keep them in the same position
                  let swappedChecks =
                      [ { check2 with Id = check1.Id }; { check1 with Id = check2.Id } ]

                  let mutated =
                      { fixture.CompatibilityProjection with
                          Checks = swappedChecks }

                  let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
                  // Should detect that IDs are swapped
                  Expect.isNonEmpty diffs "ID swap should be detected" ]

// -----------------------------------------------------------------------------
// All compatibility structural equality tests
// -----------------------------------------------------------------------------

[<Tests>]
let compatibilityStructuralEqualityTests =
    testList
        "CompatibilityStructuralEquality"
        [ exactStructuralEqualityTests
          topLevelFieldMutationsTests
          checkCountMutationsTests
          bijectionEdgeCasesTests
          perCheckFieldMutationsTests
          bijectionValidationTests ]
