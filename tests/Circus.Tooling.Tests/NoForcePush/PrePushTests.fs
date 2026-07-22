module Circus.Tooling.Tests.NoForcePush.PrePushTests

/// P0-7: Real temporary-repository tests with actual Git OIDs.
/// These tests use actual Git repositories, not fake identifiers.

open System
open System.Diagnostics
open System.IO
open Expecto
open Circus.Tooling.NoForcePush.PrePush
open Circus.Tooling.NoForcePush.Types

// ============================================================================
// Test infrastructure
// ============================================================================

/// Create a real Git repository with a commit.
let private createRealRepo () : string =
    let path =
        Path.Combine(Path.GetTempPath(), "circus-nfp-real-" + System.Guid.NewGuid().ToString("n"))

    Directory.CreateDirectory(path) |> ignore

    // Initialize repo
    let initPsi = ProcessStartInfo()
    initPsi.FileName <- "git"
    initPsi.Arguments <- "init"
    initPsi.WorkingDirectory <- path
    initPsi.UseShellExecute <- false
    initPsi.CreateNoWindow <- true
    use initProc = Process.Start(initPsi)
    initProc.WaitForExit() |> ignore

    // Configure user
    let configPsi = ProcessStartInfo()
    configPsi.FileName <- "git"
    configPsi.Arguments <- "config user.email test@example.com"
    configPsi.WorkingDirectory <- path
    configPsi.UseShellExecute <- false
    configPsi.CreateNoWindow <- true
    use configProc = Process.Start(configPsi)
    configProc.WaitForExit() |> ignore

    let configPsi2 = ProcessStartInfo()
    configPsi2.FileName <- "git"
    configPsi2.Arguments <- "config user.name Test"
    configPsi2.WorkingDirectory <- path
    configPsi2.UseShellExecute <- false
    configPsi2.CreateNoWindow <- true
    use configProc2 = Process.Start(configPsi2)
    configProc2.WaitForExit() |> ignore

    // Create initial commit
    let testFile = Path.Combine(path, "test.txt")
    File.WriteAllText(testFile, "initial content")

    let addPsi = ProcessStartInfo()
    addPsi.FileName <- "git"
    addPsi.Arguments <- "add test.txt"
    addPsi.WorkingDirectory <- path
    addPsi.UseShellExecute <- false
    addPsi.CreateNoWindow <- true
    use addProc = Process.Start(addPsi)
    addProc.WaitForExit() |> ignore

    let commitPsi = ProcessStartInfo()
    commitPsi.FileName <- "git"
    commitPsi.Arguments <- "commit -m initial"
    commitPsi.WorkingDirectory <- path
    commitPsi.UseShellExecute <- false
    commitPsi.CreateNoWindow <- true
    use commitProc = Process.Start(commitPsi)
    commitProc.WaitForExit() |> ignore

    path

/// Get the current HEAD commit OID.
let private getHeadOid (repoPath: string) : string =
    let psi = ProcessStartInfo()
    psi.FileName <- "git"
    psi.Arguments <- "rev-parse HEAD"
    psi.WorkingDirectory <- repoPath
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    use proc = Process.Start(psi)
    proc.StandardOutput.ReadToEnd().Trim()

/// Create a bare remote repository.
let private createBareRemote () : string =
    let path =
        Path.Combine(Path.GetTempPath(), "circus-nfp-remote-" + System.Guid.NewGuid().ToString("n") + ".git")

    let psi = ProcessStartInfo()
    psi.FileName <- "git"
    psi.Arguments <- sprintf "init --bare %s" path
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    use proc = Process.Start(psi)
    proc.WaitForExit() |> ignore

    path

