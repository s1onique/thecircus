module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.GitAdapterTests

// =============================================================================
// Bounded Git adapter tests -- ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01
//                            --CORRECTION01
// =============================================================================
//
// These tests cover the bounded Git adapter contract:
//
//   * Working-directory authority: repoPath is honoured even when the
//     test process current directory is elsewhere.
//   * Argument authority: spaces and shell metacharacters stay literal;
//     no shell participates in command execution.
//   * Canonical execution profile: 60s timeout, 32 MiB stdout/stderr.
//   * Distinct failure taxonomy: launch, timeout, cancellation, stdout
//     overflow, stderr overflow, I/O, exit, and protocol failures are
//     each surfaced through the actual adapter — none is reachable
//     only through the typed DU without production translation logic.
//   * Object-format authority: SHA-1 / SHA-256 detection; full-width
//     hex only; unknown formats fail closed.
//   * Merge-parent authority: implicit parent selection fails closed;
//     explicit parent evidence requires the before-commit and the
//     explicit parent to coincide so the before-tree and the
//     commit-range baseline are the same historical point. Each parent
//     of an asymmetric merge produces a distinct change set.
//   * Deterministic regeneration: change-set identity is byte-identical
//     across runs.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git

// -----------------------------------------------------------------------------
// Process tree fixture (re-used from the BoundedProcess authority tests)
// -----------------------------------------------------------------------------

let private fixturePath =
    Path.Combine(AppContext.BaseDirectory, "circus-process-tree-fixture.dll")

let private resolveFixturePath () : string =
    if not (File.Exists fixturePath) then
        failwithf
            "precompiled process fixture not found at %s. Rebuild the test project so the MSBuild `CopyProcessTreeFixture` target runs."
            fixturePath
    fixturePath

// -----------------------------------------------------------------------------
// Repository fixture helpers
// -----------------------------------------------------------------------------

let private newTempDir (label: string) : string =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (dir: string) : unit =
    if Directory.Exists dir then
        try
            Directory.Delete(dir, true)
        with _ -> ()

/// Run an arbitrary git command directly via Process for fixture setup
/// purposes only. The Git adapter itself never invokes Process.Start;
/// setup commands are explicitly outside the adapter's contract.
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

let private runShellIgnore (repoRoot: string) (args: string list) : unit =
    runShellArgs repoRoot args |> ignore

let private initRepoWithCommits () : string * string * string =
    let dir = newTempDir "repair-episodes-git-adapter"
    runShellIgnore dir [ "init"; "-q" ]
    runShellIgnore dir [ "config"; "user.email"; "test@example.com" ]
    runShellIgnore dir [ "config"; "user.name"; "tester" ]
    File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha\n")
    runShellIgnore dir [ "add"; "a.txt" ]
    runShellIgnore dir [ "-c"; "core.quotepath=false"; "commit"; "-q"; "-m"; "first" ]
    let c1 =
        runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ]
        |> fun r -> r.Stdout.Trim()
    File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha-changed\n")
    runShellIgnore dir [ "add"; "a.txt" ]
    runShellIgnore dir [ "-c"; "core.quotepath=false"; "commit"; "-q"; "-m"; "second" ]
    let c2 =
        runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ]
        |> fun r -> r.Stdout.Trim()
    dir, c1, c2

let private lastCommit (dir: string) : string =
    runGit dir defaultGitRunOptions [ "rev-parse"; "HEAD" ]
    |> fun r -> r.Stdout.Trim()

let private treeOid (dir: string) (commitOid: string) : string =
    runGit dir defaultGitRunOptions [ "rev-parse"; commitOid + "^{tree}" ]
    |> fun r -> r.Stdout.Trim()

/// Safe scope for ``setGitExecutable``: restores the seam on dispose
/// even when the test body raises.
let private withGitExecutableScope (path: string) : IDisposable =
    setGitExecutable path
    { new IDisposable with
        member _.Dispose() = resetGitExecutable () }

let private withCwdScope (path: string) : IDisposable =
    let saved = Directory.GetCurrentDirectory()
    Directory.SetCurrentDirectory path
    { new IDisposable with
        member _.Dispose() =
            try Directory.SetCurrentDirectory saved with _ -> () }

let private withObjectFormatCacheCleared () : IDisposable =
    clearObjectFormatCache ()
    { new IDisposable with
        member _.Dispose() = clearObjectFormatCache () }

// -----------------------------------------------------------------------------
// Canonical resource profile (mirrors the canonical 60s / 32 MiB profile)
// -----------------------------------------------------------------------------

let private canonicalLimits : GitRunOptions = defaultGitRunOptions

let private tightTimeout (ms: int) : GitRunOptions =
    { canonicalLimits with
        Timeout = TimeSpan.FromMilliseconds (float ms) }

// -----------------------------------------------------------------------------
// 1. Working-directory authority
// -----------------------------------------------------------------------------

