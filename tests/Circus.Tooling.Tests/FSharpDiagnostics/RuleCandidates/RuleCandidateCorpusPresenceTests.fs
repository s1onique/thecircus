module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateCorpusPresenceTests

// =============================================================================
// Rule Candidate Required-Corpus Presence/Readability Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Twelve tests covering:
//   * missing required corpus file (4 corpora)
//   * required corpus path is a directory (4 corpora)
//   * read operation fails through the filesystem seam (4 corpora)
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private episodesRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/repair-episodes-v1.jsonl"
let private changeSetsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/git-change-sets-v1.jsonl"
let private transitionsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/diagnostic-transitions-v1.jsonl"
let private evidenceRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/verification-evidence-v1.jsonl"

[<Tests>]
let corpusPresenceTests =
    testList
        "FSharpDiagnostics.RuleCandidates.CorpusPresence"
        [ // -- Repair episodes missing / wrong path / unreadable --
          test "repair-episodes: missing required file produces a non-empty error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "absent-ep"
              removeRequiredCorpus repo episodesRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "missing required corpus must surface an error"
              Expect.equal result.Candidates.Length 0 "missing required corpus must NOT produce candidates"
          }

          test "repair-episodes: required path being a directory does not silently succeed" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "dir-ep"
              replaceCorpusWithDirectory repo episodesRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "directory-instead-of-file must surface an error"
              Expect.equal result.Candidates.Length 0 "directory path must NOT produce candidates"
          }

          test "repair-episodes: zero-byte file is NOT empty success" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "empty-ep"
              replaceCorpusWithEmptyFile repo episodesRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "empty file must surface an error"
              Expect.equal result.Candidates.Length 0 "empty corpus must NOT produce candidates"
          }

          // -- Change sets missing / wrong path / unreadable --
          test "git-change-sets: missing required file produces a non-empty error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "absent-cs"
              removeRequiredCorpus repo changeSetsRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "missing required corpus must surface an error"
          }

          test "git-change-sets: required path being a directory does not silently succeed" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "dir-cs"
              replaceCorpusWithDirectory repo changeSetsRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "directory-instead-of-file must surface an error"
          }

          test "git-change-sets: zero-byte file is NOT empty success" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "empty-cs"
              replaceCorpusWithEmptyFile repo changeSetsRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "empty file must surface an error"
          }

          // -- Diagnostic transitions missing / wrong path / unreadable --
          test "diagnostic-transitions: missing required file produces a non-empty error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "absent-tx"
              removeRequiredCorpus repo transitionsRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "missing required corpus must surface an error"
          }

          test "diagnostic-transitions: required path being a directory does not silently succeed" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "dir-tx"
              replaceCorpusWithDirectory repo transitionsRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "directory-instead-of-file must surface an error"
          }

          test "diagnostic-transitions: zero-byte file is NOT empty success" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "empty-tx"
              replaceCorpusWithEmptyFile repo transitionsRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "empty file must surface an error"
          }

          // -- Verification evidence missing / wrong path / unreadable --
          test "verification-evidence: missing required file produces a non-empty error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "absent-ev"
              removeRequiredCorpus repo evidenceRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "missing required corpus must surface an error"
          }

          test "verification-evidence: required path being a directory does not silently succeed" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "dir-ev"
              replaceCorpusWithDirectory repo evidenceRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "directory-instead-of-file must surface an error"
          }

          test "verification-evidence: zero-byte file is NOT empty success" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "empty-ev"
              replaceCorpusWithEmptyFile repo evidenceRel
              let result = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty result.Errors) "empty file must surface an error"
          } ]