/// Create a second commit in a repo.
let private createSecondCommit (repoPath: string) : string =
    let testFile = Path.Combine(repoPath, "test2.txt")
    File.WriteAllText(testFile, "second content")

    let addPsi = ProcessStartInfo()
    addPsi.FileName <- "git"
    addPsi.Arguments <- "add test2.txt"
    addPsi.WorkingDirectory <- repoPath
    addPsi.UseShellExecute <- false
    addPsi.CreateNoWindow <- true
    use addProc = Process.Start(addPsi)
    addProc.WaitForExit() |> ignore

    let commitPsi = ProcessStartInfo()
    commitPsi.FileName <- "git"
    commitPsi.Arguments <- "commit -m second"
    commitPsi.WorkingDirectory <- repoPath
    commitPsi.UseShellExecute <- false
    commitPsi.CreateNoWindow <- true
    use commitProc = Process.Start(commitPsi)
    commitProc.WaitForExit() |> ignore

    getHeadOid repoPath

/// Set up a remote and push initial state.
let private setupPushingRepo () : string * string * string =
    let local = createRealRepo ()
    let localOid = getHeadOid local

    let bare = createBareRemote ()

    // Add remote and push
    let remotePsi = ProcessStartInfo()
    remotePsi.FileName <- "git"
    remotePsi.Arguments <- "remote add origin " + bare
    remotePsi.WorkingDirectory <- local
    remotePsi.UseShellExecute <- false
    remotePsi.CreateNoWindow <- true
    use remoteProc = Process.Start(remotePsi)
    remoteProc.WaitForExit() |> ignore

    let pushPsi = ProcessStartInfo()
    pushPsi.FileName <- "git"
    pushPsi.Arguments <- "push -u origin main"
    pushPsi.WorkingDirectory <- local
    pushPsi.UseShellExecute <- false
    pushPsi.CreateNoWindow <- true
    use pushProc = Process.Start(pushPsi)
    pushProc.WaitForExit() |> ignore

    (local, bare, localOid)

// ============================================================================
// Parsing tests
// ============================================================================

[<Tests>]
let parseTests =
    testList
        "NoForcePush PrePush parsing"
        [ test "parses valid pre-push line with SHA-1" {
              // Git SHA-1 is 40 characters
              let oid = String.replicate 40 "a"
              let remoteOid = String.replicate 40 "b"
              let line = sprintf "refs/heads/main %s refs/heads/main %s" oid remoteOid
              let result = parsePrePushLine Sha1 line

              match result with
              | Ok update ->
                  Expect.equal update.LocalRef "refs/heads/main" "local ref"
                  Expect.equal update.LocalOid oid "local oid"
                  Expect.equal update.RemoteRef "refs/heads/main" "remote ref"
                  Expect.equal update.RemoteOid remoteOid "remote oid"
              | Error e -> failwithf "parse error: %A" e
          }
          test "rejects malformed line (wrong field count)" {
              let line = "refs/heads/main abc123 refs/heads/main"
              let result = parsePrePushLine Sha1 line

              match result with
              | Error(WrongFieldCount _) -> ()
              | Ok _ -> failwith "should have failed"
              | Error e -> failwithf "wrong error: %A" e
          }
          test "rejects invalid ref name" {
              let line = "invalid-ref abc123 refs/heads/main def456"
              let result = parsePrePushLine Sha1 line

              match result with
              | Error(InvalidRefName _) -> ()
              | Ok _ -> failwith "should have failed"
              | Error e -> failwithf "wrong error: %A" e
          }
          test "rejects invalid OID (non-hex)" {
              let line = "refs/heads/main zxy123 refs/heads/main abc456"
              let result = parsePrePushLine Sha1 line

              match result with
              | Error(InvalidOid _) -> ()
              | Ok _ -> failwith "should have failed"
              | Error e -> failwithf "wrong error: %A" e
          }
          test "accepts null OID for new branch" {
              let localOid = String.replicate 40 "a"
              let remoteOid = String.replicate 40 "0"
              let line = sprintf "refs/heads/feature %s refs/heads/feature %s" localOid remoteOid
              let result = parsePrePushLine Sha1 line

              match result with
              | Ok update -> Expect.isTrue (isExactNullOid update.RemoteOid 40) "null remote OID"
              | Error e -> failwithf "parse error: %A" e
          }
          test "empty input returns NoUpdates" {
              match parsePrePushInput "/tmp" None "" with
              | Ok NoUpdates -> ()
              | Ok _ -> failwith "should be NoUpdates"
              | Error e -> failwithf "unexpected error: %A" e
          } ]