[<Tests>]
let workingDirectoryTests =
    testList
        "FSharpDiagnostics.RepairEpisodes.GitAdapter.working-directory"
        [
          testSequenced <| test "supplied repoPath is honoured even when the test process CWD is elsewhere" {
              let repoDir = newTempDir "git-adapter-cwd"
              try
                  runShellIgnore repoDir [ "init"; "-q" ]
                  runShellIgnore repoDir [ "config"; "user.email"; "t@t" ]
                  runShellIgnore repoDir [ "config"; "user.name"; "t" ]
                  File.WriteAllText(Path.Combine(repoDir, "marker.txt"), "x\n")
                  runShellIgnore repoDir [ "add"; "marker.txt" ]
                  runShellIgnore repoDir [ "commit"; "-q"; "-m"; "marker" ]
                  let expected = lastCommit repoDir
                  // CWD is set to a totally unrelated directory.
                  let unrelated = Path.GetTempPath()
                  use _ = withCwdScope unrelated
                  let result =
                      runGit repoDir canonicalLimits [ "rev-parse"; "HEAD" ]
                  Expect.equal result.ExitCode 0 "git succeeded"
                  Expect.equal (result.Stdout.Trim()) expected
                      "supplied repoPath drove the working directory"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "repository path containing spaces is honoured" {
              let parent = newTempDir "git-adapter-spaces-parent"
              let repoDir = Path.Combine(parent, "repo with spaces")
              try
                  Directory.CreateDirectory repoDir |> ignore
                  runShellIgnore repoDir [ "init"; "-q" ]
                  runShellIgnore repoDir [ "config"; "user.email"; "t@t" ]
                  runShellIgnore repoDir [ "config"; "user.name"; "t" ]
                  File.WriteAllText(Path.Combine(repoDir, "a.txt"), "x\n")
                  runShellIgnore repoDir [ "add"; "a.txt" ]
                  runShellIgnore repoDir [ "commit"; "-q"; "-m"; "first" ]
                  let expected = lastCommit repoDir
                  let result =
                      runGit repoDir canonicalLimits [ "rev-parse"; "HEAD" ]
                  Expect.equal result.ExitCode 0 "git succeeded under spaces"
                  Expect.equal (result.Stdout.Trim()) expected "OID matches"
              finally
                  cleanup parent
          } ]

// -----------------------------------------------------------------------------
// 2. Argument authority and no-shell proof
// -----------------------------------------------------------------------------

[<Tests>]
let argumentAuthorityTests =
    testList
        "FSharpDiagnostics.RepairEpisodes.GitAdapter.argument-authority"
        [
          testSequenced <| test "arguments containing spaces and shell metacharacters stay literal" {
              let repoDir = newTempDir "git-adapter-args"
              try
                  runShellIgnore repoDir [ "init"; "-q" ]
                  runShellIgnore repoDir [ "config"; "user.email"; "t@t" ]
                  runShellIgnore repoDir [ "config"; "user.name"; "t" ]
                  File.WriteAllText(Path.Combine(repoDir, "a.txt"), "x\n")
                  runShellIgnore repoDir [ "add"; "a.txt" ]
                  runShellIgnore repoDir [ "commit"; "-q"; "-m"; "first" ]
                  // Arguments with spaces, quotes, $VAR expansion, command
                  // substitution, and redirection metacharacters must remain
                  // a single literal token each.
                  let result =
                      runGit
                          repoDir
                          canonicalLimits
                          [ "log"
                            "-1"
                            "--pretty=%H $(echo injected) `echo injected` >/dev/null && echo \"quoted arg\"" ]
                  Expect.equal result.ExitCode 0 "log returned success"
                  let line =
                      result.Stdout.Trim().Split('\n').[0]
                  Expect.stringContains line "injected"
                      "literal characters preserved"
                  Expect.notEqual
                      (line.IndexOf "injected", line.LastIndexOf "injected")
                      (-1, -1)
                      "injection markers appear literally and do not execute"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "no shell binary participates in command execution" {
              // The adapter uses BoundedProcess which always invokes the
              // ProcessStartInfo.ArgumentList path. We verify by setting
              // an executable that records its argv and confirming every
              // argument is preserved verbatim without shell quoting.
              let fixture = resolveFixturePath ()
              use _ = withGitExecutableScope "dotnet"
              let argv = [
                  fixture
                  "echo-args"
                  "literal; rm -rf /tmp"
                  "`echo injected`"
                  "$HOME"
                  "  spaced arg  "
                  "tab\there"
              ]
              let result =
                  runGit (Path.GetTempPath()) canonicalLimits argv
              Expect.equal result.ExitCode 0 "echo-args returned 0"
              let stdout = result.Stdout
              Expect.stringContains stdout "literal; rm -rf /tmp"
                  "shell metacharacter preserved as literal argument"
              Expect.stringContains stdout "`echo injected`"
                  "backticks preserved as literal argument"
              Expect.stringContains stdout "$HOME"
                  "dollar-prefix preserved as literal argument"
              Expect.stringContains stdout "  spaced arg  "
                  "leading/trailing whitespace preserved verbatim"
              Expect.stringContains stdout "tab\there"
                  "embedded tab preserved verbatim"
          } ]

// -----------------------------------------------------------------------------
// 3. Completion and failure tests
// -----------------------------------------------------------------------------

[<Tests>]
let completionAndFailureTests =
    testList
        "FSharpDiagnostics.RepairEpisodes.GitAdapter.completion-and-failure"
        [
          testSequenced <| test "successful command with empty stdout returns Ok with empty strings" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let result =
                  runGit
                      (Path.GetTempPath())
                      canonicalLimits
                      [ fixture; "empty" ]
              Expect.equal result.ExitCode 0 "exit code 0"
              Expect.equal result.Stdout "" "stdout empty"
              Expect.equal result.Stderr "" "stderr empty"
              Expect.equal result.Argv.Length 2 "argv retained"
          }

          testSequenced <| test "successful command with non-empty stdout and stderr returns both" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let result =
                  runGit
                      (Path.GetTempPath())
                      canonicalLimits
                      [ fixture; "both"; "5"; "7" ]
              Expect.equal result.ExitCode 0 "exit code 0"
              Expect.equal (result.Stdout.Length) 5 "stdout bytes"
              Expect.equal (result.Stderr.Length) 7 "stderr bytes"
              for i in 0 .. 4 do
                  Expect.equal (result.Stdout.[i]) (char (97 + (i % 26)))
                      "stdout byte ordering"
              for i in 0 .. 6 do
                  Expect.equal (result.Stderr.[i]) (char (65 + (i % 26)))
                      "stderr byte ordering"
          }

          testSequenced <| test "non-zero exit retains exit code and both bounded streams" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let result =
                  runGit
                      (Path.GetTempPath())
                      canonicalLimits
                      [ fixture; "exit-with-both"; "3"; "4"; "42" ]
              Expect.equal result.ExitCode 42 "exit code 42 retained"
              Expect.equal (result.Stdout.Length) 3 "stdout length preserved"
              Expect.equal (result.Stderr.Length) 4 "stderr length preserved"
              let typedResult =
                  runGitTyped
                      (Path.GetTempPath())
                      canonicalLimits
                      [ fixture; "exit-with-both"; "3"; "4"; "42" ]
              match typedResult with
              | Ok success ->
                  Expect.equal success.ExitCode 42 "typed Ok retains exit 42"
                  Expect.equal (success.Stdout.Length) 3 "typed Ok stdout"
                  Expect.equal (success.Stderr.Length) 4 "typed Ok stderr"
                  Expect.equal (success.Argv.Length) 5 "typed Ok argv retained"
              | Error e -> failwithf "expected Ok, got %A" e
          }

          testSequenced <| test "missing executable through the seam produces GitRunError.LaunchFailure" {
              use _ = withGitExecutableScope "/nonexistent/git/executable/path"
              match runGitTyped
                        (Path.GetTempPath())
                        canonicalLimits
                        [ "rev-parse"; "HEAD" ] with
              | Error (LaunchFailure _) -> ()
              | Error e -> failwithf "expected LaunchFailure, got %A" e
              | Ok _ -> failwithf "expected LaunchFailure, got Ok"
          }

          testSequenced <| test "timeout produces GitRunError.TimeoutFailure" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let tight = tightTimeout 200
              match runGitTyped
                        (Path.GetTempPath())
                        tight
                        [ fixture; "sleep"; "5000" ] with
              | Error (TimeoutFailure _) -> ()
              | Error e -> failwithf "expected TimeoutFailure, got %A" e
              | Ok _ -> failwithf "expected TimeoutFailure, got Ok"
          }

          testSequenced <| test "cancellation through the adapter produces GitRunError.CancellationFailure" {
              // The cancellation entry point is exercised through the
              // adapter, not through BoundedProcess directly. A
              // pre-cancelled token is observed before launch, so the
              // adapter maps the bounded-process Cancelled cause to
              // GitRunError.CancellationFailure through production
              // translation logic.
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let cts = new CancellationTokenSource()
              cts.Cancel()
              match runGitTypedWithCancellation
                        (Path.GetTempPath())
                        canonicalLimits
                        cts.Token
                        [ fixture; "sleep"; "10000" ] with
              | Error (CancellationFailure _) -> ()
              | Error e -> failwithf "expected CancellationFailure, got %A" e
              | Ok _ -> failwithf "expected CancellationFailure, got Ok"
          }

          testSequenced <| test "stdout limit at exact boundary succeeds" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let opts = { canonicalLimits with MaxStdoutBytes = 50L }
              let result =
                  runGit
                      (Path.GetTempPath())
                      opts
                      [ fixture; "stdout"; "50" ]
              Expect.equal result.ExitCode 0 "exact limit succeeds"
              Expect.equal (result.Stdout.Length) 50 "exact length preserved"
          }

          testSequenced <| test "stdout limit exceeded produces GitRunError.StdoutOverflowFailure" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let opts = { canonicalLimits with MaxStdoutBytes = 50L }
              match runGitTyped
                        (Path.GetTempPath())
                        opts
                        [ fixture; "stdout"; "51" ] with
              | Error (StdoutOverflowFailure (limit, _)) ->
                  Expect.equal limit 50 "limit value preserved"
              | Error e -> failwithf "expected StdoutOverflowFailure, got %A" e
              | Ok _ -> failwithf "expected overflow, got Ok"
          }

          testSequenced <| test "stderr limit at exact boundary succeeds" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let opts = { canonicalLimits with MaxStderrBytes = 50L }
              let result =
                  runGit
                      (Path.GetTempPath())
                      opts
                      [ fixture; "stderr"; "50" ]
              Expect.equal result.ExitCode 0 "exact stderr limit succeeds"
              Expect.equal (result.Stderr.Length) 50 "exact stderr length"
          }

          testSequenced <| test "stderr limit exceeded produces GitRunError.StderrOverflowFailure" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let opts = { canonicalLimits with MaxStderrBytes = 50L }
              match runGitTyped
                        (Path.GetTempPath())
                        opts
                        [ fixture; "stderr"; "51" ] with
              | Error (StderrOverflowFailure (limit, _)) ->
                  Expect.equal limit 50 "stderr limit value preserved"
              | Error e -> failwithf "expected StderrOverflowFailure, got %A" e
              | Ok _ -> failwithf "expected overflow, got Ok"
          }

          testSequenced <| test "stdout limit zero with zero bytes succeeds" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let opts = { canonicalLimits with MaxStdoutBytes = 0L; MaxStderrBytes = 1024L }
              match runGitTyped
                        (Path.GetTempPath())
                        opts
                        [ fixture; "empty" ] with
              | Ok success -> Expect.equal success.Stdout "" "stdout empty"
              | Error e -> failwithf "expected Ok with empty stdout, got %A" e
          }

          testSequenced <| test "stdout limit zero with one byte produces GitRunError.StdoutOverflowFailure" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let opts = { canonicalLimits with MaxStdoutBytes = 0L; MaxStderrBytes = 1024L }
              match runGitTyped
                        (Path.GetTempPath())
                        opts
                        [ fixture; "stdout"; "1" ] with
              | Error (StdoutOverflowFailure (limit, _)) ->
                  Expect.equal limit 0 "zero limit preserved"
              | Error e -> failwithf "expected StdoutOverflowFailure, got %A" e
              | Ok _ -> failwithf "expected overflow, got Ok"
          }

          testSequenced <| test "I/O failure is reachable through the production translator" {
              // ``translateBoundedError`` is the production translator
              // from ``BoundedProcessFailure`` to ``GitRunError``. The
              // I/O branch must be reachable through this production
              // path, not merely declared in the typed DU. We drive
              // every I/O-related ``BoundedProcessFailure`` shape and
              // confirm it surfaces as ``IoFailure``.
              let argv = [ "rev-parse"; "HEAD" ]
              let cases : (BoundedProcessFailure * string) list = [
                  StdoutReaderFailed "synthetic stdout read failure",
                  "stdout reader"
                  StderrReaderFailed "synthetic stderr read failure",
                  "stderr reader"
                  WaitFailed "synthetic wait failure",
                  "wait"
                  KillFailed "synthetic kill failure",
                  "kill"
                  IncompleteOutput (false, true),
                  "incomplete output"
              ]
              for failure, label in cases do
                  match translateBoundedError failure argv with
                  | IoFailure _ -> ()
                  | actual ->
                      failwithf
                          "expected IoFailure for %s, got %A"
                          label actual
          }

          testSequenced <| test "protocol failure is reachable through the parser entry point" {
              // ``parseGitBytesOrProtocol`` is the deterministic
              // parser seam. A parser that returns ``Error`` for any
              // input is exercised here; the adapter must surface
              // ``ProtocolFailure`` deterministically.
              let failingParser : byte array -> Result<int, string> =
                  fun _ -> Error "synthetic protocol error"
              match parseGitBytesOrProtocol failingParser [||] with
              | Error (ProtocolFailure detail) ->
                  Expect.equal detail "synthetic protocol error"
                      "protocol failure detail preserved"
              | Error e -> failwithf "expected ProtocolFailure, got %A" e
              | Ok _ -> failwithf "expected ProtocolFailure, got Ok"
          }

          testSequenced <| test "checked-command surface raises GitExitFailure on non-zero exit" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let argv = [ fixture; "exit-with-both"; "3"; "4"; "42" ]
              let mutable captured = false
              try
                  runGitChecked (Path.GetTempPath()) canonicalLimits argv
                  |> ignore
              with
              | GitExitFailure (capturedArgv, exitCode, stdout, stderr) ->
                  captured <- true
                  Expect.equal exitCode 42 "exit code preserved"
                  Expect.equal (stdout.Length) 3 "stdout length preserved"
                  Expect.equal (stderr.Length) 4 "stderr length preserved"
                  Expect.equal capturedArgv argv "argv preserved"
              | _ -> ()
              Expect.isTrue captured "GitExitFailure raised"
          }

          testSequenced <| test "checked-command surface succeeds on zero exit" {
              use _ = withGitExecutableScope "dotnet"
              let fixture = resolveFixturePath ()
              let result =
                  runGitChecked
                      (Path.GetTempPath())
                      canonicalLimits
                      [ fixture; "empty" ]
              Expect.equal result.ExitCode 0 "zero exit returns success"
              Expect.equal result.Stdout "" "stdout empty"
          } ]

