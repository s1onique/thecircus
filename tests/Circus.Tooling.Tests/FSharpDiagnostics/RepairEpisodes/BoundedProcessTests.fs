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
// Fixture smoke test
//
// Every generated .fsx script is run via `dotnet fsi --exec` before it is
// used as a process under test. The smoke assertion catches fixture-script
// compilation errors (e.g. FS0039 on a missing qualification) and runtime
// errors (e.g. an undefined identifier) so that BoundedProcess assertions
// are about production behaviour, not fixture defects.
//
// The smoke test only fails on:
//   - compile errors (any stderr line containing "error FS")
//   - hangs (timeout)
// The intended exit code is NOT asserted: a fixture that should exit with
// a non-zero code (e.g. `createExitFixture`) is still a valid fixture.
// -----------------------------------------------------------------------------

let private smokeTestFixture (label: string) (path: string) : unit =
    let psi = ProcessStartInfo()
    psi.FileName <- "dotnet"
    // Pass --smoke so sleep fixtures exit immediately. The flag is
    // irrelevant for non-sleep fixtures; they ignore unknown args.
    //
    // ArgumentList is preferred over the manually-escaped `Arguments`
    // string: ProcessStartInfo treats each entry as a single argv slot,
    // so embedded quotes and spaces do not need shell-level escaping.
    psi.ArgumentList.Add("fsi")
    psi.ArgumentList.Add("--exec")
    psi.ArgumentList.Add(path)
    psi.ArgumentList.Add("--smoke")
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.CreateNoWindow <- true
    use p = Process.Start(psi)
    // Drain BOTH redirected streams concurrently.  Reading only one
    // after WaitForExit is the documented deadlock pattern when the
    // child writes to a redirected pipe faster than the OS buffers free.
    let stdoutTask = p.StandardOutput.ReadToEndAsync()
    let stderrTask = p.StandardError.ReadToEndAsync()
    if not (p.WaitForExit(30000)) then
        try p.Kill() with | _ -> ()
        try stdoutTask.Wait(5000) |> ignore with | _ -> ()
        try stderrTask.Wait(5000) |> ignore with | _ -> ()
        failwithf "smoke test timed out for %s (%s)" label path
    let err =
        try stderrTask.GetAwaiter().GetResult()
        with | _ -> ""
    try stdoutTask.Wait(5000) |> ignore with | _ -> ()
    if err.Contains("error FS") then
        failwithf "smoke test detected F# compile error in %s (%s): %s" label path err

// -----------------------------------------------------------------------------
// Raw-byte test fixtures
//
// Every generated script begins with `open System` so that
// `Console.OpenStandardOutput()` / `Console.OpenStandardError()` resolve
// inside the `dotnet fsi --exec` script context, which does not
// auto-open `System`. Binary writes are retained — the raw bytes are
// piped through `Stream.Write(array, offset, count)` rather than
// converted to strings.
// -----------------------------------------------------------------------------

/// Renders a byte array as an F# byte-literal array expression. The empty
/// case produces `[||]`; otherwise each byte is rendered as `Nuy` and
/// the array is closed with `|]`.
let private renderByteLiteralArray (bytes: byte array) : string =
    if bytes.Length = 0 then
        "[||]"
    else
        let literals = String.concat "uy;" (Array.map string bytes)
        sprintf "[|%s|]" (literals + "uy")

/// Creates a fixture that writes raw bytes to stdout using binary stream
let private createRawStdoutFixture (bytes: byte array) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-raw-stdout-{Guid.NewGuid():N}.fsx")
    let literalArray = renderByteLiteralArray bytes
    let content =
        sprintf "open System\nlet bytes = %s\nlet stream = Console.OpenStandardOutput()\nstream.Write(bytes, 0, bytes.Length)\nstream.Flush()\n"
            literalArray
    File.WriteAllText(path, content)
    path

