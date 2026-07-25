module Circus.Tooling.Tests.CanonicalEvidence.DomainTests

// =============================================================================
// Pure model tests for the canonical evidence provider
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// Tests 1–13: pure-domain correctness (no execution, no IO).
// =============================================================================

open System
open Expecto

open Circus.Tooling.CanonicalEvidence.Domain

let private sampleCheck (id: string) (status: EvidenceStatus) : EvidenceCheckResult =
    {
        Id = id
        CommandArgv = [ "dotnet"; "test" ]
        WorkingDirectory = "/repo"
        DurationMilliseconds = 100L
        ExitCode = Some 0
        Status = status
        StdoutSha256 = Some (String.replicate 64 "a")
        StderrSha256 = Some (String.replicate 64 "b")
        FailureKind = None
    }

let private allCheckIds : string list =
    SupportedCheckIds

[<Tests>]
let tests =
    testList
        "CanonicalEvidence.Domain"
        [
          // 1. Required pass produces overall pass
          test "required pass produces overall pass" {
              let checks = [ sampleCheck "tooling-build" Pass ]
              Expect.equal (computeOverallStatus checks) Pass "overall pass"
          }

          // 2. Required fail produces overall fail
          test "required fail produces overall fail" {
              let checks = [ sampleCheck "tooling-build" Fail ]
              Expect.equal (computeOverallStatus checks) Fail "overall fail"
          }

          // 3. Required unavailable produces overall fail
          test "required unavailable produces overall fail" {
              let checks = [ sampleCheck "tooling-build" Unavailable ]
              Expect.equal (computeOverallStatus checks) Fail "unavailable fail"
          }

          // 4. Optional unavailable does not become pass
          test "optional unavailable does not become pass" {
              let checks = [
                  sampleCheck "tooling-build" Pass
                  sampleCheck "tooling-tests-build" Unavailable
              ]
              Expect.equal (computeOverallStatus checks) Fail "unavailable must fail"
          }

          // 5. Check ordering is deterministic
          test "sortChecksDeterministic is stable by id" {
              let unsorted = [
                  sampleCheck "zeta" Pass
                  sampleCheck "alpha" Pass
                  sampleCheck "mu" Pass
              ]
              let sorted = sortChecksDeterministic unsorted
              Expect.equal (List.map (fun c -> c.Id) sorted) [ "alpha"; "mu"; "zeta" ] "sorted by id"
          }

          // 6. Unknown check ID fails
          test "isSupportedCheckId rejects unknown id" {
              Expect.isFalse (isSupportedCheckId "not-a-check") "unknown id rejected"
              Expect.isTrue (isSupportedCheckId "tooling-build") "known id accepted"
          }

          // 7. Unknown schema version fails
          // Validation is performed in Validation.fs; we prove the
          // domain constant is the only supported version.
          test "supported schema version constant" {
              Expect.equal SchemaVersionValue 1 "schema version is 1"
          }

          // 8. SHA-1 and SHA-256 widths are validated
          test "isValidOid accepts full-width sha1 and sha256" {
              let sha1 = String.replicate 40 "a"
              let sha256 = String.replicate 64 "a"
              Expect.isTrue (isValidOid "sha1" sha1) "sha1 full width"
              Expect.isTrue (isValidOid "sha256" sha256) "sha256 full width"
          }

          // 9. Abbreviated OIDs fail
          test "isValidOid rejects abbreviated OIDs" {
              Expect.isFalse (isValidOid "sha1" (String.replicate 7 "a")) "abbreviated sha1"
              Expect.isFalse (isValidOid "sha1" (String.replicate 39 "a")) "off-by-one sha1"
              Expect.isFalse (isValidOid "sha256" (String.replicate 63 "a")) "off-by-one sha256"
              Expect.isFalse (isValidOid "sha256" (String.replicate 12 "a")) "abbreviated sha256"
          }

          // 10. Semantic hash ignores timestamps
          test "semantic hash is invariant without timestamp fields" {
              let commit = String.replicate 40 "a"
              let tree = String.replicate 40 "b"
              let checks = [ sampleCheck "tooling-build" Pass ]
              let doc1 = {
                  SchemaVersion = 1
                  ProviderName = "circus-canonical-evidence"
                  ProviderVersion = "1.0.0"
                  TestedCommitOid = commit
                  TestedTreeOid = tree
                  ObjectFormat = "sha1"
                  Checks = checks
                  OverallStatus = Pass
                  SemanticSha256 = ""
              }
              let doc2 = {
                  SchemaVersion = 1
                  ProviderName = "circus-canonical-evidence"
                  ProviderVersion = "1.0.0"
                  TestedCommitOid = commit
                  TestedTreeOid = tree
                  ObjectFormat = "sha1"
                  Checks = checks
                  OverallStatus = Pass
                  SemanticSha256 = "ignored"
              }
              Expect.equal (computeSemanticHash doc1) (computeSemanticHash doc2) "hash ignores surrounding fields"
          }

          // 11. Semantic hash changes when meaningful evidence changes
          test "semantic hash changes when a check result changes" {
              let commit = String.replicate 40 "a"
              let tree = String.replicate 40 "b"
              let checksA = [ sampleCheck "tooling-build" Pass ]
              let checksB = [ sampleCheck "tooling-build" Fail ]
              let docA = {
                  SchemaVersion = 1
                  ProviderName = "circus-canonical-evidence"
                  ProviderVersion = "1.0.0"
                  TestedCommitOid = commit
                  TestedTreeOid = tree
                  ObjectFormat = "sha1"
                  Checks = checksA
                  OverallStatus = Pass
                  SemanticSha256 = ""
              }
              let docB = {
                  SchemaVersion = 1
                  ProviderName = "circus-canonical-evidence"
                  ProviderVersion = "1.0.0"
                  TestedCommitOid = commit
                  TestedTreeOid = tree
                  ObjectFormat = "sha1"
                  Checks = checksB
                  OverallStatus = Fail
                  SemanticSha256 = ""
              }
              Expect.notEqual (computeSemanticHash docA) (computeSemanticHash docB) "hash reflects meaningful change"
          }

          // 12. Post-publication fields fail pre-publication validation
          test "ForbiddenIdentityFields includes post-publication fields" {
              Expect.isTrue (Set.contains "tag_object_oid" ForbiddenIdentityFieldSet) "tag_object_oid is forbidden"
              Expect.isTrue (Set.contains "push_target_oid" ForbiddenIdentityFieldSet) "push_target_oid is forbidden"
              Expect.isTrue (Set.contains "correction02_commit_oid" ForbiddenIdentityFieldSet) "correction02_commit_oid is forbidden"
              Expect.isTrue (Set.contains "origin_main_oid" ForbiddenIdentityFieldSet) "origin_main_oid is forbidden"
          }

          // 13. Self-referential identity fields fail validation
          test "firstForbiddenIdentityField detects a forbidden key" {
              let keys = [
                  "schema_version"
                  "provider_name"
                  "tag_object_oid"
              ]
              Expect.equal (firstForbiddenIdentityField keys) (Some "tag_object_oid") "forbidden field detected"
          }
        ]
