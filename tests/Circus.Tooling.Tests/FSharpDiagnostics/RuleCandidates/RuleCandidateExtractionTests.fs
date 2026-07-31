module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateExtractionTests

// =============================================================================
// Rule Candidate Extraction Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Classification
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Selection
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.FSharpDiagnostics.Paths

// -----------------------------------------------------------------------------
// Test: fsb-0025 should produce exactly one ParserCascadeRepair candidate
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "RuleCandidateExtraction"
        [ test "fsb-0025 produces exactly one ParserCascadeRepair candidate" {
              let repoRoot =
                  Directory.GetParent(__SOURCE_DIRECTORY__).Parent.Parent.Parent.Parent.FullName

              // Run the full extraction
              let result = extractCandidates repoRoot

              // Check eligibility
              Expect.equal result.EligibleEpisodes 1 "should have 1 eligible episode"

              // The key assertion: exactly one candidate
              if result.Errors.IsEmpty then
                  Expect.equal result.Candidates.Length 1 "fsb-0025 should produce exactly one candidate"
              else
                  failwithf "Extraction had errors: %A" result.Errors
          }

          test "fsb-0025 candidate has ParserCascadeRepair kind" {
              let repoRoot =
                  Directory.GetParent(__SOURCE_DIRECTORY__).Parent.Parent.Parent.Parent.FullName

              let result = extractCandidates repoRoot

              if not result.Errors.IsEmpty then
                  failwithf "Extraction had errors: %A" result.Errors

              Expect.isNonEmpty result.Candidates "should have at least one candidate"

              let candidate = result.Candidates.Head

              Expect.equal
                  candidate.Kind
                  Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain.RuleCandidateKind.ParserCascadeRepair
                  "candidate kind should be ParserCascadeRepair"
          }

          test "fsb-0025 candidate has SingleEpisodeObservedRepair evidence strength" {
              let repoRoot =
                  Directory.GetParent(__SOURCE_DIRECTORY__).Parent.Parent.Parent.Parent.FullName

              let result = extractCandidates repoRoot

              if not result.Errors.IsEmpty then
                  failwithf "Extraction had errors: %A" result.Errors

              Expect.isNonEmpty result.Candidates "should have at least one candidate"

              let candidate = result.Candidates.Head

              Expect.equal
                  candidate.EvidenceStrength
                  Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain.EvidenceStrength.SingleEpisodeObservedRepair
                  "evidence strength should be SingleEpisodeObservedRepair"
          }

          test "fsb-0025 candidate references fsb-0025 episode" {
              let repoRoot =
                  Directory.GetParent(__SOURCE_DIRECTORY__).Parent.Parent.Parent.Parent.FullName

              let result = extractCandidates repoRoot

              if not result.Errors.IsEmpty then
                  failwithf "Extraction had errors: %A" result.Errors

              Expect.isNonEmpty result.Candidates "should have at least one candidate"

              let candidate = result.Candidates.Head
              Expect.equal candidate.Evidence.EpisodeKey "fsb-0025" "episode key should be fsb-0025"
          }

          test "fsb-0025 candidate has limitations that state structural bounds" {
              let repoRoot =
                  Directory.GetParent(__SOURCE_DIRECTORY__).Parent.Parent.Parent.Parent.FullName

              let result = extractCandidates repoRoot

              if not result.Errors.IsEmpty then
                  failwithf "Extraction had errors: %A" result.Errors

              Expect.isNonEmpty result.Candidates "should have at least one candidate"

              let candidate = result.Candidates.Head
              Expect.isNonEmpty candidate.Limitations "limitations must not be empty"

              Expect.isTrue
                  (candidate.Limitations
                   |> Seq.exists (fun l -> l.Contains("one") || l.Contains("single") || l.Contains("episode")))
                  "limitations must state structural bounds about episode count"
          } ]
