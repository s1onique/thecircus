module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.BoundedProcessTests

open Expecto
open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// -----------------------------------------------------------------------------
// Test fixture: a simple F# script that emits controlled output
// -----------------------------------------------------------------------------

/// Runs a fixture script using dotnet fsi.
/// The script supports these commands:
///   stdout <count>         - emit <count> bytes to stdout
///   stderr <count>         - emit <count> bytes to stderr
///   both <stdout> <stderr> - emit to both streams
///   sleep <ms>             - sleep for <ms> milliseconds
///   exit <code>            - exit with <code>
///   echo-args              - echo all arguments to stdout
///   working-directory      - print current directory to stdout
///   nul                    - try to read from /dev/null
let private runFixture
    (fixtureScript: string)
    (args: string list)
    (timeout: TimeSpan)
    (stdoutLimit: int)
    (stderrLimit: int)
    (cancellationToken: CancellationToken)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    let request = {
        Executable = "dotnet"
        WorkingDirectory = Path.GetTempPath()
        Arguments = [ "fsi"; "--exec"; fixtureScript ] @ args
        Environment = []
        Limits = {
            Timeout = timeout
            StdoutLimitBytes = stdoutLimit
            StderrLimitBytes = stderrLimit
        }
    }
    BoundedProcess.run request cancellationToken

let private makeStdoutBytes (count: int) : byte array =
    // Emit printable ASCII bytes
    Array.init count (fun i -> byte (97 + (i % 26)))

let private makeStderrBytes (count: int) : byte array =
    // Emit printable ASCII bytes (different pattern)
    Array.init count (fun i -> byte (65 + (i % 26)))

