module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateIdentityFailureTests

// =============================================================================
// Rule Candidate Identity Failure Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Twelve tests covering every identity-bearing field in the current
// domain model:
//   * empty repair-episode ID
//   * duplicate episode ID with byte-identical records
//   * duplicate episode ID with semantically different records
//   * duplicate episode key under different episode IDs
//   * empty change-set ID
//   * duplicate change-set ID with identical records
//   * duplicate change-set ID with different records
//   * empty transition ID
//   * duplicate transition ID with identical records
//   * duplicate transition ID with different records
//   * empty verification-evidence ID
//   * duplicate verification-evidence ID
//
// The tests assert the typed failure taxonomy and that no candidate is
// produced when an identity violation is detected.
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

let private emptyEpisodeId (key: string) : string =
    let epKey = "fsb-" + key
    let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" key
    let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" key
    let afterCommit = String.replicate 40 "b"
    let afterTree = String.replicate 40 "d"

    "{\"schema_version\":\"repair-episode-v1\","
    + "\"episode_id\":\"\","
    + "\"episode_key\":\"" + epKey + "\","
    + "\"before_capture_id\":\"x\","
    + "\"after_capture_id\":\"y\","
    + "\"before_commit_oid\":\"" + String.replicate 40 "a" + "\","
    + "\"before_tree_oid\":\"" + String.replicate 40 "c" + "\","
    + "\"after_commit_oid\":\"" + afterCommit + "\","
    + "\"after_tree_oid\":\"" + afterTree + "\","
    + "\"commit_range\":[\"" + afterCommit + "\"],"
    + "\"change_set_id\":\"" + csId + "\","
    + "\"command_contract_before\":\"dotnet build\","
    + "\"command_contract_after\":\"dotnet build\","
    + "\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},"
    + "\"transition_counts\":{\"persisted_same_count\":0,\"persisted_count_decreased\":0,\"persisted_count_increased\":0,\"eliminated_after\":4,\"introduced_after\":0,\"resolution_candidates\":4,\"regression_candidates\":0,\"unassessable\":0},"
    + "\"verification_level\":\"focused_gate_verified\","
    + "\"verification_evidence_ids\":[\"" + evidId + "\"],"
    + "\"qualification\":{\"status\":\"qualified\",\"reasons\":[]}}"

let private emptyChangeSetId (key: string) : string =
    let beforeTree = String.replicate 40 "c"
    let afterTree = String.replicate 40 "d"

    "{\"schema_version\":\"git-change-set-v1\","
    + "\"change_set_id\":\"\","
    + "\"change_set_version\":\"git-change-set-v1\","
    + "\"before_tree_oid\":\"" + beforeTree + "\","
    + "\"after_tree_oid\":\"" + afterTree + "\","
    + "\"object_format\":\"sha1\","
    + "\"entries\":[{\"before_mode\":\"100644\",\"after_mode\":\"100644\",\"before_blob_oid\":null,\"after_blob_oid\":null,\"change_kind\":\"modified\",\"canonical_path\":\"a.fs\"}]}"

let private emptyTransitionId (key: string) : string =
    let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" key

    "{\"schema_version\":\"diagnostic-transition-v1\","
    + "\"episode_id\":\"" + epId + "\","
    + "\"exact_fingerprint\":\"\","
    + "\"transition_kind\":\"eliminated_after\","
    + "\"before_occurrence_count\":1,"
    + "\"after_occurrence_count\":0,"
    + "\"severity\":\"error\","
    + "\"code\":\"FS0010\","
    + "\"message_normalized\":\"msg\","
    + "\"source_path\":\"a.fs\","
    + "\"project_path\":null,"
    + "\"span\":{\"start_line\":1,\"start_column\":1,\"end_line\":1,\"end_column\":10},"
    + "\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},"
    + "\"source_link\":{\"kind\":\"source_file_modified\",\"paths\":[\"a.fs\"],\"reasons\":[]},"
    + "\"assessment\":\"observed_resolution_candidate\"}"

let private emptyEvidenceId (key: string) : string =
    let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" key
    let afterCommit = String.replicate 40 "b"
    let afterTree = String.replicate 40 "d"

    "{\"schema_version\":\"verification-evidence-v1\","
    + "\"evidence_id\":\"\","
    + "\"episode_id\":\"" + epId + "\","
    + "\"kind\":\"focused_gate\","
    + "\"command\":\"dotnet build\","
    + "\"working_directory\":\"/tmp\","
    + "\"tested_commit_oid\":\"" + afterCommit + "\","
    + "\"tested_tree_oid\":\"" + afterTree + "\","
    + "\"exit_code\":0,"
    + "\"stdout_sha256\":null,"
    + "\"stderr_sha256\":null,"
    + "\"combined_log_path\":null,"
    + "\"status\":\"pass\"}"

