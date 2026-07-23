module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.GitIdentityTests

open Expecto
open System
open System.Diagnostics
open System.IO
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git

let private newTempDir () =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            "repair-episodes-git-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (dir: string) =
    if Directory.Exists dir then
        try
            Directory.Delete(dir, true)
        with _ -> ()

let private runShellArgs (repoRoot: string) (args: string list) : int =
    let psi = ProcessStartInfo()
    psi.FileName <- "git"
    psi.WorkingDirectory <- repoRoot
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    for a in args do
        psi.ArgumentList.Add a
    let p = Process.Start psi
    p.WaitForExit() |> ignore
    p.ExitCode

let private runShellArgsIgnore (repoRoot: string) (args: string list) : unit =
    runShellArgs repoRoot args |> ignore

let private runShell (repoRoot: string) (args: string) : int =
    let parts = args.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    runShellArgs repoRoot parts

let private runShellIgnore (repoRoot: string) (args: string) : unit =
    runShell repoRoot args |> ignore

let private initRepoWithCommits () : string * string * string =
    let dir = newTempDir ()
    runShellIgnore dir "init -q"
    runShellIgnore dir "config user.email test@example.com"
    runShellIgnore dir "config user.name tester"
    File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha\n")
    runShellIgnore dir "add a.txt"
    runShellIgnore dir "-c core.quotepath=false commit -q -m first"
    let c1 = runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ] |> fun r -> r.Stdout.Trim()
    File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha-changed\n")
    runShellIgnore dir "add a.txt"
    runShellIgnore dir "-c core.quotepath=false commit -q -m second"
    let c2 = runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ] |> fun r -> r.Stdout.Trim()
    dir, c1, c2

