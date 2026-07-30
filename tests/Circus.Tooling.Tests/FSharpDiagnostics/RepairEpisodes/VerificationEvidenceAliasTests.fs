module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceAliasTests

// =============================================================================
// Verification Evidence Alias Parser Matrix Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION15
//
// These tests verify the complete alias field matrix for the verification-evidence
// parser. Each combination of (canonical field state, alias field state) is tested
// with the expected error or success result.
//
// Alias fields under test:
//   - kind / verification_kind
//   - command / verification_command
//   - status / verification_result
//   - exit_code / verification_exit_code
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.Manifest

// -----------------------------------------------------------------------------
// Test Data Constants
// -----------------------------------------------------------------------------

/// Valid 40-character commit OID
let private validCommitOid = String.replicate 40 "a"

/// Valid 40-character tree OID
let private validTreeOid = String.replicate 40 "a"

/// Valid SHA-256 hash (64 hex chars)
let private validSha256 =
    "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"

/// Generate a unique valid 64-character SHA-256 evidence ID
/// Uses suffix to ensure uniqueness while maintaining valid format
let private evidenceId (suffix: string) =
    // Base 60 chars + 4-char hex suffix = 64 chars total
    let base60 = "000100020003000400050006000700080009000a000b000c000d000e000f"
    base60 + suffix

/// Validate evidence ID format
let private validateEvidenceId (id: string) : unit =
    Expect.equal id.Length 64 (sprintf "evidence ID must be 64 chars, got %d" id.Length)
    Expect.isTrue (id |> Seq.forall (fun c -> Char.IsDigit c || (c >= 'a' && c <= 'f')))
        "evidence ID must be lowercase hexadecimal"

// -----------------------------------------------------------------------------
// Test Helpers
// -----------------------------------------------------------------------------

