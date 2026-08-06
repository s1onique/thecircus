module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateVerificationBindingFailureTests

// =============================================================================
// Rule Candidate Verification-Binding Failure Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Twelve tests covering every typed binding failure:
//   1. evidence status is fail
//   2. evidence status is pass, exit code is non-zero
//   3. evidence status is fail, exit code is zero
//   4. tested commit differs from episode after-commit
//   5. tested tree differs from episode after-tree
//   6. evidence episode ID differs from owning episode
//   7. tested commit is missing
//   8. tested tree is missing
//   9. one of multiple evidence records fails
//  10. one of multiple evidence records is stale (mismatching commit)
//  11. duplicate evidence reference appears in the episode
//  12. reorder the same mixed evidence set and obtain the same failure
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private episodesRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/repair-episodes-v1.jsonl"
let private changeSetsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/git-change-sets-v1.jsonl"
let private transitionsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/diagnostic-transitions-v1.jsonl"
let private evidenceRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/verification-evidence-v1.jsonl"

let private mkEvidenceRecord (evidKey: string) (status: string) (exitCode: int) (testedCommit: string) (testedTree: string) (episodeId: string) : string =
    let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" evidKey
    "{\"schema_version\":\"verification-evidence-v1\",\"evidence_id\":\"" + evidId + "\",\"episode_id\":\"" + episodeId + "\",\"kind\":\"focused_gate\",\"command\":\"dotnet build\",\"working_directory\":\"/tmp\",\"tested_commit_oid\":\"" + testedCommit + "\",\"tested_tree_oid\":\"" + testedTree + "\",\"exit_code\":" + string exitCode + ",\"stdout_sha256\":null,\"stderr_sha256\":null,\"combined_log_path\":null,\"status\":\"" + status + "\"}"

