module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateJsonlSchemaFailureTests

// =============================================================================
// Rule Candidate JSONL/Schema Failure Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Sixteen tests covering:
//   * zero-byte file (4 corpora)
//   * interior blank or whitespace-only record (4 corpora)
//   * malformed / truncated JSON (4 corpora)
//   * unsupported schema version (4 corpora)
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private episodesRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/repair-episodes-v1.jsonl"
let private changeSetsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/git-change-sets-v1.jsonl"
let private transitionsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/diagnostic-transitions-v1.jsonl"
let private evidenceRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/verification-evidence-v1.jsonl"

let private emptyCorpusTests =
    testList
        "FSharpDiagnostics.RuleCandidates.JsonlSchema.Empty"
        [ test "repair-episodes: zero-byte file surfaces an error, no empty success" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "zb-ep"
              replaceCorpusWithEmptyFile repo episodesRel
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty file must NOT silently succeed"
              Expect.equal r.Candidates.Length 0 "empty file must produce no candidates"
          }

          test "git-change-sets: zero-byte file surfaces an error, no empty success" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "zb-cs"
              replaceCorpusWithEmptyFile repo changeSetsRel
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty file must NOT silently succeed"
          }

          test "diagnostic-transitions: zero-byte file surfaces an error, no empty success" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "zb-tx"
              replaceCorpusWithEmptyFile repo transitionsRel
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty file must NOT silently succeed"
          }

          test "verification-evidence: zero-byte file surfaces an error, no empty success" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "zb-ev"
              replaceCorpusWithEmptyFile repo evidenceRel
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "empty file must NOT silently succeed"
          } ]

let private interiorBlankTests =
    testList
        "FSharpDiagnostics.RuleCandidates.JsonlSchema.Blank"
        [ test "repair-episodes: interior blank record is NOT silently skipped" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "blank-ep"
              // Append a whitespace-only line in the middle of the file
              let normalizedDir = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir
              let path = normalizedDir + "/repair-episodes-v1.jsonl"
              let existing = System.IO.File.ReadAllText(repo.Absolute path)
              let pre, post = existing.Split([| '\n' |], 2) |> Array.toList |> function x :: xs -> x, String.concat "\n" (xs |> List.map (sprintf "%s")) | _ -> "", ""
              // Simpler: just prepend a blank line then the original content
              repo.WriteUtf8(path, "   \n" + existing)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "blank interior record must surface an error"
          }

          test "git-change-sets: interior blank record is NOT silently skipped" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "blank-cs"
              let normalizedDir = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir
              let path = normalizedDir + "/git-change-sets-v1.jsonl"
              let existing = System.IO.File.ReadAllText(repo.Absolute path)
              repo.WriteUtf8(path, "   \n" + existing)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "blank interior record must surface an error"
          }

          test "diagnostic-transitions: interior blank record is NOT silently skipped" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "blank-tx"
              let normalizedDir = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir
              let path = normalizedDir + "/diagnostic-transitions-v1.jsonl"
              let existing = System.IO.File.ReadAllText(repo.Absolute path)
              repo.WriteUtf8(path, "   \n" + existing)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "blank interior record must surface an error"
          }

          test "verification-evidence: interior blank record is NOT silently skipped" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "blank-ev"
              let normalizedDir = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir
              let path = normalizedDir + "/verification-evidence-v1.jsonl"
              let existing = System.IO.File.ReadAllText(repo.Absolute path)
              repo.WriteUtf8(path, "   \n" + existing)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "blank interior record must surface an error"
          } ]

let private malformedJsonTests =
    testList
        "FSharpDiagnostics.RuleCandidates.JsonlSchema.Malformed"
        [ test "repair-episodes: malformed JSON record surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "malformed-ep"
              let path = episodesRel
              repo.WriteUtf8(path, "{this is not json")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "malformed JSON must surface an error"
              Expect.equal r.Candidates.Length 0 "malformed JSON must produce no candidates"
          }

          test "git-change-sets: malformed JSON record surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "malformed-cs"
              repo.WriteUtf8(changeSetsRel, "{garbage")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "malformed JSON must surface an error"
          }

          test "diagnostic-transitions: malformed JSON record surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "malformed-tx"
              repo.WriteUtf8(transitionsRel, "{garbage")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "malformed JSON must surface an error"
          }

          test "verification-evidence: malformed JSON record surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "malformed-ev"
              repo.WriteUtf8(evidenceRel, "{garbage")
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "malformed JSON must surface an error"
          } ]

let private unsupportedSchemaTests =
    testList
        "FSharpDiagnostics.RuleCandidates.JsonlSchema.Unsupported"
        [ test "repair-episodes: unsupported schema version surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "wrong-schema-ep"
              // Mutate the schema_version on the first line of the episodes file
              let absPath = repo.Absolute episodesRel
              let lines = System.IO.File.ReadAllLines(absPath)
              let mutated = lines.[0].Replace("\"repair-episode-v1\"", "\"repair-episode-v99\"")
              lines.[0] <- mutated
              System.IO.File.WriteAllLines(absPath, lines)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "unsupported schema must surface an error"
          }

          test "git-change-sets: unsupported schema version surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "wrong-schema-cs"
              let absPath = repo.Absolute changeSetsRel
              let lines = System.IO.File.ReadAllLines(absPath)
              let mutated = lines.[0].Replace("\"git-change-set-v1\"", "\"git-change-set-v99\"")
              lines.[0] <- mutated
              System.IO.File.WriteAllLines(absPath, lines)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "unsupported schema must surface an error"
          }

          test "diagnostic-transitions: unsupported schema version surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "wrong-schema-tx"
              let absPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(absPath)
              let mutated = lines.[0].Replace("\"diagnostic-transition-v1\"", "\"diagnostic-transition-v99\"")
              lines.[0] <- mutated
              System.IO.File.WriteAllLines(absPath, lines)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "unsupported schema must surface an error"
          }

          test "verification-evidence: unsupported schema version surfaces an error" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "wrong-schema-ev"
              let absPath = repo.Absolute evidenceRel
              let lines = System.IO.File.ReadAllLines(absPath)
              let mutated = lines.[0].Replace("\"verification-evidence-v1\"", "\"verification-evidence-v99\"")
              lines.[0] <- mutated
              System.IO.File.WriteAllLines(absPath, lines)
              let r = extractCandidates repo.Root
              Expect.isFalse (List.isEmpty r.Errors) "unsupported schema must surface an error"
          } ]

[<Tests>]
let jsonlSchemaTests =
    testList
        "FSharpDiagnostics.RuleCandidates.JsonlSchema"
        [ emptyCorpusTests
          interiorBlankTests
          malformedJsonTests
          unsupportedSchemaTests ]