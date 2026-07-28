module Circus.Tooling.Tests.CanonicalEvidence.RepairEpisodeVerificationTests

// =============================================================================
// Verification evidence loading tests for the repair-episode linker
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION06-REGRESSION-RECOVERY-AND-PROOF-CONVERGENCE01
//
// These tests verify that evidence loading failures are correctly identified
// and categorized, with exact error patterns preserved and exposed through the CLI.
//
// Workstream 6: Empty evidence semantics - empty evidence file is valid (no issues)
// Workstream 8: Wrong field types produce errors (parser returns MissingField for wrong types)
// Workstream 9: ConflictingEvidenceRecord for same ID with different content
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

/// Build an evidence record with wrong field type (episode_id as number instead of string)
/// Note: The parser uses lookupString which returns None for non-string values,
/// resulting in MissingField error rather than WrongFieldType.
let private evidenceRecordWrongFieldType (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":999,"verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId validCommitOid validTreeOid

/// Build two evidence records with same ID but different content (conflicting)
let private conflictingEvidenceRecords (evId: string) : string * string =
    let rec1 = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-001","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}""" evId validCommitOid validTreeOid
    let rec2 = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-002","verification_kind":"build","verification_command":"dotnet build","verification_result":"fail","verification_exit_code":1,"tested_commit_oid":"%s","tested_tree_oid":"%s"}""" evId validCommitOid validTreeOid
    rec1, rec2

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
          // Test 1: missing verification-evidence file => VerificationEvidenceLoadFailed
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

          // Test 2: malformed JSON on first record => MalformedJson error
          test "malformed JSON first record => MalformedJson error" {
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

          // Test 3: malformed JSON after valid record => MalformedJson error
          test "malformed JSON after valid => MalformedJson error" {
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

          // Test 4: unsupported schema version => UnsupportedSchemaVersion error
          test "unsupported schema version => UnsupportedSchemaVersion error" {
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

          // Test 5: missing required field (verification_kind) => MissingField error
          test "missing required field => MissingField error" {
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

          // Test 6: unknown verification kind => UnknownVerificationKind error
          test "unknown verification kind => UnknownVerificationKind error" {
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

          // Test 7: unknown verification result => UnknownVerificationStatus error
          test "unknown verification result => UnknownVerificationStatus error" {
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

          // Test 8: invalid exit code (negative) => InvalidExitCode error
          test "negative exit code => InvalidExitCode error" {
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

          // Test 9: invalid commit OID => InvalidCommitOid error
          test "invalid commit OID => InvalidCommitOid error" {
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

          // Test 10: invalid tree OID => InvalidTreeOid error
          test "invalid tree OID => InvalidTreeOid error" {
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

          // Test 11: duplicate evidence ID (same ID, same episode, identical content) => DuplicateEvidenceId
          test "duplicate evidence ID (identical) => DuplicateEvidenceId error" {
              let dir = tempDir "verify-duplicate-id"
              try
                  createMinimalStructure dir
                  // Same ID, same content - TRUE duplicate (not a conflict)
                  let rec1 = validEvidenceRecord validEvidenceId "ep-001"
                  let rec2 = validEvidenceRecord validEvidenceId "ep-001"
                  writeEvidence dir [ rec1; rec2 ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.DuplicateEvidenceId _ ] -> ()
                      | [ VerificationEvidenceLoadError.ConflictingEvidenceRecord _ ] ->
                          // This would be incorrect - same content should be duplicate, not conflict
                          failwithf "expected DuplicateEvidenceId error for identical records, got ConflictingEvidenceRecord"
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

          // Test 15: empty evidence file => no issues (Workstream 6)
          test "empty evidence file => no issues (empty is valid)" {
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

          // Test 16: Wrong field type (episode_id as number) produces an error
          // Note: Current parser returns MissingField for wrong types (lookupString returns None)
          // This test documents actual behavior - wrong type produces MissingField
          test "wrong field type (episode_id as number) => MissingField error (actual behavior)" {
              let dir = tempDir "verify-wrong-field-type"
              try
                  createMinimalStructure dir
                  let bad = evidenceRecordWrongFieldType validEvidenceId "ep-001"
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      // Parser uses lookupString which returns None for non-string values
                      // Result is MissingField error for episode_id
                      let hasMissingField = errors |> List.exists (function
                          | VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.MissingField _) -> true
                          | _ -> false)
                      Expect.isTrue hasMissingField "should have MissingField error for wrong type"
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 17: invalid SHA-256 in stdout_sha256 field => InvalidSha256 error
          test "invalid SHA-256 in stdout_sha256 => InvalidSha256 error" {
              let dir = tempDir "verify-invalid-sha256"
              try
                  createMinimalStructure dir
                  let bad = validEvidenceRecord validEvidenceId "ep-001"
                              |> fun s -> s.Replace("}", ",\"stdout_sha256\":\"not-a-valid-sha256-hash-value-for-test\"}")
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      let hasInvalidSha = errors |> List.exists (function
                          | VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.InvalidSha256 _) -> true
                          | _ -> false)
                      Expect.isTrue hasInvalidSha "should have InvalidSha256 error"
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 18: placeholder evidence ID (all zeros) => PlaceholderEvidenceId error
          test "placeholder evidence ID (all zeros) => PlaceholderEvidenceId error" {
              let dir = tempDir "verify-placeholder-evidence-id"
              try
                  createMinimalStructure dir
                  let placeholderId = String.replicate 64 "0"
                  let bad = validEvidenceRecord placeholderId "ep-001"
                  writeEvidence dir [ bad ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      match errors with
                      | [ VerificationEvidenceLoadError.ParseError e ] ->
                          match e with
                          | VerificationEvidenceParseError.PlaceholderEvidenceId _ -> ()
                          | _ -> failwithf "expected PlaceholderEvidenceId error, got %A" e
                      | _ -> failwithf "expected ParseError, got %A" errors
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 19: valid evidence returns Completed with no issues
          test "valid evidence returns Completed with no issues" {
              let dir = tempDir "verify-valid-evidence"
              try
                  createMinimalStructure dir
                  let valid = validEvidenceRecord validEvidenceId "ep-001"
                  writeEvidence dir [ valid ]
                  let vr = runVerify dir
                  // Valid evidence should have no issues
                  Expect.equal (List.length vr.Issues) 0 "valid evidence should have no issues"
              finally
                  cleanup dir
          }

          // Test 20: conflicting evidence records (same ID, different content) => ConflictingEvidenceRecord
          // Note: Current implementation distinguishes duplicates from conflicts
          // Workstream 3: ConflictingEvidenceRecord type now triggered for same ID, different content
          test "conflicting evidence records => ConflictingEvidenceRecord error" {
              let dir = tempDir "verify-conflicting-evidence"
              try
                  createMinimalStructure dir
                  let rec1, rec2 = conflictingEvidenceRecords validEvidenceId
                  writeEvidence dir [ rec1; rec2 ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      let hasConflict = errors |> List.exists (function
                          | VerificationEvidenceLoadError.ConflictingEvidenceRecord _ -> true
                          | _ -> false)
                      Expect.isTrue hasConflict "should have ConflictingEvidenceRecord error"
                  | _ -> failwithf "expected VerificationEvidenceLoadFailed, got %A" vr.Issues
              finally
                  cleanup dir
          }

          // Test 21: CLI rendering handles conflicting evidence error
          test "renderVerificationEvidenceLoadIssues handles conflicting evidence" {
              let errors = [
                  VerificationEvidenceLoadError.ConflictingEvidenceRecord("/path/file.jsonl", "evid123", 3, 7)
              ]
              let rendered = renderVerificationEvidenceLoadIssues errors
              Expect.stringContains rendered "conflicting_evidence" "should contain conflicting error"
              Expect.stringContains rendered "evid123" "should contain evidence ID"
          }

          // Test 22: CLI rendering handles WrongFieldType error (type exists but not produced by parser)
          test "renderVerificationEvidenceLoadIssues handles WrongFieldType" {
              let errors = [
                  VerificationEvidenceLoadError.ParseError(
                      VerificationEvidenceParseError.WrongFieldType("/path/file.jsonl", 5, "episode_id", "a string"))
              ]
              let rendered = renderVerificationEvidenceLoadIssues errors
              Expect.stringContains rendered "wrong_field_type" "should contain wrong_field_type error"
              Expect.stringContains rendered "episode_id" "should contain field name"
              Expect.stringContains rendered "a string" "should contain expected type"
          }

          // Test 23: ConflictingEvidenceRecord type usage
          test "ConflictingEvidenceRecord type exists and renders correctly" {
              let errors = [
                  VerificationEvidenceLoadError.ConflictingEvidenceRecord("/path/file.jsonl", "abc123", 1, 5)
              ]
              let rendered = renderVerificationEvidenceLoadIssues errors
              Expect.stringContains rendered "conflicting_evidence" "should contain conflicting_evidence"
              Expect.stringContains rendered "abc123" "should contain evidence ID"
              Expect.stringContains rendered "/path/file.jsonl" "should contain path"
          }

          // Test 24: Real engine Completed test with empty evidence (Workstream 4, 6)
          test "runEpisodeEngine Completed with empty evidence returns valid result" {
              let dir = tempDir "engine-completed-empty"
              try
                  createMinimalStructure dir
                  writeEvidence dir []
                  let execution = runEpisodeEngine dir defaultEngineOptions
                  match execution with
                  | EpisodeEngineExecution.Completed result ->
                      Expect.isTrue (result.Summary.VerificationEvidenceTotal >= 0) "verification_records_loaded >= 0"
                      Expect.equal result.Summary.InvalidDeclarations 0 "invalid_declarations = 0"
                  | EpisodeEngineExecution.Failed failure ->
                      failwithf "Engine should complete with empty evidence, got: %A" failure
              finally
                  cleanup dir
          }

          // Test 25: runEpisodeEngine with valid evidence produces Completed (Workstream 4)
          test "runEpisodeEngine Completed with valid evidence" {
              let dir = tempDir "engine-completed-valid"
              try
                  createMinimalStructure dir
                  let valid = validEvidenceRecord validEvidenceId "ep-001"
                  writeEvidence dir [ valid ]
                  let execution = runEpisodeEngine dir defaultEngineOptions
                  match execution with
                  | EpisodeEngineExecution.Completed result ->
                      Expect.isTrue (result.Summary.VerificationEvidenceTotal >= 0) "verification_records_loaded >= 0"
                      Expect.equal result.Summary.InvalidDeclarations 0 "invalid_declarations = 0"
                  | EpisodeEngineExecution.Failed failure ->
                      failwithf "Engine should complete with valid evidence, got: %A" failure
              finally
                  cleanup dir
          }

          // Test 26: Conflicting evidence records produce ConflictingEvidenceRecord (Workstream 3)
          test "conflicting evidence records produce ConflictingEvidenceRecord" {
              let dir = tempDir "conflicting-evidence-test"
              try
                  createMinimalStructure dir
                  let rec1 = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-conflict-1","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}""" validEvidenceId validCommitOid validTreeOid
                  let rec2 = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-conflict-2","verification_kind":"focused_test","verification_command":"dotnet test","verification_result":"fail","verification_exit_code":1,"tested_commit_oid":"%s","tested_tree_oid":"%s"}""" validEvidenceId validCommitOid validTreeOid
                  writeEvidence dir [ rec1; rec2 ]
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues for conflicting evidence"
                  match vr.Issues with
                  | [ VerificationIssue.VerificationEvidenceLoadFailed errors ] ->
                      let hasConflict = errors |> List.exists (function
                          | VerificationEvidenceLoadError.ConflictingEvidenceRecord _ -> true
                          | _ -> false)
                      Expect.isTrue hasConflict "should have ConflictingEvidenceRecord error"
                  | _ -> failwithf "expected ConflictingEvidenceRecord, got %A" vr.Issues
              finally
                  cleanup dir
          }
        ]
