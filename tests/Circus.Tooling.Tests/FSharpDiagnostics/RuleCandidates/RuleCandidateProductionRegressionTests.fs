module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateProductionRegressionTests

// =============================================================================
// Rule Candidate Production Regression Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Eight read-only tests that operate against the real committed
// repository corpus to prove the production candidate remains valid
// after the production changes made by this ACT.
// =============================================================================

open System.IO
open Expecto
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private expectedCandidateId = "7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89"
let private expectedJsonlSha = "c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3"
let private expectedSummarySha = "b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d"

let private repoRoot () : string =
    Directory.GetParent(__SOURCE_DIRECTORY__).Parent.Parent.Parent.FullName

[<Tests>]
let productionRegressionTests =
    testList
        "FSharpDiagnostics.RuleCandidates.ProductionRegression"
        [ test "extraction returns no errors against the committed corpus" {
              let r = extractCandidates (repoRoot ())
              Expect.isEmpty r.Errors "production corpus extraction must succeed without errors"
          }

          test "eligible episode count equals one" {
              let r = extractCandidates (repoRoot ())
              Expect.equal r.EligibleEpisodes 1 "production corpus must yield exactly one eligible episode"
          }

          test "candidate count equals one" {
              let r = extractCandidates (repoRoot ())
              Expect.equal r.Candidates.Length 1 "production corpus must yield exactly one candidate"
          }

          test "episode key equals fsb-0025" {
              let r = extractCandidates (repoRoot ())
              Expect.equal r.Candidates.Head.Evidence.EpisodeKey "fsb-0025" "episode key must be fsb-0025"
          }

          test "candidate id equals preserved ID" {
              let r = extractCandidates (repoRoot ())
              Expect.equal r.Candidates.Head.CandidateId expectedCandidateId "candidate id must equal preserved ID"
          }

          test "supporting transition count equals four" {
              let r = extractCandidates (repoRoot ())
              Expect.equal r.Candidates.Head.TransitionPartition.SupportingTransitionIds.Length 4 "supporting transition count must be 4"
          }

          test "candidate status flags remain all false" {
              let r = extractCandidates (repoRoot ())
              let c = r.Candidates.Head
              Expect.isFalse c.StatusFlags.CausalFamilyCurated "causal_family_curated must be false"
              Expect.isFalse c.StatusFlags.RepairAdviceAvailable "repair_advice_available must be false"
              Expect.isFalse c.StatusFlags.LlmTipAvailable "llm_tip_available must be false"
          }

          test "read-only verification preserves both exact canonical hashes" {
              let root = repoRoot ()
              let jsonlPath = Path.Combine(root, ruleCandidatesJsonlRelativePath)
              let summaryPath = Path.Combine(root, ruleCandidatesSummaryRelativePath)
              Expect.isTrue (File.Exists jsonlPath) "jsonl canonical must exist"
              Expect.isTrue (File.Exists summaryPath) "summary canonical must exist"

              let preJsonl = File.ReadAllBytes jsonlPath
              let preSummary = File.ReadAllBytes summaryPath

              let _verdict, _byteIdentical = runReadOnlyVerify root

              let postJsonl = File.ReadAllBytes jsonlPath
              let postSummary = File.ReadAllBytes summaryPath

              Expect.equal (preJsonl = postJsonl) true "jsonl bytes must be preserved by verification"
              Expect.equal (preSummary = postSummary) true "summary bytes must be preserved by verification"

              let computeSha256 (bytes: byte array) : string =
                  use h = System.Security.Cryptography.SHA256.Create()
                  h.ComputeHash(bytes)
                  |> Array.map (fun b -> b.ToString("x2"))
                  |> String.concat ""

              Expect.equal (computeSha256 preJsonl) expectedJsonlSha "jsonl hash must match preserved canonical hash"
              Expect.equal (computeSha256 preSummary) expectedSummarySha "summary hash must match preserved canonical hash"
          } ]