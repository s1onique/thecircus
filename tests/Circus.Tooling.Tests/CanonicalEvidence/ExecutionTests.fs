module Circus.Tooling.Tests.CanonicalEvidence.ExecutionTests

// =============================================================================
// Execution adapter tests for the canonical evidence provider
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// Tests 14–27: check execution through BoundedProcess.run with the
// precompiled F# fixture (the same fixture the bounded-process
// authority tests use).
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess
open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.Provider

let private fixturePath =
    Path.Combine(AppContext.BaseDirectory, "circus-process-tree-fixture.dll")

let private resolveFixturePath () : string =
    if not (File.Exists fixturePath) then
        failwithf "precompiled process fixture not found at %s. Build the test project first." fixturePath
    fixturePath

let private tempWorkDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "circus-canonev-" + Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory dir |> ignore
    dir

let private makeDef (id: string) (args: string list) (workingDir: string) : EvidenceCheckDefinition =
    {
        Id = id
        Executable = "dotnet"
        WorkingDirectory = workingDir
        Arguments = (resolveFixturePath() :: args)
        Required = true
        Timeout = TimeSpan.FromSeconds(30.0)
        StdoutLimitBytes = 1024 * 1024
        StderrLimitBytes = 1024 * 1024
    }

let private makeDefRaw (id: string) (executable: string) (args: string list) (workingDir: string) : EvidenceCheckDefinition =
    {
        Id = id
        Executable = executable
        WorkingDirectory = workingDir
        Arguments = args
        Required = true
        Timeout = TimeSpan.FromSeconds(30.0)
        StdoutLimitBytes = 1024 * 1024
        StderrLimitBytes = 1024 * 1024
    }

