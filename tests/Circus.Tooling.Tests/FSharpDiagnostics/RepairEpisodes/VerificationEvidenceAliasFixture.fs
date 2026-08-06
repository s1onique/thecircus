module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceAliasFixture

// =============================================================================
// Verification Evidence Alias Test Fixtures
//
// Shared fixtures and helpers for alias parser matrix tests.
// Restored for ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01.
// =============================================================================

open System
open System.IO
open System.Security.Cryptography
open System.Text

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths
open Circus.Tooling.FSharpDiagnostics.Paths

// -----------------------------------------------------------------------------
// Test Data Constants
// -----------------------------------------------------------------------------

/// Valid 40-character commit OID
let validCommitOid = String.replicate 40 "a"

/// Valid 40-character tree OID
let validTreeOid = String.replicate 40 "a"

/// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01:
/// Spec §13 - deterministic evidence ID generation for test fixtures.
/// The framing is:
///   sha256( UTF8("verification-evidence-alias-fixture-v1") + NUL + UTF8(testCaseKey) )
/// This guarantees:
///   * output length is exactly 64 lowercase hexadecimal characters
///   * the same test_case_key always produces the same ID
///   * different test_case_key values produce different IDs
///   * the result is independent of any global counter, timestamp, GUID, or filesystem path
let evidenceId (testCaseKey: string) : string =
    let prefix = Encoding.UTF8.GetBytes "verification-evidence-alias-fixture-v1"
    let nul = [| byte 0 |]
    let keyBytes = Encoding.UTF8.GetBytes testCaseKey

    use h = SHA256.Create()
    let hash = h.ComputeHash(Array.concat [ prefix; nul; keyBytes ])

    hash
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""
    |> fun s -> if s.Length <> 64 then failwithf "deterministicEvidenceId produced %d chars, expected 64" s.Length else s

// -----------------------------------------------------------------------------
// Directory Helpers
// -----------------------------------------------------------------------------

let tempDir (label: string) =
    let dir = Path.Combine(Path.GetTempPath(), label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let cleanup (dir: string) =
    try if Directory.Exists dir then Directory.Delete(dir, true) with _ -> ()

let createMinimalStructure (dir: string) =
    let declDir = Path.Combine(dir, canonicalRootRelative, "corpus", "episodes", "declarations")
    let capDir = Path.Combine(dir, canonicalRootRelative, "corpus", "captures")
    Directory.CreateDirectory declDir |> ignore
    Directory.CreateDirectory capDir |> ignore

// -----------------------------------------------------------------------------
// Evidence File Helpers
// -----------------------------------------------------------------------------

let writeEvidence (dir: string) (records: string list) =
    let path = Path.Combine(dir, verificationEvidenceCanonicalPath)
    let evidenceDir = Path.GetDirectoryName(path)
    if not (Directory.Exists evidenceDir) then Directory.CreateDirectory(evidenceDir) |> ignore
    File.WriteAllLines(path, records)

/// Run verification pipeline on a directory
let runVerify (dir: string) : VerificationResult =
    verifyPipeline dir defaultEngineOptions