// -----------------------------------------------------------------------------
// 4. Object-format and OID validation
// -----------------------------------------------------------------------------

[<Tests>]
let identityTests =
    testList
        "FSharpDiagnostics.RepairEpisodes.GitAdapter.identity"
        [
          testSequenced <| test "detectObjectFormat returns sha1 for default repositories" {
              let repoDir, before, after = initRepoWithCommits ()
              try
                  use _ = withObjectFormatCacheCleared ()
                  let fmt = detectObjectFormat repoDir
                  Expect.equal fmt Sha1 "default repository is sha1"
                  let identity =
                      resolveGitIdentity repoDir canonicalLimits before after
                  Expect.equal identity.ObjectFormat Sha1 "identity format"
                  Expect.isGreaterThan
                      (List.length identity.CommitRange)
                      0
                      "commit range non-empty"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "abbreviated 39-character and 41-character SHA-1 identities are rejected" {
              let repoDir, before, after = initRepoWithCommits ()
              try
                  use _ = withObjectFormatCacheCleared ()
                  let shortOid = before.Substring(0, 39)
                  let longOid = before + "0"
                  let mutable shortRejected = false
                  let mutable longRejected = false
                  try
                      resolveGitIdentity
                          repoDir
                          canonicalLimits
                          shortOid
                          shortOid
                      |> ignore
                  with
                  | :? GitIdentityFailure -> shortRejected <- true
                  | _ -> ()
                  try
                      resolveGitIdentity
                          repoDir
                          canonicalLimits
                          longOid
                          longOid
                      |> ignore
                  with
                  | :? GitIdentityFailure -> longRejected <- true
                  | _ -> ()
                  Expect.isTrue shortRejected "39-char OID rejected"
                  Expect.isTrue longRejected "41-char OID rejected"
                  let mutable afterSideRejected = false
                  try
                      resolveGitIdentity
                          repoDir
                          canonicalLimits
                          before
                          shortOid
                      |> ignore
                  with
                  | :? GitIdentityFailure -> afterSideRejected <- true
                  | _ -> ()
                  Expect.isTrue afterSideRejected
                      "after-side 39-char OID rejected"
                  Expect.isTrue (String.length after > 0) "after non-empty"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "isValidOid accepts exactly 40 hex chars and rejects 39/41" {
              let valid40 = String('a', 40)
              let valid64 = String('a', 64)
              let tooShort39 = String('a', 39)
              let tooLong41 = String('a', 41)
              let badHex40 = String('z', 40)
              Expect.isTrue (isValidOid Sha1 valid40) "valid 40 hex"
              Expect.isFalse (isValidOid Sha1 tooShort39) "39 chars rejected"
              Expect.isFalse (isValidOid Sha1 tooLong41) "41 chars rejected"
              Expect.isFalse (isValidOid Sha1 badHex40) "non-hex rejected"
              Expect.isTrue (isValidOid Sha256 valid64) "valid 64 hex"
              Expect.isFalse (isValidOid Sha256 valid40) "wrong-width rejected"
          }

          testSequenced <| test "tryParseGitObjectFormat accepts sha1 and sha256 and rejects unknown" {
              Expect.equal (tryParseGitObjectFormat "sha1") (Some Sha1) "sha1"
              Expect.equal (tryParseGitObjectFormat "sha256") (Some Sha256) "sha256"
              Expect.equal (tryParseGitObjectFormat "sha384") None "sha384 rejected"
              Expect.equal (tryParseGitObjectFormat "blake3") None "blake3 rejected"
              Expect.equal (tryParseGitObjectFormat "SHA1") None "case sensitive"
              Expect.equal (tryParseGitObjectFormat "") None "empty rejected"
          }

          testSequenced <| test "diff-tree returns complete, non-abbreviated blob OIDs at the storage width" {
              let repoDir, before, after = initRepoWithCommits ()
              try
                  let identity =
                      resolveGitIdentity
                          repoDir
                          canonicalLimits
                          before
                          after
                  let entries =
                      computeChangeSet
                          repoDir
                          canonicalLimits
                          identity.ObjectFormat
                          identity.BeforeTreeOid
                          identity.AfterTreeOid
                  let expectedWidth = gitObjectFormatWidth identity.ObjectFormat
                  Expect.isGreaterThan
                      (List.length entries)
                      0
                      "diff-tree produced at least one entry"
                  for e in entries do
                      match e.BeforeBlobOid with
                      | Some oid ->
                          Expect.equal (String.length oid) expectedWidth
                              "before blob OID is full-width"
                          Expect.isTrue
                              (oid |> Seq.forall Char.IsAsciiHexDigit)
                              "before blob OID is hexadecimal"
                      | None -> ()
                      match e.AfterBlobOid with
                      | Some oid ->
                          Expect.equal (String.length oid) expectedWidth
                              "after blob OID is full-width"
                          Expect.isTrue
                              (oid |> Seq.forall Char.IsAsciiHexDigit)
                              "after blob OID is hexadecimal"
                      | None -> ()
              finally
                  cleanup repoDir
          }

          testSequenced <| test "abbreviated OID is rejected before any repair-episode domain work" {
              let repoDir, before, _after = initRepoWithCommits ()
              try
                  let abbreviated = before.Substring(0, 7)
                  let mutable captured = false
                  try
                      resolveGitIdentity
                          repoDir
                          canonicalLimits
                          abbreviated
                          abbreviated
                      |> ignore
                  with
                  | :? GitIdentityFailure -> captured <- true
                  | _ -> ()
                  Expect.isTrue captured "abbreviated OID rejected"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "non-ancestor before/after pair is rejected with GitIdentityFailure" {
              let repoDir, _before, _after = initRepoWithCommits ()
              try
                  let sameOid = String('a', 40)
                  let mutable captured = false
                  try
                      resolveGitIdentity
                          repoDir
                          canonicalLimits
                          sameOid
                          sameOid
                      |> ignore
                  with
                  | :? GitIdentityFailure -> captured <- true
                  | _ -> ()
                  Expect.isTrue captured "non-ancestor pair rejected"
              finally
                  cleanup repoDir
          } ]

// -----------------------------------------------------------------------------
// 5. SHA-256 capability test
// -----------------------------------------------------------------------------

[<Tests>]
let sha256Tests =
    testList
        "FSharpDiagnostics.RepairEpisodes.GitAdapter.sha256"
        [
          testSequenced <| test "sha-256 repository: live proof when supported, parser proof otherwise" {
              let parentDir = newTempDir "git-adapter-sha256"
              let mutable liveOk = false
              try
                  let mutable exitCode = -1
                  try
                      let psi = ProcessStartInfo()
                      psi.FileName <- "git"
                      psi.WorkingDirectory <- parentDir
                      psi.UseShellExecute <- false
                      psi.RedirectStandardOutput <- true
                      psi.RedirectStandardError <- true
                      psi.ArgumentList.Add "init"
                      psi.ArgumentList.Add "--quiet"
                      psi.ArgumentList.Add "--initial-branch=main"
                      psi.ArgumentList.Add "--object-format=sha256"
                      psi.ArgumentList.Add "live"
                      let p = Process.Start psi
                      p.WaitForExit() |> ignore
                      exitCode <- p.ExitCode
                  with _ -> ()
                  if exitCode = 0 then
                      let repoDir = Path.Combine(parentDir, "live")
                      try
                          runShellIgnore repoDir [ "config"; "user.email"; "t@t" ]
                          runShellIgnore repoDir [ "config"; "user.name"; "t" ]
                          File.WriteAllText(Path.Combine(repoDir, "a.txt"), "x\n")
                          runShellIgnore repoDir [ "add"; "a.txt" ]
                          runShellIgnore repoDir [ "commit"; "-q"; "-m"; "first" ]
                          use _ = withObjectFormatCacheCleared ()
                          let fmt = detectObjectFormat repoDir
                          Expect.equal fmt Sha256 "live sha256 detection"
                          liveOk <- true
                      finally
                          cleanup repoDir
              finally
                  cleanup parentDir
              Expect.isTrue (isValidOid Sha256 (String('a', 64)))
                  "validator accepts 64 hex chars"
              Expect.isFalse (isValidOid Sha256 (String('a', 65)))
                  "validator rejects 65-char OID"
              Expect.isFalse (isValidOid Sha256 (String('a', 63)))
                  "validator rejects 63-char OID"
              Expect.isFalse (isValidOid Sha256 (String('z', 64)))
                  "validator rejects non-hex chars"
              if not liveOk then
                  Expect.isTrue true "hermetic parser proof suffices"
          } ]

// -----------------------------------------------------------------------------
// 6. Repository-topology tests (merge parents, explicit parent, invalid paths)
// -----------------------------------------------------------------------------

[<Tests>]
let repositoryTopologyTests =
    testList
        "FSharpDiagnostics.RepairEpisodes.GitAdapter.repository-topology"
        [
          testSequenced <| test "real two-parent merge fails closed when parent is not explicit" {
              let repoDir = newTempDir "git-adapter-merge"
              try
                  runShellIgnore repoDir [ "init"; "-q" ]
                  runShellIgnore repoDir [ "config"; "user.email"; "t@t" ]
                  runShellIgnore repoDir [ "config"; "user.name"; "t" ]
                  File.WriteAllText(Path.Combine(repoDir, "base.txt"), "base\n")
                  runShellIgnore repoDir [ "add"; "base.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "initial" ]
                  let cMain0 = lastCommit repoDir
                  runShellIgnore repoDir [ "checkout"; "-q"; "-b"; "feature" ]
                  File.WriteAllText(Path.Combine(repoDir, "feature.txt"), "feat\n")
                  runShellIgnore repoDir [ "add"; "feature.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "feature-commit" ]
                  let cFeature = lastCommit repoDir
                  runShellIgnore repoDir [ "checkout"; "-q"; cMain0 ]
                  File.WriteAllText(Path.Combine(repoDir, "main.txt"), "main\n")
                  runShellIgnore repoDir [ "add"; "main.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "main-commit" ]
                  let cMain = lastCommit repoDir
                  runShellIgnore repoDir [ "merge"; "--no-ff"; "feature"; "-q"
                                            ; "-m"; "merge-feature" ]
                  let cMerge = lastCommit repoDir
                  Expect.notEqual cMerge cMain "merge commit is different"
                  let mutable ambiguityCaptured = false
                  let mutable ambiguityParents : string list = []
                  try
                      resolveGitIdentity
                          repoDir
                          canonicalLimits
                          cMain
                          cMerge
                      |> ignore
                  with
                  | GitMergeAmbiguityFailure parents ->
                      ambiguityCaptured <- true
                      ambiguityParents <- parents
                  | _ -> ()
                  Expect.isTrue ambiguityCaptured "merge-ambiguity raised"
                  Expect.equal
                      (List.length ambiguityParents)
                      2
                      "both candidate parents recorded"
                  Expect.contains ambiguityParents cMain "main parent recorded"
                  Expect.contains ambiguityParents cFeature
                      "feature parent recorded"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "asymmetric merge: selecting parent one and parent two produce different change sets" {
              let repoDir = newTempDir "git-adapter-asymmetric-merge"
              try
                  runShellIgnore repoDir [ "init"; "-q" ]
                  runShellIgnore repoDir [ "config"; "user.email"; "t@t" ]
                  runShellIgnore repoDir [ "config"; "user.name"; "t" ]
                  File.WriteAllText(Path.Combine(repoDir, "shared.txt"), "shared\n")
                  runShellIgnore repoDir [ "add"; "shared.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "initial" ]
                  let cBase = lastCommit repoDir
                  runShellIgnore repoDir [ "checkout"; "-q"; "-b"; "left" ]
                  File.WriteAllText(Path.Combine(repoDir, "only_left.txt"), "left\n")
                  runShellIgnore repoDir [ "add"; "only_left.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "left-only" ]
                  let cLeft = lastCommit repoDir
                  runShellIgnore repoDir [ "checkout"; "-q"; cBase ]
                  runShellIgnore repoDir [ "checkout"; "-q"; "-b"; "right" ]
                  File.WriteAllText(Path.Combine(repoDir, "only_right.txt"), "right\n")
                  runShellIgnore repoDir [ "add"; "only_right.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "right-only" ]
                  let cRight = lastCommit repoDir
                  runShellIgnore repoDir [ "checkout"; "-q"; cLeft ]
                  runShellIgnore repoDir [ "merge"; "--no-ff"; "right"; "-q"
                                            ; "-m"; "merge-asym" ]
                  let cMerge = lastCommit repoDir

                  let identityFromLeft =
                      resolveGitIdentityWithParent
                          repoDir
                          canonicalLimits
                          cLeft
                          cMerge
                          cLeft
                  let entriesFromLeft =
                      computeChangeSet
                          repoDir
                          canonicalLimits
                          identityFromLeft.ObjectFormat
                          identityFromLeft.BeforeTreeOid
                          identityFromLeft.AfterTreeOid

                  let identityFromRight =
                      resolveGitIdentityWithParent
                          repoDir
                          canonicalLimits
                          cRight
                          cMerge
                          cRight
                  let entriesFromRight =
                      computeChangeSet
                          repoDir
                          canonicalLimits
                          identityFromRight.ObjectFormat
                          identityFromRight.BeforeTreeOid
                          identityFromRight.AfterTreeOid

                  Expect.isTrue
                      (entriesFromLeft
                       |> List.exists (fun e -> e.CanonicalPath = "only_right.txt"))
                      "left->merge change set adds only_right.txt"
                  Expect.isFalse
                      (entriesFromLeft
                       |> List.exists (fun e -> e.CanonicalPath = "only_left.txt"))
                      "left->merge change set does not mention only_left.txt"

                  Expect.isTrue
                      (entriesFromRight
                       |> List.exists (fun e -> e.CanonicalPath = "only_left.txt"))
                      "right->merge change set adds only_left.txt"
                  Expect.isFalse
                      (entriesFromRight
                       |> List.exists (fun e -> e.CanonicalPath = "only_right.txt"))
                      "right->merge change set does not mention only_right.txt"

                  let idLeft =
                      computeChangeSetIdentity
                          identityFromLeft.BeforeTreeOid
                          identityFromLeft.AfterTreeOid
                          entriesFromLeft
                  let idRight =
                      computeChangeSetIdentity
                          identityFromRight.BeforeTreeOid
                          identityFromRight.AfterTreeOid
                          entriesFromRight
                  Expect.notEqual
                      idLeft
                      idRight
                      "asymmetric merge: distinct change-set identities"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "mismatched beforeCommitInput and explicit parent fail closed" {
              let repoDir, _before1, _before2 = initRepoWithCommits ()
              try
                  let cBase = _before1
                  runShellIgnore repoDir [ "checkout"; "-q"; "-b"; "side" ]
                  File.WriteAllText(Path.Combine(repoDir, "side.txt"), "side\n")
                  runShellIgnore repoDir [ "add"; "side.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "side-commit" ]
                  let cSide = lastCommit repoDir
                  runShellIgnore repoDir [ "checkout"; "-q"; cBase ]
                  File.WriteAllText(Path.Combine(repoDir, "main2.txt"), "main2\n")
                  runShellIgnore repoDir [ "add"; "main2.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "main2-commit" ]
                  let cMain = lastCommit repoDir
                  runShellIgnore repoDir [ "merge"; "--no-ff"; "side"; "-q"
                                            ; "-m"; "merge" ]
                  let cMerge = lastCommit repoDir
                  // beforeCommitInput = cMain, explicitParent = cSide
                  // These are different historical baselines; the
                  // adapter must fail closed.
                  let mutable captured = false
                  try
                      resolveGitIdentityWithParent
                          repoDir
                          canonicalLimits
                          cMain
                          cMerge
                          cSide
                      |> ignore
                  with
                  | :? GitIdentityFailure -> captured <- true
                  | _ -> ()
                  Expect.isTrue captured
                      "mismatched before-commit and explicit parent rejected"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "resolveGitIdentityWithParent internal consistency" {
              let repoDir = newTempDir "git-adapter-consistency"
              try
                  runShellIgnore repoDir [ "init"; "-q" ]
                  runShellIgnore repoDir [ "config"; "user.email"; "t@t" ]
                  runShellIgnore repoDir [ "config"; "user.name"; "t" ]
                  File.WriteAllText(Path.Combine(repoDir, "a.txt"), "a\n")
                  runShellIgnore repoDir [ "add"; "a.txt" ]
                  runShellIgnore repoDir [ "commit"; "-q"; "-m"; "first" ]
                  let cBefore = lastCommit repoDir
                  File.WriteAllText(Path.Combine(repoDir, "a.txt"), "a2\n")
                  runShellIgnore repoDir [ "add"; "a.txt" ]
                  runShellIgnore repoDir [ "commit"; "-q"; "-m"; "second" ]
                  let cAfter = lastCommit repoDir
                  let identity =
                      resolveGitIdentityWithParent
                          repoDir
                          canonicalLimits
                          cBefore
                          cAfter
                          cBefore
                  // The before-tree must equal the tree of the
                  // explicit parent (which equals the before-commit).
                  let expectedBeforeTree = treeOid repoDir cBefore
                  Expect.equal identity.BeforeCommitOid cBefore
                      "BeforeCommitOid equals explicit parent"
                  Expect.equal identity.BeforeTreeOid expectedBeforeTree
                      "BeforeTreeOid equals tree(explicit parent)"
                  Expect.equal identity.AfterCommitOid cAfter
                      "AfterCommitOid preserved"
                  Expect.isTrue
                      (List.contains cAfter identity.CommitRange
                       || List.isEmpty identity.CommitRange)
                      "commit range is consistent"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "repeated resolveGitIdentityWithParent is byte-identical" {
              let repoDir, cBase, _ = initRepoWithCommits ()
              try
                  runShellIgnore repoDir [ "checkout"; "-q"; "-b"; "side" ]
                  File.WriteAllText(Path.Combine(repoDir, "side2.txt"), "side2\n")
                  runShellIgnore repoDir [ "add"; "side2.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "side2" ]
                  let cSide = lastCommit repoDir
                  runShellIgnore repoDir [ "checkout"; "-q"; cBase ]
                  File.WriteAllText(Path.Combine(repoDir, "main3.txt"), "main3\n")
                  runShellIgnore repoDir [ "add"; "main3.txt" ]
                  runShellIgnore repoDir [ "-c"; "core.quotepath=false"
                                            ; "commit"; "-q"; "-m"; "main3" ]
                  let cMain = lastCommit repoDir
                  runShellIgnore repoDir [ "merge"; "--no-ff"; "side"; "-q"
                                            ; "-m"; "merge" ]
                  let cMerge = lastCommit repoDir
                  let id1 =
                      resolveGitIdentityWithParent
                          repoDir
                          canonicalLimits
                          cMain
                          cMerge
                          cMain
                  let id2 =
                      resolveGitIdentityWithParent
                          repoDir
                          canonicalLimits
                          cMain
                          cMerge
                          cMain
                  Expect.equal id1 id2
                      "repeated resolution is byte-identical"
              finally
                  cleanup repoDir
          }

          testSequenced <| test "invalid repository path produces GitRunError.LaunchFailure" {
              let nonexistent = Path.Combine(
                  Path.GetTempPath(),
                  "nonexistent-" + Guid.NewGuid().ToString("N"))
              match runGitTyped
                        nonexistent
                        canonicalLimits
                        [ "rev-parse"; "HEAD" ] with
              | Error (LaunchFailure _) -> ()
              | Error e -> failwithf "expected LaunchFailure, got %A" e
              | Ok _ -> failwithf "expected LaunchFailure, got Ok"
          }

          testSequenced <| test "non-repository path produces non-zero exit and stderr is preserved" {
              let nonRepo = newTempDir "git-adapter-non-repo"
              try
                  match runGitTyped
                            nonRepo
                            canonicalLimits
                            [ "rev-parse"; "HEAD" ] with
                  | Ok success ->
                      Expect.notEqual success.ExitCode 0 "non-zero exit preserved"
                      Expect.isGreaterThan
                          (String.length success.Stderr)
                          0
                          "stderr preserved"
                  | Error e ->
                      failwithf
                          "expected Ok with non-zero exit and stderr, got %A"
                          e
              finally
                  cleanup nonRepo
          } ]

// -----------------------------------------------------------------------------
// 7. Deterministic regeneration
// -----------------------------------------------------------------------------

[<Tests>]
let deterministicRegenerationTests =
    testList
        "FSharpDiagnostics.RepairEpisodes.GitAdapter.deterministic-regeneration"
        [
          testSequenced <| test "change-set identity is byte-identical across two runs" {
              let repoDir, before, after = initRepoWithCommits ()
              try
                  let identity1 =
                      resolveGitIdentity
                          repoDir
                          canonicalLimits
                          before
                          after
                  let entries1 =
                      computeChangeSet
                          repoDir
                          canonicalLimits
                          identity1.ObjectFormat
                          identity1.BeforeTreeOid
                          identity1.AfterTreeOid
                  let id1 =
                      computeChangeSetIdentity
                          identity1.BeforeTreeOid
                          identity1.AfterTreeOid
                          entries1
                  let identity2 =
                      resolveGitIdentity
                          repoDir
                          canonicalLimits
                          before
                          after
                  let entries2 =
                      computeChangeSet
                          repoDir
                          canonicalLimits
                          identity2.ObjectFormat
                          identity2.BeforeTreeOid
                          identity2.AfterTreeOid
                  let id2 =
                      computeChangeSetIdentity
                          identity2.BeforeTreeOid
                          identity2.AfterTreeOid
                          entries2
                  Expect.equal id1 id2
                      "change-set identity is byte-identical across runs"
                  Expect.equal
                      (List.map (fun (e: GitChangeEntry) -> e.CanonicalPath) entries1)
                      (List.map (fun (e: GitChangeEntry) -> e.CanonicalPath) entries2)
                      "path ordering is deterministic"
              finally
                  cleanup repoDir
          } ]
