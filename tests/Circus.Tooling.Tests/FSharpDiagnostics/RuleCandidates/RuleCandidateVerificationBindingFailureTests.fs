module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateVerificationBindingFailureTests

// =============================================================================
// Rule Candidate Verification-Binding Failure Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Twelve tests covering every typed binding failure.  All multi-evidence
// tests write evidence records that the episode under test references,
// so the failure observed is the intended verification-binding rejection
// (not an unresolved-reference failure).
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private episodesRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/repair-episodes-v1.jsonl"
let private changeSetsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/git-change-sets-v1.jsonl"
let private transitionsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/diagnostic-transitions-v1.jsonl"
let private evidenceRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/verification-evidence-v1.jsonl"

let private afterCommit = String.replicate 40 "b"
let private afterTree = String.replicate 40 "d"

/// Build a minimal corpus with an episode that references the supplied
/// evidence IDs.  The episode, change set, transitions, and evidence
/// records are all written.  This guarantees the multi-evidence binding
/// tests exercise the binding check rather than an unresolved reference.
let writeCorpusWithEpisodeReferencing (repo: TempRepository) (key: string) (evidenceIds: string list) : string =
    let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" key
    let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" key
    let epKey = "fsb-" + key
    repo.WriteUtf8(episodesRel, mkRepairEpisodeJsonWithId epId epKey csId evidenceIds + "\n")
    repo.WriteUtf8(changeSetsRel, mkChangeSetJsonWithId csId "a.fs" + "\n")
    repo.WriteUtf8(transitionsRel,
        mkValidDiagnosticTransitionJson key "FS0010" "a.fs" + "\n"
        + mkValidDiagnosticTransitionJson key "FS3118" "a.fs" + "\n")
    epId

[<Tests>]
let verificationBindingTests =
    testList
        "FSharpDiagnostics.RuleCandidates.VerificationBinding"
        [ test "evidence status=fail rejects the episode" {
              use repo = new TempRepository()
              let key = "vb-fail-status"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-fail"]
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId "ev-fail" epId "fail" 0 afterCommit afterTree + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "fail status must NOT yield a candidate"
          }

          test "evidence status=pass with non-zero exit_code rejects the episode" {
              use repo = new TempRepository()
              let key = "vb-nonzero-exit"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-nz"]
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId "ev-nz" epId "pass" 2 afterCommit afterTree + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "non-zero exit must NOT yield a candidate"
          }

          test "evidence status=fail with exit_code=0 rejects the episode (inconsistent)" {
              use repo = new TempRepository()
              let key = "vb-fail-zero"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-fz"]
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId "ev-fz" epId "fail" 0 afterCommit afterTree + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "fail status must dominate"
          }

          test "tested_commit_oid differs from episode after_commit_oid rejects the episode" {
              use repo = new TempRepository()
              let key = "vb-wrong-commit"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-wc"]
              let wrongCommit = String.replicate 40 "f"
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId "ev-wc" epId "pass" 0 wrongCommit afterTree + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "wrong commit binding must NOT yield a candidate"
          }

          test "tested_tree_oid differs from episode after_tree_oid rejects the episode" {
              use repo = new TempRepository()
              let key = "vb-wrong-tree"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-wt"]
              let wrongTree = String.replicate 40 "f"
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId "ev-wt" epId "pass" 0 afterCommit wrongTree + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "wrong tree binding must NOT yield a candidate"
          }

          test "evidence episode_id differs from owning episode rejects the episode" {
              use repo = new TempRepository()
              let key = "vb-ev-ep-mismatch"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-em"]
              let otherEpId = deterministicSha256 "rule-candidate-fixture-episode-v1" "other-episode"
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId "ev-em" otherEpId "pass" 0 afterCommit afterTree + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "mismatched episode_id must NOT yield a candidate"
          }

          test "tested_commit_oid missing (empty) rejects the episode" {
              use repo = new TempRepository()
              let key = "vb-missing-commit"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-mc"]
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId "ev-mc" epId "pass" 0 "" afterTree + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "missing commit binding must NOT yield a candidate"
          }

          test "tested_tree_oid missing (empty) rejects the episode" {
              use repo = new TempRepository()
              let key = "vb-missing-tree"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-mt"]
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId "ev-mt" epId "pass" 0 afterCommit "" + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "missing tree binding must NOT yield a candidate"
          }

          test "one of multiple evidence records fails: episode still rejected" {
              use repo = new TempRepository()
              let key = "vb-multi-fail"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-mfa"; "ev-mfb"]
              let a = mkVerificationEvidenceJsonWithId "ev-mfa" epId "pass" 0 afterCommit afterTree
              let b = mkVerificationEvidenceJsonWithId "ev-mfb" epId "fail" 0 afterCommit afterTree
              repo.WriteUtf8(evidenceRel, a + "\n" + b + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "any failing evidence must reject the episode"
          }

          test "one of multiple evidence records is stale: episode still rejected" {
              use repo = new TempRepository()
              let key = "vb-multi-stale"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-msa"; "ev-msb"]
              let staleCommit = String.replicate 40 "f"
              let a = mkVerificationEvidenceJsonWithId "ev-msa" epId "pass" 0 afterCommit afterTree
              let b = mkVerificationEvidenceJsonWithId "ev-msb" epId "pass" 0 staleCommit afterTree
              repo.WriteUtf8(evidenceRel, a + "\n" + b + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "any stale evidence must reject the episode"
          }

          test "duplicate evidence reference appears in the episode: rejected" {
              use repo = new TempRepository()
              let key = "vb-dup-ref"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" key
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" key
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" key
              // Episode references the SAME evidence_id twice.
              let epJson = mkRepairEpisodeJsonWithId epId ("fsb-" + key) csId [ evidId; evidId ]
              repo.WriteUtf8(episodesRel, epJson + "\n")
              repo.WriteUtf8(changeSetsRel, mkChangeSetJsonWithId csId "a.fs" + "\n")
              repo.WriteUtf8(transitionsRel,
                  mkValidDiagnosticTransitionJson key "FS0010" "a.fs" + "\n"
                  + mkValidDiagnosticTransitionJson key "FS3118" "a.fs" + "\n")
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId evidId epId "pass" 0 afterCommit afterTree + "\n")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "duplicate evidence reference must surface an error"
          }

          test "reorder of mixed evidence set yields same failure outcome" {
              use repo = new TempRepository()
              let key = "vb-reorder"
              let epId = writeCorpusWithEpisodeReferencing repo key ["ev-ra"; "ev-rb"]
              let a = mkVerificationEvidenceJsonWithId "ev-ra" epId "pass" 0 afterCommit afterTree
              let b = mkVerificationEvidenceJsonWithId "ev-rb" epId "fail" 0 afterCommit afterTree
              repo.WriteUtf8(evidenceRel, a + "\n" + b + "\n")
              let r1 = extractCandidates repo.Root
              repo.WriteUtf8(evidenceRel, b + "\n" + a + "\n")
              let r2 = extractCandidates repo.Root
              Expect.equal r1.Candidates.Length r2.Candidates.Length "reorder must not change outcome"
              Expect.equal r1.Candidates.Length 0 "both orderings must reject the episode"
          } ]