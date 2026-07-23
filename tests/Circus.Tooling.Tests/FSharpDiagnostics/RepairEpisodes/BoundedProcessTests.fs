module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.BoundedProcessTests

open Expecto
open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// -----------------------------------------------------------------------------
// Test fixture helpers
// -----------------------------------------------------------------------------

/// Creates a simple fixture that writes bytes to stdout
let private createStdoutFixture (bytes: byte array) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-stdout-{Guid.NewGuid():N}.fsx")
    let content =
        sprintf
            "stdout.Write(System.Text.Encoding.UTF8.GetString([|%s|]))\nstdout.Flush()\n"
            (String.concat "uy;" (Array.map string bytes) + "uy")
    File.WriteAllText(path, content)
    path

/// Creates a simple fixture that writes bytes to stderr
let private createStderrFixture (bytes: byte array) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-stderr-{Guid.NewGuid():N}.fsx")
    let content =
        sprintf
            "stderr.Write(System.Text.Encoding.UTF8.GetString([|%s|]))\nstderr.Flush()\n"
            (String.concat "uy;" (Array.map string bytes) + "uy")
    File.WriteAllText(path, content)
    path

/// Creates a fixture that writes bytes to both stdout and stderr
let private createBothFixture (stdoutBytes: byte array) (stderrBytes: byte array) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-both-{Guid.NewGuid():N}.fsx")
    let stdoutStr = String.concat "uy;" (Array.map string stdoutBytes) + "uy"
    let stderrStr = String.concat "uy;" (Array.map string stderrBytes) + "uy"
    let content =
        sprintf
            "stdout.Write(System.Text.Encoding.UTF8.GetString([|%s|]))\nstdout.Flush()\nstderr.Write(System.Text.Encoding.UTF8.GetString([|%s|]))\nstderr.Flush()\n"
            stdoutStr stderrStr
    File.WriteAllText(path, content)
    path

/// Creates a fixture that sleeps for specified milliseconds
let private createSleepFixture (ms: int) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-sleep-{Guid.NewGuid():N}.fsx")
    let content = sprintf "Thread.Sleep(%d)\nstdout.Write(\"done\")\nstdout.Flush()\n" ms
    File.WriteAllText(path, content)
    path

/// Creates a fixture that exits with a specific code
let private createExitFixture (code: int) (stdoutBytes: byte array) (stderrBytes: byte array) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-exit-{Guid.NewGuid():N}.fsx")
    let stdoutStr = String.concat "uy;" (Array.map string stdoutBytes) + "uy"
    let stderrStr = String.concat "uy;" (Array.map string stderrBytes) + "uy"
    let content =
        sprintf
            "stdout.Write(System.Text.Encoding.UTF8.GetString([|%s|]))\nstdout.Flush()\nstderr.Write(System.Text.Encoding.UTF8.GetString([|%s|]))\nstderr.Flush()\nexit %d\n"
            stdoutStr stderrStr code
    File.WriteAllText(path, content)
    path

/// Creates a fixture that echoes arguments
let private createEchoArgsFixture () : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-echo-{Guid.NewGuid():N}.fsx")
    let content = """
for arg in fsi.CommandLineArgs |> Array.skip 3 do
    stdout.Write(arg + " ")
stdout.WriteLine()
stdout.Flush()
"""
    File.WriteAllText(path, content)
    path

/// Creates a fixture that prints the working directory
let private createWorkingDirFixture () : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-workdir-{Guid.NewGuid():N}.fsx")
    let content = "stdout.Write(Directory.GetCurrentDirectory())\nstdout.Flush()\n"
    File.WriteAllText(path, content)
    path

/// Helper to run bounded process
let private runBounded
    (executable: string)
    (workingDirectory: string)
    (args: string list)
    (env: (string * string) list)
    (timeout: TimeSpan)
    (stdoutLimit: int)
    (stderrLimit: int)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    let request = {
        Executable = executable
        WorkingDirectory = workingDirectory
        Arguments = args
        Environment = env
        Limits = {
            Timeout = timeout
            StdoutLimitBytes = stdoutLimit
            StderrLimitBytes = stderrLimit
        }
    }
    BoundedProcess.run request CancellationToken.None

