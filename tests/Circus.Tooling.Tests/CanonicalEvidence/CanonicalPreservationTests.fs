module Circus.Tooling.Tests.CanonicalEvidence.CanonicalPreservationTests

// =============================================================================
// Canonical preservation tests for evidence loading failures
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION06-REGRESSION-RECOVERY-AND-PROOF-CONVERGENCE01
//
// These tests verify that existing canonical files survive evidence loading
// failures - the engine does not modify or corrupt canonical files when
// evidence loading fails. Tests use production path constants.
//
// Test Authority: dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build -- --filter "CanonicalPreservation"
// =============================================================================

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto

open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess
open Circus.Tooling.FSharpDiagnostics.Hashing

// -----------------------------------------------------------------------------
// Test infrastructure
// -----------------------------------------------------------------------------

/// Valid 64-character hexadecimal evidence ID for SHA-256
let private validEvidenceId =
    "000100020003000400050006000700080009000a000b000c000d000e000f0010"

/// Valid 40-character commit OID
let private validCommitOid = String.replicate 40 "a"

/// Valid 40-character tree OID
let private validTreeOid = String.replicate 40 "a"

/// Default timeout for regeneration tests
let private defaultTimeout = TimeSpan.FromSeconds(60.0)

/// Default output limits
let private defaultOutputLimit = 1024 * 1024

