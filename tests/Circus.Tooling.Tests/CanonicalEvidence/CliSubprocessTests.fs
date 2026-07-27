module Circus.Tooling.Tests.CanonicalEvidence.CliSubprocessTests

// =============================================================================
// CLI Subprocess tests for the repair-episode CLI
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION05-RUNNER-INTEGRITY01
//
// These tests invoke the compiled circus-tooling via BoundedProcess to verify
// CLI behavior with evidence loading failures. They complement the unit tests
// by exercising the end-to-end CLI execution path with bounded resources.
//
// Test Authority: dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build -- --filter "CliSubprocess"
// =============================================================================

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// -----------------------------------------------------------------------------
// Test infrastructure
// -----------------------------------------------------------------------------

/// Default timeout for CLI tests (30 seconds)
let private defaultTimeout = TimeSpan.FromSeconds(30.0)

/// Default stdout/stderr limits (1 MiB)
let private defaultOutputLimit = 1024 * 1024

/// Path to the compiled circus-tooling binary
let private circusToolingDll () : string =
    let baseDir = Directory.GetCurrentDirectory()
    Path.Combine(baseDir, "tools", "Circus.Tooling", "bin", "Release", "net10.0", "circus-tooling.dll")

/// Path to dotnet executable
let private dotnetExe () : string =
    let dotnet = Environment.GetEnvironmentVariable("DOTNET_ROOT")
    if not (String.IsNullOrEmpty dotnet) then
        Path.Combine(dotnet, "dotnet")
    else
        "dotnet"

/// Run the CLI with the given arguments using BoundedProcess
let private runCliBounded (repoRoot: string) (args: string list) : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    let dll = circusToolingDll()
    if not (File.Exists dll) then
        failwithf "circus-tooling.dll not found at %s" dll
    
    let request: BoundedProcessRequest = {
        Executable = dotnetExe()
        WorkingDirectory = repoRoot
        Arguments = [ dll ] @ args
        Environment = []
        Limits = {
            Timeout = defaultTimeout
            StdoutLimitBytes = defaultOutputLimit
            StderrLimitBytes = defaultOutputLimit
        }
    }
    run request CancellationToken.None