[<Tests>]
let tests =
    testList
        "CanonicalEvidence.Execution"
        [
          // 14. Successful command
          test "successful command => pass" {
              let wd = tempWorkDir ()
              let def = makeDef "ok" [ "empty" ] wd
              let result = runCheck def
              Expect.equal result.Status Pass "successful exit 0"
              Expect.equal result.ExitCode (Some 0) "exit code 0"
              Expect.equal result.Id "ok" "id preserved"
              Expect.isSome result.StdoutSha256 "stdout hash captured"
              Expect.isNone result.FailureKind "no failure kind"
          }

          // 15. Non-zero exit
          test "non-zero exit => fail" {
              let wd = tempWorkDir ()
              let def = makeDef "exit1" [ "exit"; "7" ] wd
              let result = runCheck def
              Expect.equal result.Status Fail "non-zero exit => fail"
              Expect.equal result.ExitCode (Some 7) "exit code 7"
              Expect.isSome result.FailureKind "failure kind set"
              Expect.stringContains result.FailureKind.Value "non_zero_exit" "non_zero_exit recorded"
          }

          // 16. Missing executable
          test "missing executable => unavailable" {
              let wd = tempWorkDir ()
              let def = makeDefRaw "missing" "/absolute/nonexistent/circus-tooling-binary" [ "empty" ] wd
              let result = runCheck def
              Expect.equal result.Status Unavailable "missing executable => unavailable"
              Expect.isNone result.ExitCode "no exit code"
              Expect.isSome result.FailureKind "failure kind set"
              Expect.stringContains result.FailureKind.Value "launch_failed" "launch_failed recorded"
          }

          // 17. Timeout
          test "timeout => unavailable" {
              let wd = tempWorkDir ()
              let def = {
                  Id = "slow"
                  Executable = "dotnet"
                  WorkingDirectory = wd
                  Arguments = (resolveFixturePath() :: [ "sleep"; "8000" ])
                  Required = true
                  Timeout = TimeSpan.FromMilliseconds(1500.0)
                  StdoutLimitBytes = 1024 * 1024
                  StderrLimitBytes = 1024 * 1024
              }
              let result = runCheck def
              Expect.equal result.Status Unavailable "timeout => unavailable"
              Expect.isSome result.FailureKind "failure kind set"
              Expect.stringContains result.FailureKind.Value "timed_out" "timed_out recorded"
          }

          // 18. Cancellation
          test "cancelled => unavailable" {
              let wd = tempWorkDir ()
              let cts = new System.Threading.CancellationTokenSource()
              let request: BoundedProcessRequest = {
                  Executable = "dotnet"
                  WorkingDirectory = wd
                  Arguments = (resolveFixturePath() :: [ "sleep"; "8000" ])
                  Environment = []
                  Limits = {
                      Timeout = TimeSpan.FromSeconds(20.0)
                      StdoutLimitBytes = 1024 * 1024
                      StderrLimitBytes = 1024 * 1024
                  }
              }
              let task =
                  run request cts.Token
              cts.CancelAfter(200)
              let result =
                  task
                  |> Async.AwaitTask
                  |> Async.RunSynchronously
              let _ = task
              Expect.isError result "cancelled task returns error"
              match result with
              | Error failure ->
                  let kind = boundedFailureKind failure
                  Expect.stringContains kind "cancelled" "cancelled failure kind"
              | Ok _ ->
                  failwith "expected cancelled failure"
          }

          // 19. stdout exact limit
          test "stdout exact limit is allowed" {
              let wd = tempWorkDir ()
              let def = {
                  Id = "stdout-exact"
                  Executable = "dotnet"
                  WorkingDirectory = wd
                  Arguments = (resolveFixturePath() :: [ "stdout"; "100" ])
                  Required = true
                  Timeout = TimeSpan.FromSeconds(15.0)
                  StdoutLimitBytes = 100
                  StderrLimitBytes = 1024 * 1024
              }
              let result = runCheck def
              Expect.equal result.Status Pass "exact limit passes"
              Expect.equal result.ExitCode (Some 0) "exit 0"
          }

          // 20. stdout limit plus one
          test "stdout limit plus one fails closed" {
              let wd = tempWorkDir ()
              let def = {
                  Id = "stdout-overflow"
                  Executable = "dotnet"
                  WorkingDirectory = wd
                  Arguments = (resolveFixturePath() :: [ "stdout"; "101" ])
                  Required = true
                  Timeout = TimeSpan.FromSeconds(15.0)
                  StdoutLimitBytes = 100
                  StderrLimitBytes = 1024 * 1024
              }
              let result = runCheck def
              Expect.equal result.Status Unavailable "overflow => unavailable"
              Expect.isSome result.FailureKind "failure kind set"
              Expect.stringContains result.FailureKind.Value "stdout_limit_exceeded" "stdout limit exceeded"
          }

          // 21. stderr exact limit
          test "stderr exact limit is allowed" {
              let wd = tempWorkDir ()
              let def = {
                  Id = "stderr-exact"
                  Executable = "dotnet"
                  WorkingDirectory = wd
                  Arguments = (resolveFixturePath() :: [ "stderr"; "100" ])
                  Required = true
                  Timeout = TimeSpan.FromSeconds(15.0)
                  StdoutLimitBytes = 1024 * 1024
                  StderrLimitBytes = 100
              }
              let result = runCheck def
              Expect.equal result.Status Pass "exact limit passes"
          }

          // 22. stderr limit plus one
          test "stderr limit plus one fails closed" {
              let wd = tempWorkDir ()
              let def = {
                  Id = "stderr-overflow"
                  Executable = "dotnet"
                  WorkingDirectory = wd
                  Arguments = (resolveFixturePath() :: [ "stderr"; "101" ])
                  Required = true
                  Timeout = TimeSpan.FromSeconds(15.0)
                  StdoutLimitBytes = 1024 * 1024
                  StderrLimitBytes = 100
              }
              let result = runCheck def
              Expect.equal result.Status Unavailable "overflow => unavailable"
              Expect.isSome result.FailureKind "failure kind set"
              Expect.stringContains result.FailureKind.Value "stderr_limit_exceeded" "stderr limit exceeded"
          }

          // 23. reader failure translation
          test "reader failure is translated to unavailable" {
              // Synthesize a BoundedProcessFailure value via the
              // public translation function. The reader failure
              // path is reachable through BoundedProcess when the
              // underlying stream raises; here we prove the
              // translation target is correct.
              let fw = BoundedProcessFailure.StdoutReaderFailed "synthetic"
              let kind = boundedFailureKind fw
              Expect.stringContains kind "stdout_reader_failed" "reader failure kind"
              Expect.equal (mapFailureToStatus fw) Unavailable "reader failure => unavailable"
              let fr = BoundedProcessFailure.StderrReaderFailed "synthetic"
              Expect.equal (mapFailureToStatus fr) Unavailable "stderr reader failure => unavailable"
          }

          // 24. incomplete-output translation
          test "incomplete output is translated to unavailable" {
              let fw = BoundedProcessFailure.IncompleteOutput(true, false)
              Expect.equal (mapFailureToStatus fw) Unavailable "incomplete output => unavailable"
              Expect.stringContains (boundedFailureKind fw) "incomplete_output" "kind token"
          }

          // 25. arguments with spaces and metacharacters remain literal
          test "arguments containing spaces and metacharacters remain literal" {
              let wd = tempWorkDir ()
              let def = makeDef "echo-meta" [ "echo-args"; "hello world"; "a;b&c"; "'quoted'" ] wd
              let result = runCheck def
              Expect.equal result.Status Pass "echo-args exits 0"
              Expect.stringContains (result.CommandArgv |> List.fold (fun a b -> a + " " + b) "")
                  "hello world" "spaces preserved in argv"
              Expect.stringContains (result.CommandArgv |> List.fold (fun a b -> a + " " + b) "")
                  "a;b&c" "metacharacters preserved literally"
          }

          // 26. supplied working directory is honored
          test "supplied working directory is honored" {
              let wd = tempWorkDir ()
              let def = makeDef "wd" [ "working-directory" ] wd
              let result = runCheck def
              Expect.equal result.Status Pass "working-directory mode exits 0"
              Expect.isSome result.StdoutSha256 "stdout captured"
              // The hash proves the captured bytes match the working
              // directory; we compare against the expected hash of
              // the absolute path bytes.
              let expected = Circus.Tooling.FSharpDiagnostics.Hashing.sha256OfUtf8 wd
              Expect.equal result.StdoutSha256.Value expected "stdout = SHA-256(workingDirectory)"
          }

          // 27. no provider-owned Process.Start exists
          test "no provider-owned Process.Start exists" {
              let providerSrc = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "tools", "Circus.Tooling", "CanonicalEvidence", "Provider.fs"))
              let hasForbidden =
                  providerSrc.Contains("Process.Start")
                  || providerSrc.Contains("DataReceivedEventHandler")
                  || providerSrc.Contains("BeginOutputReadLine")
                  || providerSrc.Contains("BeginErrorReadLine")
                  || providerSrc.Contains("StandardOutput.BaseStream")
                  || providerSrc.Contains("StandardError.BaseStream")
              Expect.isFalse hasForbidden "provider source must not invoke Process.Start directly"
          }
        ]