/// Create a valid verification evidence record
let private validEvidenceRecord (evId: string) (epId: string) : string =
    sprintf
        """{"schema_version":"verification-evidence-v1","verification_evidence_id":"%s","episode_id":"%s","verification_kind":"build","verification_command":"dotnet build","verification_result":"pass","verification_exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId
        epId
        validCommitOid
        validTreeOid

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

/// Create minimal directory structure needed by the repair-episode engine.
let private createMinimalStructure (dir: string) : unit =
    let declarationsDir =
        Path.Combine(dir, canonicalRootRelative, "corpus", "episodes", "declarations")

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

/// Seed a canonical file with known content
let private seedCanonicalFile (dir: string) (canonicalPath: string) (content: string) : unit =
    let fullPath = Path.Combine(dir, canonicalPath)
    let dirPath = Path.GetDirectoryName(fullPath)

    if not (Directory.Exists dirPath) then
        Directory.CreateDirectory(dirPath) |> ignore

    File.WriteAllText(fullPath, content)

/// Get SHA-256 of a file's content
let private getFileSha256 (filePath: string) : string option =
    if File.Exists filePath then
        let content = File.ReadAllText(filePath)
        Some(sha256OfUtf8 content)
    else
        None

/// Get file content as bytes for exact comparison
let private getFileBytes (filePath: string) : byte[] option =
    if File.Exists filePath then
        Some(File.ReadAllBytes(filePath))
    else
        None

/// Get the path to the compiled circus-tooling binary
let private circusToolingDll () : string =
    let baseDir = Directory.GetCurrentDirectory()
    Path.Combine(baseDir, "tools", "Circus.Tooling", "bin", "Release", "net10.0", "circus-tooling.dll")

/// Run regenerate command via BoundedProcess
let private runRegenerate (dir: string) : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    let dll = circusToolingDll ()

    let request: BoundedProcessRequest =
        { Executable = "dotnet"
          WorkingDirectory = dir
          Arguments = [ dll; "fsharp-diagnostics"; "repair-episodes"; "regenerate" ]
          Environment = []
          Limits =
            { Timeout = defaultTimeout
              StdoutLimitBytes = defaultOutputLimit
              StderrLimitBytes = defaultOutputLimit } }

    run request CancellationToken.None

/// Run verifyPipeline
let private runVerify (dir: string) : VerificationResult = verifyPipeline dir defaultEngineOptions

// -----------------------------------------------------------------------------
// Test list
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "CanonicalPreservation"
        [
          // Test 1: canonical files survive missing evidence file
          test "canonical files survive missing evidence file" {
              let dir = tempDir "preserve-missing-evidence"

              try
                  createMinimalStructure dir

                  // Seed canonical files with known content using production path constants
                  let episodePath = Path.Combine(dir, repairEpisodesCanonicalPath)
                  let transitionPath = Path.Combine(dir, diagnosticTransitionsCanonicalPath)
                  let summaryPath = Path.Combine(dir, repairEpisodeSummaryCanonicalPath)

                  let episodeContent = "canonical-episodes-v1\n"
                  let transitionContent = "canonical-transitions-v1\n"
                  let summaryContent = "canonical-summary-v1\n"

                  seedCanonicalFile dir repairEpisodesCanonicalPath episodeContent
                  seedCanonicalFile dir diagnosticTransitionsCanonicalPath transitionContent
                  seedCanonicalFile dir repairEpisodeSummaryCanonicalPath summaryContent

                  // Capture SHA-256 before failure
                  let episodeShaBefore = getFileSha256 episodePath
                  let transitionShaBefore = getFileSha256 transitionPath
                  let summaryShaBefore = getFileSha256 summaryPath

                  // Run verify - should fail due to missing evidence
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"

                  // Verify SHA-256 unchanged
                  let episodeShaAfter = getFileSha256 episodePath
                  let transitionShaAfter = getFileSha256 transitionPath
                  let summaryShaAfter = getFileSha256 summaryPath

                  Expect.equal episodeShaBefore episodeShaAfter "episode file SHA-256 unchanged"
                  Expect.equal transitionShaBefore transitionShaAfter "transition file SHA-256 unchanged"
                  Expect.equal summaryShaBefore summaryShaAfter "summary file SHA-256 unchanged"
              finally
                  cleanup dir
          }

          // Test 2: canonical files survive malformed evidence
          test "canonical files survive malformed evidence" {
              let dir = tempDir "preserve-malformed-evidence"

              try
                  createMinimalStructure dir

                  // Seed canonical files with known content
                  let episodePath = Path.Combine(dir, repairEpisodesCanonicalPath)
                  let transitionPath = Path.Combine(dir, diagnosticTransitionsCanonicalPath)

                  let episodeContent = "canonical-episodes-v2\n"
                  let transitionContent = "canonical-transitions-v2\n"

                  seedCanonicalFile dir repairEpisodesCanonicalPath episodeContent
                  seedCanonicalFile dir diagnosticTransitionsCanonicalPath transitionContent

                  // Write malformed evidence
                  writeEvidence dir [ """{"schema""" ]

                  // Capture SHA-256 before failure
                  let episodeShaBefore = getFileSha256 episodePath
                  let transitionShaBefore = getFileSha256 transitionPath

                  // Run verify - should fail due to malformed evidence
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"

                  // Verify SHA-256 unchanged
                  let episodeShaAfter = getFileSha256 episodePath
                  let transitionShaAfter = getFileSha256 transitionPath

                  Expect.equal episodeShaBefore episodeShaAfter "episode file SHA-256 unchanged"
                  Expect.equal transitionShaBefore transitionShaAfter "transition file SHA-256 unchanged"
              finally
                  cleanup dir
          }

          // Test 3: canonical files survive invalid SHA-256 evidence
          test "canonical files survive invalid SHA-256 evidence" {
              let dir = tempDir "preserve-invalid-sha256-evidence"

              try
                  createMinimalStructure dir

                  // Seed canonical files with known content
                  let episodePath = Path.Combine(dir, repairEpisodesCanonicalPath)
                  let summaryPath = Path.Combine(dir, repairEpisodeSummaryCanonicalPath)

                  let episodeContent = "canonical-episodes-v3\n"
                  let summaryContent = "canonical-summary-v3\n"

                  seedCanonicalFile dir repairEpisodesCanonicalPath episodeContent
                  seedCanonicalFile dir repairEpisodeSummaryCanonicalPath summaryContent

                  // Write evidence with invalid SHA-256
                  let bad =
                      validEvidenceRecord validEvidenceId "ep-001"
                      |> fun s -> s.Replace("}", ",\"stdout_sha256\":\"not-valid\"}")

                  writeEvidence dir [ bad ]

                  // Capture SHA-256 before failure
                  let episodeShaBefore = getFileSha256 episodePath
                  let summaryShaBefore = getFileSha256 summaryPath

                  // Run verify - should fail due to invalid SHA-256
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"

                  // Verify SHA-256 unchanged
                  let episodeShaAfter = getFileSha256 episodePath
                  let summaryShaAfter = getFileSha256 summaryPath

                  Expect.equal episodeShaBefore episodeShaAfter "episode file SHA-256 unchanged"
                  Expect.equal summaryShaBefore summaryShaAfter "summary file SHA-256 unchanged"
              finally
                  cleanup dir
          }

          // Test 4: canonical files survive duplicate evidence ID
          test "canonical files survive duplicate evidence ID" {
              let dir = tempDir "preserve-duplicate-evidence-id"

              try
                  createMinimalStructure dir

                  // Seed canonical files with known content
                  let episodePath = Path.Combine(dir, repairEpisodesCanonicalPath)
                  let changeSetPath = Path.Combine(dir, gitChangeSetsCanonicalPath)

                  let episodeContent = "canonical-episodes-v4\n"
                  let changeSetContent = "canonical-changesets-v4\n"

                  seedCanonicalFile dir repairEpisodesCanonicalPath episodeContent
                  seedCanonicalFile dir gitChangeSetsCanonicalPath changeSetContent

                  // Write duplicate evidence
                  let rec1 = validEvidenceRecord validEvidenceId "ep-001"
                  let rec2 = validEvidenceRecord validEvidenceId "ep-002"
                  writeEvidence dir [ rec1; rec2 ]

                  // Capture SHA-256 before failure
                  let episodeShaBefore = getFileSha256 episodePath
                  let changeSetShaBefore = getFileSha256 changeSetPath

                  // Run verify - should fail due to duplicate evidence ID
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"

                  // Verify SHA-256 unchanged
                  let episodeShaAfter = getFileSha256 episodePath
                  let changeSetShaAfter = getFileSha256 changeSetPath

                  Expect.equal episodeShaBefore episodeShaAfter "episode file SHA-256 unchanged"
                  Expect.equal changeSetShaBefore changeSetShaAfter "change set file SHA-256 unchanged"
              finally
                  cleanup dir
          }

          // Test 5: canonical files survive placeholder evidence ID
          test "canonical files survive placeholder evidence ID" {
              let dir = tempDir "preserve-placeholder-evidence-id"

              try
                  createMinimalStructure dir

                  // Seed canonical files with known content
                  let episodePath = Path.Combine(dir, repairEpisodesCanonicalPath)

                  let episodeContent = "canonical-episodes-v5\n"
                  seedCanonicalFile dir repairEpisodesCanonicalPath episodeContent

                  // Write evidence with placeholder ID
                  let placeholderId = String.replicate 64 "0"
                  let evidenceRec = validEvidenceRecord placeholderId "ep-001"
                  writeEvidence dir [ evidenceRec ]

                  // Capture SHA-256 before failure
                  let episodeShaBefore = getFileSha256 episodePath

                  // Run verify - should fail due to placeholder evidence ID
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "should have issues"

                  // Verify SHA-256 unchanged
                  let episodeShaAfter = getFileSha256 episodePath
                  Expect.equal episodeShaBefore episodeShaAfter "episode file SHA-256 unchanged"
              finally
                  cleanup dir
          }

          // Test 6: actual regeneration preserves all 5 canonical files (bytes unchanged)
          testTask "regenerate preserves all canonical files (bytes unchanged)" {
              let dir = tempDir "preserve-regenerate-all"

              try
                  createMinimalStructure dir

                  // Seed ALL 5 canonical files with known bytes
                  let episodePath = Path.Combine(dir, repairEpisodesCanonicalPath)
                  let transitionPath = Path.Combine(dir, diagnosticTransitionsCanonicalPath)
                  let changeSetPath = Path.Combine(dir, gitChangeSetsCanonicalPath)
                  let summaryPath = Path.Combine(dir, repairEpisodeSummaryCanonicalPath)
                  let evidencePath = Path.Combine(dir, verificationEvidenceCanonicalPath)

                  let episodeContent = "SEEDED-EPISODES-V6-CONTENT-0123456789ABCDEF"
                  let transitionContent = "SEEDED-TRANSITIONS-V6-CONTENT-0123456789ABCDEF"
                  let changeSetContent = "SEEDED-CHANGESETS-V6-CONTENT-0123456789ABCDEF"
                  let summaryContent = "{\"schema\":\"summary-v1\"}"
                  let evidenceContent = ""

                  seedCanonicalFile dir repairEpisodesCanonicalPath episodeContent
                  seedCanonicalFile dir diagnosticTransitionsCanonicalPath transitionContent
                  seedCanonicalFile dir gitChangeSetsCanonicalPath changeSetContent
                  seedCanonicalFile dir repairEpisodeSummaryCanonicalPath summaryContent
                  seedCanonicalFile dir verificationEvidenceCanonicalPath evidenceContent

                  // Capture bytes before regeneration
                  let episodeBytesBefore = getFileBytes episodePath
                  let transitionBytesBefore = getFileBytes transitionPath
                  let changeSetBytesBefore = getFileBytes changeSetPath
                  let summaryBytesBefore = getFileBytes summaryPath
                  let evidenceBytesBefore = getFileBytes evidencePath

                  // Run regeneration via CLI
                  let! result = runRegenerate dir

                  // Regardless of result, verify bytes unchanged
                  let episodeBytesAfter = getFileBytes episodePath
                  let transitionBytesAfter = getFileBytes transitionPath
                  let changeSetBytesAfter = getFileBytes changeSetPath
                  let summaryBytesAfter = getFileBytes summaryPath
                  let evidenceBytesAfter = getFileBytes evidencePath

                  // Compare as strings for simplicity (bytes should be identical)
                  let episodeStrBefore = episodeBytesBefore |> Option.map Encoding.UTF8.GetString
                  let episodeStrAfter = episodeBytesAfter |> Option.map Encoding.UTF8.GetString

                  let transitionStrBefore =
                      transitionBytesBefore |> Option.map Encoding.UTF8.GetString

                  let transitionStrAfter = transitionBytesAfter |> Option.map Encoding.UTF8.GetString
                  let changeSetStrBefore = changeSetBytesBefore |> Option.map Encoding.UTF8.GetString
                  let changeSetStrAfter = changeSetBytesAfter |> Option.map Encoding.UTF8.GetString
                  let summaryStrBefore = summaryBytesBefore |> Option.map Encoding.UTF8.GetString
                  let summaryStrAfter = summaryBytesAfter |> Option.map Encoding.UTF8.GetString
                  let evidenceStrBefore = evidenceBytesBefore |> Option.map Encoding.UTF8.GetString
                  let evidenceStrAfter = evidenceBytesAfter |> Option.map Encoding.UTF8.GetString

                  Expect.equal episodeStrBefore episodeStrAfter "episode file bytes unchanged after regenerate"
                  Expect.equal transitionStrBefore transitionStrAfter "transition file bytes unchanged after regenerate"
                  Expect.equal changeSetStrBefore changeSetStrAfter "change set file bytes unchanged after regenerate"
                  Expect.equal summaryStrBefore summaryStrAfter "summary file bytes unchanged after regenerate"
                  Expect.equal evidenceStrBefore evidenceStrAfter "evidence file bytes unchanged after regenerate"
              finally
                  cleanup dir
          }

          // Test 7: verify canonical file preservation across verify failures
          test "canonical files preserved across verify failures (SHA-256 comparison)" {
              let dir = tempDir "preserve-verify-sha256"

              try
                  createMinimalStructure dir

                  // Seed all canonical files
                  let files =
                      [ (repairEpisodesCanonicalPath, "EPISODES-CONTENT-ABCDEF123456")
                        (diagnosticTransitionsCanonicalPath, "TRANSITIONS-CONTENT-GHIJKL789012")
                        (gitChangeSetsCanonicalPath, "CHANGESETS-CONTENT-MNOPQR345678")
                        (repairEpisodeSummaryCanonicalPath, "{\"schema\":\"summary-v2\"}") ]

                  files |> List.iter (fun (path, content) -> seedCanonicalFile dir path content)

                  // Write malformed evidence to trigger verify failure
                  writeEvidence dir [ """{"invalid json""" ]

                  // Capture SHA-256 of all files before
                  let sha256Before =
                      files
                      |> List.map (fun (path, _) ->
                          let fullPath = Path.Combine(dir, path)
                          path, getFileSha256 fullPath)

                  // Run verify - should fail
                  let vr = runVerify dir
                  Expect.isTrue (List.length vr.Issues > 0) "verify should fail"

                  // Verify SHA-256 unchanged
                  sha256Before
                  |> List.iter (fun (path, shaBefore) ->
                      let fullPath = Path.Combine(dir, path)
                      let shaAfter = getFileSha256 fullPath
                      Expect.equal shaBefore shaAfter (sprintf "SHA-256 of %s unchanged" path))
              finally
                  cleanup dir
          } ]
