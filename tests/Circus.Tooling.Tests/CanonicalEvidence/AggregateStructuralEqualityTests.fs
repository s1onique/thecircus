module Circus.Tooling.Tests.CanonicalEvidence.AggregateStructuralEqualityTests

// =============================================================================
// Canonical evidence – aggregate structural equality tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04
//
// Tests for exact aggregate structural equality using production comparator:
//   - Pure typed comparison authority covers every aggregate field
//   - RecordIds comparison is exact and order-sensitive
//   - Every aggregate mutation is rejected with exact typed differences
//   - Published aggregate equals provider projection exactly
//
// The production comparator is imported from Validation.compareAggregate to ensure
// test and production code share the same comparison authority.
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.RecordPipeline
open Circus.Tooling.CanonicalEvidence.Validation
open Circus.Tooling.Tests.CanonicalEvidence.PublicationFixture

// -----------------------------------------------------------------------------
// Helper: check for specific AggregateDifference case
// -----------------------------------------------------------------------------

let private containsDiff (diffs: AggregateDifference list) (expected: AggregateDifference) : bool =
    diffs
    |> List.exists (fun d ->
        match expected, d with
        | AggregateDifference.SchemaVersion(e0, a0), AggregateDifference.SchemaVersion(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.SubjectCommitOid(e0, a0), AggregateDifference.SubjectCommitOid(e1, a1) ->
            e0 = e1 && a0 = a1
        | AggregateDifference.SubjectTreeOid(e0, a0), AggregateDifference.SubjectTreeOid(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.RecordsTotal(e0, a0), AggregateDifference.RecordsTotal(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.RecordsPassed(e0, a0), AggregateDifference.RecordsPassed(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.RecordsFailed(e0, a0), AggregateDifference.RecordsFailed(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.RecordsUnavailable(e0, a0), AggregateDifference.RecordsUnavailable(e1, a1) ->
            e0 = e1 && a0 = a1
        | AggregateDifference.TestsTotal(e0, a0), AggregateDifference.TestsTotal(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.TestsPassed(e0, a0), AggregateDifference.TestsPassed(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.TestsIgnored(e0, a0), AggregateDifference.TestsIgnored(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.TestsFailed(e0, a0), AggregateDifference.TestsFailed(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.TestsErrored(e0, a0), AggregateDifference.TestsErrored(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.RequiredChecksTotal(e0, a0), AggregateDifference.RequiredChecksTotal(e1, a1) ->
            e0 = e1 && a0 = a1
        | AggregateDifference.RequiredChecksPassed(e0, a0), AggregateDifference.RequiredChecksPassed(e1, a1) ->
            e0 = e1 && a0 = a1
        | AggregateDifference.RequiredChecksFailed(e0, a0), AggregateDifference.RequiredChecksFailed(e1, a1) ->
            e0 = e1 && a0 = a1
        | AggregateDifference.RecordIds(e0, a0), AggregateDifference.RecordIds(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.OverallStatus(e0, a0), AggregateDifference.OverallStatus(e1, a1) -> e0 = e1 && a0 = a1
        | AggregateDifference.SemanticSha256(e0, a0), AggregateDifference.SemanticSha256(e1, a1) -> e0 = e1 && a0 = a1
        | _ -> false)

// -----------------------------------------------------------------------------
// Test group: ExactStructuralEquality
// -----------------------------------------------------------------------------

let exactStructuralEqualityTests =
    testList
        "ExactStructuralEquality"
        [ testCase "identical aggregates produce empty difference list"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              // Use production comparator from Validation module
              let diffs = compareAggregate fixture.Aggregate fixture.Aggregate
              Expect.isEmpty diffs "identical aggregates should have no differences"

          testCase "semantic hash equality does not mask structural difference"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              // Create a mutated aggregate with a valid (but different) semantic hash
              let mutated =
                  { fixture.Aggregate with
                      SubjectCommitOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let withValidHash = finalizeAggregate mutated
              // The semantic hash is now valid, but the structural comparison must still detect the difference
              let diffs = compareAggregate fixture.Aggregate withValidHash
              Expect.isNonEmpty diffs "structural difference must be detected even with valid hash"

              let expected =
                  AggregateDifference.SubjectCommitOid(
                      fixture.Aggregate.SubjectCommitOid,
                      withValidHash.SubjectCommitOid
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "SubjectCommitOid difference should be reported with exact values" ]

// -----------------------------------------------------------------------------
// Test group: FieldMutations
// -----------------------------------------------------------------------------

let fieldMutationsTests =
    testList
        "FieldMutations"
        [ testCase "schema_version mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      SchemaVersion = 999 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "schema_version mutation should be detected"

              let expected =
                  AggregateDifference.SchemaVersion(fixture.Aggregate.SchemaVersion, 999)

              Expect.isTrue
                  (containsDiff diffs expected)
                  "SchemaVersion difference should be reported with exact values"

          testCase "subject_commit_oid mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      SubjectCommitOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "subject_commit_oid mutation should be detected"

              let expected =
                  AggregateDifference.SubjectCommitOid(
                      fixture.Aggregate.SubjectCommitOid,
                      "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "SubjectCommitOid difference should be reported with exact values"

          testCase "subject_tree_oid mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      SubjectTreeOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "subject_tree_oid mutation should be detected"

              let expected =
                  AggregateDifference.SubjectTreeOid(
                      fixture.Aggregate.SubjectTreeOid,
                      "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "SubjectTreeOid difference should be reported with exact values"

          testCase "records_total mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      RecordsTotal = fixture.Aggregate.RecordsTotal + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "records_total mutation should be detected"

              let expected =
                  AggregateDifference.RecordsTotal(fixture.Aggregate.RecordsTotal, fixture.Aggregate.RecordsTotal + 1)

              Expect.isTrue (containsDiff diffs expected) "RecordsTotal difference should be reported with exact values"

          testCase "records_passed mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      RecordsPassed = fixture.Aggregate.RecordsPassed + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "records_passed mutation should be detected"

              let expected =
                  AggregateDifference.RecordsPassed(
                      fixture.Aggregate.RecordsPassed,
                      fixture.Aggregate.RecordsPassed + 1
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "RecordsPassed difference should be reported with exact values"

          testCase "records_failed mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      RecordsFailed = fixture.Aggregate.RecordsFailed + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "records_failed mutation should be detected"

              let expected =
                  AggregateDifference.RecordsFailed(
                      fixture.Aggregate.RecordsFailed,
                      fixture.Aggregate.RecordsFailed + 1
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "RecordsFailed difference should be reported with exact values"

          testCase "records_unavailable mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      RecordsUnavailable = fixture.Aggregate.RecordsUnavailable + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "records_unavailable mutation should be detected"

              let expected =
                  AggregateDifference.RecordsUnavailable(
                      fixture.Aggregate.RecordsUnavailable,
                      fixture.Aggregate.RecordsUnavailable + 1
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "RecordsUnavailable difference should be reported with exact values"

          testCase "tests_total mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      TestsTotal = fixture.Aggregate.TestsTotal + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "tests_total mutation should be detected"

              let expected =
                  AggregateDifference.TestsTotal(fixture.Aggregate.TestsTotal, fixture.Aggregate.TestsTotal + 1)

              Expect.isTrue (containsDiff diffs expected) "TestsTotal difference should be reported with exact values"

          testCase "tests_passed mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      TestsPassed = fixture.Aggregate.TestsPassed + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "tests_passed mutation should be detected"

              let expected =
                  AggregateDifference.TestsPassed(fixture.Aggregate.TestsPassed, fixture.Aggregate.TestsPassed + 1)

              Expect.isTrue (containsDiff diffs expected) "TestsPassed difference should be reported with exact values"

          testCase "tests_ignored mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      TestsIgnored = fixture.Aggregate.TestsIgnored + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "tests_ignored mutation should be detected"

              let expected =
                  AggregateDifference.TestsIgnored(fixture.Aggregate.TestsIgnored, fixture.Aggregate.TestsIgnored + 1)

              Expect.isTrue (containsDiff diffs expected) "TestsIgnored difference should be reported with exact values"

          testCase "tests_failed mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      TestsFailed = fixture.Aggregate.TestsFailed + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "tests_failed mutation should be detected"

              let expected =
                  AggregateDifference.TestsFailed(fixture.Aggregate.TestsFailed, fixture.Aggregate.TestsFailed + 1)

              Expect.isTrue (containsDiff diffs expected) "TestsFailed difference should be reported with exact values"

          testCase "tests_errored mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      TestsErrored = fixture.Aggregate.TestsErrored + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "tests_errored mutation should be detected"

              let expected =
                  AggregateDifference.TestsErrored(fixture.Aggregate.TestsErrored, fixture.Aggregate.TestsErrored + 1)

              Expect.isTrue (containsDiff diffs expected) "TestsErrored difference should be reported with exact values"

          testCase "required_checks_total mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      RequiredChecksTotal = fixture.Aggregate.RequiredChecksTotal + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "required_checks_total mutation should be detected"

              let expected =
                  AggregateDifference.RequiredChecksTotal(
                      fixture.Aggregate.RequiredChecksTotal,
                      fixture.Aggregate.RequiredChecksTotal + 1
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "RequiredChecksTotal difference should be reported with exact values"

          testCase "required_checks_passed mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      RequiredChecksPassed = fixture.Aggregate.RequiredChecksPassed + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "required_checks_passed mutation should be detected"

              let expected =
                  AggregateDifference.RequiredChecksPassed(
                      fixture.Aggregate.RequiredChecksPassed,
                      fixture.Aggregate.RequiredChecksPassed + 1
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "RequiredChecksPassed difference should be reported with exact values"

          testCase "required_checks_failed mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      RequiredChecksFailed = fixture.Aggregate.RequiredChecksFailed + 1 }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "required_checks_failed mutation should be detected"

              let expected =
                  AggregateDifference.RequiredChecksFailed(
                      fixture.Aggregate.RequiredChecksFailed,
                      fixture.Aggregate.RequiredChecksFailed + 1
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "RequiredChecksFailed difference should be reported with exact values"

          testCase "overall_status mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let newStatus =
                  if fixture.Aggregate.OverallStatus = RecordPass then
                      RecordFail
                  else
                      RecordPass

              let mutated =
                  { fixture.Aggregate with
                      OverallStatus = newStatus }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "overall_status mutation should be detected"

              let expected =
                  AggregateDifference.OverallStatus(fixture.Aggregate.OverallStatus, newStatus)

              Expect.isTrue
                  (containsDiff diffs expected)
                  "OverallStatus difference should be reported with exact values"

          testCase "semantic_sha256 mutation is detected with exact values"
          <| fun () ->
              let fixture = createValidPublicationFixture ()

              let mutated =
                  { fixture.Aggregate with
                      SemanticSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "semantic_sha256 mutation should be detected"

              let expected =
                  AggregateDifference.SemanticSha256(
                      fixture.Aggregate.SemanticSha256,
                      "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                  )

              Expect.isTrue
                  (containsDiff diffs expected)
                  "SemanticSha256 difference should be reported with exact values" ]

// -----------------------------------------------------------------------------
// Test group: RecordIdsComparison
// -----------------------------------------------------------------------------

let recordIdsComparisonTests =
    testList
        "RecordIdsComparison"
        [ testCase "record_ids order change is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              // Reverse the RecordIds - comparison is order-sensitive
              let reversedRecordIds = List.rev fixture.Aggregate.RecordIds

              let mutated =
                  { fixture.Aggregate with
                      RecordIds = reversedRecordIds }

              let diffs = compareAggregate fixture.Aggregate mutated
              // Lists are compared element-by-element, so order matters
              Expect.isNonEmpty diffs "record_ids order change should be detected"

              let expected =
                  AggregateDifference.RecordIds(fixture.Aggregate.RecordIds, reversedRecordIds)

              Expect.isTrue (containsDiff diffs expected) "RecordIds difference should be reported with exact values"

          testCase "record_ids different content is detected"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              // Replace first ID with something different
              let differentIds =
                  match fixture.Aggregate.RecordIds with
                  | [] -> []
                  | _ :: rest -> "different_id_0000000000000000000000000000000000000000000000000000" :: rest

              let mutated =
                  { fixture.Aggregate with
                      RecordIds = differentIds }

              let diffs = compareAggregate fixture.Aggregate mutated
              Expect.isNonEmpty diffs "record_ids different content should be detected"

              let expected =
                  AggregateDifference.RecordIds(fixture.Aggregate.RecordIds, differentIds)

              Expect.isTrue (containsDiff diffs expected) "RecordIds difference should be reported with exact values" ]

// -----------------------------------------------------------------------------
// Test group: ComputedFieldsConsistency
// -----------------------------------------------------------------------------

let computedFieldsConsistencyTests =
    testList
        "ComputedFieldsConsistency"
        [ testCase "aggregate semantic hash recomputes correctly"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              let recomputedHash = computeAggregateSemanticHash fixture.Aggregate

              Expect.equal
                  recomputedHash
                  fixture.Aggregate.SemanticSha256
                  "aggregate semantic hash should recompute to same value"

          testCase "aggregate structural comparison detects count changes"
          <| fun () ->
              let fixture = createValidPublicationFixture ()
              // Directly mutate the counts to verify the aggregate structural comparison detects changes
              let mutatedAggregate =
                  { fixture.Aggregate with
                      RecordsTotal = fixture.Aggregate.RecordsTotal + 1 }

              let diffs = compareAggregate fixture.Aggregate mutatedAggregate
              Expect.isNonEmpty diffs "changing aggregate counts should change aggregate"

              let expected =
                  AggregateDifference.RecordsTotal(fixture.Aggregate.RecordsTotal, fixture.Aggregate.RecordsTotal + 1)

              Expect.isTrue (containsDiff diffs expected) "RecordsTotal difference should be reported with exact values" ]

// -----------------------------------------------------------------------------
// All aggregate structural equality tests
// -----------------------------------------------------------------------------

[<Tests>]
let aggregateStructuralEqualityTests =
    testList
        "AggregateStructuralEquality"
        [ exactStructuralEqualityTests
          fieldMutationsTests
          recordIdsComparisonTests
          computedFieldsConsistencyTests ]
