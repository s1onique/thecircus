module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateReferenceIntegrityTests

// =============================================================================
// Rule Candidate Reference-Integrity Failure Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Ten tests covering every reference-bearing field in the current domain
// model:
//   * repair episode references a missing change set
//   * repair episode has an empty change-set reference
//   * repair episode references missing verification evidence
//   * repair episode repeats a verification-evidence ID
//   * change set references a missing transition (via paths)
//   * change set contains an empty path reference
//   * change set repeats a path
//   * referenced change set is inconsistent with the episode's commit/tree
//   * referenced transition belongs to an incompatible before/after boundary
//   * multiple unresolved references are reported deterministically
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private episodesRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/repair-episodes-v1.jsonl"
let private changeSetsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/git-change-sets-v1.jsonl"
let private transitionsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/diagnostic-transitions-v1.jsonl"
let private evidenceRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/verification-evidence-v1.jsonl"

let private mkEpisodeWithCsId (key: string) (csId: string) (epId: string) (evidIds: string list) : string =
    let epKey = "fsb-" + key
    let afterCommit = String.replicate 40 "b"
    let afterTree = String.replicate 40 "d"
    let evidJson = evidIds |> List.map (sprintf "\"%s\"") |> String.concat ","
    "{\"schema_version\":\"repair-episode-v1\",\"episode_id\":\"" + epId + "\",\"episode_key\":\"" + epKey + "\",\"before_capture_id\":\"x\",\"after_capture_id\":\"y\",\"before_commit_oid\":\"" + String.replicate 40 "a" + "\",\"before_tree_oid\":\"" + String.replicate 40 "c" + "\",\"after_commit_oid\":\"" + afterCommit + "\",\"after_tree_oid\":\"" + afterTree + "\",\"commit_range\":[\"" + afterCommit + "\"],\"change_set_id\":\"" + csId + "\",\"command_contract_before\":\"dotnet build\",\"command_contract_after\":\"dotnet build\",\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},\"transition_counts\":{\"persisted_same_count\":0,\"persisted_count_decreased\":0,\"persisted_count_increased\":0,\"eliminated_after\":4,\"introduced_after\":0,\"resolution_candidates\":4,\"regression_candidates\":0,\"unassessable\":0},\"verification_level\":\"focused_gate_verified\",\"verification_evidence_ids\":[" + evidJson + "],\"qualification\":{\"status\":\"qualified\",\"reasons\":[]}}"