/// Creates a fixture that writes raw bytes to stderr using binary stream
let private createRawStderrFixture (bytes: byte array) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-raw-stderr-{Guid.NewGuid():N}.fsx")
    let literalArray = renderByteLiteralArray bytes
    let content =
        sprintf "open System\nlet bytes = %s\nlet stream = Console.OpenStandardError()\nstream.Write(bytes, 0, bytes.Length)\nstream.Flush()\n"
            literalArray
    File.WriteAllText(path, content)
    path

/// Creates a fixture that writes raw bytes to both stdout and stderr
let private createRawBothFixture (stdoutBytes: byte array) (stderrBytes: byte array) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-raw-both-{Guid.NewGuid():N}.fsx")
    let stdoutLiteralArray = renderByteLiteralArray stdoutBytes
    let stderrLiteralArray = renderByteLiteralArray stderrBytes
    let content =
        sprintf "open System\nlet stdoutBytes = %s\nlet stderrBytes = %s\nlet stdoutStream = Console.OpenStandardOutput()\nstdoutStream.Write(stdoutBytes, 0, stdoutBytes.Length)\nstdoutStream.Flush()\nlet stderrStream = Console.OpenStandardError()\nstderrStream.Write(stderrBytes, 0, stderrBytes.Length)\nstderrStream.Flush()\n"
            stdoutLiteralArray stderrLiteralArray
    File.WriteAllText(path, content)
    path

/// Creates a fixture that sleeps for specified milliseconds and writes
/// "done" to stdout afterwards. When the `--smoke` flag is passed (the
/// smoke test path), the script exits immediately without sleeping so the
/// BoundedProcess test alone owns the timing budget.
let private createSleepFixture (ms: int) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-sleep-{Guid.NewGuid():N}.fsx")
    let content =
        sprintf "open System\nopen System.Threading\nlet stdout = Console.OpenStandardOutput()\nlet isSmoke = fsi.CommandLineArgs |> Array.exists (fun a -> a = \"--smoke\")\nif not isSmoke then Thread.Sleep(%d)\nlet payload = System.Text.Encoding.UTF8.GetBytes(\"done\")\nstdout.Write(payload, 0, payload.Length)\nstdout.Flush()\n" ms
    File.WriteAllText(path, content)
    path

/// Creates a fixture that writes raw bytes to stdout/stderr and exits
/// with the specified code.
let private createExitFixture (code: int) (stdoutBytes: byte array) (stderrBytes: byte array) : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-exit-{Guid.NewGuid():N}.fsx")
    let stdoutLiteralArray = renderByteLiteralArray stdoutBytes
    let stderrLiteralArray = renderByteLiteralArray stderrBytes
    let content =
        sprintf "open System\nlet stdoutBytes = %s\nlet stderrBytes = %s\nlet stdoutStream = Console.OpenStandardOutput()\nstdoutStream.Write(stdoutBytes, 0, stdoutBytes.Length)\nstdoutStream.Flush()\nlet stderrStream = Console.OpenStandardError()\nstderrStream.Write(stderrBytes, 0, stderrBytes.Length)\nstderrStream.Flush()\nexit %d\n"
            stdoutLiteralArray stderrLiteralArray code
    File.WriteAllText(path, content)
    path

/// Creates a fixture that echoes its arguments to stdout. Each argument
/// is followed by a single space, then a trailing newline is written
/// using a binary `Stream.Write` so the fixture stays on the raw-byte
/// path (no `TextWriter.WriteLine`).
///
/// `fsi.CommandLineArgs` for `dotnet fsi --exec file.fsx arg1 arg2 ...`
/// returns `[script-path; arg1; arg2; ...]` in --exec mode, so the
/// script path is the first element and the user-supplied arguments
/// start at index 1.
let private createEchoArgsFixture () : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-echo-{Guid.NewGuid():N}.fsx")
    let content =
        "open System\nlet stdout = Console.OpenStandardOutput()\nfor arg in fsi.CommandLineArgs |> Array.skip 1 do\n    let payload = System.Text.Encoding.UTF8.GetBytes(arg + \" \")\n    stdout.Write(payload, 0, payload.Length)\nlet newline = System.Text.Encoding.UTF8.GetBytes(System.Environment.NewLine)\nstdout.Write(newline, 0, newline.Length)\nstdout.Flush()\n"
    File.WriteAllText(path, content)
    path

