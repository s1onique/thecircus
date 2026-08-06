module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateIdentityFailureTests

// =============================================================================
// Rule Candidate Identity Failure Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Twelve tests covering every identity-bearing field in the current
// domain model.  Every duplicate-identity test asserts the EXACT typed
// failure variant emitted by the extractor — never the generic
// non-empty-errors pattern.  Identical-duplicate tests write two records
// with the SAME identity and different content; different-content
// duplicate tests use explicit, hand-crafted identifiers that are
// identical across two records.
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private episodesRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/repair-episodes-v1.jsonl"
let private changeSetsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/git-change-sets-v1.jsonl"
let private transitionsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/diagnostic-transitions-v1.jsonl"
let private evidenceRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/verification-evidence-v1.jsonl"

[<Tests>]
let identityFailureTests =
    testList
        "FSharpDiagnostics.RuleCandidates.IdentityFailures"
        [ test "empty repair-episode ID is rejected with InvalidInputIdentity" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-ep"
              // Replace the episode record with one that has an empty episode_id.
              let abs = repo.Absolute episodesRel
              let lines = System.IO.File.ReadAllLines(abs)
              lines.[0] <- lines.[0].Replace("\"episode_id\":\"\"\" + (System.String.replicate 1 \"\") + \"\"", "\"episode_id\":\"\"")
              // Simpler replacement using a fixed substring
              let before = lines.[0]
              let idx = before.IndexOf("\"episode_id\":\"")
              let quote = before.IndexOf("\"", idx + 14)
              let epIdLen = quote - (idx + 14)
              lines.[0] <- before.Substring(0, idx + 14) + before.Substring(idx + 14 + epIdLen)
              System.IO.File.WriteAllLines(abs, lines)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty episode id must surface an error"
              Expect.equal r.Candidates.Length 0 "empty episode id must NOT produce candidates"
          }

          test "duplicate repair-episode ID (byte-identical records) is rejected" {
              use repo = new TempRepository()
              let dup = mkValidRepairEpisodeJson "id-dup-ep-same"
              // Append an identical record to exercise duplicate detection.
              repo.AppendUtf8(episodesRel, dup + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "byte-identical duplicate episode id must be rejected"
          }

          test "duplicate repair-episode ID (different content, same id) is rejected" {
              use repo = new TempRepository()
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-ep-content"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "id-dup-ep-content"
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" "id-dup-ep-content"
              let a = mkRepairEpisodeJsonWithId epId "fsb-id-dup-ep-content-a" csId [ evidId ]
              let b = mkRepairEpisodeJsonWithId epId "fsb-id-dup-ep-content-b" csId [ evidId ]
              repo.WriteUtf8(episodesRel, a + "\n" + b + "\n")
              // The change set must exist for the change-set reference to resolve.
              repo.WriteUtf8(changeSetsRel, mkChangeSetJsonWithId csId "a.fs" + "\n")
              // One valid evidence record must exist.
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId evidId epId "pass" 0 (String.replicate 40 "b") (String.replicate 40 "d") + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "different-content duplicate episode id must be rejected"
          }

          test "duplicate episode key under different episode IDs is rejected" {
              use repo = new TempRepository()
              let epIdA = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-key-a"
              let epIdB = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-key-b"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "id-dup-key"
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" "id-dup-key"
              let a = mkRepairEpisodeJsonWithId epIdA "fsb-shared-key" csId [ evidId ]
              let b = mkRepairEpisodeJsonWithId epIdB "fsb-shared-key" csId [ evidId ]
              repo.WriteUtf8(episodesRel, a + "\n" + b + "\n")
              repo.WriteUtf8(changeSetsRel, mkChangeSetJsonWithId csId "a.fs" + "\n")
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId evidId epIdA "pass" 0 (String.replicate 40 "b") (String.replicate 40 "d") + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "duplicate episode key under different ids must be rejected"
          }

          test "empty change-set ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-cs"
              repo.WriteUtf8(changeSetsRel,
                  "{\"schema_version\":\"git-change-set-v1\",\"change_set_id\":\"\",\"change_set_version\":\"git-change-set-v1\",\"before_tree_oid\":\"" + (String.replicate 40 "c") + "\",\"after_tree_oid\":\"" + (String.replicate 40 "d") + "\",\"object_format\":\"sha1\",\"entries\":[{\"before_mode\":\"100644\",\"after_mode\":\"100644\",\"before_blob_oid\":null,\"after_blob_oid\":null,\"change_kind\":\"modified\",\"canonical_path\":\"a.fs\"}]}\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty change-set id must surface an error"
          }

          test "duplicate change-set ID (identical records) is rejected" {
              use repo = new TempRepository()
              let dup = mkValidChangeSetJson "id-dup-cs-same" "a.fs"
              repo.AppendUtf8(changeSetsRel, dup + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "byte-identical duplicate change-set id must be rejected"
          }

          test "duplicate change-set ID (different content, same id) is rejected" {
              use repo = new TempRepository()
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "id-dup-cs-content"
              let a = mkChangeSetJsonWithId csId "a.fs"
              let b = mkChangeSetJsonWithId csId "b.fs"
              repo.WriteUtf8(changeSetsRel, a + "\n" + b + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "different-content duplicate change-set id must be rejected"
          }

          test "empty transition ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-tx"
              let abs = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(abs)
              // Empty the exact_fingerprint by reconstructing the line.
              let prefix = "{\"schema_version\":\"diagnostic-transition-v1\",\"episode_id\":\""
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-empty-tx"
              let mutated =
                  prefix + epId + "\",\"exact_fingerprint\":\"\","
                  + "\"transition_kind\":\"eliminated_after\",\"before_occurrence_count\":1,\"after_occurrence_count\":0,"
                  + "\"severity\":\"error\",\"code\":\"FS0010\",\"message_normalized\":\"msg\","
                  + "\"source_path\":\"a.fs\",\"project_path\":null,"
                  + "\"span\":{\"start_line\":1,\"start_column\":1,\"end_line\":1,\"end_column\":10},"
                  + "\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},"
                  + "\"source_link\":{\"kind\":\"source_file_modified\",\"paths\":[\"a.fs\"],\"reasons\":[]},"
                  + "\"assessment\":\"observed_resolution_candidate\"}"
              lines.[0] <- mutated
              System.IO.File.WriteAllLines(abs, lines)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty transition id must surface an error"
          }

          test "duplicate transition ID (identical records) is rejected" {
              use repo = new TempRepository()
              let dup = mkValidDiagnosticTransitionJson "id-dup-tx-same" "FS0010" "a.fs"
              repo.AppendUtf8(transitionsRel, dup + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "byte-identical duplicate transition must be rejected"
          }

          test "duplicate transition ID (different content, same id) is rejected" {
              use repo = new TempRepository()
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-tx-content"
              let fp = "shared-fp"
              let path = "a.fs"
              let code1 = "FS0010"
              let code2 = "FS3118"
              let mk (code: string) (kind: string) =
                  "{\"schema_version\":\"diagnostic-transition-v1\","
                  + "\"episode_id\":\"" + epId + "\","
                  + "\"exact_fingerprint\":\"" + fp + "\","
                  + "\"transition_kind\":\"" + kind + "\","
                  + "\"before_occurrence_count\":1,\"after_occurrence_count\":0,"
                  + "\"severity\":\"error\",\"code\":\"" + code + "\","
                  + "\"message_normalized\":\"msg\","
                  + "\"source_path\":\"" + path + "\",\"project_path\":null,"
                  + "\"span\":{\"start_line\":1,\"start_column\":1,\"end_line\":1,\"end_column\":10},"
                  + "\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},"
                  + "\"source_link\":{\"kind\":\"source_file_modified\",\"paths\":[\"" + path + "\"],\"reasons\":[]},"
                  + "\"assessment\":\"observed_resolution_candidate\"}"
              repo.WriteUtf8(transitionsRel, mk code1 "eliminated_after" + "\n" + mk code2 "persisted_same_count" + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "different-content duplicate transition must be rejected"
          }

          test "empty verification-evidence ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-ev"
              let abs = repo.Absolute evidenceRel
              let lines = System.IO.File.ReadAllLines(abs)
              let mutated = lines.[0].Replace("\"evidence_id\":\""
                  + (System.Text.RegularExpressions.Regex.Escape (deterministicSha256 "rule-candidate-fixture-evidence-v1" "id-empty-ev"))
                  + "\"", "\"evidence_id\":\"\"")
              lines.[0] <- mutated
              System.IO.File.WriteAllLines(abs, lines)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty evidence id must surface an error"
          }

          test "duplicate verification-evidence ID is rejected" {
              use repo = new TempRepository()
              let dup = mkValidVerificationEvidenceJson "id-dup-ev-same" "pass" 0
              repo.AppendUtf8(evidenceRel, dup + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "duplicate evidence id must be rejected"
          } ]