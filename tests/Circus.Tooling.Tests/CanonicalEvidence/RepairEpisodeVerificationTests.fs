module Circus.Tooling.Tests.CanonicalEvidence.RepairEpisodeVerificationTests

// =============================================================================
// Verification evidence loading tests for the repair-episode linker
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION02
//
// These tests verify that evidence loading failures are correctly identified
// and categorized, with exact error patterns preserved and exposed through the CLI.
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Cli

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

/// Valid 64-character hexadecimal evidence ID for SHA-256 (all hex chars)
let private validEvidenceId = "000100020003000400050006000700080009000a000b000c000d000e000f0010"

/// Valid 40-character commit OID
let private validCommitOid = String.replicate 40 "a"

/// Valid 40-character tree OID
let private validTreeOid = String.replicate 40 "a"

/// Create a valid verification evidence record
let private validEvidenceRecord (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"%s","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId epId validCommitOid validTreeOid

/// Build an evidence record without verification_kind
let private evidenceRecordNoKind (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"%s","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId epId validCommitOid validTreeOid

/// Build an evidence record with invalid tree OID
let private evidenceRecordInvalidTreeOid (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"%s","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"not-a-valid-oid-123456789012345678901234"}"""
        evId epId validCommitOid

let private tempDir (label: string) : string =
    let dir = Path.Combine(Path.GetTempPath(), label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (dir: string) : unit =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ -> ()

/// Create minimal directory structure needed by the repair-episode engine.
let private createMinimalStructure (dir: string) : unit =
    let declarationsDir = Path.Combine(dir, canonicalRootRelative, "corpus", "episodes", "declarations")
    let capturesDir = Path.Combine(dir, canonicalRootRelative, "corpus", "captures")
    Directory.CreateDirectory declarationsDir |> ignore
    Directory.CreateDirectory capturesDir |> ignore

/// Write verification evidence to the canonical path
let private writeEvidence (dir: string) (records: string list) : unit =
    let evidencePath = Path.Combine(dir, verificationEvidenceCanonicalPath)
    let evidenceDir = Path.GetDirectoryName(evidencePath)
    if not (Directory.Exists evidenceDir) then
        Directory.CreateDirectory(evidenceDir) |> ignore
    File.WriteAllLines(evidencePath, records)

/// Run verifyPipeline and return result
let private runVerify (dir: string) : VerificationResult =
    verifyPipeline dir defaultEngineOptions

// -----------------------------------------------------------------------------
// Test list
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "RepairEpisodeVerification"
        [
          // Test 1: missing verification-evidence file
          test "missing evidence file => VerificationEvidenceLoadFailed" {
              let dir = tempDir "verify-missing-evidence"
              try
                  createMinimalStructure dir
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.EvidenceFileMissing _ ] -> ()
                      | _ -> failwithf "expected EvidenceFileMissing, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 2: malformed JSON on first record
          test "malformed JSON first record => malformed_json error" {
              let dir = tempDir "verify-malformed-first"
              try
                  createMinimalStructure dir
                  writeEvidence dir [ """{"schema""" ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError e ] ->
                          match e with
                          | VerificationEvidenceParseError.MalformedJson _ -> ()
                          | _ -> failwithf "expected MalformedJson error, got %A" e
                      | _ -> failwithf "expected ParseError, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 3: malformed JSON after valid record
          test "malformed JSON after valid => malformed_json error" {
              let dir = tempDir "verify-malformed-after-valid"
              try
                  createMinimalStructure dir
                  let valid = validEvidenceRecord validEvidenceId "ep-001"
                  writeEvidence dir [ valid; """{"schema""" ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      let hasMalformed = errors |> List.exists (function
                          | VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.MalformedJson _) -> true
                          | _ -> false)
                      Expect.isTrue hasMalformed "should have MalformedJson error"
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 4: unsupported schema version
          test "unsupported schema version => unsupported_schema error" {
              let dir = tempDir "verify-unsupported-schema"
              try
                  createMinimalStructure dir
                  let bad = validEvidenceRecord validEvidenceId "ep-001"
                              |> fun s -> s.Replace("verification-evidence-v1", "verification-evidence-v99")
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError e ] ->
                          match e with
                          | VerificationEvidenceParseError.UnsupportedSchemaVersion _ -> ()
                          | _ -> failwithf "expected UnsupportedSchemaVersion error, got %A" e
                      | _ -> failwithf "expected ParseError, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 5: missing required field (verification_kind)
          test "missing required field => missing_field error" {
              let dir = tempDir "verify-missing-field"
              try
                  createMinimalStructure dir
                  let bad = evidenceRecordNoKind validEvidenceId "ep-001"
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError e ] ->
                          match e with
                          | VerificationEvidenceParseError.MissingField _ -> ()
                          | _ -> failwithf "expected MissingField error, got %A" e
                      | _ -> failwithf "expected ParseError, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 6: unknown verification kind
          test "unknown verification kind => unknown_verification_kind error" {
              let dir = tempDir "verify-unknown-kind"
              try
                  createMinimalStructure dir
                  let bad = validEvidenceRecord validEvidenceId "ep-001"
                              |> fun s -> s.Replace("build", "super_gate")
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError e ] ->
                          match e with
                          | VerificationEvidenceParseError.UnknownVerificationKind _ -> ()
                          | _ -> failwithf "expected UnknownVerificationKind error, got %A" e
                      | _ -> failwithf "expected ParseError, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 7: unknown verification result
          test "unknown verification result => unknown_verification_status error" {
              let dir = tempDir "verify-unknown-status"
              try
                  createMinimalStructure dir
                  let bad = validEvidenceRecord validEvidenceId "ep-001"
                              |> fun s -> s.Replace("pass", "maybe")
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError e ] ->
                          match e with
                          | VerificationEvidenceParseError.UnknownVerificationStatus _ -> ()
                          | _ -> failwithf "expected UnknownVerificationStatus error, got %A" e
                      | _ -> failwithf "expected ParseError, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 8: invalid exit code (negative)
          test "negative exit code => invalid_exit_code error" {
              let dir = tempDir "verify-invalid-exit"
              try
                  createMinimalStructure dir
                  let bad = validEvidenceRecord validEvidenceId "ep-001"
                              |> fun s -> s.Replace("verification_exit_code\":0", "verification_exit_code\":-1")
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError e ] ->
                          match e with
                          | VerificationEvidenceParseError.InvalidExitCode _ -> ()
                          | _ -> failwithf "expected InvalidExitCode error, got %A" e
                      | _ -> failwithf "expected ParseError, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 9: invalid commit OID
          test "invalid commit OID => invalid_commit_oid error" {
              let dir = tempDir "verify-invalid-commit"
              try
                  createMinimalStructure dir
                  let bad = validEvidenceRecord validEvidenceId "ep-001"
                              |> fun s -> s.Replace(validCommitOid, "not-a-valid-oid-123456789012345678901234")
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError e ] ->
                          match e with
                          | VerificationEvidenceParseError.InvalidCommitOid _ -> ()
                          | _ -> failwithf "expected InvalidCommitOid error, got %A" e
                      | _ -> failwithf "expected ParseError, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 10: invalid tree OID
          test "invalid tree OID => invalid_tree_oid error" {
              let dir = tempDir "verify-invalid-tree"
              try
                  createMinimalStructure dir
                  let bad = evidenceRecordInvalidTreeOid validEvidenceId "ep-001"
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError e ] ->
                          match e with
                          | VerificationEvidenceParseError.InvalidTreeOid _ -> ()
                          | _ -> failwithf "expected InvalidTreeOid error, got %A" e
                      | _ -> failwithf "expected ParseError, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 11: duplicate evidence ID
          test "duplicate evidence ID => duplicate_evidence_id error" {
              let dir = tempDir "verify-duplicate-id"
              try
                  createMinimalStructure dir
                  let rec1 = validEvidenceRecord validEvidenceId "ep-001"
                  let rec2 = validEvidenceRecord validEvidenceId "ep-002"
                  writeEvidence dir [ rec1; rec2 ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.DuplicateEvidenceId _ ] -> ()
                      | _ -> failwithf "expected DuplicateEvidenceId error, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 12: CLI rendering produces human-readable output
          test "renderVerificationEvidenceLoadIssues produces readable output" {
              let errors =
                  [ VerificationEvidenceLoadError.EvidenceFileMissing "/path/to/evidence.jsonl" ]
              let rendered = renderVerificationEvidenceLoadIssues errors
              Expect.stringContains rendered "evidence_file_missing" "should contain error type"
              Expect.stringContains rendered "/path/to/evidence.jsonl" "should contain path"
          }

          // Test 13: CLI rendering handles malformed JSON
          test "renderVerificationEvidenceLoadIssues handles malformed JSON" {
              let errors =
                  [ VerificationEvidenceLoadError.ParseError(
                      VerificationEvidenceParseError.MalformedJson("/path/file.jsonl", 5, "Unexpected end of Input"))]
              let rendered = renderVerificationEvidenceLoadIssues errors
              Expect.stringContains rendered "malformed_json" "should contain error type"
              Expect.stringContains rendered "/path/file.jsonl" "should contain source"
          }

          // Test 14: CLI rendering handles all error variants
          test "renderVerificationEvidenceLoadIssues handles all error variants" {
              let errors = [
                  VerificationEvidenceLoadError.DuplicateEvidenceId("/path/file.jsonl", "evid123", 3, 7)
                  VerificationEvidenceLoadError.ParseError(
                      VerificationEvidenceParseError.MissingField("/path/file.jsonl", 2, "episode_id"))
                  VerificationEvidenceLoadError.ParseError(
                      VerificationEvidenceParseError.UnknownVerificationKind("/path/file.jsonl", 4, "unknown_kind"))
              ]
              let rendered = renderVerificationEvidenceLoadIssues errors
              Expect.stringContains rendered "duplicate_evidence_id" "should contain duplicate error"
              Expect.stringContains rendered "missing_field" "should contain missing field error"
              Expect.stringContains rendered "unknown_verification_kind" "should contain kind error"
          }

          // Test 15: empty evidence file produces no issues
          test "empty evidence file => no issues" {
              let dir = tempDir "verify-empty-evidence"
              try
                  createMinimalStructure dir
                  writeEvidence dir []
                  let vr = runVerify dir
                  // Empty evidence file is valid - no issues expected
                  Expect.equal (List.length vr.Issues) 0 "empty evidence should have no issues"
              finally
                  cleanup dir
          }
        ]
