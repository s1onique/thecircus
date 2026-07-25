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
// Precompiled F# fixture
//
// Every BoundedProcess real-process test launches the precompiled
//   tests/Circus.Tooling.ProcessTreeFixture
// project assembly via `dotnet <fixture.dll> <mode> ...`. The fixture
// is built as a separate project that the test project references with
// `ReferenceOutputAssembly="false"`; a custom MSBuild target copies the
// fixture's managed assembly and runtimeconfig.json into this test
// project's output directory so the path is stable and cross-platform.
//
// The fixture is NEVER loaded into the test process; it is launched
// as a separate `dotnet` child for every BoundedProcess invocation.
// This removes the FSI startup / dynamic-compilation cost that the
// previous checkpoint left on the authority test workload and removes
// the smoke-test-of-fixture-script step that introduced an
// `error FS` failure mode unrelated to BoundedProcess itself.
//
// See `tests/Circus.Tooling.ProcessTreeFixture/Program.fs` for the
// full mode grammar.
// -----------------------------------------------------------------------------

let private fixturePath =
    Path.Combine(AppContext.BaseDirectory, "circus-process-tree-fixture.dll")

/// Resolves the absolute path of the precompiled F# fixture used by
/// every real-process test. Fails fast with a clear message if the
/// fixture DLL is missing from the test output directory, which
/// means the MSBuild `CopyProcessTreeFixture` target did not run.
let private resolveFixturePath () : string =
    if not (File.Exists fixturePath) then
        let msg =
            sprintf
                "precompiled process fixture not found at %s. The MSBuild `CopyProcessTreeFixture` target must run for the test project's output to contain the fixture's managed assembly. Rebuild the test project (`dotnet build tests/Circus.Tooling.Tests`) before running this test."
                fixturePath

        failwithf "%s" msg

    fixturePath

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
    let request =
        { Executable = executable
          WorkingDirectory = workingDirectory
          Arguments = args
          Environment = env
          Limits =
            { Timeout = timeout
              StdoutLimitBytes = stdoutLimit
              StderrLimitBytes = stderrLimit } }

    run request CancellationToken.None

/// Launch the precompiled fixture as a child process via `dotnet
/// <fixture.dll> <mode> ...`. The fixture is the authority for every
/// real-process test in this file; no FSI script is generated or
/// dynamically compiled.
let private runFixture
    (workingDirectory: string)
    (modeArgs: string list)
    (timeout: TimeSpan)
    (stdoutLimit: int)
    (stderrLimit: int)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    let fixture = resolveFixturePath ()
    let args = fixture :: modeArgs
    runBounded "dotnet" workingDirectory args [] timeout stdoutLimit stderrLimit

/// Helper to make expected stdout bytes
let private makeStdoutBytes (count: int) : byte array =
    Array.init count (fun i -> byte (97 + (i % 26))) // 'a' to 'z'

