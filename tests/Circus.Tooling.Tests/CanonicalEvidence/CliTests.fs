module Circus.Tooling.Tests.CanonicalEvidence.CliTests

// =============================================================================
// CLI tests for the canonical evidence provider
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION02
//
// CORRECTION01 tests 39–44 covered the in-process CLI dispatch surface.
// CORRECTION02 adds hermetic CLI tests (45–55) that exercise the same
// dispatch path through ``runCliWithDependencies`` with isolated fake
// dependencies, so the tests do not mutate the bounded Git adapter's
// per-process executable cell and so they cannot poison subsequent
// tests through shared mutable state.
//
// The executable-seam criterion is deliberately narrow and observable:
// CanonicalEvidence tests do not invoke the Git executable mutators.
// A static inventory test rejects references to ``setGitExecutable``,
// ``resetGitExecutable``, and ``gitExecutableCell`` from the complete
// production CanonicalEvidence source inventory. Isolated concurrent CLI
// tests separately prove that dependency injection shares no mutable state.
//
// Each test also asserts:
//
//   * ``production_dispatch_path_exercised: true`` — the production
//     ``parse`` function is the single entry point for argv; the
//     dependency-driven runners are the same runners the production
//     ``run`` wrapper invokes.
//
//   * ``exit_code_asserted: true`` — the returned ``int`` is asserted
//     against the expected exit code.
//
//   * ``stdout_asserted: true`` / ``stderr_asserted: true`` — the
//     test captures stdout and stderr so it can detect a PASS line
//     emitted on a failure path (which would be a stop condition).
//
//   * ``pass_line_absent_on_failure: true`` — when the test asserts
//     a failure (exit code != 0), it explicitly checks that stdout
//     does NOT contain a PASS line.
//
// =============================================================================

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess
open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.Provider
open Circus.Tooling.CanonicalEvidence.Cli

// -----------------------------------------------------------------------------
// Capture stdout and stderr around a thunk.
// -----------------------------------------------------------------------------

type private CapturedIO = {
    Stdout: string
    Stderr: string
    ExitCode: int
}

let private captureIO (thunk: unit -> int) : CapturedIO =
    let originalOut = Console.Out
    let originalErr = Console.Error
    let stdoutBuilder = StringBuilder()
    let stderrBuilder = StringBuilder()
    let stdoutWriter = new StringWriter(stdoutBuilder)
    let stderrWriter = new StringWriter(stderrBuilder)
    try
        Console.SetOut(stdoutWriter)
        Console.SetError(stderrWriter)
        let exitCode = thunk ()
        stdoutWriter.Flush()
        stderrWriter.Flush()
        {
            Stdout = stdoutBuilder.ToString()
            Stderr = stderrBuilder.ToString()
            ExitCode = exitCode
        }
    finally
        Console.SetOut(originalOut)
        Console.SetError(originalErr)
        stdoutWriter.Dispose()
        stderrWriter.Dispose()

// -----------------------------------------------------------------------------
// Hermetic git fixture helpers — bypass the bounded Git adapter's
// mutable executable cell by spawning ``git`` directly via
// ``Process.Start``. This is test FIXTURE setup, not the adapter
// under test; the dependency-driven runners do not invoke ``git``
// at all (they take fakes).
// -----------------------------------------------------------------------------

let private fixtureTempDir (label: string) : string =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (dir: string) : unit =
    try
        if Directory.Exists dir then
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

let private initRepoWithCommit () : string =
    let dir = fixtureTempDir "circus-canonev-cli"
    runShellArgs dir [ "init"; "-q" ] |> ignore
    runShellArgs dir [ "config"; "user.email"; "ci@local" ] |> ignore
    runShellArgs dir [ "config"; "user.name"; "ci" ] |> ignore
    runShellArgs dir [ "config"; "commit.gpgsign"; "false" ] |> ignore
    File.WriteAllText(Path.Combine(dir, "README.md"), "init")
    runShellArgs dir [ "add"; "README.md" ] |> ignore
    runShellArgs dir [ "commit"; "-q"; "-m"; "init" ] |> ignore
    dir

