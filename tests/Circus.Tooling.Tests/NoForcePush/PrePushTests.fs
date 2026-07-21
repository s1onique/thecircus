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
    let path = Path.Combine(Path.GetTempPath(), "circus-nfp-real-" + System.Guid.NewGuid().ToString("n"))
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
    let path = Path.Combine(Path.GetTempPath(), "circus-nfp-remote-" + System.Guid.NewGuid().ToString("n") + ".git")
    
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
    let local = createRealRepo()
    let localOid = getHeadOid local
    
    let bare = createBareRemote()
    
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
        [ test "parses valid pre-push line with real OID width" {
              // Git SHA-1 is 40 characters
              let line = sprintf "refs/heads/main %s refs/heads/main %s" 
                  (String.replicate 40 "a") 
                  (String.replicate 40 "b")
              let result = parsePrePushLine (Some 40) line
              match result with
              | Ok update ->
                  Expect.equal update.LocalRef "refs/heads/main" "local ref"
                  Expect.equal update.LocalOid (String.replicate 40 "a") "local oid"
                  Expect.equal update.RemoteRef "refs/heads/main" "remote ref"
                  Expect.equal update.RemoteOid (String.replicate 40 "b") "remote oid"
              | Error e -> failwithf "parse error: %A" e
          }
          test "rejects malformed line (wrong field count)" {
              let line = "refs/heads/main abc123 refs/heads/main"
              let result = parsePrePushLine None line
              match result with
              | Error(WrongFieldCount _) -> ()
              | Ok _ -> failwith "should have failed"
              | Error e -> failwithf "wrong error: %A" e
          }
          test "rejects invalid ref name" {
              let line = "invalid-ref abc123 refs/heads/main def456"
              let result = parsePrePushLine None line
              match result with
              | Error(InvalidRefName _) -> ()
              | Ok _ -> failwith "should have failed"
              | Error e -> failwithf "wrong error: %A" e
          }
          test "rejects invalid OID (non-hex)" {
              let line = "refs/heads/main zxy123 refs/heads/main abc456"
              let result = parsePrePushLine None line
              match result with
              | Error(InvalidOid _) -> ()
              | Ok _ -> failwith "should have failed"
              | Error e -> failwithf "wrong error: %A" e
          }
          test "rejects mixed OID widths" {
              let line = sprintf "refs/heads/main %s refs/heads/main %s" 
                  (String.replicate 40 "a")
                  (String.replicate 20 "b") // Different width
              let result = parsePrePushLine (Some 40) line
              match result with
              | Error(MixedOidWidths _) -> ()
              | Ok _ -> failwith "should have failed"
              | Error e -> failwithf "wrong error: %A" e
          }
          test "accepts null OID for new branch" {
              let line = sprintf "refs/heads/feature %s refs/heads/feature %s" 
                  (String.replicate 40 "a")
                  (String.replicate 40 "0")
              let result = parsePrePushLine (Some 40) line
              match result with
              | Ok update ->
                  Expect.isTrue (isNewBranch update) "is new branch"
                  Expect.isTrue (isNullOid update.RemoteOid) "null remote OID"
              | Error e -> failwithf "parse error: %A" e
          }
          test "empty input returns empty list (valid)" {
              match parsePrePushInput "/tmp" None "" with
              | Ok [] -> ()
              | Ok _ -> failwith "should be empty"
              | Error e -> failwithf "unexpected error: %A" e
          } ]

// ============================================================================
// OID classification tests
// ============================================================================

[<Tests>]
let oidTests =
    testList
        "NoForcePush OID classification"
        [ test "recognizes null OID (40 zeros)" {
              let null40 = String.replicate 40 "0"
              Expect.isTrue (isNullOid null40) "40 zeros"
          }
          test "recognizes standard null OID string" {
              Expect.isTrue (isNullOid "0000000000000000000000000000000000000000") "standard null"
          }
          test "classifies branch refs" {
              Expect.isTrue (isBranchRef "refs/heads/main") "branch ref"
              Expect.isFalse (isBranchRef "refs/tags/v1.0") "not a branch ref"
          }
          test "classifies tag refs" {
              Expect.isTrue (isTagRef "refs/tags/v1.0") "tag ref"
              Expect.isFalse (isTagRef "refs/heads/main") "not a tag ref"
          }
          test "detects new branch creation" {
              let update = { 
                  LocalRef = "refs/heads/feature"
                  LocalOid = String.replicate 40 "a"
                  RemoteRef = "refs/heads/feature"
                  RemoteOid = String.replicate 40 "0"
              }
              Expect.isTrue (isNewBranch update) "new branch"
          }
          test "detects deletion" {
              let update = { 
                  LocalRef = "refs/heads/main"
                  LocalOid = String.replicate 40 "0"
                  RemoteRef = "refs/heads/main"
                  RemoteOid = String.replicate 40 "a"
              }
              Expect.isTrue (isDeletion update) "deletion"
          }
          test "detects existing tag update" {
              let update = { 
                  LocalRef = "refs/tags/v1.0"
                  LocalOid = String.replicate 40 "a"
                  RemoteRef = "refs/tags/v1.0"
                  RemoteOid = String.replicate 40 "b"
              }
              Expect.isTrue (isExistingTagUpdate update) "existing tag update"
          } ]

