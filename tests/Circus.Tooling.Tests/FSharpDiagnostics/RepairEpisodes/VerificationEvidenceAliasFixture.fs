module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceAliasFixture

// =============================================================================
// Verification Evidence Alias Test Fixtures
//
// Shared fixtures and helpers for alias parser matrix tests.
// =============================================================================

open System
open System.IO

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

/// Generate a unique valid 64-character SHA-256 evidence ID
let evidenceId (suffix: string) =
    let base60 = "000100020003000400050006000700080009000a000b000c000d000e000f"
    base60 + suffix

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