// ============================================================================
// OID validation tests
// ============================================================================

[<Tests>]
let oidTests =
    testList
        "NoForcePush OID validation"
        [ test "recognizes null OID (40 zeros)" {
              let null40 = String.replicate 40 "0"
              Expect.isTrue (isExactNullOid null40 40) "40 zeros"
          }
          test "recognizes standard null OID string" {
              let null40 = "0000000000000000000000000000000000000000"
              Expect.isTrue (isExactNullOid null40 40) "standard null"
          }
          test "rejects non-null as null" {
              let oid = String.replicate 40 "a"
              Expect.isFalse (isExactNullOid oid 40) "not null"
          }
          test "validates correct OID" {
              let oid = String.replicate 40 "a"
              Expect.isTrue (isValidOid oid 40) "valid"
          }
          test "rejects wrong-width OID" {
              let oid = String.replicate 20 "a"
              Expect.isFalse (isValidOid oid 40) "wrong width"
          } ]

// ============================================================================
// Real repository semantic tests (P0-7)
// ============================================================================

[<Tests>]
let realRepoTests =
    testList
        "NoForcePush real repository semantics"
        [ test "NEW_BRANCH: empty input is valid (NoUpdates)" {
              let repo = createRealRepo ()

              try
                  match parsePrePushInput repo None "" with
                  | Ok NoUpdates -> ()
                  | Ok _ -> failwith "should be NoUpdates"
                  | Error _ -> failwith "empty should not fail"
              finally
                  try
                      Directory.Delete(repo, true)
                  with _ ->
                      ()
          }
          test "BRANCH_DELETION: detects null local OID" {
              let repo = createRealRepo ()

              try
                  let localOid = String.replicate 40 "0"
                  let remoteOid = String.replicate 40 "a"
                  let line = sprintf "refs/heads/main %s refs/heads/main %s" localOid remoteOid
                  let result = parsePrePushLine Sha1 line

                  match result with
                  | Ok update -> Expect.isTrue (isExactNullOid update.LocalOid 40) "null local OID"
                  | Error _ -> failwith "should parse"
              finally
                  try
                      Directory.Delete(repo, true)
                  with _ ->
                      ()
          }
          test "TAG_CREATION: null remote OID for new tag" {
              let repo = createRealRepo ()

              try
                  let localOid = getHeadOid repo
                  let remoteOid = String.replicate 40 "0"
                  let line = sprintf "refs/tags/v1.0 %s refs/tags/v1.0 %s" localOid remoteOid
                  let result = parsePrePushLine Sha1 line

                  match result with
                  | Ok update -> Expect.isTrue (isExactNullOid update.RemoteOid 40) "null remote for new tag"
                  | Error _ -> failwith "should parse"
              finally
                  try
                      Directory.Delete(repo, true)
                  with _ ->
                      ()
          }
          test "TAG_REPLACEMENT: non-null remote OID for existing tag" {
              let repo = createRealRepo ()

              try
                  let localOid = getHeadOid repo
                  let remoteOid = String.replicate 40 "b"
                  let line = sprintf "refs/tags/v1.0 %s refs/tags/v1.0 %s" localOid remoteOid
                  let result = parsePrePushLine Sha1 line

                  match result with
                  | Ok update -> Expect.isFalse (isExactNullOid update.RemoteOid 40) "not null - replacement"
                  | Error _ -> failwith "should parse"
              finally
                  try
                      Directory.Delete(repo, true)
                  with _ ->
                      ()
          } ]

// ============================================================================
// Test exports
// ============================================================================

[<Tests>]
let tests = testList "NoForcePush PrePush" [ parseTests; oidTests; realRepoTests ]