let private expectGitIdentityFailure (label: string) (body: unit -> 'a) =
    let mutable captured = false
    try
        body () |> ignore
    with
    | :? GitIdentityFailure -> captured <- true
    | _ -> ()
    Expect.isTrue captured label

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.RepairEpisodes.GitIdentity"
        [ test "detectObjectFormat returns sha1 for default repositories" {
              let dir, before, after = initRepoWithCommits ()
              try
                  let fmt = detectObjectFormat dir
                  Expect.equal fmt Sha1 "expected sha1 format"
                  let identity = resolveGitIdentity dir defaultGitRunOptions before after
                  Expect.equal identity.BeforeCommitOid before "before commit oid"
                  Expect.equal identity.AfterCommitOid after "after commit oid"
                  Expect.isGreaterThan (List.length identity.CommitRange) 0 "commit range"
                  Expect.equal identity.ObjectFormat Sha1 "object format"
              finally
                  cleanup dir
          }
          test "abbreviated OIDs are rejected" {
              let dir, before, _after = initRepoWithCommits ()
              try
                  let abbreviated = before.Substring(0, 7)
                  expectGitIdentityFailure "abbreviated OID must be rejected" (fun () ->
                      resolveGitIdentity dir defaultGitRunOptions abbreviated abbreviated
                      |> ignore)
              finally
                  cleanup dir
          }
          test "wrong-width OIDs are rejected" {
              let dir, _before, _after = initRepoWithCommits ()
              try
                  let tooShort = "abc123"
                  expectGitIdentityFailure "wrong-width OID must be rejected" (fun () ->
                      resolveGitIdentity dir defaultGitRunOptions tooShort tooShort
                      |> ignore)
              finally
                  cleanup dir
          }
          test "missing objects are rejected" {
              let dir, _before, _after = initRepoWithCommits ()
              try
                  let sameOid = String('0', 40)
                  expectGitIdentityFailure "missing OID must be rejected" (fun () ->
                      resolveGitIdentity dir defaultGitRunOptions sameOid sameOid
                      |> ignore)
              finally
                  cleanup dir
          }
          test "diff-tree parses modify entries" {
              let dir, before, after = initRepoWithCommits ()
              try
                  let identity = resolveGitIdentity dir defaultGitRunOptions before after
                  let entries =
                      computeChangeSet dir defaultGitRunOptions identity.ObjectFormat
                          identity.BeforeTreeOid identity.AfterTreeOid
                  Expect.isGreaterThan (List.length entries) 0 "expected at least one entry"
                  let aTxt = entries |> List.tryFind (fun e -> e.CanonicalPath = "a.txt")
                  Expect.isSome aTxt "expected a.txt entry"
                  let aTxt = Option.get aTxt
                  Expect.equal aTxt.ChangeKind Modified "kind is Modified"
                  Expect.isSome aTxt.BeforeBlobOid "before blob present"
                  Expect.isSome aTxt.AfterBlobOid "after blob present"
              finally
                  cleanup dir
          }
          test "rename appears as delete+add under --no-renames" {
              let dir = newTempDir ()
              try
                  runShellIgnore dir "init -q"
                  runShellIgnore dir "config user.email test@example.com"
                  runShellIgnore dir "config user.name tester"
                  File.WriteAllText(Path.Combine(dir, "old.txt"), "rename-content\n")
                  runShellIgnore dir "add old.txt"
                  runShellIgnore dir "-c core.quotepath=false commit -q -m initial"
                  let c1 = runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ] |> fun r -> r.Stdout.Trim()
                  runShellIgnore dir "mv old.txt new.txt"
                  runShellIgnore dir "-c core.quotepath=false commit -q -m rename"
                  let c2 = runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ] |> fun r -> r.Stdout.Trim()
                  let identity = resolveGitIdentity dir defaultGitRunOptions c1 c2
                  let entries =
                      computeChangeSet dir defaultGitRunOptions identity.ObjectFormat
                          identity.BeforeTreeOid identity.AfterTreeOid
                  let hasDelete =
                      entries
                      |> List.exists (fun e ->
                          e.ChangeKind = Deleted && e.CanonicalPath = "old.txt")
                  let hasAdd =
                      entries
                      |> List.exists (fun e ->
                          e.ChangeKind = Added && e.CanonicalPath = "new.txt")
                  Expect.isTrue hasDelete "rename appears as delete of old.txt"
                  Expect.isTrue hasAdd "rename appears as add of new.txt"
              finally
                  cleanup dir
          }
          test "change-set identity is deterministic across runs" {
              let dir, before, after = initRepoWithCommits ()
              try
                  let identity = resolveGitIdentity dir defaultGitRunOptions before after
                  let entries =
                      computeChangeSet dir defaultGitRunOptions identity.ObjectFormat
                          identity.BeforeTreeOid identity.AfterTreeOid
                  let id1 =
                      computeChangeSetIdentity identity.BeforeTreeOid identity.AfterTreeOid entries
                  let id2 =
                      computeChangeSetIdentity identity.BeforeTreeOid identity.AfterTreeOid entries
                  Expect.equal id1 id2 "identity must be deterministic"
                  Expect.equal (String.length id1) 64 "sha256 length"
              finally
                  cleanup dir
          }
          test "object format detection is cached across calls" {
              let dir, before, after = initRepoWithCommits ()
              try
                  clearObjectFormatCache ()
                  let f1 = detectObjectFormat dir
                  let f2 = detectObjectFormat dir
                  Expect.equal f1 f2 "object format detection must be stable"
                  let identity = resolveGitIdentity dir defaultGitRunOptions before after
                  Expect.equal identity.ObjectFormat f1 "identity uses cached format"
              finally
                  cleanup dir
          }
          test "isValidOid accepts sha1 OIDs" {
              Expect.isTrue (isValidOid Sha1 (String('a', 40))) "sha1 OID"
              Expect.isFalse (isValidOid Sha1 "abc") "wrong width"
              Expect.isFalse (isValidOid Sha1 (String('z', 40))) "non-hex character"
          }
          test "isValidOid accepts sha256 OIDs" {
              Expect.isTrue (isValidOid Sha256 (String('a', 64))) "sha256 OID"
              Expect.isFalse (isValidOid Sha256 (String('a', 40))) "wrong width"
          }
          test "non-ancestor relationship is rejected" {
              let dir, _before, _after = initRepoWithCommits ()
              try
                  // Same commit used as before and after ⇒ not a proper ancestor range.
                  let sameOid = String('a', 40)
                  expectGitIdentityFailure "non-ancestor must be rejected" (fun () ->
                      resolveGitIdentity dir defaultGitRunOptions sameOid sameOid
                      |> ignore)
              finally
                  cleanup dir
          }
          test "diff-tree entries are ordinally ordered by canonical path" {
              let dir = newTempDir ()
              try
                  runShellIgnore dir "init -q"
                  runShellIgnore dir "config user.email t@t"
                  runShellIgnore dir "config user.name t"
                  File.WriteAllText(Path.Combine(dir, "z.txt"), "z\n")
                  File.WriteAllText(Path.Combine(dir, "a.txt"), "a\n")
                  File.WriteAllText(Path.Combine(dir, "m.txt"), "m\n")
                  runShellIgnore dir "add z.txt a.txt m.txt"
                  runShellIgnore dir "-c core.quotepath=false commit -q -m initial"
                  let c1 = runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ] |> fun r -> r.Stdout.Trim()
                  File.WriteAllText(Path.Combine(dir, "z.txt"), "z2\n")
                  File.WriteAllText(Path.Combine(dir, "a.txt"), "a2\n")
                  File.WriteAllText(Path.Combine(dir, "m.txt"), "m2\n")
                  runShellIgnore dir "add z.txt a.txt m.txt"
                  runShellIgnore dir "-c core.quotepath=false commit -q -m second"
                  let c2 = runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ] |> fun r -> r.Stdout.Trim()
                  let identity = resolveGitIdentity dir defaultGitRunOptions c1 c2
                  let entries =
                      computeChangeSet dir defaultGitRunOptions identity.ObjectFormat
                          identity.BeforeTreeOid identity.AfterTreeOid
                  let paths = entries |> List.map (fun e -> e.CanonicalPath)
                  let sorted = List.sort paths
                  Expect.equal paths sorted "paths are ordinally sorted"
              finally
                  cleanup dir
          }
          test "paths with spaces are preserved" {
              let dir = newTempDir ()
              try
                  runShellIgnore dir "init -q"
                  runShellIgnore dir "config user.email t@t"
                  runShellIgnore dir "config user.name t"
                  File.WriteAllText(Path.Combine(dir, "space dir.txt"), "x\n")
                  runShellArgsIgnore dir [ "add"; "space dir.txt" ]
                  runShellIgnore dir "-c core.quotepath=false commit -q -m initial"
                  let c1 = runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ] |> fun r -> r.Stdout.Trim()
                  File.WriteAllText(Path.Combine(dir, "space dir.txt"), "y\n")
                  runShellArgsIgnore dir [ "add"; "space dir.txt" ]
                  runShellIgnore dir "-c core.quotepath=false commit -q -m second"
                  let c2 = runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ] |> fun r -> r.Stdout.Trim()
                  let identity = resolveGitIdentity dir defaultGitRunOptions c1 c2
                  let entries =
                      computeChangeSet dir defaultGitRunOptions identity.ObjectFormat
                          identity.BeforeTreeOid identity.AfterTreeOid
                  let found =
                      entries
                      |> List.exists (fun e -> e.CanonicalPath = "space dir.txt")
                  Expect.isTrue found "space-containing path preserved"
              finally
                  cleanup dir
          } ]