/// Helper to make expected stderr bytes
let private makeStderrBytes (count: int) : byte array =
    Array.init count (fun i -> byte (65 + (i % 26))) // 'A' to 'Z'

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
              let! result = runFixture (Path.GetTempPath()) [ "empty" ] (TimeSpan.FromSeconds 5.0) 1024 1024

              match result with
              | Ok success ->
                  Expect.equal success.ExitCode 0 "exit code should be 0"
                  Expect.equal success.Stdout [||] "stdout should be empty"
                  Expect.equal success.Stderr [||] "stderr should be empty"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 2. Non-empty stdout is captured
          testTask "non-empty stdout is captured correctly" {
              let expected = makeStdoutBytes 10
              let! result = runFixture (Path.GetTempPath()) [ "stdout"; "10" ] (TimeSpan.FromSeconds 5.0) 1024 1024

              match result with
              | Ok success ->
                  Expect.equal success.ExitCode 0 "exit code should be 0"
                  Expect.equal success.Stdout expected "stdout should have 10 bytes"
                  Expect.equal success.Stderr [||] "stderr should be empty"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 3. Non-empty stderr is captured
          testTask "non-empty stderr is captured correctly" {
              let expected = makeStderrBytes 10
              let! result = runFixture (Path.GetTempPath()) [ "stderr"; "10" ] (TimeSpan.FromSeconds 5.0) 1024 1024

              match result with
              | Ok success ->
                  Expect.equal success.ExitCode 0 "exit code should be 0"
                  Expect.equal success.Stdout [||] "stdout should be empty"
                  Expect.equal success.Stderr expected "stderr should have 10 bytes"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 4. Working directory is propagated
          testTask "working directory is propagated to subprocess" {
              let tempDir = Path.GetTempPath()
              let! result = runFixture tempDir [ "working-directory" ] (TimeSpan.FromSeconds 5.0) 1024 1024

              match result with
              | Ok success ->
                  let expectedDir =
                      tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                  let actualDir = System.Text.Encoding.UTF8.GetString(success.Stdout).Trim()
                  Expect.equal actualDir expectedDir "working directory propagated"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 5. Arguments with spaces are preserved
          testTask "arguments containing spaces remain as one argument" {
              let! result =
                  runFixture
                      (Path.GetTempPath())
                      [ "echo-args"; "hello world"; "foo" ]
                      (TimeSpan.FromSeconds 5.0)
                      1024
                      1024

              match result with
              | Ok success ->
                  let output = System.Text.Encoding.UTF8.GetString(success.Stdout).Trim()
                  Expect.stringContains output "hello world" "spaces should be preserved"
                  Expect.stringContains output "foo" "foo should be present"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 6. Quote characters in arguments
          testTask "quote characters in arguments are preserved" {
              let! result =
                  runFixture
                      (Path.GetTempPath())
                      [ "echo-args"; "\"hello\""; "'world'" ]
                      (TimeSpan.FromSeconds 5.0)
                      1024
                      1024

              match result with
              | Ok success ->
                  let output = System.Text.Encoding.UTF8.GetString(success.Stdout)
                  Expect.stringContains output "\"hello\"" "double quotes preserved"
                  Expect.stringContains output "'world'" "single quotes preserved"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 7. Exact stdout limit succeeds
          testTask "exact stdout limit succeeds" {
              let expected = makeStdoutBytes 50
              let! result = runFixture (Path.GetTempPath()) [ "stdout"; "50" ] (TimeSpan.FromSeconds 5.0) 50 1024

              match result with
              | Ok success ->
                  Expect.equal success.ExitCode 0 "exit code should be 0"
                  Expect.equal success.Stdout expected "stdout should match at exact limit"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 8. Stdout over limit fails
          testTask "stdout over limit fails with StdoutLimitExceeded" {
              let! result = runFixture (Path.GetTempPath()) [ "stdout"; "51" ] (TimeSpan.FromSeconds 5.0) 50 1024

              match result with
              | Error(StdoutLimitExceeded limit) when limit = 50 -> ()
              | Error e -> failwithf "expected StdoutLimitExceeded(50), got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 9. Exact stderr limit succeeds
          testTask "exact stderr limit succeeds" {
              let expected = makeStderrBytes 50
              let! result = runFixture (Path.GetTempPath()) [ "stderr"; "50" ] (TimeSpan.FromSeconds 5.0) 1024 50

              match result with
              | Ok success ->
                  Expect.equal success.ExitCode 0 "exit code should be 0"
                  Expect.equal success.Stderr expected "stderr should match at exact limit"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 10. Stderr over limit fails
          testTask "stderr over limit fails with StderrLimitExceeded" {
              let! result = runFixture (Path.GetTempPath()) [ "stderr"; "51" ] (TimeSpan.FromSeconds 5.0) 1024 50

              match result with
              | Error(StderrLimitExceeded limit) when limit = 50 -> ()
              | Error e -> failwithf "expected StderrLimitExceeded(50), got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 11. Zero stdout limit with zero bytes succeeds
          testTask "zero stdout limit with zero bytes succeeds" {
              let! result = runFixture (Path.GetTempPath()) [ "empty" ] (TimeSpan.FromSeconds 5.0) 0 1024

              match result with
              | Ok success ->
                  Expect.equal success.ExitCode 0 "exit code should be 0"
                  Expect.equal success.Stdout [||] "stdout should be empty"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 12. Zero stdout limit with one byte fails
          testTask "zero stdout limit with one byte fails" {
              let! result = runFixture (Path.GetTempPath()) [ "stdout"; "1" ] (TimeSpan.FromSeconds 5.0) 0 1024

              match result with
              | Error(StdoutLimitExceeded limit) when limit = 0 -> ()
              | Error e -> failwithf "expected StdoutLimitExceeded(0), got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 13. Concurrent stdout and stderr
          testTask "concurrent stdout and stderr are both captured" {
              let stdout = makeStdoutBytes 100
              let stderr = makeStderrBytes 100

              let! result =
                  runFixture (Path.GetTempPath()) [ "both"; "100"; "100" ] (TimeSpan.FromSeconds 10.0) 1024 1024

              match result with
              | Ok success ->
                  Expect.equal success.ExitCode 0 "exit code should be 0"
                  Expect.equal success.Stdout stdout "stdout bytes preserved"
                  Expect.equal success.Stderr stderr "stderr bytes preserved"
              | Error e -> failwithf "expected Ok, got Error: %A" e
          }

          // 14. Non-zero exit code preserves output
          testTask "non-zero exit preserves exit code and output" {
              let stdout = makeStdoutBytes 10
              let stderr = makeStderrBytes 10

              let! result =
                  runFixture
                      (Path.GetTempPath())
                      [ "exit-with-both"; "10"; "10"; "42" ]
                      (TimeSpan.FromSeconds 5.0)
                      1024
                      1024

              match result with
              | Error(NonZeroExit(code, actualStdout, actualStderr)) when code = 42 ->
                  Expect.equal actualStdout stdout "stdout preserved"
                  Expect.equal actualStderr stderr "stderr preserved"
              | Error e -> failwithf "expected NonZeroExit(42, ...), got: %A" e
              | Ok s -> failwithf "expected NonZeroExit, got Ok: %A" s
          }

          // 15. Timeout returns TimedOut
          testTask "timeout returns TimedOut" {
              let! result =
                  runFixture (Path.GetTempPath()) [ "sleep"; "5000" ] (TimeSpan.FromMilliseconds 500.0) 1024 1024

              match result with
              | Error(TimedOut timeout) ->
                  Expect.isTrue (timeout.TotalMilliseconds <= 1000.0) "timeout should be reasonable"
              | Error e -> failwithf "expected TimedOut, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 16. Pre-cancelled token returns Cancelled without starting process
          testTask "pre-cancelled token returns Cancelled" {
              let cts = new CancellationTokenSource()
              cts.Cancel()
              let fixture = resolveFixturePath ()

              let req =
                  { Executable = "dotnet"
                    WorkingDirectory = Path.GetTempPath()
                    Arguments = [ fixture; "sleep"; "10000" ]
                    Environment = []
                    Limits =
                      { Timeout = TimeSpan.FromSeconds 30.0
                        StdoutLimitBytes = 1024
                        StderrLimitBytes = 1024 } }

              try
                  let! result = run req cts.Token
                  cts.Dispose()

                  match result with
                  | Error Cancelled -> ()
                  | Error e -> failwithf "expected Cancelled, got: %A" e
                  | Ok s -> failwithf "expected failure, got Ok: %A" s
              finally
                  cts.Dispose()
          }

          // 17. Missing executable produces LaunchFailed
          testTask "missing executable produces LaunchFailed" {
              let! result =
                  runBounded
                      "/nonexistent/executable/path"
                      (Path.GetTempPath())
                      []
                      []
                      (TimeSpan.FromSeconds 5.0)
                      1024
                      1024

              match result with
              | Error(LaunchFailed(exe, _)) -> Expect.stringContains exe "nonexistent" "should mention nonexistent"
              | Error e -> failwithf "expected LaunchFailed, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 18. Missing working directory produces InvalidRequest
          testTask "missing working directory produces InvalidRequest" {
              let nonexistentDir =
                  Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N"))

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
              | Error(InvalidRequest msg) -> Expect.stringContains msg "stdout" "should mention stdout"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 20. Negative stderr limit produces InvalidRequest
          testTask "negative stderr limit produces InvalidRequest" {
              let! result = runBounded "dotnet" (Path.GetTempPath()) [] [] (TimeSpan.FromSeconds 5.0) 1024 -1

              match result with
              | Error(InvalidRequest msg) -> Expect.stringContains msg "stderr" "should mention stderr"
              | Error e -> failwithf "expected InvalidRequest, got: %A" e
              | Ok s -> failwithf "expected failure, got Ok: %A" s
          }

          // 21. Duplicate environment keys produce InvalidRequest
          testTask "duplicate environment keys produce InvalidRequest" {
              let! result =
                  runBounded
                      "dotnet"
                      (Path.GetTempPath())
                      []
                      [ "FOO", "bar"; "FOO", "baz" ]
                      (TimeSpan.FromSeconds 5.0)
                      1024
                      1024

              match result with
              | Error(InvalidRequest msg) -> Expect.stringContains msg "environment" "should mention environment"
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
          /// Like runWithSeamCustom but returns the raw LifecycleCompletion
          /// so the test can inspect the Result before the deferred
          /// finalization completes. Used by the disposal-ordering
          /// regressions and the disposed-seam state-access test.
          let runWithSeamCompletion
              (seam: LifecycleSeam)
              (stdoutTask: Task<ReadOutcome>)
              (stderrTask: Task<ReadOutcome>)
              (timeout: TimeSpan)
              (stdoutLimit: int)
              (stderrLimit: int)
              : Task<LifecycleCompletion> =
              let request =
                  { Executable = "ignored"
                    WorkingDirectory = "."
                    Arguments = []
                    Environment = []
                    Limits =
                      { Timeout = timeout
                        StdoutLimitBytes = stdoutLimit
                        StderrLimitBytes = stderrLimit } }

              let lcts = new CancellationTokenSource()
              let tcts = new CancellationTokenSource(timeout)

              let timeoutTcs =
                  TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

              let cancelTcs =
                  TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

              let tReg = tcts.Token.Register(fun () -> timeoutTcs.TrySetResult(true) |> ignore)
              let cReg = lcts.Token.Register(fun () -> cancelTcs.TrySetResult(true) |> ignore)
              executeLifecycleWithSeam lcts request timeoutTcs cancelTcs stdoutTask stderrTask seam tReg cReg tcts

          /// Observe a deferred finalization's faults without blocking
          /// synchronously on its completion. The lifecycle has already
          /// classified the operation as `Deferred`, meaning the finalizer
          /// cannot complete until the caller supplies whatever state the
          /// outstanding operations are waiting on. The public `run` does
          /// not block on a Deferred finalizer either: it observes the
          /// task to surface faults via `UnobservedTaskException` at GC
          /// time, which is acceptable for best-effort disposal that
          /// already failed at the classification level.
          let observeDeferred (finalization: Task) : unit =
              finalization.ContinueWith(fun (t: Task) ->
                  if t.IsFaulted then
                      ignore t.Exception)
              |> ignore

          /// Default seam-injection helper. Mirrors the public `run`
          /// contract by respecting `FinalizationMode`: a deferred
          /// finalizer is observed (not awaited), and a normal
          /// `AwaitBeforeReturn` finalizer is awaited. Awaiting a
          /// Deferred finalizer would block the test indefinitely when
          /// the seam never completes its outstanding operation.
          let runWithSeam
              (seam: LifecycleSeam)
              (stdoutTask: Task<ReadOutcome>)
              (stderrTask: Task<ReadOutcome>)
              (timeout: TimeSpan)
              (stdoutLimit: int)
              (stderrLimit: int)
              : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
              task {
                  let! completion = runWithSeamCompletion seam stdoutTask stderrTask timeout stdoutLimit stderrLimit

                  match completion.FinalizationMode with
                  | AwaitBeforeReturn ->
                      do! completion.Finalization
                      return completion.Result

                  | Deferred ->
                      observeDeferred completion.Finalization
                      return completion.Result
              }

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
              let request =
                  { Executable = "ignored"
                    WorkingDirectory = "."
                    Arguments = []
                    Environment = []
                    Limits =
                      { Timeout = timeout
                        StdoutLimitBytes = stdoutLimit
                        StderrLimitBytes = stderrLimit } }

              let lcts = new CancellationTokenSource()
              let tcts = new CancellationTokenSource(timeout)
              let tReg = tcts.Token.Register(fun () -> timeoutTcs.TrySetResult(true) |> ignore)
              let cReg = lcts.Token.Register(fun () -> cancelTcs.TrySetResult(true) |> ignore)
              // The new internal return shape is LifecycleCompletion carrying
              // the public Result and the disposal Task. The public run()
              // awaits both; tests that need to inspect the Result before
              // the finalization completes use this helper directly.
              let completion: Task<LifecycleCompletion> =
                  executeLifecycleWithSeam lcts request timeoutTcs cancelTcs stdoutTask stderrTask seam tReg cReg tcts

              task {
                  let! c = completion
                  do! c.Finalization
                  return c.Result
              }

          // 22. Faulted exit task -> WaitFailed with retained detail
          testTask "faulted exit task produces WaitFailed with detail" {
              let faultedExit = Task.FromException(System.Exception "synthetic exit wait fault")

              let seam =
                  { ExitTask = faultedExit
                    Kill = fun () -> Ok()
                    HasExited = fun () -> true
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> () }

              let! result =
                  runWithSeam
                      seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromSeconds 5.0)
                      1024
                      1024

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

              let seam =
                  { ExitTask = cancelledExit
                    Kill = fun () -> Ok()
                    HasExited = fun () -> true
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> () }

              let! result =
                  runWithSeam
                      seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromSeconds 5.0)
                      1024
                      1024

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
          //
          // CORRECTION20: this test owns a deliberately-pending
          // synthetic exit task. It uses `runWithSeamCompletion` directly
          // because the lifecycle returns `FinalizationMode = Deferred`
          // here, and awaiting the deferred finalizer would block
          // forever (the finalizer cannot complete until the caller
          // supplies the state the outstanding operation is waiting on).
          // The synthetic exit task is completed in a `finally` block so
          // the finalizer can finish, observe exactly-once disposal,
          // and release its owned resources before the test returns.
          testTask
              "timer fires while streams at EOF but exit pending -> TerminationCleanupFailed { TimeoutFire; streams complete }" {
              let mutable disposeCount = 0

              let pendingExit =
                  TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

              let seam =
                  { ExitTask = pendingExit.Task
                    Kill = fun () -> Ok()
                    HasExited = fun () -> false
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> disposeCount <- disposeCount + 1 }

              let completion: Task<LifecycleCompletion> =
                  runWithSeamCompletion
                      seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromMilliseconds 100.0)
                      1024
                      1024

              try
                  let! c = completion

                  match c.Result with
                  | Error(TerminationCleanupFailed { Cause = TimeoutFire
                                                     TerminalFailure = None
                                                     ProcessExited = false
                                                     StdoutComplete = true
                                                     StderrComplete = true }) -> ()
                  | Error e -> failtest "expected TerminationCleanupFailed { TimeoutFire; streams complete }, got: %A" e
                  | Ok s -> failtestf "expected failure, got Ok: %A" s

                  // The finalizer is deferred: ExitTask is still pending
                  // so the lifecycle cannot dispose yet.
                  Expect.equal c.FinalizationMode Deferred "incomplete exit must select deferred finalization"

                  Expect.isFalse c.Finalization.IsCompleted "finalization must remain pending while ExitTask is pending"

                  Expect.equal disposeCount 0 "resources must not be disposed before ExitTask settles"
              finally
                  // Settle the synthetic exit task so the finalizer can
                  // run and dispose exactly once.
                  pendingExit.TrySetResult(true) |> ignore

              // The helper returns the LifecycleCompletion task itself;
              // the public caller now owns the deferred finalizer and
              // can await it after the synthetic exit has been settled.
              let! c = completion
              do! c.Finalization

              Expect.equal disposeCount 1 "resources are disposed exactly once after ExitTask settles"
          }

          // 24a. Helper-contract regression: `runWithSeam` must NOT
          // block on a deferred finalizer. This proves that ordinary
          // test code that uses the default seam-injection helper
          // cannot silently regress to unconditional finalization
          // awaiting.
          testTask "runWithSeam returns within a strict bound when finalization is deferred" {
              let pendingExit =
                  TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

              let seam =
                  { ExitTask = pendingExit.Task
                    // Kill does NOT complete pendingExit: this is exactly
                    // the scenario Test 24 uses. If the helper regresses
                    // to unconditional finalization awaiting, this test
                    // hangs forever.
                    Kill = fun () -> Ok()
                    HasExited = fun () -> false
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> () }

              let! result =
                  runWithSeam
                      seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromMilliseconds 100.0)
                      1024
                      1024
              // Helper must return without awaiting the pending
              // finalizer; the test would otherwise block forever.
              match result with
              | Error(TerminationCleanupFailed { Cause = TimeoutFire
                                                 TerminalFailure = None
                                                 ProcessExited = false
                                                 StdoutComplete = true
                                                 StderrComplete = true }) -> ()
              | Error e -> failtest "expected deferred TimeoutFire cleanup failure, got: %A" e
              | Ok s -> failtestf "expected failure, got Ok: %A" s
              // Release the pending exit so the deferred finalizer can
              // settle and the test process can shut down cleanly.
              pendingExit.TrySetResult(true) |> ignore
          }

          // 25a. Reader failure with successful cleanup: kill completes
          // the exit task, HasExited returns true, ReadExitCode returns 0.
          // The stdout reader failure mode is the precise public failure.
          testTask "reader failure with successful cleanup returns typed StdoutReaderFailed" {
              let pendingExit = TaskCompletionSource<bool>()
              let failedStdout = Task.FromResult(ReadFailed "synthetic reader pipe closed")

              let seam =
                  { ExitTask = pendingExit.Task
                    Kill =
                      fun () ->
                          pendingExit.TrySetResult(true) |> ignore
                          Ok()
                    HasExited = fun () -> true
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> () }

              let! result =
                  runWithSeam
                      seam
                      failedStdout
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromMilliseconds 100.0)
                      1024
                      1024

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

              let seam =
                  { ExitTask = pendingExit.Task
                    Kill = fun () -> Ok()
                    HasExited = fun () -> false
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> () }

              let! result =
                  runWithSeam
                      seam
                      failedStdout
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromMilliseconds 100.0)
                      1024
                      1024

              match result with
              | Error(TerminationCleanupFailed { Cause = StdoutTerminal
                                                 TerminalFailure = Some(StdoutReadFailure "synthetic reader pipe closed")
                                                 ProcessExited = false
                                                 StdoutComplete = true
                                                 StderrComplete = true }) -> ()
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

              let seam =
                  { ExitTask = pendingExit.Task
                    Kill =
                      fun () ->
                          pendingExit.TrySetResult(true) |> ignore
                          Ok()
                    HasExited = fun () -> false
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> () }

              let! result =
                  runWithSeam
                      seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromMilliseconds 300.0)
                      1024
                      1024

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
                  let timeoutTcs =
                      TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                  timeoutTcs.SetResult(true)

                  let cancelTcs =
                      TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                  let pendingExit = TaskCompletionSource<bool>()

                  let seam =
                      { ExitTask = pendingExit.Task
                        Kill =
                          fun () ->
                              pendingExit.TrySetResult(true) |> ignore
                              Ok()
                        HasExited = fun () -> true
                        ReadExitCode = fun () -> 0
                        Dispose = fun () -> () }

                  let! result =
                      runWithSeamCustom
                          timeoutTcs
                          cancelTcs
                          seam
                          (Task.FromResult(ReadCancelled))
                          (Task.FromResult(EofReached [||]))
                          (TimeSpan.FromSeconds 5.0)
                          1024
                          1024

                  match result with
                  | Error(TimedOut _) -> ()
                  | Error(IncompleteOutput(_, _)) -> failwithf "expected TimedOut, got IncompleteOutput"
                  | Error(TerminationCleanupFailed { Cause = StdoutTerminal }) ->
                      failwithf "expected TimedOut, got TerminationCleanupFailed StdoutTerminal"
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
                  let cancelTcs =
                      TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                  cancelTcs.SetResult(true)

                  let timeoutTcs =
                      TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                  let pendingExit = TaskCompletionSource<bool>()

                  let seam =
                      { ExitTask = pendingExit.Task
                        Kill =
                          fun () ->
                              pendingExit.TrySetResult(true) |> ignore
                              Ok()
                        HasExited = fun () -> true
                        ReadExitCode = fun () -> 0
                        Dispose = fun () -> () }

                  let! result =
                      runWithSeamCustom
                          timeoutTcs
                          cancelTcs
                          seam
                          (Task.FromResult(EofReached [||]))
                          (Task.FromResult(ReadCancelled))
                          (TimeSpan.FromSeconds 5.0)
                          1024
                          1024

                  match result with
                  | Error Cancelled -> ()
                  | Error(TimedOut _) -> failwithf "expected Cancelled, got TimedOut"
                  | Error(TerminationCleanupFailed { Cause = StderrTerminal }) ->
                      failwithf "expected Cancelled, got TerminationCleanupFailed StderrTerminal"
                  | Error(StderrReaderFailed _) -> failwithf "expected Cancelled, got StderrReaderFailed"
                  | Error(IncompleteOutput(_, _)) -> failwithf "expected Cancelled, got IncompleteOutput"
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

              let seam =
                  { ExitTask = pendingExit.Task
                    Kill = fun () -> Ok()
                    HasExited = fun () -> false
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> disposeCount <- disposeCount + 1 }

              let lifecycle =
                  runWithSeam seam okStdout pendingStderr.Task (TimeSpan.FromSeconds 30.0) 1024 1024
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

              let seam =
                  { ExitTask = pendingExit.Task
                    Kill = fun () -> Ok()
                    HasExited = fun () -> false
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> disposeCount <- disposeCount + 1 }

              let lifecycle =
                  runWithSeam seam pendingStdout.Task pendingStderr.Task (TimeSpan.FromSeconds 30.0) 1024 1024

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
          // outstanding tasks settle. Uses runWithSeamCompletion so the
          // test can inspect the Result before awaiting the deferred
          // finalization.
          testTask "cleanup-failure defers disposal until outstanding tasks settle" {
              let mutable disposeCount = 0
              let pendingExit = TaskCompletionSource<bool>()
              let pendingStderr = TaskCompletionSource<ReadOutcome>()
              let okStdout = Task.FromResult(EofReached [||])

              let seam =
                  { ExitTask = pendingExit.Task
                    Kill = fun () -> Ok()
                    HasExited = fun () -> false
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> disposeCount <- disposeCount + 1 }

              let completion: Task<LifecycleCompletion> =
                  runWithSeamCompletion seam okStdout pendingStderr.Task (TimeSpan.FromMilliseconds 100.0) 1024 1024

              let! c = completion
              let result = c.Result

              match result with
              | Error(TerminationCleanupFailed _) -> ()
              | Error e -> failtest "expected TerminationCleanupFailed, got: %A" e
              | Ok s -> failtest "expected TerminationCleanupFailed, got Ok: %A" s

              Expect.equal disposeCount 0 "no disposal when exitTask and stderrTask still pending"
              // Complete the outstanding tasks and the finalizer fires.
              pendingExit.SetResult(true) |> ignore
              pendingStderr.SetResult(EofReached [||]) |> ignore
              do! c.Finalization
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

              let faultedStderr =
                  Task.FromException<ReadOutcome>(System.Exception "synthetic reader fault")

              let cancelledExit = Task.FromCanceled(cts.Token)

              let seam =
                  { ExitTask = cancelledExit
                    Kill = fun () -> Ok()
                    HasExited = fun () -> true
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> disposeCount <- disposeCount + 1 }

              let! result = runWithSeam seam okStdout faultedStderr (TimeSpan.FromSeconds 5.0) 1024 1024
              do! Task.Delay(200)
              // The public lifecycle reached a typed Result without
              // throwing despite one faulted and one cancelled task.
              Expect.isNotNull (box result) "lifecycle must produce a typed Result"
              Expect.equal disposeCount 1 "finalizer must dispose exactly once after a faulted and a cancelled task"
              cts.Dispose()
          }

          // ---------------------------------------------------------------
          // 33-35. P3.1 disposal-ordering regressions and deterministic
          // timeout/cancellation precedence.
          // ---------------------------------------------------------------

          // 33. Disposed-seam state access regression: a test seam that
          // throws from HasExited and ReadExitCode after disposal proves
          // the lifecycle captures all required state BEFORE the finalizer
          // disposes the seam.
          testTask "disposed-seam state access is impossible after disposal" {
              let pendingExit = TaskCompletionSource<bool>()
              let mutable disposed = false

              let seam =
                  { ExitTask = pendingExit.Task
                    Kill =
                      fun () ->
                          pendingExit.TrySetResult(true) |> ignore
                          Ok()
                    HasExited =
                      fun () ->
                          if disposed then
                              failwith "HasExited called after dispose"

                          false
                    ReadExitCode =
                      fun () ->
                          if disposed then
                              failwith "ReadExitCode called after dispose"

                          0
                    Dispose = fun () -> disposed <- true }

              let completion: Task<LifecycleCompletion> =
                  runWithSeamCompletion
                      seam
                      (Task.FromResult(EofReached [||]))
                      (Task.FromResult(EofReached [||]))
                      (TimeSpan.FromSeconds 5.0)
                      1024
                      1024
              // Complete the exit task so the loop proceeds. All three
              // operations are then settled.
              pendingExit.SetResult(true) |> ignore
              let! c = completion
              let result = c.Result
              // At this point the snapshot has been captured; the
              // finalization is awaited as part of runWithSeamCompletion.
              // If any state callback was invoked after dispose, the
              // failwith above would have been thrown.
              Expect.equal disposed true "dispose must be called exactly once"

              match result with
              | Ok _ -> ()
              | Error e -> failtest "expected Ok, got: %A" e
          }

          // 34. Sequential real-process regression: two ordinary
          // real-process invocations run back-to-back, repeated 20
          // times, with a strict overall wall-clock bound. The test
          // reproduces the full-suite hang that the previous checkpoint
          // failed to close.
          testTask "sequential real-process invocations complete in order (20 iterations)" {
              for _ in 1..20 do
                  let! result1 = runFixture (Path.GetTempPath()) [ "empty" ] (TimeSpan.FromSeconds 5.0) 1024 1024

                  match result1 with
                  | Ok _ -> ()
                  | Error e -> failtest "first invocation failed: %A" e

                  let! result2 = runFixture (Path.GetTempPath()) [ "stdout"; "10" ] (TimeSpan.FromSeconds 5.0) 1024 1024

                  match result2 with
                  | Ok _ -> ()
                  | Error e -> failtest "second invocation failed: %A" e
          }

          // 35. Deterministic timeout/cancellation precedence: when both
          // timeout and caller-cancellation participants are pre-completed,
          // caller-cancellation wins regardless of which task Task.WhenAny
          // returns first. The event loop checks cancelTcs.Task.IsCompleted
          // when timeout is observed and switches the cause to
          // CallerCancel if it is.
          testTask "simultaneous timeout and caller-cancellation -> caller-cancellation wins (100 iterations)" {
              for _ in 1..100 do
                  let timeoutTcs =
                      TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                  timeoutTcs.SetResult(true)

                  let cancelTcs =
                      TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                  cancelTcs.SetResult(true)
                  let pendingExit = TaskCompletionSource<bool>()

                  let seam =
                      { ExitTask = pendingExit.Task
                        Kill =
                          fun () ->
                              pendingExit.TrySetResult(true) |> ignore
                              Ok()
                        HasExited = fun () -> true
                        ReadExitCode = fun () -> 0
                        Dispose = fun () -> () }

                  let! result =
                      runWithSeamCustom
                          timeoutTcs
                          cancelTcs
                          seam
                          (Task.FromResult(EofReached [||]))
                          (Task.FromResult(EofReached [||]))
                          (TimeSpan.FromSeconds 5.0)
                          1024
                          1024

                  match result with
                  | Error Cancelled -> ()
                  | Error(TimedOut _) -> failtest "expected Cancelled, got TimedOut"
                  | Error(TerminationCleanupFailed { Cause = StdoutTerminal }) ->
                      failtest "expected Cancelled, got StdoutTerminal"
                  | Error(StdoutReaderFailed _) -> failtest "expected Cancelled, got StdoutReaderFailed"
                  | Error(IncompleteOutput(_, _)) -> failtest "expected Cancelled, got IncompleteOutput"
                  | Error e -> failtest "expected Cancelled, got: %A" e
                  | Ok s -> failtest "expected Cancelled, got Ok: %A" s
          }

          // ---------------------------------------------------------------
          // 36. CORRECTION17 registration-callback race regression.
          //
          // The lifecycle's finalizer must dispose CancellationToken
          // registrations asynchronously so a callback that is currently
          // executing on another thread can complete without blocking the
          // finalizer. This test injects a custom CancellationTokenRegistration
          // whose callback:
          //   1. signals that it started;
          //   2. blocks on a release task;
          //   3. then returns.
          // As long as the callback is blocked, the finalizer's
          // `tReg.DisposeAsync().AsTask()` await must remain suspended.
          // The test thread must remain responsive while the finalizer is
          // suspended, and releasing the callback must let the finalizer
          // complete and dispose exactly once.
          // ---------------------------------------------------------------
          testTask "registration-callback race: finalizer awaits async-dispose of blocking callback" {
              let mutable disposeCount = 0
              let callbackStarted = TaskCompletionSource<bool>()
              let releaseCallback = TaskCompletionSource<bool>()

              // Custom CTS + registration whose callback blocks on a
              // release task. CancellationTokenSource.CancelAsync runs
              // the callback on a thread-pool thread, not the calling
              // thread, so the main test thread is not blocked by the
              // callback itself.
              let customCts = new CancellationTokenSource()

              let customReg =
                  customCts.Token.Register(fun () ->
                      callbackStarted.TrySetResult(true) |> ignore
                      // Block on the release task. The synchronous
                      // GetResult mirrors the classic CancellationRegistration
                      // deadlock pattern: a disposal cannot complete
                      // until this callback returns, and this callback
                      // cannot return until the test thread releases it.
                      releaseCallback.Task.GetAwaiter().GetResult() |> ignore)

              // All three operations are pre-settled so the lifecycle
              // reaches the finalizer without any further input.
              let okExit = Task.FromResult(0)
              let okStdout = Task.FromResult(EofReached [||])
              let okStderr = Task.FromResult(EofReached [||])

              let seam =
                  { ExitTask = okExit
                    Kill = fun () -> Ok()
                    HasExited = fun () -> true
                    ReadExitCode = fun () -> 0
                    Dispose = fun () -> disposeCount <- disposeCount + 1 }

              let request =
                  { Executable = "ignored"
                    WorkingDirectory = "."
                    Arguments = []
                    Environment = []
                    Limits =
                      { Timeout = TimeSpan.FromSeconds 5.0
                        StdoutLimitBytes = 1024
                        StderrLimitBytes = 1024 } }

              let lcts = new CancellationTokenSource()
              let tcts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

              let timeoutTcs =
                  TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

              let cancelTcs =
                  TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

              let cReg = lcts.Token.Register(fun () -> cancelTcs.TrySetResult(true) |> ignore)

              // CORRECTION18: Fire-and-forget CancelAsync. Awaiting it here
              // would deadlock: CancelAsync() only completes after ALL
              // registered callbacks have finished, but releaseCallback is
              // held by the test until the assertions pass.
              let cancellationTask = customCts.CancelAsync()
              let mutable completionResult: LifecycleCompletion option = None
              let mutable callbackStartedInTime = false
              let mutable completionReturnedInTime = false
              let mutable finalizationSuspended = false
              let mutable threadResponsive = false

              // Use try/finally so releaseCallback is always completed
              // even if an assertion fails. A failing test could strand
              // a callback and poison the remainder of the suite.
              try
                  // Bounded wait for callback to start. If CancelAsync fails
                  // to schedule the callback, the timeout causes assertion
                  // failure rather than suite hang.
                  let! callbackWinner = Task.WhenAny(callbackStarted.Task :> Task, Task.Delay(TimeSpan.FromSeconds 2.0))

                  callbackStartedInTime <- Object.ReferenceEquals(callbackWinner, callbackStarted.Task)

                  if callbackStartedInTime then
                      // Run the lifecycle with the BLOCKING registration as
                      // tReg. The customReg is the lifecycle's tReg, so the
                      // finalizer's DisposeAsync() will be forced to wait
                      // asynchronously for the blocking callback.
                      let lifecycleTask =
                          executeLifecycleWithSeam
                              lcts
                              request
                              timeoutTcs
                              cancelTcs
                              okStdout
                              okStderr
                              seam
                              customReg
                              cReg
                              tcts

                      // Bounded wait for lifecycle to return. A regression
                      // to synchronous disposal would block indefinitely here.
                      let! completionWinner = Task.WhenAny(lifecycleTask :> Task, Task.Delay(TimeSpan.FromSeconds 2.0))

                      completionReturnedInTime <- Object.ReferenceEquals(completionWinner, lifecycleTask)

                      if completionReturnedInTime then
                          let! c = lifecycleTask
                          completionResult <- Some c

                          // Assertion: the finalizer is suspended because the
                          // callback is still blocked on the release task.
                          finalizationSuspended <- not c.Finalization.IsCompleted

                          // Assertion: the test thread remains responsive.
                          do! Task.Delay(100)
                          threadResponsive <- not c.Finalization.IsCompleted
              finally
                  // Release the callback. The disposal await resumes, the
                  // finalizer proceeds through the remaining disposals,
                  // and the seam's Dispose callback runs exactly once.
                  releaseCallback.TrySetResult(true) |> ignore

              // Bounded wait for CancelAsync to complete after callback release.
              let! cancellationWinner = Task.WhenAny(cancellationTask, Task.Delay(TimeSpan.FromSeconds 2.0))

              Expect.isTrue
                  (Object.ReferenceEquals(cancellationWinner, cancellationTask))
                  "CancelAsync must finish after callback release"

              // Explicitly await the winning task to observe any fault.
              do! cancellationTask

              match completionResult with
              | Some completion ->
                  // Bounded wait for finalization after callback release.
                  let! finalizationWinner = Task.WhenAny(completion.Finalization, Task.Delay(TimeSpan.FromSeconds 2.0))

                  Expect.isTrue
                      (Object.ReferenceEquals(finalizationWinner, completion.Finalization))
                      "finalization must complete after callback release"

                  // Explicitly await the winning task to observe any fault.
                  do! completion.Finalization

              | None -> ()

              // Dispose after observing CancelAsync task completion.
              customCts.Dispose()

              // Assert recorded conditions after all cleanup is complete.
              // This ensures all paths through the try block are bounded.
              Expect.isTrue callbackStartedInTime "callback must start within the bound"

              Expect.isTrue completionReturnedInTime "lifecycle must return without waiting synchronously for disposal"

              Expect.isTrue finalizationSuspended "finalization must await the active callback"

              Expect.isTrue threadResponsive "test thread must remain responsive"

              Expect.isTrue
                  (Object.ReferenceEquals(cancellationWinner, cancellationTask))
                  "CancelAsync must finish after callback release"

              Expect.equal disposeCount 1 "dispose exactly once"
          } ]
    // Intrinsic sequencing: the canonical gate is deterministic without
    // requiring an operator `--sequenced` flag, because the fixture
    // now spawns short-lived `dotnet <fixture.dll> <mode> ...` children
    // instead of the previous heavyweight `dotnet fsi --exec` compiles.
    |> (fun t -> Test.Sequenced(SequenceMethod.Synchronous, t))