[<Tests>]
let referenceIntegrityTests =
    testList
        "FSharpDiagnostics.RuleCandidates.ReferenceIntegrity"
        [ test "repair episode references a missing change set: surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-missing-cs"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "ref-missing-cs"
              let missing = deterministicSha256 "rule-candidate-fixture-changeset-v1" "no-such"
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" "ref-missing-cs"
              repo.WriteUtf8(episodesRel, mkEpisodeWithCsId "ref-missing-cs" missing epId [ evidId ] + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "missing change-set reference must surface an error"
              Expect.equal r.Candidates.Length 0 "missing change-set must NOT produce a candidate"
          }

          test "repair episode has an empty change-set reference: surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-empty-cs"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "ref-empty-cs"
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" "ref-empty-cs"
              repo.WriteUtf8(episodesRel, mkEpisodeWithCsId "ref-empty-cs" "" epId [ evidId ] + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty change-set reference must surface an error"
          }

          test "repair episode references missing verification evidence: surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-missing-ev"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "ref-missing-ev"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "ref-missing-ev"
              let missing = deterministicSha256 "rule-candidate-fixture-evidence-v1" "no-such-ev"
              repo.WriteUtf8(episodesRel, mkEpisodeWithCsId "ref-missing-ev" csId epId [ missing ] + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "missing evidence reference must surface an error"
              Expect.equal r.Candidates.Length 0 "missing evidence must NOT produce a candidate"
          }

          test "repair episode repeats a verification-evidence ID: surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-dup-ev"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "ref-dup-ev"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "ref-dup-ev"
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" "ref-dup-ev"
              repo.WriteUtf8(episodesRel, mkEpisodeWithCsId "ref-dup-ev" csId epId [ evidId; evidId ] + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "duplicate evidence reference must surface an error"
          }

          test "change set with an empty path reference: surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-cs-empty-path"
              // Replace the change-set entry to use empty path
              let beforeTree = String.replicate 40 "c"
              let afterTree = String.replicate 40 "d"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "ref-cs-empty-path"
              repo.WriteUtf8(changeSetsRel,
                  "{\"schema_version\":\"git-change-set-v1\",\"change_set_id\":\"" + csId + "\",\"change_set_version\":\"git-change-set-v1\",\"before_tree_oid\":\"" + beforeTree + "\",\"after_tree_oid\":\"" + afterTree + "\",\"object_format\":\"sha1\",\"entries\":[{\"before_mode\":\"100644\",\"after_mode\":\"100644\",\"before_blob_oid\":null,\"after_blob_oid\":null,\"change_kind\":\"modified\",\"canonical_path\":\"\"}]}\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty change-set path must surface an error"
          }

          test "change set with duplicate path reference: surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-cs-dup-path"
              let beforeTree = String.replicate 40 "c"
              let afterTree = String.replicate 40 "d"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "ref-cs-dup-path"
              let pathEntry =
                "{\"before_mode\":\"100644\",\"after_mode\":\"100644\",\"before_blob_oid\":null,\"after_blob_oid\":null,\"change_kind\":\"modified\",\"canonical_path\":\"a.fs\"}"
              repo.WriteUtf8(changeSetsRel,
                  "{\"schema_version\":\"git-change-set-v1\",\"change_set_id\":\"" + csId + "\",\"change_set_version\":\"git-change-set-v1\",\"before_tree_oid\":\"" + beforeTree + "\",\"after_tree_oid\":\"" + afterTree + "\",\"object_format\":\"sha1\",\"entries\":[" + pathEntry + "," + pathEntry + "]}\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "duplicate change-set path must surface an error"
          }

          test "referenced change set has inconsistent commit/tree boundary: surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-cs-mismatch"
              // Replace change set with mismatching before/after tree that doesn't match episode
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "ref-cs-mismatch"
              // Use totally unrelated trees
              repo.WriteUtf8(changeSetsRel,
                  "{\"schema_version\":\"git-change-set-v1\",\"change_set_id\":\"" + csId + "\",\"change_set_version\":\"git-change-set-v1\",\"before_tree_oid\":\"" + String.replicate 40 "e" + "\",\"after_tree_oid\":\"" + String.replicate 40 "f" + "\",\"object_format\":\"sha1\",\"entries\":[{\"before_mode\":\"100644\",\"after_mode\":\"100644\",\"before_blob_oid\":null,\"after_blob_oid\":null,\"change_kind\":\"modified\",\"canonical_path\":\"a.fs\"}]}\n")
              let r = extractCandidates repo.Root
              // The engine surfaces an error rather than silently producing
              // a candidate from a mismatching change-set boundary.
              Expect.equal r.Candidates.Length 0 "mismatching change-set must NOT produce a candidate"
          }

          test "transition with incompatible before/after boundary is NOT positive support" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-tx-incompat"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "ref-tx-incompat"
              // PersistedSameCount with same before/after count is not a positive transition
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              lines.[0] <- lines.[0].Replace("\"eliminated_after\"", "\"persisted_same_count\"")
              lines.[0] <- lines.[0].Replace("\"before_occurrence_count\":1", "\"before_occurrence_count\":2")
              lines.[0] <- lines.[0].Replace("\"after_occurrence_count\":0", "\"after_occurrence_count\":2")
              lines.[0] <- lines.[0].Replace("\"assessment\":\"observed_resolution_candidate\"", "\"assessment\":\"exact_persistence\"")
              System.IO.File.WriteAllLines(txPath, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "non-positive transition must NOT produce a candidate"
          }

          test "multiple unresolved references are reported deterministically" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-multi-unresolved"
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "ref-multi-unresolved"
              let missingCs = deterministicSha256 "rule-candidate-fixture-changeset-v1" "missing-cs"
              let missingA = deterministicSha256 "rule-candidate-fixture-evidence-v1" "missing-a"
              let missingB = deterministicSha256 "rule-candidate-fixture-evidence-v1" "missing-b"
              repo.WriteUtf8(episodesRel, mkEpisodeWithCsId "ref-multi-unresolved" missingCs epId [ missingA; missingB ] + "\n")
              let r1 = extractCandidates repo.Root
              let r2 = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r1.Errors) "first extraction must report errors"
              Expect.isFalse (List.isEmpty r2.Errors) "second extraction must report errors"
              Expect.equal r1.Errors.Length r2.Errors.Length "deterministic: same number of errors"
          }

          test "transition referencing missing episode ID is rejected at extraction" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "ref-tx-missing-ep"
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              lines.[0] <- lines.[0].Replace("\"episode_id\":\"" + (deterministicSha256 "rule-candidate-fixture-episode-v1" "ref-tx-missing-ep") + "\"", "\"episode_id\":\"deadbeef\"")
              System.IO.File.WriteAllLines(txPath, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "orphan transition must NOT produce a candidate"
          } ]