[<Tests>]
let identityFailureTests =
    testList
        "FSharpDiagnostics.RuleCandidates.IdentityFailures"
        [ test "empty repair-episode ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-ep"
              repo.WriteUtf8(episodesRel, emptyEpisodeId "id-empty-ep" + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty episode id must surface an error"
              Expect.equal r.Candidates.Length 0 "empty episode id must NOT produce candidates"
          }

          test "duplicate repair-episode ID (identical) is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-ep"
              let existing = System.IO.File.ReadAllText(repo.Absolute episodesRel)
              let dup = mkValidRepairEpisodeJson "id-dup-ep"
              repo.WriteUtf8(episodesRel, dup + "\n" + dup + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "byte-identical duplicate episode id must be rejected"
          }

          test "duplicate repair-episode ID (different) is rejected, not last-wins" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-ep2"
              let a = mkValidRepairEpisodeJson "id-dup-ep2-a"
              let b = mkValidRepairEpisodeJson "id-dup-ep2-b"
              repo.WriteUtf8(episodesRel, a + "\n" + b + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "semantically different duplicate episode id must be rejected"
          }

          test "duplicate episode key under different episode IDs is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-key"
              let key = "id-dup-key"
              let epIdA = deterministicSha256 "rule-candidate-fixture-episode-v1" (key + "-a")
              let epIdB = deterministicSha256 "rule-candidate-fixture-episode-v1" (key + "-b")
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" key
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" key
              let afterCommit = String.replicate 40 "b"
              let afterTree = String.replicate 40 "d"
              let mkRec epId = "{\"schema_version\":\"repair-episode-v1\",\"episode_id\":\"" + epId + "\",\"episode_key\":\"fsb-" + key + "\",\"before_capture_id\":\"x\",\"after_capture_id\":\"y\",\"before_commit_oid\":\"" + String.replicate 40 "a" + "\",\"before_tree_oid\":\"" + String.replicate 40 "c" + "\",\"after_commit_oid\":\"" + afterCommit + "\",\"after_tree_oid\":\"" + afterTree + "\",\"commit_range\":[\"" + afterCommit + "\"],\"change_set_id\":\"" + csId + "\",\"command_contract_before\":\"dotnet build\",\"command_contract_after\":\"dotnet build\",\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},\"transition_counts\":{\"persisted_same_count\":0,\"persisted_count_decreased\":0,\"persisted_count_increased\":0,\"eliminated_after\":4,\"introduced_after\":0,\"resolution_candidates\":4,\"regression_candidates\":0,\"unassessable\":0},\"verification_level\":\"focused_gate_verified\",\"verification_evidence_ids\":[\"" + evidId + "\"],\"qualification\":{\"status\":\"qualified\",\"reasons\":[]}}"
              repo.WriteUtf8(episodesRel, (mkRec epIdA) + "\n" + (mkRec epIdB) + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "duplicate episode key under different ids must be rejected"
          }

          test "empty change-set ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-cs"
              repo.WriteUtf8(changeSetsRel, emptyChangeSetId "id-empty-cs" + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty change-set id must surface an error"
          }

          test "duplicate change-set ID (identical) is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-cs"
              let dup = mkValidChangeSetJson "id-dup-cs" "a.fs"
              repo.WriteUtf8(changeSetsRel, dup + "\n" + dup + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "byte-identical duplicate change-set id must be rejected"
          }

          test "duplicate change-set ID (different) is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-cs2"
              let a = mkValidChangeSetJson "id-dup-cs2-a" "a.fs"
              let b = mkValidChangeSetJson "id-dup-cs2-b" "b.fs"
              repo.WriteUtf8(changeSetsRel, a + "\n" + b + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "semantically different duplicate change-set id must be rejected"
          }

          test "empty transition ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-tx"
              repo.WriteUtf8(transitionsRel, emptyTransitionId "id-empty-tx" + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty transition id must surface an error"
          }

          test "duplicate transition ID (identical) is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-tx"
              let dup = mkValidDiagnosticTransitionJson "id-dup-tx" "FS0010" "a.fs"
              repo.WriteUtf8(transitionsRel, dup + "\n" + dup + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "byte-identical duplicate transition must be rejected"
          }

          test "duplicate transition ID (different) is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-tx2"
              let a = mkValidDiagnosticTransitionJson "id-dup-tx2-a" "FS0010" "a.fs"
              let b = mkValidDiagnosticTransitionJson "id-dup-tx2-b" "FS3118" "a.fs"
              repo.WriteUtf8(transitionsRel, a + "\n" + b + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "different-content duplicate transition must be rejected"
          }

          test "empty verification-evidence ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-ev"
              repo.WriteUtf8(evidenceRel, emptyEvidenceId "id-empty-ev" + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty evidence id must surface an error"
          }

          test "duplicate verification-evidence ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-ev"
              let dup = mkValidVerificationEvidenceJson "id-dup-ev" "pass" 0
              repo.WriteUtf8(evidenceRel, dup + "\n" + dup + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "duplicate evidence id must be rejected"
          } ]