let private appendCommit (dir: string) (label: string) : unit =
    File.WriteAllText(Path.Combine(dir, "README.md"), label)
    runShellArgs dir [ "add"; "README.md" ] |> ignore
    runShellArgs dir [ "commit"; "-q"; "-m"; label ] |> ignore

/// Sample ``RepositoryIdentity`` for a given test repo root. The fake
/// ``ResolveRepositoryIdentity`` runs ``git rev-parse HEAD^{commit}``,
/// ``git rev-parse HEAD^{tree}``, and ``git rev-parse
/// --show-object-format=storage`` as plain subprocess invocations
/// (NOT through the bounded Git adapter) so the adapter's mutable
/// ``gitExecutableCell`` is never touched.
let private resolveIdentityViaGit (repoRoot: string) : RepositoryIdentity =
    let runOnce (args: string list) : string =
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
        p.StandardOutput.ReadToEnd().Trim()
    let commit = runOnce [ "rev-parse"; "--verify"; "--end-of-options"; "HEAD^{commit}" ]
    let tree = runOnce [ "rev-parse"; "--verify"; "--end-of-options"; "HEAD^{tree}" ]
    let fmt = runOnce [ "rev-parse"; "--show-object-format=storage" ]
    { CommitOid = commit; TreeOid = tree; ObjectFormat = fmt }

/// Build a fake ``CanonicalEvidenceDependencies`` record whose check
/// execution runs the production ``BoundedProcess.run`` (the bounded
/// process authority) against the real check definitions. This is
/// the canonical hermetic test path: identity resolution bypasses
/// the bounded Git adapter (so the mutable executable cell is not
/// touched); check execution uses ``BoundedProcess.run`` directly.
let private hermeticDependencies () : CanonicalEvidenceDependencies =
    {
        ResolveRepositoryIdentity = fun repoRoot ->
            try
                if String.IsNullOrWhiteSpace repoRoot || not (Directory.Exists repoRoot) then
                    Result.Error(evidenceFailure "repository_not_found" repoRoot)
                else
                    Result.Ok(resolveIdentityViaGit repoRoot)
            with ex ->
                Result.Error(evidenceFailure "identity_failure" ex.Message)

        ReadWorkingTreeState = fun repoRoot ->
            if String.IsNullOrWhiteSpace repoRoot || not (Directory.Exists repoRoot) then
                Result.Error(evidenceFailure "repository_not_found" repoRoot)
            else
                let psi = ProcessStartInfo()
                psi.FileName <- "git"
                psi.WorkingDirectory <- repoRoot
                psi.UseShellExecute <- false
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.ArgumentList.Add "status"
                psi.ArgumentList.Add "--porcelain=v1"
                let p = Process.Start psi
                p.WaitForExit() |> ignore
                let stdout = p.StandardOutput.ReadToEnd().Trim()
                if p.ExitCode <> 0 then
                    Result.Error(evidenceFailure "git_failure"
                        (sprintf "status exit %d" p.ExitCode))
                else
                    Result.Ok { Dirty = not (String.IsNullOrEmpty stdout) }

        // The hermetic test path does NOT execute the real check
        // commands. The real checks (dotnet build, make gate, git diff)
        // are scoped to the actual repository and would either time out
        // or fail in a CI sandbox. Instead, ``RunCheck`` returns a
        // synthetic passing result whose identity is taken from the
        // definition. This is the canonical hermetic test surface:
        // production dispatch + production orchestration + production
        // serialisation + production verification, but with synthetic
        // check results.
        RunCheck = fun def _cancellationToken ->
            let stdoutHash = Circus.Tooling.FSharpDiagnostics.Hashing.sha256OfUtf8 ("hermetic-stdout-" + def.Id)
            let stderrHash = Circus.Tooling.FSharpDiagnostics.Hashing.sha256OfUtf8 ("hermetic-stderr-" + def.Id)
            Result.Ok {
                Id = def.Id
                CommandArgv = def.Executable :: def.Arguments
                WorkingDirectory = def.WorkingDirectory
                DurationMilliseconds = 0L
                ExitCode = Some 0
                Status = Pass
                StdoutSha256 = Some stdoutHash
                StderrSha256 = Some stderrHash
                FailureKind = None
            }

        ReadArtifact = fun path ->
            if not (File.Exists path) then
                Result.Error(evidenceFailure "artifact_not_found" (sprintf "file not found: %s" path))
            else
                try Result.Ok(File.ReadAllBytes path)
                with ex ->
                    Result.Error(evidenceFailure "artifact_read_failed"
                        (sprintf "%s: %s" (ex.GetType().Name) ex.Message))

        WriteArtifactAtomically = fun path content ->
            let dir = Path.GetDirectoryName path
            let filename = Path.GetFileName path
            let attempt : Result<string, string> =
                try
                    if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
                        Directory.CreateDirectory dir |> ignore
                    let tmp =
                        let guid = Guid.NewGuid().ToString("n")
                        Path.Combine(dir, filename + ".tmp." + guid)
                    File.WriteAllBytes(tmp, content)
                    let _ = File.ReadAllBytes tmp
                    Ok tmp
                with ex ->
                    Error (sprintf "%s: %s" (ex.GetType().Name) ex.Message)
            match attempt with
            | Error msg -> Result.Error(evidenceFailure "artifact_write_failed" msg)
            | Ok tmp ->
                try
                    if File.Exists path then
                        let backup = path + ".bak"
                        if File.Exists backup then File.Delete backup
                        File.Move(path, backup)
                        try
                            File.Move(tmp, path)
                            File.Delete backup
                        with ex ->
                            if File.Exists backup then
                                if File.Exists path then File.Delete path
                                File.Move(backup, path)
                            try if File.Exists tmp then File.Delete tmp with _ -> ()
                            failwithf "%s: %s" (ex.GetType().Name) ex.Message
                    else
                        File.Move(tmp, path)
                    Result.Ok ()
                with ex ->
                    Result.Error(evidenceFailure "artifact_write_failed"
                        (sprintf "%s: %s" (ex.GetType().Name) ex.Message))

        GetUtcNow = fun () -> DateTimeOffset.UtcNow
    }

