module Circus.Tooling.Tests.CanonicalEvidence.CliSubprocessTests

// =============================================================================
// CLI Subprocess tests for the repair-episode CLI
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION04-RUNNER-AUTHORITY01
//
// These tests invoke the compiled circus-tooling via subprocess to verify
// CLI behavior with evidence loading failures. They complement the unit tests
// by exercising the end-to-end CLI execution path.
//
// Test Authority: dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build -- --filter "CliSubprocess"
// =============================================================================

open System
open System.Diagnostics
open System.IO
open Expecto

// -----------------------------------------------------------------------------
// Test infrastructure
// -----------------------------------------------------------------------------

/// Result from a CLI subprocess invocation
type private CliResult = {
    ExitCode: int
    Stdout: string
    Stderr: string
}

/// Path to the compiled circus-tooling binary
let private circusToolingExe () : string =
    let baseDir = Directory.GetCurrentDirectory()
    let releaseBin = Path.Combine(baseDir, "tools", "Circus.Tooling", "bin", "Release", "net10.0", "circus-tooling")
    if File.Exists releaseBin then
        releaseBin
    else
        failwithf "circus-tooling not found at %s" releaseBin

/// Get the path to the DLL (for running with dotnet)
let private circusToolingDll () : string =
    let baseDir = Directory.GetCurrentDirectory()
    let dllPath = Path.Combine(baseDir, "tools", "Circus.Tooling", "bin", "Release", "net10.0", "circus-tooling.dll")
    if File.Exists dllPath then
        dllPath
    else
        failwithf "circus-tooling.dll not found at %s" dllPath

/// Run the CLI with the given arguments in a temp directory
let private runCli (repoRoot: string) (args: string list) : CliResult =
    let exe = circusToolingExe()
    let psi = ProcessStartInfo()
    psi.FileName <- exe
    psi.WorkingDirectory <- repoRoot
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.CreateNoWindow <- true
    for arg in args do
        psi.ArgumentList.Add(arg)
    
    let p = Process.Start psi
    let stdout = p.StandardOutput.ReadToEnd()
    let stderr = p.StandardError.ReadToEnd()
    p.WaitForExit()
    
    { ExitCode = p.ExitCode
      Stdout = stdout
      Stderr = stderr }

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
    let evidenceDir = Path.Combine(dir, canonical, "normalized")
    Directory.CreateDirectory declarationsDir |> ignore
    Directory.CreateDirectory capturesDir |> ignore
    Directory.CreateDirectory evidenceDir |> ignore