// Create a temporary fixture script
let private withFixture (body: string -> 'a) : 'a =
    let scriptPath = Path.Combine(Path.GetTempPath(), $"fixture-{Guid.NewGuid():N}.fsx")
    File.WriteAllText(scriptPath, body scriptPath)
    try
        body scriptPath |> ignore
        failwith "body must call the passed script path"
    finally
        if File.Exists scriptPath then File.Delete scriptPath

let private withTempFixture (body: string -> 'a) : 'a =
    let scriptPath = Path.Combine(Path.GetTempPath(), $"fixture-{Guid.NewGuid():N}.fsx")
    try
        File.WriteAllText(scriptPath, "")
        body scriptPath
    finally
        if File.Exists scriptPath then File.Delete scriptPath

// Helper to create a fixture that supports the test commands
let private createFixture (commands: string) : string =
    // The fixture is an F# script that parses commands from args
    // Each command is: verb [arg1] [arg2]
    // We use fsi to execute this
    sprintf """
module Fixture
open System
open System.IO
open System.Threading

let emit (stream: StreamWriter) count charCode =
    for i in 1 .. count do
        stream.Write(char charCode)
    stream.Flush()

let mutable idx = 0
let args = fsi.CommandLineArgs |> Array.skip 2 |> Array.toList // skip fsi and --exec

let rec process () =
    if idx >= List.length args then ()
    else
        match List.item idx args with
        | "stdout" ->
            idx <- idx + 1
            let count = int (List.item idx args); idx <- idx + 1
            emit stdout count (char 97)
            process ()
        | "stderr" ->
            idx <- idx + 1
            let count = int (List.item idx args); idx <- idx + 1
            emit stderr count (char 65)
            process ()
        | "both" ->
            idx <- idx + 1
            let sc = int (List.item idx args); idx <- idx + 1
            let ec = int (List.item idx args); idx <- idx + 1
            emit stdout sc (char 97)
            emit stderr ec (char 65)
            process ()
        | "sleep" ->
            idx <- idx + 1
            let ms = int (List.item idx args); idx <- idx + 1
            Thread.Sleep(ms)
            process ()
        | "exit" ->
            idx <- idx + 1
            let code = int (List.item idx args); idx <- idx + 1
            exit code
        | "echo-args" ->
            idx <- idx + 1
            args |> List.iter (fun a -> stdout.Write(a + " "))
            stdout.WriteLine()
            stdout.Flush()
            process ()
        | "working-directory" ->
            idx <- idx + 1
            stdout.Write(Directory.GetCurrentDirectory())
            stdout.WriteLine()
            stdout.Flush()
            process ()
        | "nul" ->
            idx <- idx + 1
            try
                use _ = File.OpenRead("/dev/null")
                stdout.WriteLine("nul-accessible")
            with _ ->
                stderr.WriteLine("nul-inaccessible")
            stdout.Flush()
            stderr.Flush()
            process ()
        | other ->
            idx <- idx + 1
            stdout.WriteLine(other)
            stdout.Flush()
            process ()

process ()
"""

// A simpler fixture that writes specific bytes
let private simpleFixturePath (): string =
    let path = Path.Combine(Path.GetTempPath(), $"simple-fixture-{Guid.NewGuid():N}.fsx")
    path

// Create fixture for stdout-only tests
let private createSimpleStdoutFixture (bytes: byte array) : string =
    let path = simpleFixturePath ()
    let content =
        sprintf """
stdout.Write(System.Text.Encoding.UTF8.GetString([|%s|]))
stdout.Flush()
"""
            (bytes |> Array.map (sprintf "%d") |> String.concat "; ")
    File.WriteAllText(path, content)
    path

// Create fixture for stderr-only tests
let private createSimpleStderrFixture (bytes: byte array) : string =
    let path = simpleFixturePath ()
    let content =
        sprintf """
stderr.Write(System.Text.Encoding.UTF8.GetString([|%s|]))
stderr.Flush()
"""
            (bytes |> Array.map (sprintf "%d") |> String.concat "; ")
    File.WriteAllText(path, content)
    path

// Create fixture for both stdout and stderr
let private createSimpleBothFixture (stdoutBytes: byte array) (stderrBytes: byte array) : string =
    let path = simpleFixturePath ()
    let stdoutStr = sprintf "[|%s|]" (stdoutBytes |> Array.map (sprintf "%d") |> String.concat "; ")
    let stderrStr = sprintf "[|%s|]" (stderrBytes |> Array.map (sprintf "%d") |> String.concat "; ")
    let content = sprintf """
stdout.Write(System.Text.Encoding.UTF8.GetString(%s))
stdout.Flush()
stderr.Write(System.Text.Encoding.UTF8.GetString(%s))
stderr.Flush()
""" stdoutStr stderrStr
    File.WriteAllText(path, content)
    path

// Create fixture for sleep
let private createSleepFixture (ms: int) : string =
    let path = simpleFixturePath ()
    let content = sprintf """
open System.Threading
Thread.Sleep(%d)
stdout.Write("done")
stdout.Flush()
""" ms
    File.WriteAllText(path, content)
    path

// Create fixture for exit code
let private createExitFixture (code: int) : string =
    let path = simpleFixturePath ()
    let content = sprintf """
exit %d
""" code
    File.WriteAllText(path, content)
    path

// Create fixture for echo-args
let private createEchoArgsFixture () : string =
    let path = simpleFixturePath ()
    let content = """
for arg in fsi.CommandLineArgs |> Array.skip 3 do
    stdout.Write(arg + " ")
stdout.WriteLine()
stdout.Flush()
"""
    File.WriteAllText(path, content)
    path

// Create fixture for working directory
let private createWorkingDirFixture () : string =
    let path = simpleFixturePath ()
    let content = """
stdout.Write(System.IO.Directory.GetCurrentDirectory())
stdout.Flush()
"""
    File.WriteAllText(path, content)
    path

// Helper to run bounded process with defaults
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

// -----------------------------------------------------------------------------
// Tests
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.RepairEpisodes.BoundedProcess"
        [ // 1. Successful empty-output process
          test "successful empty-output process returns Ok" {
              let fixture = createSimpleStdoutFixture [||]
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout [| |] "stdout should be empty"
                      Expect.equal success.Stderr [| |] "stderr should be empty"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 2. Stdout bytes preserved
          test "stdout bytes are preserved" {
              let expected = makeStdoutBytes 100
              let fixture = createSimpleStdoutFixture expected
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout expected "stdout bytes preserved"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 3. Stderr bytes preserved
          test "stderr bytes are preserved" {
              let expected = makeStderrBytes 100
              let fixture = createSimpleStderrFixture expected
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stderr expected "stderr bytes preserved"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 4. Embedded NUL byte preserved
          test "embedded NUL byte is preserved" {
              let expected = [| 0uy; 1uy; 0uy; 2uy; 0uy; 3uy |]
              let fixturePath = simpleFixturePath ()
              let content = sprintf """
let bytes = [| %s |]
stdout.Write(System.Text.Encoding.UTF8.GetString(bytes))
stdout.Flush()
""" (expected |> Array.map string |> String.concat "; ")
              File.WriteAllText(fixturePath, content)
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixturePath ] [] (TimeSpan.FromSeconds 5) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout expected "NUL bytes preserved"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixturePath then File.Delete fixturePath
          }

          // 5. Working directory propagated
          test "working directory is propagated" {
              let tempDir = Path.GetTempPath()
              let fixture = createWorkingDirFixture ()
              try
                  let! result = runBounded "dotnet" tempDir [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      let expectedDir = tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      let actualDir = System.Text.Encoding.UTF8.GetString(success.Stdout).TrimEnd()
                      Expect.equal actualDir expectedDir "working directory propagated"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 6. Argument containing spaces remains one argument
          test "argument containing spaces remains one argument" {
              let fixture = createEchoArgsFixture ()
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture; "hello world"; "foo" ] [] (TimeSpan.FromSeconds 5) 1024 1024
                  match result with
                  | Ok success ->
                      let output = System.Text.Encoding.UTF8.GetString(success.Stdout).Trim()
                      Expect.stringContains output "hello world" "spaces should be preserved in argument"
                      Expect.stringContains output "foo" "foo should be present"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 7. Empty argument remains present
          test "empty argument remains present" {
              let fixture = createEchoArgsFixture ()
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture; ""; "x" ] [] (TimeSpan.FromSeconds 5) 1024 1024
                  match result with
                  | Ok success ->
                      let output = System.Text.Encoding.UTF8.GetString(success.Stdout)
                      // Empty argument should produce empty string in output
                      Expect.stringContains output " x" "empty and x should be present"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 8. Quote characters remain part of the argument
          test "quote characters remain part of argument" {
              let fixture = createEchoArgsFixture ()
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture; "\"hello\""; "'world'" ] [] (TimeSpan.FromSeconds 5) 1024 1024
                  match result with
                  | Ok success ->
                      let output = System.Text.Encoding.UTF8.GetString(success.Stdout)
                      Expect.stringContains output "\"hello\"" "double quotes preserved"
                      Expect.stringContains output "'world'" "single quotes preserved"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 9. Exact stdout limit succeeds
          test "exact stdout limit succeeds" {
              let expected = makeStdoutBytes 50
              let fixture = createSimpleStdoutFixture expected
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 50 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout expected "stdout should match at exact limit"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 10. Stdout at limit plus one fails
          test "stdout at limit plus one fails with StdoutLimitExceeded" {
              let bytes = makeStdoutBytes 51
              let fixture = createSimpleStdoutFixture bytes
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 50 1024
                  match result with
                  | Error(StdoutLimitExceeded 50) -> ()
                  | Error e -> failwithf "expected StdoutLimitExceeded, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 11. Exact stderr limit succeeds
          test "exact stderr limit succeeds" {
              let expected = makeStderrBytes 50
              let fixture = createSimpleStderrFixture expected
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 1024 50
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stderr expected "stderr should match at exact limit"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 12. Stderr at limit plus one fails
          test "stderr at limit plus one fails with StderrLimitExceeded" {
              let bytes = makeStderrBytes 51
              let fixture = createSimpleStderrFixture bytes
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 1024 50
                  match result with
                  | Error(StderrLimitExceeded 50) -> ()
                  | Error e -> failwithf "expected StderrLimitExceeded, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 13. Zero stdout limit accepts zero bytes
          test "zero stdout limit accepts zero bytes" {
              let fixture = createSimpleStdoutFixture [||]
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 0 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout [| |] "stdout should be empty"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 14. Zero stdout limit rejects one byte
          test "zero stdout limit rejects one byte" {
              let bytes = [| 65uy |]
              let fixture = createSimpleStdoutFixture bytes
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5) 0 1024
                  match result with
                  | Error(StdoutLimitExceeded 0) -> ()
                  | Error e -> failwithf "expected StdoutLimitExceeded, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 15. Concurrent stdout and stderr do not deadlock
          test "concurrent stdout and stderr do not deadlock" {
              let stdout = makeStdoutBytes 100
              let stderr = makeStderrBytes 100
              let fixture = createSimpleBothFixture stdout stderr
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 10) 1024 1024
                  match result with
                  | Ok success ->
                      Expect.equal success.ExitCode 0 "exit code should be 0"
                      Expect.equal success.Stdout stdout "stdout bytes preserved"
                      Expect.equal success.Stderr stderr "stderr bytes preserved"
                  | Error e -> failwithf "expected Ok, got Error: %A" e
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 16. Nonzero exit preserves exit code and both streams
          test "nonzero exit preserves exit code and both streams" {
              let stdout = makeStdoutBytes 10
              let stderr = makeStderrBytes 10
              let fixture = createSimpleBothFixture stdout stderr
              let exitFixturePath = simpleFixturePath ()
              // Create a fixture that writes output then exits with code 42
              let content = sprintf """
stdout.Write(System.Text.Encoding.UTF8.GetString([|%s|]))
stdout.Flush()
stderr.Write(System.Text.Encoding.UTF8.GetString([|%s|]))
stderr.Flush()
exit 42
""" (stdout |> Array.map string |> String.concat "; ") (stderr |> Array.map string |> String.concat "; ")
              File.WriteAllText(exitFixturePath, content)
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; exitFixturePath ] [] (TimeSpan.FromSeconds 5) 1024 1024
                  match result with
                  | Error(NonZeroExit(42, actualStdout, actualStderr)) ->
                      Expect.equal actualStdout stdout "stdout preserved"
                      Expect.equal actualStderr stderr "stderr preserved"
                  | Error e -> failwithf "expected NonZeroExit(42, ...), got: %A" e
                  | Ok s -> failwithf "expected NonZeroExit, got Ok: %A" s
              finally
                  if File.Exists exitFixturePath then File.Delete exitFixturePath
          }

          // 17. Timeout is not success
          test "timeout returns TimedOut" {
              let fixture = createSleepFixture 5000 // 5 seconds
              try
                  let! result = runBounded "dotnet" Path.GetTempPath() [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromMilliseconds 500) 1024 1024
              match result with
              | Error(TimedOut timeout) ->
                  Expect.isTrue (timeout.TotalMilliseconds <= 1000.0) "timeout should be reasonable"
              | Error e -> failwithf "expected TimedOut, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 18. Caller cancellation is not timeout
          test "caller cancellation returns Cancelled" {
              use cts = new CancellationTokenSource()
              cts.Cancel()
              let fixture = createSleepFixture 10000 // 10 seconds
              try
                  let! result = BoundedProcess.run
                      { Executable = "dotnet"
                        WorkingDirectory = Path.GetTempPath()
                        Arguments = [ "fsi"; "--exec"; fixture ]
                        Environment = []
                        Limits = {
                            Timeout = TimeSpan.FromSeconds 30
                            StdoutLimitBytes = 1024
                            StderrLimitBytes = 1024
                        } }
                      cts.Token
                  match result with
                  | Error Cancelled -> ()
                  | Error e -> failwithf "expected Cancelled, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 19. Missing executable produces LaunchFailed
          test "missing executable produces LaunchFailed" {
              let! result = runBounded "/nonexistent/executable/path" Path.GetTempPath() [] [] (TimeSpan.FromSeconds 5) 1024 1024
              match result with
              | Error(LaunchFailed(exe, _)) ->
                  Expect.stringContains exe "nonexistent" "should mention nonexistent"
              | Error e -> failwithf "expected LaunchFailed, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 20. Missing working directory produces InvalidRequest
          test "missing working directory produces InvalidRequest" {
              let nonexistentDir = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N"))
              let! result = runBounded "dotnet" nonexistentDir [] [] (TimeSpan.FromSeconds 5) 1024 1024
              match result with
              | Error(InvalidRequest msg) ->
                  Expect.stringContains msg "working directory" "should mention working directory"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 21. Negative limit produces InvalidRequest
          test "negative stdout limit produces InvalidRequest" {
              let! result = runBounded "dotnet" Path.GetTempPath() [] [] (TimeSpan.FromSeconds 5) -1 1024
              match result with
              | Error(InvalidRequest msg) ->
                  Expect.stringContains msg "stdout" "should mention stdout"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          test "negative stderr limit produces InvalidRequest" {
              let! result = runBounded "dotnet" Path.GetTempPath() [] [] (TimeSpan.FromSeconds 5) 1024 -1
              match result with
              | Error(InvalidRequest msg) ->
                  Expect.stringContains msg "stderr" "should mention stderr"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          test "duplicate environment keys produce InvalidRequest" {
              let! result = runBounded "dotnet" Path.GetTempPath() [] [ "FOO"; "bar"; "FOO"; "baz" ] (TimeSpan.FromSeconds 5) 1024 1024
              match result with
              | Error(InvalidRequest msg) ->
                  Expect.stringContains msg "environment" "should mention environment"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }
        ]