/// Creates a fixture that prints the working directory to stdout.
let private createWorkingDirFixture () : string =
    let path = Path.Combine(Path.GetTempPath(), $"fixture-workdir-{Guid.NewGuid():N}.fsx")
    let content =
        "open System\nopen System.IO\nlet stdout = Console.OpenStandardOutput()\nlet bytes = System.Text.Encoding.UTF8.GetBytes(Directory.GetCurrentDirectory())\nstdout.Write(bytes, 0, bytes.Length)\nstdout.Flush()\n"
    File.WriteAllText(path, content)
    path

// -----------------------------------------------------------------------------
// Test helpers
// -----------------------------------------------------------------------------

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
    run request CancellationToken.None

/// Helper to create a fixture and assert it compiles + runs to completion.
let private createAndSmoke (label: string) (create: unit -> string) : string =
    let path = create()
    smokeTestFixture label path
    path

/// Helper to create a fixture without smoke testing. Used for the
/// pre-cancellation test, where the test's purpose is to prove that
/// production does NOT launch the child, so smoke-running the fixture
/// would be wasteful and confounded with the test's own timing.
let private createNoSmoke (create: unit -> string) : string =
    create()

/// Helper to make expected stdout bytes
let private makeStdoutBytes (count: int) : byte array =
    Array.init count (fun i -> byte (97 + (i % 26)))  // 'a' to 'z'