/// Helper to make expected stdout bytes
let private makeStdoutBytes (count: int) : byte array =
    Array.init count (fun i -> byte (97 + (i % 26)))

/// Helper to make expected stderr bytes
let private makeStderrBytes (count: int) : byte array =
    Array.init count (fun i -> byte (65 + (i % 26)))

// -----------------------------------------------------------------------------
// Tests
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.RepairEpisodes.BoundedProcess"
        [
          // 1. Empty stdout process succeeds
          testTask "empty stdout process returns Ok with empty arrays" {
              let fixture = createStdoutFixture [||]
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout [||] "stdout should be empty"
                      Expect.equal success.Stderr [||] "stderr should be empty"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 2. Non-empty stdout is captured
          testTask "non-empty stdout is captured correctly" {
              let expected = makeStdoutBytes 10
              let fixture = createStdoutFixture expected
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout expected "stdout should have 10 bytes"
                      Expect.equal success.Stderr [||] "stderr should be empty"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 3. Non-empty stderr is captured
          testTask "non-empty stderr is captured correctly" {
              let expected = makeStderrBytes 10
              let fixture = createStderrFixture expected
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout [||] "stdout should be empty"
                      Expect.equal success.Stderr expected "stderr should have 10 bytes"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 4. Working directory is propagated
          testTask "working directory is propagated to subprocess" {
              let tempDir = Path.GetTempPath()
              let fixture = createWorkingDirFixture ()
              try
                  let! result = runBounded "dotnet" tempDir [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Ok success ->
                      let expectedDir = tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      let actualDir = System.Text.Encoding.UTF8.GetString(success.Stdout).Trim()
                      Expect.equal actualDir expectedDir "working directory propagated"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 5. Arguments with spaces are preserved
          testTask "arguments containing spaces remain as one argument" {
              let fixture = createEchoArgsFixture ()
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture; "hello world"; "foo" ] [] (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Ok success ->
                      let output = System.Text.Encoding.UTF8.GetString(success.Stdout).Trim()
                      Expect.stringContains output "hello world" "spaces should be preserved"
                      Expect.stringContains output "foo" "foo should be present"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 6. Quote characters in arguments
          testTask "quote characters in arguments are preserved" {
              let fixture = createEchoArgsFixture ()
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture; "\"hello\""; "'world'" ] [] (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Ok success ->
                      let output = System.Text.Encoding.UTF8.GetString(success.Stdout)
                      Expect.stringContains output "\"hello\"" "double quotes preserved"
                      Expect.stringContains output "'world'" "single quotes preserved"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 7. Exact stdout limit succeeds
          testTask "exact stdout limit succeeds" {
              let expected = makeStdoutBytes 50
              let fixture = createStdoutFixture expected
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 50 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout expected "stdout should match at exact limit"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 8. Stdout over limit fails
          testTask "stdout over limit fails with StdoutLimitExceeded" {
              let bytes = makeStdoutBytes 51
              let fixture = createStdoutFixture bytes
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 50 1024
                  match result with
                  | Error(StdoutLimitExceeded 50) -> ()
                  | Error e -> failwithf "expected StdoutLimitExceeded, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 9. Exact stderr limit succeeds
          testTask "exact stderr limit succeeds" {
              let expected = makeStderrBytes 50
              let fixture = createStderrFixture expected
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 1024 50
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stderr expected "stderr should match at exact limit"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 10. Stderr over limit fails
          testTask "stderr over limit fails with StderrLimitExceeded" {
              let bytes = makeStderrBytes 51
              let fixture = createStderrFixture bytes
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 1024 50
                  match result with
                  | Error(StderrLimitExceeded 50) -> ()
                  | Error e -> failwithf "expected StderrLimitExceeded, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 11. Zero stdout limit with zero bytes succeeds
          testTask "zero stdout limit with zero bytes succeeds" {
              let fixture = createStdoutFixture [||]
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 0 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout [||] "stdout should be empty"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 12. Zero stdout limit with one byte fails
          testTask "zero stdout limit with one byte fails" {
              let bytes = [| 65uy |]
              let fixture = createStdoutFixture bytes
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 0 1024
                  match result with
                  | Error(StdoutLimitExceeded 0) -> ()
                  | Error e -> failwithf "expected StdoutLimitExceeded, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 13. Concurrent stdout and stderr
          testTask "concurrent stdout and stderr are both captured" {
              let stdout = makeStdoutBytes 100
              let stderr = makeStderrBytes 100
              let fixture = createBothFixture stdout stderr
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 10.0) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout stdout "stdout bytes preserved"
                      Expect.equal success.Stderr stderr "stderr bytes preserved"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 14. Non-zero exit code preserves output
          testTask "non-zero exit preserves exit code and output" {
              let stdout = makeStdoutBytes 10
              let stderr = makeStderrBytes 10
              let fixture = createExitFixture 42 stdout stderr
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Error(NonZeroExit(42, actualStdout, actualStderr)) ->
                      Expect.equal actualStdout stdout "stdout preserved"
                      Expect.equal actualStderr stderr "stderr preserved"
                  | Error e -> failwithf "expected NonZeroExit(42, ...), got: %A" e
                  | Ok s -> failwithf "expected NonZeroExit, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 15. Timeout returns TimedOut
          testTask "timeout returns TimedOut" {
              let fixture = createSleepFixture 5000 // 5 seconds
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromMilliseconds 500.0) 1024 1024
                  match result with
                  | Error(TimedOut timeout) ->
                      Expect.isTrue (timeout.TotalMilliseconds <= 1000.0) "timeout should be reasonable"
                  | Error e -> failwithf "expected TimedOut, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 16. Pre-cancelled token returns Cancelled
          testTask "pre-cancelled token returns Cancelled" {
              let cts = new CancellationTokenSource()
              cts.Cancel()
              let fixture = createSleepFixture 10000 // 10 seconds
              let req = {
                  Executable = "dotnet"
                  WorkingDirectory = Path.GetTempPath()
                  Arguments = [ "fsi"; "--exec"; fixture ]
                  Environment = []
                  Limits = {
                      Timeout = TimeSpan.FromSeconds 30.0
                      StdoutLimitBytes = 1024
                      StderrLimitBytes = 1024
                  }
              }
              try
                  let! result = BoundedProcess.run req cts.Token
                  cts.Dispose()
                  match result with
                  | Error Cancelled -> ()
                  | Error e -> failwithf "expected Cancelled, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 17. Missing executable produces LaunchFailed
          testTask "missing executable produces LaunchFailed" {
              let! result = runBounded "/nonexistent/executable/path" (Path.GetTempPath()) [] [] (TimeSpan.FromSeconds 5.0) 1024 1024
              match result with
              | Error(LaunchFailed(exe, _)) ->
                  Expect.stringContains exe "nonexistent" "should mention nonexistent"
              | Error e -> failwithf "expected LaunchFailed, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 18. Missing working directory produces InvalidRequest
          testTask "missing working directory produces InvalidRequest" {
              let nonexistentDir = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N"))
              let! result = runBounded "dotnet" nonexistentDir [] [] (TimeSpan.FromSeconds 5.0) 1024 1024
              match result with
              | Error(InvalidRequest msg) ->
                  Expect.stringContains msg "working directory" "should mention working directory"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 19. Negative stdout limit produces InvalidRequest
          testTask "negative stdout limit produces InvalidRequest" {
              let! result = runBounded "dotnet" (Path.GetTempPath()) [] [] (TimeSpan.FromSeconds 5.0) -1 1024
              match result with
              | Error(InvalidRequest msg) ->
                  Expect.stringContains msg "stdout" "should mention stdout"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 20. Negative stderr limit produces InvalidRequest
          testTask "negative stderr limit produces InvalidRequest" {
              let! result = runBounded "dotnet" (Path.GetTempPath()) [] [] (TimeSpan.FromSeconds 5.0) 1024 -1
              match result with
              | Error(InvalidRequest msg) ->
                  Expect.stringContains msg "stderr" "should mention stderr"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 21. Duplicate environment keys produce InvalidRequest
          testTask "duplicate environment keys produce InvalidRequest" {
              let! result = runBounded "dotnet" (Path.GetTempPath()) [] [ "FOO", "bar"; "FOO", "baz" ] (TimeSpan.FromSeconds 5.0) 1024 1024
              match result with
              | Error(InvalidRequest msg) ->
                  Expect.stringContains msg "environment" "should mention environment"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }
        ]