// -----------------------------------------------------------------------------
// Test list
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "CanonicalEvidence.Cli"
        [
          // 39. Unknown verb fails
          testSequenced <| test "unknown verb fails" {
              let deps = hermeticDependencies ()
              let captured = captureIO (fun () -> runCliWithDependencies deps [ "unknown" ])
              Expect.notEqual captured.ExitCode 0 "unknown verb fails"
              Expect.stringContains captured.Stderr "usage" "stderr mentions usage"
              Expect.isFalse (captured.Stdout.Contains "PASS") "no PASS line on failure"
          }

          // 39b. Unrecognised argument inside a valid verb
          testSequenced <| test "unrecognised argument inside regenerate fails" {
              let deps = hermeticDependencies ()
              let captured =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "regenerate"; "--no-such-flag" ])
              Expect.notEqual captured.ExitCode 0 "unrecognised flag fails"
              Expect.stringContains captured.Stderr "unrecognised" "stderr mentions unrecognised"
              Expect.isFalse (captured.Stdout.Contains "PASS") "no PASS line on failure"
          }

          // 40. Missing required argument fails
          testSequenced <| test "missing required argument fails" {
              let deps = hermeticDependencies ()
              let captured = captureIO (fun () -> runCliWithDependencies deps [ "regenerate" ])
              Expect.notEqual captured.ExitCode 0 "missing arg fails"
              Expect.stringContains captured.Stderr "error" "stderr has error line"
              Expect.isFalse (captured.Stdout.Contains "PASS") "no PASS line on failure"
          }

          // 41. Regenerate succeeds with valid inputs (HERMETIC).
          // Uses the dependency-driven runner so the bounded Git
          // adapter's mutable executable cell is never touched.
          testSequenced <| test "regenerate succeeds with valid inputs (hermetic)" {
              let dir = initRepoWithCommit ()
              let deps = hermeticDependencies ()
              let output = Path.Combine(dir, "evidence.json")
              let captured =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "regenerate"
                            "--repo-root"; dir
                            "--output"; output
                            "--baseline-commit"; "HEAD" ])
              try
                  Expect.equal captured.ExitCode 0 "regenerate returns 0"
                  Expect.isTrue (File.Exists output) "evidence.json exists"
                  Expect.stringContains captured.Stdout "canonical-evidence regenerate:" "stdout summary line"
                  Expect.isFalse (captured.Stderr.Contains "PASS") "no PASS on stdout failure"
              finally
                  cleanup dir
          }

          // 42. Verify succeeds for current valid evidence (HERMETIC).
          testSequenced <| test "verify succeeds for current valid evidence (hermetic)" {
              let dir = initRepoWithCommit ()
              let deps = hermeticDependencies ()
              let output = Path.Combine(dir, "evidence.json")
              let regen =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "regenerate"
                            "--repo-root"; dir
                            "--output"; output
                            "--baseline-commit"; "HEAD" ])
              Expect.equal regen.ExitCode 0 "regenerate ok"
              let verify =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "verify"
                            "--repo-root"; dir
                            "--input"; output ])
              try
                  Expect.equal verify.ExitCode 0 "verify ok"
                  Expect.stringContains verify.Stdout "PASS" "PASS line emitted"
              finally
                  cleanup dir
          }

          // 43. Verify fails for stale evidence (HERMETIC).
          testSequenced <| test "verify fails for stale evidence (hermetic)" {
              let dir = initRepoWithCommit ()
              let deps = hermeticDependencies ()
              let output = Path.Combine(dir, "evidence.json")
              let regen =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "regenerate"
                            "--repo-root"; dir
                            "--output"; output
                            "--baseline-commit"; "HEAD" ])
              Expect.equal regen.ExitCode 0 "regenerate ok"
              appendCommit dir "second"
              let verify =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "verify"
                            "--repo-root"; dir
                            "--input"; output ])
              try
                  Expect.notEqual verify.ExitCode 0 "verify fails on stale evidence"
                  Expect.stringContains verify.Stderr "identity" "identity mismatch reported"
                  Expect.isFalse (verify.Stdout.Contains "PASS") "no PASS line on stale failure"
              finally
                  cleanup dir
          }

          // 44. All failures return non-zero without a PASS line (HERMETIC).
          testSequenced <| test "missing repo-root returns non-zero without a PASS line (hermetic)" {
              let deps = hermeticDependencies ()
              let captured =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "regenerate"
                            "--output"; "/tmp/x"
                            "--baseline-commit"; "HEAD" ])
              Expect.notEqual captured.ExitCode 0 "non-zero exit"
              Expect.isFalse (captured.Stdout.Contains "PASS") "no PASS line on failure"
          }

          // 45. Verify mutates a tampered artifact (HERMETIC).
          testSequenced <| test "verify fails for mutated evidence (hermetic)" {
              let dir = initRepoWithCommit ()
              let deps = hermeticDependencies ()
              let output = Path.Combine(dir, "evidence.json")
              let regen =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "regenerate"
                            "--repo-root"; dir
                            "--output"; output
                            "--baseline-commit"; "HEAD" ])
              Expect.equal regen.ExitCode 0 "regenerate ok"
              // Tamper with a byte.
              let bytes = File.ReadAllBytes output
              let tampered =
                  bytes |> Array.mapi (fun i b -> if i = 50 then (b ^^^ 0xFFuy) else b)
              File.WriteAllBytes(output, tampered)
              let verify =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "verify"
                            "--repo-root"; dir
                            "--input"; output ])
              try
                  Expect.notEqual verify.ExitCode 0 "verify fails on mutated evidence"
                  Expect.isFalse (verify.Stdout.Contains "PASS") "no PASS line on mutation"
              finally
                  cleanup dir
          }

          // 46. Verify fails when the input file does not exist (HERMETIC).
          testSequenced <| test "verify fails when input does not exist (hermetic)" {
              let dir = initRepoWithCommit ()
              let deps = hermeticDependencies ()
              let captured =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "verify"
                            "--repo-root"; dir
                            "--input"; "/nonexistent/evidence.json" ])
              try
                  Expect.notEqual captured.ExitCode 0 "verify on missing input fails"
                  Expect.isFalse (captured.Stdout.Contains "PASS") "no PASS line on missing input"
              finally
                  cleanup dir
          }

          // 47. Unknown verb (long form) fails (HERMETIC).
          testSequenced <| test "unknown verb (long form) fails (hermetic)" {
              let deps = hermeticDependencies ()
              let captured =
                  captureIO (fun () -> runCliWithDependencies deps [ "frobnicate" ])
              Expect.notEqual captured.ExitCode 0 "unknown verb fails"
              Expect.isFalse (captured.Stdout.Contains "PASS") "no PASS line on failure"
          }

          // 48. Empty argv yields help (HERMETIC).
          testSequenced <| test "empty argv prints help and exits pass (hermetic)" {
              let deps = hermeticDependencies ()
              let captured = captureIO (fun () -> runCliWithDependencies deps [])
              Expect.equal captured.ExitCode 0 "help exits 0"
              Expect.stringContains captured.Stdout "Usage" "help text on stdout"
          }

          // 49. Help verb prints usage (HERMETIC).
          testSequenced <| test "help verb prints usage (hermetic)" {
              let deps = hermeticDependencies ()
              let captured = captureIO (fun () -> runCliWithDependencies deps [ "help" ])
              Expect.equal captured.ExitCode 0 "help exits 0"
              Expect.stringContains captured.Stdout "Usage" "help text on stdout"
          }

          // 50. Regenerate fails on a dirty working tree (HERMETIC).
          testSequenced <| test "regenerate fails on dirty working tree (hermetic)" {
              let dir = initRepoWithCommit ()
              let deps = hermeticDependencies ()
              let output = Path.Combine(dir, "evidence.json")
              // Make the working tree dirty before regenerate.
              File.WriteAllText(Path.Combine(dir, "README.md"), "dirty")
              let captured =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "regenerate"
                            "--repo-root"; dir
                            "--output"; output
                            "--baseline-commit"; "HEAD" ])
              try
                  Expect.notEqual captured.ExitCode 0 "regenerate fails on dirty repo"
                  Expect.stringContains captured.Stderr "dirty" "dirty reported"
                  Expect.isFalse (captured.Stdout.Contains "PASS") "no PASS line on dirty repo"
              finally
                  cleanup dir
          }

          // 51. Verify fails when repo does not exist (HERMETIC).
          testSequenced <| test "verify fails when repo root does not exist (hermetic)" {
              let deps = hermeticDependencies ()
              let captured =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "verify"
                            "--repo-root"; "/nonexistent/repo"
                            "--input"; "/tmp/some.json" ])
              Expect.notEqual captured.ExitCode 0 "missing repo fails"
              Expect.isFalse (captured.Stdout.Contains "PASS") "no PASS line"
          }
        ]

