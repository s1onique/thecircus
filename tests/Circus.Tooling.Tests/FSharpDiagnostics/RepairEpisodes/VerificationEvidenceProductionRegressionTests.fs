module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceProductionRegressionTests

// =============================================================================
// Verification Evidence Production Regression Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION03:
// Spec §16 — six committed regression tests that prove the production
// `fsb-0025` rule candidate remains byte-identical and semantically
// unchanged after the alias-contract closure.
//
// Required tests:
//   1. repair-episode engine loads the production corpus
//   2. exactly one episode is eligible
//   3. exactly one rule candidate is present
//   4. candidate episode key is fsb-0025
//   5. candidate ID is unchanged
//   6. read-only verification reports byte-identical canonical artifacts
//
// Tests MUST NOT mutate production corpus artifacts.  Read-only verification
// is exercised by hashing the canonical artifacts BEFORE and AFTER the
// call and asserting byte-equality, rather than trusting a display string.
// =============================================================================

open System
open System.IO
open System.Security.Cryptography
open Expecto

open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

// Production regression tests live at
//   tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/*.fs
// The __SOURCE_DIRECTORY__ is therefore FSharpDiagnostics/RepairEpisodes.
// One Directory.GetParent gives FSharpDiagnostics; three further .Parent
// hops yield Circus.Tooling.Tests, tests, and finally the repository
// root (thecircus/).  Total: 1 GetParent + 3 .Parent hops.
let private repoRoot () : string =
    Directory.GetParent(__SOURCE_DIRECTORY__).Parent.Parent.Parent.FullName

let private expectedCandidateId =
    "7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89"

let private canonicalArtifacts () : string list =
    [ toAbsolutePath (repoRoot ()) ruleCandidatesJsonlRelativePath
      toAbsolutePath (repoRoot ()) ruleCandidatesSummaryRelativePath ]

/// Compute a lowercase-hex SHA-256 digest of the file at `path`.  Fails
/// the test if the file does not exist.
let private sha256OfFile (path: string) : string =
    if not (File.Exists path) then
        failwithf "canonical artifact missing: %s" path

    use stream = File.OpenRead path
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash stream
    bytes
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""

// -----------------------------------------------------------------------------
// Spec §16 — six committed production regression tests
// -----------------------------------------------------------------------------

[<Tests>]
let productionRegressionTests =
    testList "production regression" [
        // 1. repair-episode engine loads the production corpus
        test "repair-episode engine loads the production corpus" {
            let result = extractCandidates (repoRoot ())

            Expect.isEmpty
                result.Errors
                "extraction must succeed with no errors"
        }
        // 2. exactly one episode is eligible
        test "exactly one episode is eligible" {
            let result = extractCandidates (repoRoot ())

            Expect.isEmpty result.Errors (sprintf "errors: %A" result.Errors)
            Expect.equal result.EligibleEpisodes 1 "exactly one eligible episode expected"
        }
        // 3. exactly one rule candidate is present
        test "exactly one rule candidate is present" {
            let result = extractCandidates (repoRoot ())

            Expect.isEmpty result.Errors (sprintf "errors: %A" result.Errors)
            Expect.equal result.Candidates.Length 1 "exactly one rule candidate expected"
        }
        // 4. candidate episode key is fsb-0025
        test "candidate episode_key is fsb-0025" {
            let result = extractCandidates (repoRoot ())

            Expect.isEmpty result.Errors (sprintf "errors: %A" result.Errors)
            let candidate = result.Candidates.Head
            Expect.equal
                candidate.Evidence.EpisodeKey
                "fsb-0025"
                (sprintf "candidate episode_key must be fsb-0025, got %s" candidate.Evidence.EpisodeKey)
        }
        // 5. candidate ID is unchanged
        test "candidate_id is unchanged" {
            let result = extractCandidates (repoRoot ())

            Expect.isEmpty result.Errors (sprintf "errors: %A" result.Errors)
            let candidate = result.Candidates.Head
            Expect.equal
                candidate.CandidateId
                expectedCandidateId
                (sprintf "candidate_id must equal %s, got %s" expectedCandidateId candidate.CandidateId)
        }
        // 6. read-only verification reports byte-identical canonical artifacts
        test "read-only verification: canonical artifacts byte-identical before and after" {
            let paths = canonicalArtifacts ()

            // Hash BEFORE calling runReadOnlyVerify
            let hashesBefore =
                paths
                |> List.map sha256OfFile
            // Invoke the read-only verifier
            let verdict, byteIdentical = runReadOnlyVerify (repoRoot ())
            // Hash AFTER calling runReadOnlyVerify
            let hashesAfter =
                paths
                |> List.map sha256OfFile

            Expect.equal
                (sprintf "%A" verdict)
                "Verified"
                "verifier must report VERIFIED"
            Expect.isTrue byteIdentical "runReadOnlyVerify must report byteIdentical = true"

            // Assert byte-equality of every canonical artifact.
            for (path, before, after) in List.zip3 paths hashesBefore hashesAfter do
                Expect.equal before after (sprintf "artifact %s must be byte-identical before and after verify" path)

            // Cross-check: combined hash before vs combined hash after
            let combinedBefore = String.concat ":" hashesBefore
            let combinedAfter = String.concat ":" hashesAfter
            Expect.equal combinedBefore combinedAfter "combined canonical hash before vs after must match"
        }
    ]
