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

/// Valid 64-character hexadecimal evidence ID for SHA-256
let private validEvidenceId =
    "000100020003000400050006000700080009000a000b000c000d000e000f0010"

/// Valid 40-character commit OID
let private validCommitOid = String.replicate 40 "a"

/// Valid 40-character tree OID
let private validTreeOid = String.replicate 40 "a"

/// Valid SHA-256 hash (64 hex chars)
let private validSha256 =
    "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"

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
// Base Evidence Record Builders
// -----------------------------------------------------------------------------

/// Build a minimal valid evidence record with all required fields.
let private baseEvidenceRecord (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s","stdout_sha256":"%s","stderr_sha256":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid
        validSha256
        validSha256

// -----------------------------------------------------------------------------
// Alias Matrix Test Cases
// -----------------------------------------------------------------------------

/// Test Case: Neither canonical nor alias field present => OK (field uses alias resolution)
let private evidenceNeitherPresent (evId: string) (epId: string) : string =
    // Neither "kind" nor "verification_kind" present - should fail with MissingField
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Both canonical and alias present with same valid value => DuplicateSemanticField
let private evidenceBothSameValue (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","verification_kind":"build","command":"dotnet build","verification_command":"dotnet build","status":"pass","verification_result":"pass","exit_code":0,"verification_exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Both canonical and alias present with different valid values => ConflictingSemanticFields
let private evidenceBothDifferentValues (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","verification_kind":"test","command":"dotnet build","verification_command":"dotnet test","status":"pass","verification_result":"fail","exit_code":0,"verification_exit_code":1,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Canonical present with wrong type, alias valid => WrongFieldType for canonical
let private evidenceCanonicalWrongTypeAliasValid (evId: string) (epId: string) : string =
    // "status" is number instead of string, "verification_result" is valid
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":123,"verification_result":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Canonical valid, alias wrong type => WrongFieldType for alias
let private evidenceCanonicalValidAliasWrongType (evId: string) (epId: string) : string =
    // "status" is valid, "verification_result" is number instead of string
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":"pass","verification_result":456,"exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Both wrong type (string fields) => WrongFieldType with canonical's actual type
let private evidenceBothWrongTypeStrings (evId: string) (epId: string) : string =
    // Both "status" and "verification_result" are numbers
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":111,"verification_result":222,"exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Canonical wrong type, alias missing => WrongFieldType (from canonical)
let private evidenceCanonicalWrongTypeAliasMissing (evId: string) (epId: string) : string =
    // "status" is number, no "verification_result"
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":999,"exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

// -----------------------------------------------------------------------------
// Integer Field Tests
// -----------------------------------------------------------------------------

/// Test Case: Both wrong type (integer fields) => WrongFieldType with "integer" expected
let private evidenceBothWrongTypeIntegers (evId: string) (epId: string) : string =
    // Both "exit_code" and "verification_exit_code" are strings
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":"pass","exit_code":"zero","verification_exit_code":"one","tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Canonical valid, alias fractional => InvalidExitCode
let private evidenceCanonicalValidAliasFractional (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":"pass","exit_code":0,"verification_exit_code":1.5,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Both fractional integers => InvalidExitCode with canonical's value
let private evidenceBothFractionalIntegers (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":"pass","exit_code":2.5,"verification_exit_code":3.5,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Exit code out of Int32 range => InvalidExitCode
let private evidenceExitCodeOutOfRange (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":"pass","exit_code":9999999999,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

/// Test Case: Negative exit code => InvalidExitCode
let private evidenceNegativeExitCode (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":"pass","exit_code":-1,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

// -----------------------------------------------------------------------------
// Test List
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "VerificationEvidenceAlias"
        [
          // ===================================================================
          // String Field Tests: kind / verification_kind
          // ===================================================================

          test "canonical only present => OK" {
              let dir = tempDir "alias-canonical-only"
              let evId = validEvidenceId + "0001"

              try
                  createMinimalStructure dir
                  // Use alias field only (verification_kind)
                  let record =
                      sprintf
                          """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-001","verification_kind":"build","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
                          evId
                          validCommitOid
                          validTreeOid

                  writeEvidence dir [ record ]
                  let vr = runVerify dir

                  // Should succeed (no parse errors)
                  let hasParseErrors =
                      vr.Issues
                      |> List.exists (function
                          | VerificationIssue.VerificationEvidenceLoadFailed _ -> true
                          | _ -> false)

                  Expect.isFalse hasParseErrors "alias-only field should parse successfully"
              finally
                  cleanup dir
          }

          test "alias only present => OK" {
              let dir = tempDir "alias-alias-only"
              let evId = validEvidenceId + "0002"

              try
                  createMinimalStructure dir
                  // Use canonical field only (kind)
                  let record =
                      sprintf
                          """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-001","kind":"build","command":"dotnet build","status":"pass","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
                          evId
                          validCommitOid
                          validTreeOid

                  writeEvidence dir [ record ]
                  let vr = runVerify dir

                  // Should succeed (no parse errors)
                  let hasParseErrors =
                      vr.Issues
                      |> List.exists (function
                          | VerificationIssue.VerificationEvidenceLoadFailed _ -> true
                          | _ -> false)

                  Expect.isFalse hasParseErrors "canonical-only field should parse successfully"
              finally
                  cleanup dir
          }

          test "both same value => DuplicateSemanticField error" {
              let dir = tempDir "alias-both-same"
              let evId = validEvidenceId + "0003"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceBothSameValue evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      let hasDuplicate =
                          errors
                          |> List.exists (function
                              | VerificationEvidenceLoadError.ParseError(
                                  VerificationEvidenceParseError.DuplicateSemanticField _) -> true
                              | _ -> false)

                      Expect.isTrue hasDuplicate "should have DuplicateSemanticField error"
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "both different values => ConflictingSemanticFields error" {
              let dir = tempDir "alias-both-different"
              let evId = validEvidenceId + "0004"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceBothDifferentValues evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      let hasConflict =
                          errors
                          |> List.exists (function
                              | VerificationEvidenceLoadError.ParseError(
                                  VerificationEvidenceParseError.ConflictingSemanticFields _) -> true
                              | _ -> false)

                      Expect.isTrue hasConflict "should have ConflictingSemanticFields error"
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          // ===================================================================
          // Both Wrong Type Tests (THE KEY TEST CASES)
          // ===================================================================

          test "string fields both wrong type => WrongFieldType with canonical's actual type" {
              let dir = tempDir "alias-both-wrong-strings"
              let evId = validEvidenceId + "0005"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceBothWrongTypeStrings evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.WrongFieldType(
                                  _,
                                  _,
                                  fieldName,
                                  expected,
                                  actual)) ] ->
                          // The field should be "status" (canonical name)
                          Expect.equal fieldName "status" "field name should be canonical"
                          // Expected should be "string" (canonical's expected type)
                          Expect.equal expected "string" "expected type should be 'string'"
                          // Actual should be "number" (canonical's actual type)
                          Expect.equal actual "number" "actual type should be 'number'"
                      | _ -> failwithf "expected WrongFieldType error, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "integer fields both wrong type => WrongFieldType with 'integer' expected" {
              let dir = tempDir "alias-both-wrong-integers"
              let evId = validEvidenceId + "0006"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceBothWrongTypeIntegers evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.WrongFieldType(
                                  _,
                                  _,
                                  fieldName,
                                  expected,
                                  actual)) ] ->
                          // The field should be "exit_code" (canonical name)
                          Expect.equal fieldName "exit_code" "field name should be canonical"
                          // Expected should be "integer"
                          Expect.equal expected "integer" "expected type should be 'integer'"
                          // Actual should be "string" (canonical's actual JSON type)
                          Expect.equal actual "string" "actual type should be 'string'"
                      | _ -> failwithf "expected WrongFieldType error, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "canonical wrong type alias valid => WrongFieldType for canonical field" {
              let dir = tempDir "alias-canonical-wrong"
              let evId = validEvidenceId + "0007"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceCanonicalWrongTypeAliasValid evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.WrongFieldType(
                                  _,
                                  _,
                                  fieldName,
                                  expected,
                                  actual)) ] ->
                          // The field should be "status" (canonical name)
                          Expect.equal fieldName "status" "field name should be canonical"
                          Expect.equal expected "string" "expected type should be 'string'"
                          Expect.equal actual "number" "actual type should be 'number'"
                      | _ -> failwithf "expected WrongFieldType error, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "canonical valid alias wrong type => WrongFieldType for alias field" {
              let dir = tempDir "alias-alias-wrong"
              let evId = validEvidenceId + "0008"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceCanonicalValidAliasWrongType evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.WrongFieldType(
                                  _,
                                  _,
                                  fieldName,
                                  expected,
                                  actual)) ] ->
                          // The field should be "verification_result" (alias name) because alias is wrong
                          Expect.equal fieldName "verification_result" "field name should be alias"
                          Expect.equal expected "string" "expected type should be 'string'"
                          Expect.equal actual "number" "actual type should be 'number'"
                      | _ -> failwithf "expected WrongFieldType error, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          // ===================================================================
          // Integer Field Tests
          // ===================================================================

          test "canonical valid alias fractional => InvalidExitCode" {
              let dir = tempDir "alias-fractional-exit"
              let evId = validEvidenceId + "0009"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceCanonicalValidAliasFractional evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.InvalidExitCode _) ] -> ()
                      | _ -> failwithf "expected InvalidExitCode error, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "both fractional integers => InvalidExitCode" {
              let dir = tempDir "alias-both-fractional"
              let evId = validEvidenceId + "000a"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceBothFractionalIntegers evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.InvalidExitCode _) ] -> ()
                      | _ -> failwithf "expected InvalidExitCode error, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "exit code out of Int32 range => InvalidExitCode" {
              let dir = tempDir "alias-exit-out-of-range"
              let evId = validEvidenceId + "000b"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceExitCodeOutOfRange evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.InvalidExitCode _) ] -> ()
                      | _ -> failwithf "expected InvalidExitCode error, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          test "negative exit code => InvalidExitCode" {
              let dir = tempDir "alias-negative-exit"
              let evId = validEvidenceId + "000c"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceNegativeExitCode evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.InvalidExitCode _) ] -> ()
                      | _ -> failwithf "expected InvalidExitCode error, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }

          // ===================================================================
          // Canonical Wrong Type, Alias Missing Test
          // ===================================================================

          test "canonical wrong type alias missing => WrongFieldType" {
              let dir = tempDir "alias-canonical-wrong-missing"
              let evId = validEvidenceId + "000d"

              try
                  createMinimalStructure dir
                  writeEvidence dir [ evidenceCanonicalWrongTypeAliasMissing evId "ep-001" ]
                  let vr = runVerify dir

                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError(
                              VerificationEvidenceParseError.WrongFieldType(
                                  _,
                                  _,
                                  fieldName,
                                  expected,
                                  actual)) ] ->
                          Expect.equal fieldName "status" "field name should be canonical"
                          Expect.equal expected "string" "expected type should be 'string'"
                          Expect.equal actual "number" "actual type should be 'number'"
                      | _ -> failwithf "expected WrongFieldType error, got %A" errors
                  | issues -> failwithf "expected VerificationEvidenceLoadFailed, got %A" issues
              finally
                  cleanup dir
          }
        ]