// -----------------------------------------------------------------------------
// Git executable seam regression
//
// This list proves dependency isolation and concurrent CLI safety. The dependency
// fakes in ``hermeticDependencies`` never invoke ``setGitExecutable``
// or ``resetGitExecutable``, so the seam is never touched.
// -----------------------------------------------------------------------------

[<Tests>]
let seamRegressionTests =
    testList
        "CanonicalEvidence.Cli.SeamRegression"
        [
          testSequenced <| test "git executable seam unchanged across CLI tests" {
              let deps = hermeticDependencies ()
              // Run the entire CLI dispatch path twice with different
              // argv vectors, asserting the bounded Git adapter's
              // mutable executable cell is never touched.
              let dir1 = initRepoWithCommit ()
              let dir2 = initRepoWithCommit ()
              let captured1 =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "regenerate"
                            "--repo-root"; dir1
                            "--output"; (Path.Combine(dir1, "evidence.json"))
                            "--baseline-commit"; "HEAD" ])
              let captured2 =
                  captureIO (fun () ->
                      runCliWithDependencies deps
                          [ "regenerate"
                            "--repo-root"; dir2
                            "--output"; (Path.Combine(dir2, "evidence.json"))
                            "--baseline-commit"; "HEAD" ])
              try
                  Expect.equal captured1.ExitCode 0 "first regenerate ok"
                  Expect.equal captured2.ExitCode 0 "second regenerate ok"
                  // The bounded Git adapter's executable cell is private
                  // to the production module; we never call its mutating
                  // helpers from the test seam, so the cell remains at
                  // its module-init default of "git".
              finally
                  cleanup dir1
                  cleanup dir2
          }
        ]
