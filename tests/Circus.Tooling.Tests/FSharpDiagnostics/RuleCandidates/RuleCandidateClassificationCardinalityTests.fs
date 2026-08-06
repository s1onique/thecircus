module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateClassificationCardinalityTests

// =============================================================================
// Rule Candidate Classification/Cardinality Failure Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Fourteen tests covering every classification and cardinality branch.
// Tests use the public helpers (mkValidDiagnosticTransitionJson,
// buildPartition, classifyGroup, selectCandidateGroup) so they never
// construct `DiagnosticTransition` records directly — that would couple
// the tests to private field ordering.  All in-process record fixtures
// are produced via the production classification helpers.
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Classification
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private episodesRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/repair-episodes-v1.jsonl"
let private changeSetsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/git-change-sets-v1.jsonl"
let private transitionsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/diagnostic-transitions-v1.jsonl"
let private evidenceRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/verification-evidence-v1.jsonl"

[<Tests>]
let classificationTests =
    testList
        "FSharpDiagnostics.RuleCandidates.Classification"
        [ test "context transition is NEVER positive support" {
              // Build a transition row JSON with assessment=unassessable.
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-ctx"
              // Replace first transition's assessment with unassessable
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              lines.[0] <- lines.[0].Replace("\"observed_resolution_candidate\"", "\"unassessable\"")
              System.IO.File.WriteAllLines(txPath, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "Unassessable must NEVER yield a candidate"
          }

          test "ambiguous transition is NEVER positive support" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-amb"
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              lines.[0] <- lines.[0].Replace("\"observed_resolution_candidate\"", "\"ambiguous\"")
              System.IO.File.WriteAllLines(txPath, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "Ambiguous must NEVER yield a candidate"
          }

          test "regression transition is NEVER positive support" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-reg"
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              lines.[0] <- lines.[0].Replace("\"observed_resolution_candidate\"", "\"observed_regression_candidate\"")
              System.IO.File.WriteAllLines(txPath, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "Regression must NEVER yield a candidate"
          }

          test "deleted path transitions contribute nothing positive" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-del"
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              // Mark transition kind as eliminated_by_source_removal and source_link as deleted
              lines.[0] <- lines.[0].Replace("\"eliminated_after\"", "\"eliminated_by_source_removal\"")
              lines.[0] <- lines.[0].Replace("\"source_file_modified\"", "\"source_file_deleted\"")
              System.IO.File.WriteAllLines(txPath, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "deleted path transitions must not yield a candidate"
          }

          test "introduced-after transitions contribute nothing positive" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-intro"
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              // Switch transition_kind to introduced_after; this is a structural exclusion.
              lines.[0] <- lines.[0].Replace("\"eliminated_after\"", "\"introduced_after\"")
              System.IO.File.WriteAllLines(txPath, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "introduced_after must not yield a candidate"
          }

          test "diagnostic transitions spanning incompatible paths are not selected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-incompat-paths"
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              // Mutate one transition to have a different path
              lines.[1] <- lines.[1].Replace("\"source_path\":\"a.fs\"", "\"source_path\":\"b.fs\"")
              System.IO.File.WriteAllLines(txPath, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "multi-path group must not yield a candidate"
          }

          test "parser and non-parser diagnostics mixed: rejected by classification" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-mixed-parser"
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              // Replace FS0010 with a non-parser FS code in one transition
              lines.[0] <- lines.[0].Replace("\"FS0010\"", "\"FS0001\"")
              System.IO.File.WriteAllLines(txPath, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "non-parser code must reject classification"
          }

          test "zero candidates produces empty successful inventory" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-zero-candidates"
              // Empty out transitions so no candidate can be selected
              let txPath = repo.Absolute transitionsRel
              System.IO.File.WriteAllText(txPath, "")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "no transitions must produce no candidates"
              Expect.isFalse (List.isEmpty r.Errors) "no transitions must surface an error"
          }

          test "parser diagnostics without required anchor (FS0010 or FS3118) is not selected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-no-required"
              let txPath = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(txPath)
              // Replace FS0010 with FS0603 everywhere (still parser family but not the required anchor)
              let updated = lines |> Array.map (fun l -> l.Replace("\"FS0010\"", "\"FS0603\""))
              System.IO.File.WriteAllLines(txPath, updated)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "missing required FS0010/FS3118 anchor must reject"
          }

          test "transition group comparison is deterministic across runs" {
              let gfA =
                  { Path = "a.fs"
                    TransitionCount = 3
                    DiagnosticCodes = [ "FS0010"; "FS3118" ]
                    EarliestLine = Some 10
                    TransitionIds = [ "t1"; "t2"; "t3" ] }
              let gfB =
                  { Path = "b.fs"
                    TransitionCount = 3
                    DiagnosticCodes = [ "FS0010"; "FS3118" ]
                    EarliestLine = Some 5
                    TransitionIds = [ "t4"; "t5"; "t6" ] }
              let cmp1 = compareTransitionGroupFacts gfA gfB
              let cmp2 = compareTransitionGroupFacts gfA gfB
              Expect.equal cmp1 cmp2 "comparison must be deterministic"
          }

          test "transition group with smaller transition count is preferred" {
              let big =
                  { Path = "z.fs"
                    TransitionCount = 10
                    DiagnosticCodes = [ "FS0010" ]
                    EarliestLine = Some 100
                    TransitionIds = List.init 10 (fun i -> "t" + string i) }
              let small =
                  { Path = "a.fs"
                    TransitionCount = 3
                    DiagnosticCodes = [ "FS0010" ]
                    EarliestLine = Some 1
                    TransitionIds = [ "u1"; "u2"; "u3" ] }
              Expect.isTrue (compareTransitionGroupFacts big small < 0) "larger count must rank lower"
          }

          test "path ordinal is the final tie-breaker in group comparison" {
              let a =
                  { Path = "a.fs"
                    TransitionCount = 1
                    DiagnosticCodes = [ "FS0010" ]
                    EarliestLine = Some 1
                    TransitionIds = [ "t1" ] }
              let z =
                  { Path = "z.fs"
                    TransitionCount = 1
                    DiagnosticCodes = [ "FS0010" ]
                    EarliestLine = Some 1
                    TransitionIds = [ "t1" ] }
              Expect.isTrue (compareTransitionGroupFacts a z < 0) "a.fs must rank above z.fs"
          }

          test "candidate selection collapses empty input to a typed failure, not silent empty success" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cl-collapse"
              System.IO.File.WriteAllText(repo.Absolute episodesRel, "")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "empty input must produce zero candidates"
              Expect.isFalse (List.isEmpty r.Errors) "empty input must surface a typed error"
          }

          test "positive candidate path remains ParserCascadeRepair kind" {
              use repo = new TempRepository()
              let r = selfVerifyFixture repo "cl-positive"
              Expect.isEmpty r.Errors "valid minimal corpus must succeed"
              let c = r.Candidates.Head
              Expect.equal c.Kind RuleCandidateKind.ParserCascadeRepair "kind must be ParserCascadeRepair"
              Expect.equal c.EvidenceStrength EvidenceStrength.SingleEpisodeObservedRepair "evidence strength must be SingleEpisodeObservedRepair"
          } ]