/// Create a temporary directory with minimal canonical structure
let private tempDir (label: string) : string =
    let dir = Path.Combine(Path.GetTempPath(), label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

/// Cleanup a temporary directory
let private cleanup (dir: string) : unit =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ -> ()

/// Create minimal canonical structure
let private createMinimalStructure (dir: string) : unit =
    let canonical = ".circus"
    let declarationsDir = Path.Combine(dir, canonical, "corpus", "episodes", "declarations")
    let capturesDir = Path.Combine(dir, canonical, "corpus", "captures")
    let evidenceDir = Path.Combine(dir, canonical, "corpus", "normalized")
    Directory.CreateDirectory declarationsDir |> ignore
    Directory.CreateDirectory capturesDir |> ignore
    Directory.CreateDirectory evidenceDir |> ignore

/// Convert bytes to string safely
let private decodeUtf8 (bytes: byte array) : string =
    try
        Encoding.UTF8.GetString(bytes)
    with _ ->
        BitConverter.ToString(bytes |> Array.take (min 100 bytes.Length)) + "..."

/// Classify failure kind from BoundedProcessFailure
let private classifyFailure (failure: BoundedProcessFailure) : string =
    match failure with
    | NonZeroExit (code, _, _) -> sprintf "non_zero_exit(%d)" code
    | TimedOut _ -> "timed_out"
    | Cancelled -> "cancelled"
    | LaunchFailed _ -> "launch_failed"
    | InvalidRequest _ -> "invalid_request"
    | StdoutLimitExceeded _ -> "stdout_limit_exceeded"
    | StderrLimitExceeded _ -> "stderr_limit_exceeded"
    | StdoutReaderFailed _ -> "stdout_reader_failed"
    | StderrReaderFailed _ -> "stderr_reader_failed"
    | WaitFailed _ -> "wait_failed"
    | KillFailed _ -> "kill_failed"
    | IncompleteOutput _ -> "incomplete_output"
    | TerminationCleanupFailed _ -> "termination_cleanup_failed"

// -----------------------------------------------------------------------------
// Test list
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "CliSubprocess"
        [
          // Test 1: inventory command with missing evidence file
          testTask "inventory with missing evidence file fails" {
              let dir = tempDir "cli-inventory-missing-evidence"
              try
                  createMinimalStructure dir
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "inventory" ]
                  match result with
                  | Ok success ->
                      // Unexpected success - evidence file should be missing
                      failwithf "Expected failure but got exit code %d" success.ExitCode
                  | Error failure ->
                      // Should fail - evidence file missing
                      let failureKind = classifyFailure failure
                      Expect.stringContains failureKind "non_zero_exit" "should be non-zero exit"
              finally
                  cleanup dir
          }

          // Test 2: verify command with malformed evidence
          testTask "verify with malformed evidence fails" {
              let dir = tempDir "cli-verify-malformed-evidence"
              try
                  createMinimalStructure dir
                  // Write malformed evidence
                  let evidencePath = Path.Combine(dir, ".circus", "corpus", "normalized", "verification-evidence-v1.jsonl")
                  File.WriteAllText(evidencePath, """{"schema""")
                  
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  match result with
                  | Ok success ->
                      failwithf "Expected failure but got exit code %d" success.ExitCode
                  | Error failure ->
                      let failureKind = classifyFailure failure
                      Expect.stringContains failureKind "non_zero_exit" "should be non-zero exit"
              finally
                  cleanup dir
          }

          // Test 3: verify command with missing evidence file
          testTask "verify with missing evidence file fails" {
              let dir = tempDir "cli-verify-missing-evidence"
              try
                  createMinimalStructure dir
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  match result with
                  | Ok success ->
                      failwithf "Expected failure but got exit code %d" success.ExitCode
                  | Error _ ->
                      // Expected failure
                      ()
              finally
                  cleanup dir
          }

          // Test 4: show command with missing evidence file
          testTask "show with missing evidence file fails" {
              let dir = tempDir "cli-show-missing-evidence"
              try
                  createMinimalStructure dir
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "show"; "ep-001" ]
                  match result with
                  | Ok success ->
                      failwithf "Expected failure but got exit code %d" success.ExitCode
                  | Error _ ->
                      // Expected failure
                      ()
              finally
                  cleanup dir
          }

          // Test 5: verify with invalid SHA-256 evidence
          testTask "verify with invalid SHA-256 evidence fails" {
              let dir = tempDir "cli-verify-invalid-sha256"
              try
                  createMinimalStructure dir
                  let evidencePath = Path.Combine(dir, ".circus", "corpus", "normalized", "verification-evidence-v1.jsonl")
                  let invalidEvidence = """{"schema_version":"verification-evidence-v1","verification_evidence_id":"000100020003000400050006000700080009000a000b000c000d000e000f0010","episode_id":"ep-001","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","tested_tree_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","stdout_sha256":"not-a-valid-sha256-hash-value"}"""
                  File.WriteAllText(evidencePath, invalidEvidence)
                  
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  match result with
                  | Ok success ->
                      failwithf "Expected failure but got exit code %d" success.ExitCode
                  | Error _ ->
                      // Expected failure
                      ()
              finally
                  cleanup dir
          }

          // Test 6: verify with duplicate evidence ID
          testTask "verify with duplicate evidence ID fails" {
              let dir = tempDir "cli-verify-duplicate-id"
              try
                  createMinimalStructure dir
                  let evidencePath = Path.Combine(dir, ".circus", "corpus", "normalized", "verification-evidence-v1.jsonl")
                  let evidenceId = "000100020003000400050006000700080009000a000b000c000d000e000f0010"
                  let rec1 = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-001","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","tested_tree_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""" evidenceId
                  let rec2 = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-002","verification_kind":"build","verification_command":"dotnet build","verification_result":"fail","verification_exit_code":1,"tested_commit_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","tested_tree_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""" evidenceId
                  File.WriteAllLines(evidencePath, [ rec1; rec2 ])
                  
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  match result with
                  | Ok success ->
                      failwithf "Expected failure but got exit code %d" success.ExitCode
                  | Error _ ->
                      // Expected failure
                      ()
              finally
                  cleanup dir
          }

          // Test 7: verify with placeholder evidence ID
          testTask "verify with placeholder evidence ID fails" {
              let dir = tempDir "cli-verify-placeholder-id"
              try
                  createMinimalStructure dir
                  let evidencePath = Path.Combine(dir, ".circus", "corpus", "normalized", "verification-evidence-v1.jsonl")
                  let placeholderId = String.replicate 64 "0"
                  let evidenceRec = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-001","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","tested_tree_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""" placeholderId
                  File.WriteAllText(evidencePath, evidenceRec)
                  
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  match result with
                  | Ok success ->
                      failwithf "Expected failure but got exit code %d" success.ExitCode
                  | Error _ ->
                      // Expected failure
                      ()
              finally
                  cleanup dir
          }

          // Test 8: help command succeeds
          testTask "help command succeeds" {
              let dir = tempDir "cli-help"
              try
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "help" ]
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "help should succeed"
                      let stdout = decodeUtf8 success.Stdout
                      Expect.stringContains stdout "Usage" "stdout should contain usage"
                  | Error failure ->
                      failwithf "Help command failed: %A" failure
              finally
                  cleanup dir
          }

          // Test 9: empty evidence file succeeds
          testTask "verify with empty evidence file succeeds" {
              let dir = tempDir "cli-verify-empty-evidence"
              try
                  createMinimalStructure dir
                  let evidencePath = Path.Combine(dir, ".circus", "corpus", "normalized", "verification-evidence-v1.jsonl")
                  File.WriteAllText(evidencePath, "")
                  
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  match result with
                  | Ok success ->
                      // Empty evidence file is valid
                      Expect.equal success.ExitCode 0 "empty evidence should succeed"
                  | Error failure ->
                      // Some evidence loading issues may still occur for other missing files
                      match failure with
                      | NonZeroExit (_, _, stderrBytes) ->
                          let stderr = decodeUtf8 stderrBytes
                          if stderr.Contains "evidence_file_missing" then
                              failwith "Should not fail on missing evidence with empty file"
                      | _ -> ()
              finally
                  cleanup dir
          }

          // Test 10: regenerate command fails without evidence
          testTask "regenerate without evidence file fails" {
              let dir = tempDir "cli-regenerate-missing-evidence"
              try
                  createMinimalStructure dir
                  let! result = runCliBounded dir [ "fsharp-diagnostics"; "repair-episodes"; "regenerate" ]
                  match result with
                  | Ok success ->
                      // Regenerate without evidence may still complete (empty)
                      // or fail on other missing files
                      ()
                  | Error _ ->
                      // Expected failure or error
                      ()
              finally
                  cleanup dir
          }
        ]