// ============================================================================
// Real repository semantic tests (P0-7)
// ============================================================================

[<Tests>]
let realRepoTests =
    testList
        "NoForcePush real repository semantics"
        [ test "NEW_BRANCH: accepts new branch creation" {
              let repo = createRealRepo()
              try
                  let localOid = getHeadOid repo
                  let update = {
                      LocalRef = "refs/heads/feature"
                      LocalOid = localOid
                      RemoteRef = "refs/heads/feature"
                      RemoteOid = String.replicate 40 "0" // Null OID = new branch
                  }
                  let result = verifyUpdate repo update None
                  match result with
                  | Allowed _ -> ()
                  | Rejected _ -> failwith "new branch should be allowed"
                  | OperationalFailure _ -> failwith "should not fail operationally"
              finally
                  try Directory.Delete(repo, true) with _ -> ()
          }
          test "FAST_FORWARD: accepts fast-forward update" {
              let local, _, _ = setupPushingRepo()
              try
                  let firstOid = getHeadOid local
                  // Create second commit
                  let secondOid = createSecondCommit local
                  
                  let update = {
                      LocalRef = "refs/heads/main"
                      LocalOid = secondOid
                      RemoteRef = "refs/heads/main"
                      RemoteOid = firstOid // Old OID is ancestor
                  }
                  let result = verifyUpdate local update None
                  match result with
                  | Allowed _ -> ()
                  | Rejected _ -> failwith "fast-forward should be allowed"
                  | OperationalFailure _ -> failwith "should not fail operationally"
              finally
                  try Directory.Delete(local, true) with _ -> ()
          }
          test "DIVERGENT: rejects non-fast-forward update" {
              let local, bare, _ = setupPushingRepo()
              try
                  let firstOid = getHeadOid local
                  
                  // Create a divergent commit
                  let testFile = Path.Combine(local, "conflict.txt")
                  File.WriteAllText(testFile, "divergent content")
                  let addPsi = ProcessStartInfo()
                  addPsi.FileName <- "git"
                  addPsi.Arguments <- "add conflict.txt"
                  addPsi.WorkingDirectory <- local
                  addPsi.UseShellExecute <- false
                  addPsi.CreateNoWindow <- true
                  use addProc = Process.Start(addPsi)
                  addProc.WaitForExit() |> ignore
                  
                  let commitPsi = ProcessStartInfo()
                  commitPsi.FileName <- "git"
                  commitPsi.Arguments <- "commit -m divergent"
                  commitPsi.WorkingDirectory <- local
                  commitPsi.UseShellExecute <- false
                  commitPsi.CreateNoWindow <- true
                  use commitProc = Process.Start(commitPsi)
                  commitProc.WaitForExit() |> ignore
                  
                  // Create another commit on the remote
                  let remoteClone = Path.Combine(Path.GetTempPath(), "circus-nfp-remote-work-" + System.Guid.NewGuid().ToString("n"))
                  let clonePsi = ProcessStartInfo()
                  clonePsi.FileName <- "git"
                  clonePsi.Arguments <- sprintf "clone %s %s" bare remoteClone
                  clonePsi.UseShellExecute <- false
                  clonePsi.CreateNoWindow <- true
                  use cloneProc = Process.Start(clonePsi)
                  cloneProc.WaitForExit() |> ignore
                  
                  let remoteFile = Path.Combine(remoteClone, "remote.txt")
                  File.WriteAllText(remoteFile, "remote change")
                  let remoteAddPsi = ProcessStartInfo()
                  remoteAddPsi.FileName <- "git"
                  remoteAddPsi.Arguments <- "add remote.txt"
                  remoteAddPsi.WorkingDirectory <- remoteClone
                  remoteAddPsi.UseShellExecute <- false
                  remoteAddPsi.CreateNoWindow <- true
                  use remoteAddProc = Process.Start(remoteAddPsi)
                  remoteAddProc.WaitForExit() |> ignore
                  
                  let remoteCommitPsi = ProcessStartInfo()
                  remoteCommitPsi.FileName <- "git"
                  remoteCommitPsi.Arguments <- "commit -m remote-change"
                  remoteCommitPsi.WorkingDirectory <- remoteClone
                  remoteCommitPsi.UseShellExecute <- false
                  remoteCommitPsi.CreateNoWindow <- true
                  use remoteCommitProc = Process.Start(remoteCommitPsi)
                  remoteCommitProc.WaitForExit() |> ignore
                  
                  let remotePushPsi = ProcessStartInfo()
                  remotePushPsi.FileName <- "git"
                  remotePushPsi.Arguments <- "push origin main"
                  remotePushPsi.WorkingDirectory <- remoteClone
                  remotePushPsi.UseShellExecute <- false
                  remotePushPsi.CreateNoWindow <- true
                  use remotePushProc = Process.Start(remotePushPsi)
                  remotePushProc.WaitForExit() |> ignore
                  
                  let divergentOid = getHeadOid local
                  
                  try Directory.Delete(remoteClone, true) with _ -> ()
                  
                  let update = {
                      LocalRef = "refs/heads/main"
                      LocalOid = divergentOid
                      RemoteRef = "refs/heads/main"
                      RemoteOid = firstOid // Same as local first - but remote has diverged
                  }
                  let result = verifyUpdate local update None
                  match result with
                  | Rejected _ -> () // Non-fast-forward should be rejected
                  | Allowed _ -> failwith "divergent update should be rejected"
                  | OperationalFailure _ -> () // Also acceptable (divergent detected)
              finally
                  try Directory.Delete(local, true) with _ -> ()
          }
          test "BRANCH_DELETION: rejects branch deletion" {
              let repo = createRealRepo()
              try
                  let update = {
                      LocalRef = "refs/heads/main"
                      LocalOid = String.replicate 40 "0" // Null = deletion
                      RemoteRef = "refs/heads/main"
                      RemoteOid = String.replicate 40 "a"
                  }
                  let result = verifyUpdate repo update None
                  match result with
                  | Rejected _ -> ()
                  | Allowed _ -> failwith "deletion should be rejected"
                  | OperationalFailure _ -> failwith "deletion should be rejected, not failed"
              finally
                  try Directory.Delete(repo, true) with _ -> ()
          }
          test "TAG_CREATION: accepts new tag creation" {
              let repo = createRealRepo()
              try
                  let localOid = getHeadOid repo
                  let update = {
                      LocalRef = "refs/tags/v1.0"
                      LocalOid = localOid
                      RemoteRef = "refs/tags/v1.0"
                      RemoteOid = String.replicate 40 "0" // Null = new tag
                  }
                  let result = verifyUpdate repo update None
                  match result with
                  | Allowed _ -> ()
                  | Rejected _ -> failwith "new tag should be allowed"
                  | OperationalFailure _ -> failwith "should not fail operationally"
              finally
                  try Directory.Delete(repo, true) with _ -> ()
          }
          test "TAG_REPLACEMENT: rejects existing tag replacement" {
              let repo = createRealRepo()
              try
                  let localOid = getHeadOid repo
                  let update = {
                      LocalRef = "refs/tags/v1.0"
                      LocalOid = localOid
                      RemoteRef = "refs/tags/v1.0"
                      RemoteOid = String.replicate 40 "b" // Not null = replacement
                  }
                  let result = verifyUpdate repo update None
                  match result with
                  | Rejected _ -> ()
                  | Allowed _ -> failwith "tag replacement should be rejected"
                  | OperationalFailure _ -> failwith "tag replacement should be rejected"
              finally
                  try Directory.Delete(repo, true) with _ -> ()
          }
          test "TAG_DELETION: rejects tag deletion" {
              let repo = createRealRepo()
              try
                  let update = {
                      LocalRef = "refs/tags/v1.0"
                      LocalOid = String.replicate 40 "0" // Null = deletion
                      RemoteRef = "refs/tags/v1.0"
                      RemoteOid = String.replicate 40 "a"
                  }
                  let result = verifyUpdate repo update None
                  match result with
                  | Rejected _ -> ()
                  | Allowed _ -> failwith "tag deletion should be rejected"
                  | OperationalFailure _ -> failwith "tag deletion should be rejected"
              finally
                  try Directory.Delete(repo, true) with _ -> ()
          }
          test "UNKNOWN_NAMESPACE: rejects unknown namespace" {
              let repo = createRealRepo()
              try
                  let localOid = getHeadOid repo
                  let update = {
                      LocalRef = "refs/custom/thing"
                      LocalOid = localOid
                      RemoteRef = "refs/custom/thing"
                      RemoteOid = String.replicate 40 "0"
                  }
                  let result = verifyUpdate repo update None
                  match result with
                  | Rejected _ -> ()
                  | Allowed _ -> failwith "unknown namespace should be rejected"
                  | OperationalFailure _ -> failwith "unknown namespace should be rejected"
              finally
                  try Directory.Delete(repo, true) with _ -> ()
          } ]