// -----------------------------------------------------------------------------
// Test list
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "CliSubprocess"
        [
          // Test 1: inventory command with missing evidence file
          test "inventory with missing evidence file fails" {
              let dir = tempDir "cli-inventory-missing-evidence"
              try
                  createMinimalStructure dir
                  let result = runCli dir [ "fsharp-diagnostics"; "repair-episodes"; "inventory" ]
                  // Should fail due to missing evidence file
                  Expect.notEqual result.ExitCode 0 "inventory should fail on missing evidence"
                  Expect.stringContains result.Stderr "evidence" "stderr should mention evidence"
                  Expect.isFalse (result.Stdout.Contains "PASS") "no PASS on failure"
              finally
                  cleanup dir
          }

          // Test 2: verify command with malformed evidence
          test "verify with malformed evidence fails" {
              let dir = tempDir "cli-verify-malformed-evidence"
              try
                  createMinimalStructure dir
                  // Write malformed evidence
                  let evidencePath = Path.Combine(dir, ".circus", "normalized", "verification-evidence.jsonl")
                  File.WriteAllText(evidencePath, """{"schema""")
                  
                  let result = runCli dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  Expect.notEqual result.ExitCode 0 "verify should fail on malformed evidence"
                  Expect.stringContains result.Stderr "malformed" "stderr should mention malformed"
                  Expect.isFalse (result.Stdout.Contains "PASS") "no PASS on failure"
              finally
                  cleanup dir
          }

          // Test 3: verify command with missing evidence file
          test "verify with missing evidence file fails" {
              let dir = tempDir "cli-verify-missing-evidence"
              try
                  createMinimalStructure dir
                  let result = runCli dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  Expect.notEqual result.ExitCode 0 "verify should fail on missing evidence"
                  Expect.stringContains result.Stderr "evidence" "stderr should mention evidence"
              finally
                  cleanup dir
          }

          // Test 4: show command with missing evidence file
          test "show with missing evidence file fails" {
              let dir = tempDir "cli-show-missing-evidence"
              try
                  createMinimalStructure dir
                  let result = runCli dir [ "fsharp-diagnostics"; "repair-episodes"; "show"; "ep-001" ]
                  Expect.notEqual result.ExitCode 0 "show should fail on missing evidence"
                  Expect.stringContains result.Stderr "evidence" "stderr should mention evidence"
              finally
                  cleanup dir
          }

          // Test 5: verify with invalid SHA-256 evidence
          test "verify with invalid SHA-256 evidence fails" {
              let dir = tempDir "cli-verify-invalid-sha256"
              try
                  createMinimalStructure dir
                  let evidencePath = Path.Combine(dir, ".circus", "normalized", "verification-evidence.jsonl")
                  let invalidEvidence = """{"schema_version":"verification-evidence-v1","verification_evidence_id":"000100020003000400050006000700080009000a000b000c000d000e000f0010","episode_id":"ep-001","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","tested_tree_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","stdout_sha256":"not-a-valid-sha256-hash-value"}"""
                  File.WriteAllText(evidencePath, invalidEvidence)
                  
                  let result = runCli dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  Expect.notEqual result.ExitCode 0 "verify should fail on invalid SHA-256"
                  Expect.stringContains result.Stderr "sha256" "stderr should mention SHA-256"
              finally
                  cleanup dir
          }

          // Test 6: verify with duplicate evidence ID
          test "verify with duplicate evidence ID fails" {
              let dir = tempDir "cli-verify-duplicate-id"
              try
                  createMinimalStructure dir
                  let evidencePath = Path.Combine(dir, ".circus", "normalized", "verification-evidence.jsonl")
                  let evidenceId = "000100020003000400050006000700080009000a000b000c000d000e000f0010"
                  let rec1 = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-001","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","tested_tree_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""" evidenceId
                  let rec2 = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-002","verification_kind":"build","verification_command":"dotnet build","verification_result":"fail","verification_exit_code":1,"tested_commit_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","tested_tree_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""" evidenceId
                  File.WriteAllLines(evidencePath, [ rec1; rec2 ])
                  
                  let result = runCli dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  Expect.notEqual result.ExitCode 0 "verify should fail on duplicate evidence ID"
                  Expect.stringContains result.Stderr "duplicate" "stderr should mention duplicate"
              finally
                  cleanup dir
          }

          // Test 7: verify with placeholder evidence ID
          test "verify with placeholder evidence ID fails" {
              let dir = tempDir "cli-verify-placeholder-id"
              try
                  createMinimalStructure dir
                  let evidencePath = Path.Combine(dir, ".circus", "normalized", "verification-evidence.jsonl")
                  let placeholderId = String.replicate 64 "0"
                  let evidenceRec = sprintf """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"ep-001","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","tested_tree_oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""" placeholderId
                  File.WriteAllText(evidencePath, evidenceRec)
                  
                  let result = runCli dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  Expect.notEqual result.ExitCode 0 "verify should fail on placeholder evidence ID"
                  Expect.stringContains result.Stderr "placeholder" "stderr should mention placeholder"
              finally
                  cleanup dir
          }

          // Test 8: help command succeeds
          test "help command succeeds" {
              let dir = tempDir "cli-help"
              try
                  let result = runCli dir [ "fsharp-diagnostics"; "repair-episodes"; "help" ]
                  Expect.equal result.ExitCode 0 "help should succeed"
                  Expect.stringContains result.Stdout "Usage" "stdout should contain usage"
              finally
                  cleanup dir
          }

          // Test 9: empty evidence file succeeds (valid empty)
          test "verify with empty evidence file succeeds" {
              let dir = tempDir "cli-verify-empty-evidence"
              try
                  createMinimalStructure dir
                  let evidencePath = Path.Combine(dir, ".circus", "normalized", "verification-evidence.jsonl")
                  File.WriteAllText(evidencePath, "")
                  
                  let result = runCli dir [ "fsharp-diagnostics"; "repair-episodes"; "verify" ]
                  // Empty evidence file is valid, should pass or have no evidence-related issues
                  // (may fail on other missing files but not evidence)
                  if result.ExitCode <> 0 then
                      Expect.isFalse (result.Stderr.Contains "evidence_file_missing") "should not fail on missing evidence with empty file"
              finally
                  cleanup dir
          }
        ]