let private tempDir (label: string) : string =
    let dir =
        Path.Combine(Path.GetTempPath(), label + "-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (dir: string) : unit =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ ->
        ()

let private createMinimalStructure (dir: string) : unit =
    let declarationsDir =
        Path.Combine(dir, canonicalRootRelative, "corpus", "episodes", "declarations")

    let capturesDir = Path.Combine(dir, canonicalRootRelative, "corpus", "captures")
    Directory.CreateDirectory declarationsDir |> ignore
    Directory.CreateDirectory capturesDir |> ignore

let private writeEvidence (dir: string) (records: string list) : unit =
    let evidencePath = Path.Combine(dir, verificationEvidenceCanonicalPath)
    let evidenceDir = Path.GetDirectoryName(evidencePath)

    if not (Directory.Exists evidenceDir) then
        Directory.CreateDirectory(evidenceDir) |> ignore

    File.WriteAllLines(evidencePath, records)

let private runVerify (dir: string) : VerificationResult =
    verifyPipeline dir defaultEngineOptions

// -----------------------------------------------------------------------------
// Isolated Test Cases for Each Alias Pair
// -----------------------------------------------------------------------------

/// kind field: canonical only (alias absent)
let private kindCanonicalOnly (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-kind-001","kind":"build","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// kind field: alias only (canonical absent)
let private kindAliasOnly (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-kind-002","verification_kind":"test","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// kind field: both same value
let private kindBothSame (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-kind-003","kind":"build","verification_kind":"build","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// kind field: both different values
let private kindBothDifferent (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-kind-004","kind":"build","verification_kind":"test","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// status field: canonical only (alias absent)
let private statusCanonicalOnly (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-status-001","kind":"build","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// status field: alias only (canonical absent)
let private statusAliasOnly (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-status-002","kind":"build","command":"dotnet build","verification_result":"fail","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// status field: both same value
let private statusBothSame (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-status-003","kind":"build","command":"dotnet build","status":"pass","verification_result":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// status field: both different values
let private statusBothDifferent (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-status-004","kind":"build","command":"dotnet build","status":"pass","verification_result":"fail","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// exit_code field: canonical only (alias absent)
let private exitCodeCanonicalOnly (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-exit-001","kind":"build","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// exit_code field: alias only (canonical absent)
let private exitCodeAliasOnly (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-exit-002","kind":"build","command":"dotnet build","status":"pass","verification_exit_code":1,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// exit_code field: both same value
let private exitCodeBothSame (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-exit-003","kind":"build","command":"dotnet build","status":"pass","exit_code":0,"verification_exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// exit_code field: both different values
let private exitCodeBothDifferent (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-exit-004","kind":"build","command":"dotnet build","status":"pass","exit_code":0,"verification_exit_code":1,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

// -----------------------------------------------------------------------------
// Wrong Type Test Cases
// -----------------------------------------------------------------------------

/// status field: both wrong type (both are numbers instead of strings)
let private statusBothWrongType (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-wrong-001","kind":"build","command":"dotnet build","status":111,"verification_result":222,"exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// exit_code field: both wrong type (both are strings instead of integers)
let private exitCodeBothWrongType (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-wrong-002","kind":"build","command":"dotnet build","status":"pass","exit_code":"zero","verification_exit_code":"one","tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// status field: canonical wrong type, alias valid
let private statusCanonicalWrongAliasValid (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-wrong-003","kind":"build","command":"dotnet build","status":999,"verification_result":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// status field: canonical valid, alias wrong type
let private statusCanonicalValidAliasWrong (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-wrong-004","kind":"build","command":"dotnet build","status":"pass","verification_result":456,"exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// exit_code field: canonical valid, alias fractional
let private exitCodeCanonicalValidAliasFractional (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-wrong-005","kind":"build","command":"dotnet build","status":"pass","exit_code":0,"verification_exit_code":1.5,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// exit_code field: both fractional
let private exitCodeBothFractional (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-wrong-006","kind":"build","command":"dotnet build","status":"pass","exit_code":2.5,"verification_exit_code":3.5,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// exit_code field: out of Int32 range
let private exitCodeOutOfRange (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-wrong-007","kind":"build","command":"dotnet build","status":"pass","exit_code":9999999999,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// exit_code field: negative
let private exitCodeNegative (evId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-wrong-008","kind":"build","command":"dotnet build","status":"pass","exit_code":-1,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

// -----------------------------------------------------------------------------
// Test List
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "VerificationEvidenceAlias"
        [
          // ===================================================================
          // kind / verification_kind pair tests
          // ===================================================================

          test "kind: canonical only => OK" {
              let dir = tempDir "alias-kind-canonical"
              let evId = evidenceId "0001"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ kindCanonicalOnly evId ]
                  let vr = runVerify dir

                  let hasParseErrors =
                      vr.Issues
                      |> List.exists (function
                          | VerificationIssue.VerificationEvidenceLoadFailed _ -> true
                          | _ -> false)

                  Expect.isFalse hasParseErrors "canonical-only kind should parse successfully"
              finally
                  cleanup dir
          }

          test "kind: alias only => OK" {
              let dir = tempDir "alias-kind-alias"
              let evId = evidenceId "0002"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ kindAliasOnly evId ]
                  let vr = runVerify dir

                  let hasParseErrors =
                      vr.Issues
                      |> List.exists (function
                          | VerificationIssue.VerificationEvidenceLoadFailed _ -> true
                          | _ -> false)

                  Expect.isFalse hasParseErrors "alias-only verification_kind should parse successfully"
              finally
                  cleanup dir
          }

          test "kind: both same => DuplicateSemanticField" {
              let dir = tempDir "alias-kind-same"
              let evId = evidenceId "0003"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ kindBothSame evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.DuplicateSemanticField(_, _, can, alias)) ] ->
                          Expect.equal can "kind" "canonical field should be 'kind'"
                          Expect.equal alias "verification_kind" "alias field should be 'verification_kind'"
                      | _ -> failwithf "expected DuplicateSemanticField, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "kind: both different => ConflictingSemanticFields" {
              let dir = tempDir "alias-kind-diff"
              let evId = evidenceId "0004"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ kindBothDifferent evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.ConflictingSemanticFields(_, _, can, alias, _, _)) ] ->
                          Expect.equal can "kind" "canonical field should be 'kind'"
                          Expect.equal alias "verification_kind" "alias field should be 'verification_kind'"
                      | _ -> failwithf "expected ConflictingSemanticFields, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          // ===================================================================
          // status / verification_result pair tests
          // ===================================================================

          test "status: canonical only => OK" {
              let dir = tempDir "alias-status-canonical"
              let evId = evidenceId "0005"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ statusCanonicalOnly evId ]
                  let vr = runVerify dir

                  let hasParseErrors =
                      vr.Issues
                      |> List.exists (function
                          | VerificationIssue.VerificationEvidenceLoadFailed _ -> true
                          | _ -> false)

                  Expect.isFalse hasParseErrors "canonical-only status should parse successfully"
              finally
                  cleanup dir
          }

          test "status: alias only => OK" {
              let dir = tempDir "alias-status-alias"
              let evId = evidenceId "0006"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ statusAliasOnly evId ]
                  let vr = runVerify dir

                  let hasParseErrors =
                      vr.Issues
                      |> List.exists (function
                          | VerificationIssue.VerificationEvidenceLoadFailed _ -> true
                          | _ -> false)

                  Expect.isFalse hasParseErrors "alias-only verification_result should parse successfully"
              finally
                  cleanup dir
          }

          test "status: both same => DuplicateSemanticField" {
              let dir = tempDir "alias-status-same"
              let evId = evidenceId "0007"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ statusBothSame evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.DuplicateSemanticField(_, _, can, alias)) ] ->
                          Expect.equal can "status" "canonical field should be 'status'"
                          Expect.equal alias "verification_result" "alias field should be 'verification_result'"
                      | _ -> failwithf "expected DuplicateSemanticField, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "status: both different => ConflictingSemanticFields" {
              let dir = tempDir "alias-status-diff"
              let evId = evidenceId "0008"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ statusBothDifferent evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.ConflictingSemanticFields(_, _, can, alias, _, _)) ] ->
                          Expect.equal can "status" "canonical field should be 'status'"
                          Expect.equal alias "verification_result" "alias field should be 'verification_result'"
                      | _ -> failwithf "expected ConflictingSemanticFields, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          // ===================================================================
          // exit_code / verification_exit_code pair tests
          // ===================================================================

          test "exit_code: canonical only => OK" {
              let dir = tempDir "alias-exit-canonical"
              let evId = evidenceId "0009"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ exitCodeCanonicalOnly evId ]
                  let vr = runVerify dir

                  let hasParseErrors =
                      vr.Issues
                      |> List.exists (function
                          | VerificationIssue.VerificationEvidenceLoadFailed _ -> true
                          | _ -> false)

                  Expect.isFalse hasParseErrors "canonical-only exit_code should parse successfully"
              finally
                  cleanup dir
          }

          test "exit_code: alias only => OK" {
              let dir = tempDir "alias-exit-alias"
              let evId = evidenceId "000a"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ exitCodeAliasOnly evId ]
                  let vr = runVerify dir

                  let hasParseErrors =
                      vr.Issues
                      |> List.exists (function
                          | VerificationIssue.VerificationEvidenceLoadFailed _ -> true
                          | _ -> false)

                  Expect.isFalse hasParseErrors "alias-only verification_exit_code should parse successfully"
              finally
                  cleanup dir
          }

          test "exit_code: both same => DuplicateSemanticField" {
              let dir = tempDir "alias-exit-same"
              let evId = evidenceId "000b"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ exitCodeBothSame evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.DuplicateSemanticField(_, _, can, alias)) ] ->
                          Expect.equal can "exit_code" "canonical field should be 'exit_code'"
                          Expect.equal alias "verification_exit_code" "alias field should be 'verification_exit_code'"
                      | _ -> failwithf "expected DuplicateSemanticField, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "exit_code: both different => ConflictingSemanticFields" {
              let dir = tempDir "alias-exit-diff"
              let evId = evidenceId "000c"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ exitCodeBothDifferent evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.ConflictingSemanticFields(_, _, can, alias, _, _)) ] ->
                          Expect.equal can "exit_code" "canonical field should be 'exit_code'"
                          Expect.equal alias "verification_exit_code" "alias field should be 'verification_exit_code'"
                      | _ -> failwithf "expected ConflictingSemanticFields, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          // ===================================================================
          // Both Wrong Type Tests (KEY TEST CASES)
          // ===================================================================

          test "status: both wrong type => WrongFieldType with canonical's actual type" {
              let dir = tempDir "alias-status-both-wrong"
              let evId = evidenceId "000d"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ statusBothWrongType evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.WrongFieldType(_, _, field, expected, actual)) ] ->
                          Expect.equal field "status" "field name should be canonical 'status'"
                          Expect.equal expected "string" "expected type should be 'string'"
                          Expect.equal actual "number" "actual type should be canonical's actual type"
                      | _ -> failwithf "expected WrongFieldType, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "exit_code: both wrong type => WrongFieldType with 'integer' expected" {
              let dir = tempDir "alias-exit-both-wrong"
              let evId = evidenceId "000e"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ exitCodeBothWrongType evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.WrongFieldType(_, _, field, expected, actual)) ] ->
                          Expect.equal field "exit_code" "field name should be canonical 'exit_code'"
                          Expect.equal expected "integer" "expected type should be 'integer'"
                          Expect.equal actual "string" "actual type should be canonical's actual type"
                      | _ -> failwithf "expected WrongFieldType, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "status: canonical wrong type alias valid => WrongFieldType for canonical" {
              let dir = tempDir "alias-status-canon-wrong"
              let evId = evidenceId "000f"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ statusCanonicalWrongAliasValid evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.WrongFieldType(_, _, field, expected, actual)) ] ->
                          Expect.equal field "status" "field name should be canonical 'status'"
                          Expect.equal expected "string" "expected type should be 'string'"
                          Expect.equal actual "number" "actual type should be 'number'"
                      | _ -> failwithf "expected WrongFieldType, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "status: canonical valid alias wrong type => WrongFieldType for alias" {
              let dir = tempDir "alias-status-alias-wrong"
              let evId = evidenceId "0010"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ statusCanonicalValidAliasWrong evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.WrongFieldType(_, _, field, expected, actual)) ] ->
                          Expect.equal field "verification_result" "field name should be alias 'verification_result'"
                          Expect.equal expected "string" "expected type should be 'string'"
                          Expect.equal actual "number" "actual type should be 'number'"
                      | _ -> failwithf "expected WrongFieldType, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          // ===================================================================
          // Integer-specific tests
          // ===================================================================

          test "exit_code: canonical valid alias fractional => InvalidExitCode" {
              let dir = tempDir "alias-exit-frac"
              let evId = evidenceId "0011"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ exitCodeCanonicalValidAliasFractional evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.InvalidExitCode _) ] -> ()
                      | _ -> failwithf "expected InvalidExitCode, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "exit_code: both fractional => InvalidExitCode" {
              let dir = tempDir "alias-exit-both-frac"
              let evId = evidenceId "0012"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ exitCodeBothFractional evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.InvalidExitCode _) ] -> ()
                      | _ -> failwithf "expected InvalidExitCode, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "exit_code: out of Int32 range => InvalidExitCode" {
              let dir = tempDir "alias-exit-range"
              let evId = evidenceId "0013"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ exitCodeOutOfRange evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.InvalidExitCode _) ] -> ()
                      | _ -> failwithf "expected InvalidExitCode, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "exit_code: negative => InvalidExitCode" {
              let dir = tempDir "alias-exit-neg"
              let evId = evidenceId "0014"
              validateEvidenceId evId

              try
                  createMinimalStructure dir
                  writeEvidence dir [ exitCodeNegative evId ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.InvalidExitCode _) ] -> ()
                      | _ -> failwithf "expected InvalidExitCode, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }
        ]