[<Tests>]
let verificationBindingTests =
    testList
        "FSharpDiagnostics.RuleCandidates.VerificationBinding"
        [ test "evidence status=fail rejects the episode" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-fail-status"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-fail-status"
              let evidRecord = mkEvidenceRecord "vb-fail-status" "fail" 0 (String.replicate 40 "b") (String.replicate 40 "d") epId
              repo.WriteUtf8(evidenceRel, evidRecord + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "fail status must NOT produce a candidate"
          }

          test "evidence status=pass with non-zero exit_code rejects the episode" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-nonzero-exit"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-nonzero-exit"
              let evidRecord = mkEvidenceRecord "vb-nonzero-exit" "pass" 2 (String.replicate 40 "b") (String.replicate 40 "d") epId
              repo.WriteUtf8(evidenceRel, evidRecord + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "non-zero exit must NOT produce a candidate"
          }

          test "evidence status=fail with zero exit_code rejects the episode (inconsistent)" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-fail-zero"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-fail-zero"
              let evidRecord = mkEvidenceRecord "vb-fail-zero" "fail" 0 (String.replicate 40 "b") (String.replicate 40 "d") epId
              repo.WriteUtf8(evidenceRel, evidRecord + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "fail status must dominate over exit_code"
          }

          test "tested_commit_oid differs from episode after_commit_oid rejects the episode" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-wrong-commit"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-wrong-commit"
              let evidRecord = mkEvidenceRecord "vb-wrong-commit" "pass" 0 (String.replicate 40 "f") (String.replicate 40 "d") epId
              repo.WriteUtf8(evidenceRel, evidRecord + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "wrong commit binding must NOT produce a candidate"
          }

          test "tested_tree_oid differs from episode after_tree_oid rejects the episode" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-wrong-tree"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-wrong-tree"
              let evidRecord = mkEvidenceRecord "vb-wrong-tree" "pass" 0 (String.replicate 40 "b") (String.replicate 40 "f") epId
              repo.WriteUtf8(evidenceRel, evidRecord + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "wrong tree binding must NOT produce a candidate"
          }

          test "evidence episode_id differs from owning episode rejects the episode" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-ev-ep-mismatch"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-ev-ep-mismatch"
              let otherEpId = deterministicSha256 "rule-candidate-fixture-episode-v1" "other-episode"
              let evidRecord = mkEvidenceRecord "vb-ev-ep-mismatch" "pass" 0 (String.replicate 40 "b") (String.replicate 40 "d") otherEpId
              repo.WriteUtf8(evidenceRel, evidRecord + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "mismatched episode_id must NOT produce a candidate"
          }

          test "tested_commit_oid missing (empty) rejects the episode" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-missing-commit"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-missing-commit"
              let evidRecord = mkEvidenceRecord "vb-missing-commit" "pass" 0 "" (String.replicate 40 "d") epId
              repo.WriteUtf8(evidenceRel, evidRecord + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "missing commit binding must NOT produce a candidate"
          }

          test "tested_tree_oid missing (empty) rejects the episode" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-missing-tree"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-missing-tree"
              let evidRecord = mkEvidenceRecord "vb-missing-tree" "pass" 0 (String.replicate 40 "b") "" epId
              repo.WriteUtf8(evidenceRel, evidRecord + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "missing tree binding must NOT produce a candidate"
          }

          test "one of multiple evidence records fails: episode still rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-multi-fail"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-multi-fail"
              let a = mkEvidenceRecord "vb-multi-fail-a" "pass" 0 (String.replicate 40 "b") (String.replicate 40 "d") epId
              let b = mkEvidenceRecord "vb-multi-fail-b" "fail" 0 (String.replicate 40 "b") (String.replicate 40 "d") epId
              repo.WriteUtf8(evidenceRel, a + "\n" + b + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "any failing evidence must reject the episode"
          }

          test "one of multiple evidence records is stale: episode still rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-multi-stale"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-multi-stale"
              let a = mkEvidenceRecord "vb-multi-stale-a" "pass" 0 (String.replicate 40 "b") (String.replicate 40 "d") epId
              let b = mkEvidenceRecord "vb-multi-stale-b" "pass" 0 (String.replicate 40 "f") (String.replicate 40 "d") epId
              repo.WriteUtf8(evidenceRel, a + "\n" + b + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "any stale evidence must reject the episode"
          }

          test "duplicate evidence reference is rejected by the parser" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-dup-ref"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-dup-ref"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "vb-dup-ref"
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" "vb-dup-ref"
              let epJson = "{\"schema_version\":\"repair-episode-v1\",\"episode_id\":\"" + epId + "\",\"episode_key\":\"fsb-vb-dup-ref\",\"before_capture_id\":\"x\",\"after_capture_id\":\"y\",\"before_commit_oid\":\"" + String.replicate 40 "a" + "\",\"before_tree_oid\":\"" + String.replicate 40 "c" + "\",\"after_commit_oid\":\"" + String.replicate 40 "b" + "\",\"after_tree_oid\":\"" + String.replicate 40 "d" + "\",\"commit_range\":[\"" + String.replicate 40 "b" + "\"],\"change_set_id\":\"" + csId + "\",\"command_contract_before\":\"dotnet build\",\"command_contract_after\":\"dotnet build\",\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},\"transition_counts\":{\"persisted_same_count\":0,\"persisted_count_decreased\":0,\"persisted_count_increased\":0,\"eliminated_after\":4,\"introduced_after\":0,\"resolution_candidates\":4,\"regression_candidates\":0,\"unassessable\":0},\"verification_level\":\"focused_gate_verified\",\"verification_evidence_ids\":[\"" + evidId + "\",\"" + evidId + "\"],\"qualification\":{\"status\":\"qualified\",\"reasons\":[]}}"
              repo.WriteUtf8(episodesRel, epJson + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "duplicate evidence reference must surface an error"
          }

          test "reorder of mixed evidence set yields same failure outcome" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "vb-reorder"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "vb-reorder"
              let a = mkEvidenceRecord "vb-reorder-a" "pass" 0 (String.replicate 40 "b") (String.replicate 40 "d") epId
              let b = mkEvidenceRecord "vb-reorder-b" "fail" 0 (String.replicate 40 "b") (String.replicate 40 "d") epId
              repo.WriteUtf8(evidenceRel, a + "\n" + b + "\n")
              let r1 = extractCandidates repo.Root
              repo.WriteUtf8(evidenceRel, b + "\n" + a + "\n")
              let r2 = extractCandidates repo.Root
              Expect.equal r1.Candidates.Length r2.Candidates.Length "reorder must not change outcome"
              Expect.equal r1.Candidates.Length 0 "both orderings must reject the episode"
          } ]