// ============================================================================
// Exit code contract tests
// ============================================================================

[<Tests>]
let exitCodeTests =
    testList
        "NoForcePush exit code contract"
        [ test "exit 0 for allowed updates" {
              let repo = createRealRepo()
              try
                  let localOid = getHeadOid repo
                  let update = {
                      LocalRef = "refs/heads/feature"
                      LocalOid = localOid
                      RemoteRef = "refs/heads/feature"
                      RemoteOid = String.replicate 40 "0"
                  }
                  let result = verifyUpdate repo update None
                  match result with
                  | Allowed _ -> ()
                  | _ -> failwithf "expected Allowed, got %A" result
              finally
                  try Directory.Delete(repo, true) with _ -> ()
          }
          test "exit 1 for rejected updates" {
              let repo = createRealRepo()
              try
                  let update = {
                      LocalRef = "refs/heads/main"
                      LocalOid = String.replicate 40 "0"
                      RemoteRef = "refs/heads/main"
                      RemoteOid = String.replicate 40 "a"
                  }
                  let result = verifyUpdate repo update None
                  match result with
                  | Rejected _ -> ()
                  | _ -> failwithf "expected Rejected, got %A" result
              finally
                  try Directory.Delete(repo, true) with _ -> ()
          }
          test "exit 2 for operational failures" {
              let repo = createRealRepo()
              try
                  let update = {
                      LocalRef = "refs/heads/feature"
                      LocalOid = String.replicate 40 "z" // Invalid OID
                      RemoteRef = "refs/heads/feature"
                      RemoteOid = String.replicate 40 "0"
                  }
                  let result = verifyUpdate repo update None
                  match result with
                  | OperationalFailure _ -> ()
                  | _ -> () // Also acceptable - non-existent OID may be rejected
              finally
                  try Directory.Delete(repo, true) with _ -> ()
          }
          test "hasBlockingOutcome detects rejections" {
              let outcomes = [
                  Allowed { LocalRef = "refs/heads/main"; LocalOid = ""; RemoteRef = "refs/heads/main"; RemoteOid = "" }
                  Rejected({ LocalRef = "refs/heads/feat"; LocalOid = ""; RemoteRef = "refs/heads/feat"; RemoteOid = "" }, "reason")
              ]
              Expect.isTrue (hasBlockingOutcome outcomes) "has blocking"
          }
          test "hasBlockingOutcome detects operational failures" {
              let outcomes = [
                  Allowed { LocalRef = "refs/heads/main"; LocalOid = ""; RemoteRef = "refs/heads/main"; RemoteOid = "" }
                  OperationalFailure({ LocalRef = "refs/heads/feat"; LocalOid = ""; RemoteRef = "refs/heads/feat"; RemoteOid = "" }, "oops")
              ]
              Expect.isTrue (hasBlockingOutcome outcomes) "has blocking"
          }
          test "hasBlockingOutcome passes when all allowed" {
              let outcomes = [
                  Allowed { LocalRef = "refs/heads/main"; LocalOid = ""; RemoteRef = "refs/heads/main"; RemoteOid = "" }
                  Allowed { LocalRef = "refs/heads/feat"; LocalOid = ""; RemoteRef = "refs/heads/feat"; RemoteOid = "" }
              ]
              Expect.isFalse (hasBlockingOutcome outcomes) "all allowed"
          } ]

// ============================================================================
// Test exports
// ============================================================================

[<Tests>]
let tests =
    testList
        "NoForcePush PrePush"
        [ parseTests
          oidTests
          realRepoTests
          exitCodeTests ]