/// Helper to make expected stderr bytes
let private makeStderrBytes (count: int) : byte array =
    Array.init count (fun i -> byte (65 + (i % 26)))  // 'A' to 'Z'

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
              let fixture = createAndSmoke "createRawStdoutFixture" (fun () -> createRawStdoutFixture [||])
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
              let fixture = createAndSmoke "createRawStdoutFixture" (fun () -> createRawStdoutFixture expected)
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
              let fixture = createAndSmoke "createRawStderrFixture" (fun () -> createRawStderrFixture expected)
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
              let fixture = createAndSmoke "createWorkingDirFixture" createWorkingDirFixture
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
              let fixture = createAndSmoke "createEchoArgsFixture" createEchoArgsFixture
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
              let fixture = createAndSmoke "createEchoArgsFixture" createEchoArgsFixture
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
              let fixture = createAndSmoke "createRawStdoutFixture" (fun () -> createRawStdoutFixture expected)
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
              let fixture = createAndSmoke "createRawStdoutFixture" (fun () -> createRawStdoutFixture bytes)
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 50 1024
                  match result with
                  | Error(StdoutLimitExceeded limit) when limit = 50 -> ()
                  | Error e -> failwithf "expected StdoutLimitExceeded(50), got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 9. Exact stderr limit succeeds
          testTask "exact stderr limit succeeds" {
              let expected = makeStderrBytes 50
              let fixture = createAndSmoke "createRawStderrFixture" (fun () -> createRawStderrFixture expected)
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
              let fixture = createAndSmoke "createRawStderrFixture" (fun () -> createRawStderrFixture bytes)
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 1024 50
                  match result with
                  | Error(StderrLimitExceeded limit) when limit = 50 -> ()
                  | Error e -> failwithf "expected StderrLimitExceeded(50), got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 11. Zero stdout limit with zero bytes succeeds
          testTask "zero stdout limit with zero bytes succeeds" {
              let fixture = createAndSmoke "createRawStdoutFixture" (fun () -> createRawStdoutFixture [||])
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
              let bytes = [| 0xFFuy |]  // Raw binary byte, not valid UTF-8
              let fixture = createAndSmoke "createRawStdoutFixture" (fun () -> createRawStdoutFixture bytes)
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 0 1024
                  match result with
                  | Error(StdoutLimitExceeded limit) when limit = 0 -> ()
                  | Error e -> failwithf "expected StdoutLimitExceeded(0), got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 13. Concurrent stdout and stderr
          testTask "concurrent stdout and stderr are both captured" {
              let stdout = makeStdoutBytes 100
              let stderr = makeStderrBytes 100
              let fixture = createAndSmoke "createRawBothFixture" (fun () -> createRawBothFixture stdout stderr)
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
              let fixture = createAndSmoke "createExitFixture" (fun () -> createExitFixture 42 stdout stderr)
              try
                  let! result = runBounded "dotnet" (Path.GetTempPath()) [ "fsi"; "--exec"; fixture ] [] (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Error(NonZeroExit(code, actualStdout, actualStderr)) when code = 42 ->
                      Expect.equal actualStdout stdout "stdout preserved"
                      Expect.equal actualStderr stderr "stderr preserved"
                  | Error e -> failwithf "expected NonZeroExit(42, ...), got: %A" e
                  | Ok s -> failwithf "expected NonZeroExit, got Ok: %A" s
              finally
                  if File.Exists fixture then File.Delete fixture
          }

          // 15. Timeout returns TimedOut
          testTask "timeout returns TimedOut" {
              let fixture = createAndSmoke "createSleepFixture" (fun () -> createSleepFixture 5000) // 5 seconds
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

          // 16. Pre-cancelled token returns Cancelled without starting process
          testTask "pre-cancelled token returns Cancelled" {
              let cts = new CancellationTokenSource()
              cts.Cancel()
              // Intentionally NOT smoke-tested: the test asserts that
              // production does not launch the child, so smoke-running
              // the fixture would defeat the purpose and waste 10 seconds.
              let fixture = createNoSmoke (fun () -> createSleepFixture 10000)
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
                  let! result = run req cts.Token
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

          // ---------------------------------------------------------------
          // 22-26. Five injected regressions using the LifecycleSeam.
          //
          // The seam lets the test inject OS-level states (faulted/cancelled
          // exit task, slow process that stays alive after cleanup grace,
          // kill failure) that are not reliably reachable through a real
          // child process. These complement the 21 real-process tests above.
          // ---------------------------------------------------------------

          /// Helper that drives executeLifecycleWithSeam directly with the
          /// given seam and policy. The seam owns the exit task and the
          /// stdout/stderr inputs; lifecycle plumbing is otherwise
          /// identical to the production run. The lifecycle's finalizer
          /// owns tReg, cReg, tcts, lcts, and the seam's Dispose callback.
          let runWithSeam
              (seam: LifecycleSeam)
              (stdoutTask: Task<ReadOutcome>)
              (stderrTask: Task<ReadOutcome>)
              (timeout: TimeSpan)
              (stdoutLimit: int)
              (stderrLimit: int)
              : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
              let request = {
                  Executable = "ignored"
                  WorkingDirectory = "."
                  Arguments = []
                  Environment = []
                  Limits = {
                      Timeout = timeout
                      StdoutLimitBytes = stdoutLimit
                      StderrLimitBytes = stderrLimit
                  }
              }
              let lcts = new CancellationTokenSource()
              let tcts = new CancellationTokenSource(timeout)
              let timeoutTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
              let cancelTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
              let tReg = tcts.Token.Register(fun () -> timeoutTcs.TrySetResult(true) |> ignore)
              let cReg = lcts.Token.Register(fun () -> cancelTcs.TrySetResult(true) |> ignore)
              executeLifecycleWithSeam lcts request timeoutTcs cancelTcs stdoutTask stderrTask seam tReg cReg tcts

          /// Like runWithSeam but lets the test inject the timeout and
          /// cancellation TaskCompletionSources directly so it can
          /// pre-complete them. The lifecycle's finalizer still owns
          /// tReg, cReg, tcts, lcts, and the seam's Dispose callback.
          let runWithSeamCustom
              (timeoutTcs: TaskCompletionSource<bool>)
              (cancelTcs: TaskCompletionSource<bool>)
              (seam: LifecycleSeam)
              (stdoutTask: Task<ReadOutcome>)
              (stderrTask: Task<ReadOutcome>)
              (timeout: TimeSpan)
              (stdoutLimit: int)
              (stderrLimit: int)
              : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
              let request = {
                  Executable = "ignored"
                  WorkingDirectory = "."
                  Arguments = []
                  Environment = []
                  Limits = {
                      Timeout = timeout
                      StdoutLimitBytes = stdoutLimit
                      StderrLimitBytes = stderrLimit
                  }
              }
              let lcts = new CancellationTokenSource()
              let tcts = new CancellationTokenSource(timeout)
              let tReg = tcts.Token.Register(fun () -> timeoutTcs.TrySetResult(true) |> ignore)
              let cReg = lcts.Token.Register(fun () -> cancelTcs.TrySetResult(true) |> ignore)
              executeLifecycleWithSeam lcts request timeoutTcs cancelTcs stdoutTask stderrTask seam tReg cReg tcts

          // 22. Faulted exit task -> WaitFailed with retained detail
          testTask "faulted exit task produces WaitFailed with detail" {
              let faultedExit = Task.FromException(System.Exception "synthetic exit wait fault")
              let seam = {
                  ExitTask = faultedExit
                  Kill = fun () -> Ok ()
                  HasExited = fun () -> true
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> ()
              }
              let! result =
                  runWithSeam seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromSeconds 5.0) 1024 1024
              match result with
              | Error(WaitFailed detail) ->
                  Expect.stringContains detail "synthetic exit wait fault" "should retain fault detail"
              | Error e -> failwithf "expected WaitFailed, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 23. Cancelled exit task -> WaitFailed, not caller Cancelled
          testTask "cancelled exit task produces WaitFailed (not caller Cancelled)" {
              let cts = new CancellationTokenSource()
              cts.Cancel()
              let cancelledExit = Task.FromCanceled(cts.Token)
              let seam = {
                  ExitTask = cancelledExit
                  Kill = fun () -> Ok ()
                  HasExited = fun () -> true
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> ()
              }
              let! result =
                  runWithSeam seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromSeconds 5.0) 1024 1024
              match result with
              | Error(WaitFailed _) -> ()
              | Error(Cancelled) -> failwithf "cancelled exit task leaked as caller Cancelled"
              | Error e -> failwithf "expected WaitFailed, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
              cts.Dispose()
          }

          // 24. Timer fires while both readers are at EOF but the exit
          // task is still pending. EOF alone must not impersonate process
          // exit; TimeoutFire must surface as a TerminationCleanupFailed
          // whose context proves the read streams completed cleanly.
          testTask "timer fires while streams at EOF but exit pending -> TerminationCleanupFailed { TimeoutFire; streams complete }" {
              let pendingExit = TaskCompletionSource<bool>()
              let seam = {
                  ExitTask = pendingExit.Task
                  Kill = fun () -> Ok ()
                  HasExited = fun () -> false
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> ()
              }
              let! result =
                  runWithSeam seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromMilliseconds 100.0) 1024 1024
              match result with
              | Error(
                  TerminationCleanupFailed {
                      Cause = TimeoutFire
                      TerminalFailure = None
                      ProcessExited = false
                      StdoutComplete = true
                      StderrComplete = true
                  }
                ) ->
                  ()
              | Error e -> failtest "expected TerminationCleanupFailed { TimeoutFire; streams complete }, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 25a. Reader failure with successful cleanup: kill completes
          // the exit task, HasExited returns true, ReadExitCode returns 0.
          // The stdout reader failure mode is the precise public failure.
          testTask "reader failure with successful cleanup returns typed StdoutReaderFailed" {
              let pendingExit = TaskCompletionSource<bool>()
              let failedStdout = Task.FromResult(ReadFailed "synthetic reader pipe closed")
              let seam = {
                  ExitTask = pendingExit.Task
                  Kill = fun () -> pendingExit.TrySetResult(true) |> ignore; Ok ()
                  HasExited = fun () -> true
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> ()
              }
              let! result =
                  runWithSeam seam
                      failedStdout
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromMilliseconds 100.0) 1024 1024
              match result with
              | Error(StdoutReaderFailed detail) ->
                  Expect.equal detail "synthetic reader pipe closed" "should retain reader detail"
              | Error e -> failwithf "expected StdoutReaderFailed, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 25b. Reader failure with unsuccessful cleanup: kill returns
          // Ok but does NOT complete the exit task, and HasExited keeps
          // reporting false. The classifier must surface the captured
          // cause and TerminalFailure rather than the reader error.
          testTask "reader failure with unsuccessful cleanup returns TerminationCleanupFailed { StdoutTerminal }" {
              let pendingExit = TaskCompletionSource<bool>()
              let failedStdout = Task.FromResult(ReadFailed "synthetic reader pipe closed")
              let seam = {
                  ExitTask = pendingExit.Task
                  Kill = fun () -> Ok ()
                  HasExited = fun () -> false
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> ()
              }
              let! result =
                  runWithSeam seam
                      failedStdout
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromMilliseconds 100.0) 1024 1024
              match result with
              | Error(
                  TerminationCleanupFailed {
                      Cause = StdoutTerminal
                      TerminalFailure = Some(StdoutReadFailure "synthetic reader pipe closed")
                      ProcessExited = false
                      StdoutComplete = true
                      StderrComplete = true
                  }
                ) ->
                  ()
              | Error e -> failtest "expected TerminationCleanupFailed { StdoutTerminal }, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 26. Stdout EOF + stderr EOF + sleeping child -> TimedOut.
          // The kill callback is allowed to complete the exit task so the
          // some-child-alive-after-grace risk is bounded. The contract is
          // only that EOF alone is nonterminal: TimedOut is the public
          // error, not a successful Exit 0.
          testTask "stdout EOF followed by sleeping child remains timeout-responsive" {
              let pendingExit = TaskCompletionSource<bool>()
              let seam = {
                  ExitTask = pendingExit.Task
                  Kill = fun () -> pendingExit.TrySetResult(true) |> ignore; Ok ()
                  HasExited = fun () -> false
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> ()
              }
              let! result =
                  runWithSeam seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromMilliseconds 300.0) 1024 1024
              match result with
              | Error(TimedOut _) -> ()
              | Error e -> failwithf "expected TimedOut, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // ---------------------------------------------------------------
          // 27-28. P3 authority-race regressions.
          //
          // The timeout and caller-cancellation participants must remain
          // the public cause authority even when a reader cancellation
          // is already complete by the time the loop inspects the
          // pending tasks. Running each race repeatedly proves the
          // classification is deterministic regardless of which task
          // Task.WhenAny returns first.
          // ---------------------------------------------------------------

          // 27. Timeout participant already complete + stdout cancelled
          // -> TimedOut is the public cause; reader cancellation is
          // ignored as a cause. Repeated 100x to defeat ordering races.
          testTask "timeout participant and stdout already-cancelled race -> TimedOut (100 iterations)" {
              for _ in 1..100 do
                  let timeoutTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                  timeoutTcs.SetResult(true)
                  let cancelTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                  let pendingExit = TaskCompletionSource<bool>()
                  let seam = {
                      ExitTask = pendingExit.Task
                      Kill = fun () -> pendingExit.TrySetResult(true) |> ignore; Ok ()
                      HasExited = fun () -> true
                      ReadExitCode = fun () -> 0
                      Dispose = fun () -> ()
                  }
                  let! result =
                      runWithSeamCustom
                          timeoutTcs
                          cancelTcs
                          seam
                          (Task.FromResult(ReadCancelled))
                          (Task.FromResult(EofReached [||]))
                          (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Error(TimedOut _) -> ()
                  | Error(IncompleteOutput (_, _)) -> failwithf "expected TimedOut, got IncompleteOutput"
                  | Error(TerminationCleanupFailed { Cause = StdoutTerminal }) -> failwithf "expected TimedOut, got TerminationCleanupFailed StdoutTerminal"
                  | Error(StdoutReaderFailed _) -> failwithf "expected TimedOut, got StdoutReaderFailed"
                  | Error Cancelled -> failwithf "expected TimedOut, got Cancelled"
                  | Error e -> failwithf "expected TimedOut, got: %A" e
                  | Ok s -> failwithf "expected TimedOut, got Ok: %A" s
          }

          // 28. Caller-cancellation participant already complete + stderr
          // cancelled -> Cancelled is the public cause; reader
          // cancellation is ignored as a cause. Repeated 100x to defeat
          // ordering races.
          testTask "caller-cancellation participant and stderr already-cancelled race -> Cancelled (100 iterations)" {
              for _ in 1..100 do
                  let cancelTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                  cancelTcs.SetResult(true)
                  let timeoutTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                  let pendingExit = TaskCompletionSource<bool>()
                  let seam = {
                      ExitTask = pendingExit.Task
                      Kill = fun () -> pendingExit.TrySetResult(true) |> ignore; Ok ()
                      HasExited = fun () -> true
                      ReadExitCode = fun () -> 0
                      Dispose = fun () -> ()
                  }
                  let! result =
                      runWithSeamCustom
                          timeoutTcs
                          cancelTcs
                          seam
                          (Task.FromResult(EofReached [||]))
                          (Task.FromResult(ReadCancelled))
                          (TimeSpan.FromSeconds 5.0) 1024 1024
                  match result with
                  | Error Cancelled -> ()
                  | Error(TimedOut _) -> failwithf "expected Cancelled, got TimedOut"
                  | Error(TerminationCleanupFailed { Cause = StderrTerminal }) -> failwithf "expected Cancelled, got TerminationCleanupFailed StderrTerminal"
                  | Error(StderrReaderFailed _) -> failwithf "expected Cancelled, got StderrReaderFailed"
                  | Error(IncompleteOutput (_, _)) -> failwithf "expected Cancelled, got IncompleteOutput"
                  | Error e -> failwithf "expected Cancelled, got: %A" e
                  | Ok s -> failwithf "expected Cancelled, got Ok: %A" s
          }

          // ---------------------------------------------------------------
          // 29-32. P3 resource-ownership regressions.
          //
          // One finalizer owns all five resources (tReg, cReg, lcts,
          // tcts, seam.Dispose). The test seam's Dispose increments a
          // counter so the test can observe when exactly-once disposal
          // actually fires.
          // ---------------------------------------------------------------

          // 29. Completing one of the three operations must NOT dispose.
          // The finalizer must wait for all three.
          testTask "first completion must not dispose; finalizer waits for all three operations" {
              let mutable disposeCount = 0
              let pendingExit = TaskCompletionSource<bool>()
              let pendingStderr = TaskCompletionSource<ReadOutcome>()
              let okStdout = Task.FromResult(EofReached [||])
              let seam = {
                  ExitTask = pendingExit.Task
                  Kill = fun () -> Ok ()
                  HasExited = fun () -> false
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> disposeCount <- disposeCount + 1
              }
              let lifecycle =
                  runWithSeam seam
                      okStdout
                      pendingStderr.Task
                      (TimeSpan.FromSeconds 30.0) 1024 1024
              // Yield long enough for the loop to consume okStdout
              do! Task.Delay(200)
              Expect.equal disposeCount 0 "stdoutTask completion alone must not dispose"
              // Complete stderrTask only
              pendingStderr.SetResult(EofReached [||]) |> ignore
              do! Task.Delay(200)
              Expect.equal disposeCount 0 "stderrTask completion alone must not dispose"
              // Complete exitTask; the lifecycle and the finalizer both
              // reach their all-done state.
              pendingExit.SetResult(true) |> ignore
              let! _ = lifecycle
              do! Task.Delay(200)
              Expect.equal disposeCount 1 "all tasks completing must dispose exactly once"
          }

          // 30. Simultaneous completion of all three operations must
          // still trigger exactly-once disposal.
          testTask "simultaneous completion of all three tasks disposes exactly once" {
              let mutable disposeCount = 0
              let pendingExit = TaskCompletionSource<bool>()
              let pendingStdout = TaskCompletionSource<ReadOutcome>()
              let pendingStderr = TaskCompletionSource<ReadOutcome>()
              let seam = {
                  ExitTask = pendingExit.Task
                  Kill = fun () -> Ok ()
                  HasExited = fun () -> false
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> disposeCount <- disposeCount + 1
              }
              let lifecycle =
                  runWithSeam seam
                      pendingStdout.Task
                      pendingStderr.Task
                      (TimeSpan.FromSeconds 30.0) 1024 1024
              do! Task.Delay(100)
              Expect.equal disposeCount 0 "no disposal before any task completes"
              // Complete all three simultaneously
              pendingStdout.SetResult(EofReached [||]) |> ignore
              pendingStderr.SetResult(EofReached [||]) |> ignore
              pendingExit.SetResult(true) |> ignore
              let! _ = lifecycle
              do! Task.Delay(200)
              Expect.equal disposeCount 1 "simultaneous completion still disposes exactly once"
          }

          // 31. Cleanup-failure returned from the lifecycle must NOT
          // have disposed yet. The finalizer remains pending until the
          // outstanding tasks settle.
          testTask "cleanup-failure defers disposal until outstanding tasks settle" {
              let mutable disposeCount = 0
              let pendingExit = TaskCompletionSource<bool>()
              let pendingStderr = TaskCompletionSource<ReadOutcome>()
              let okStdout = Task.FromResult(EofReached [||])
              let seam = {
                  ExitTask = pendingExit.Task
                  Kill = fun () -> Ok ()
                  HasExited = fun () -> false
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> disposeCount <- disposeCount + 1
              }
              let lifecycle =
                  runWithSeam seam
                      okStdout
                      pendingStderr.Task
                      (TimeSpan.FromMilliseconds 100.0) 1024 1024
              let! result = lifecycle
              match result with
              | Error(TerminationCleanupFailed _) -> ()
              | Error e -> failtest "expected TerminationCleanupFailed, got: %A" e
              | Ok s -> failtest "expected TerminationCleanupFailed, got Ok: %A" s
              Expect.equal disposeCount 0 "no disposal when exitTask and stderrTask still pending"
              // Complete the outstanding tasks and the finalizer fires.
              pendingExit.SetResult(true) |> ignore
              pendingStderr.SetResult(EofReached [||]) |> ignore
              do! Task.Delay(300)
              Expect.equal disposeCount 1 "finalizer disposes exactly once after outstanding tasks settle"
          }

          // 32. Faulted and cancelled tasks still finalize. The lifecycle
          // observes aggregate faults (no throw escapes) and the seam's
          // Dispose is invoked exactly once.
          testTask "faulted and cancelled tasks still finalize exactly once" {
              let mutable disposeCount = 0
              let cts = new CancellationTokenSource()
              cts.Cancel()
              let okStdout = Task.FromResult(EofReached [||])
              let faultedStderr = Task.FromException<ReadOutcome>(System.Exception "synthetic reader fault")
              let cancelledExit = Task.FromCanceled(cts.Token)
              let seam = {
                  ExitTask = cancelledExit
                  Kill = fun () -> Ok ()
                  HasExited = fun () -> true
                  ReadExitCode = fun () -> 0
                  Dispose = fun () -> disposeCount <- disposeCount + 1
              }
              let! result =
                  runWithSeam seam
                      okStdout
                      faultedStderr
                      (TimeSpan.FromSeconds 5.0) 1024 1024
              do! Task.Delay(200)
              // The public lifecycle reached a typed Result without
              // throwing despite one faulted and one cancelled task.
              Expect.isNotNull (box result) "lifecycle must produce a typed Result"
              Expect.equal disposeCount 1 "finalizer must dispose exactly once after a faulted and a cancelled task"
              cts.Dispose()
          }
        ]
    // Intrinsic sequencing: the canonical gate is deterministic without
    // requiring an operator `--sequenced` flag, because the fixtures
    // spawn heavyweight `dotnet fsi --exec` children that contend when
    // run in parallel.
    |> (fun t -> Test.Sequenced (SequenceMethod.Synchronous, t))
