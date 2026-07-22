module Circus.Tooling.NoForcePush.BoundedProcess

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

// ============================================================================
// Types
// ============================================================================

/// Raw process completion result - captures everything without judgment.
type ProcessCompletion =
    { ExitCode: int
      Stdout: string
      Stderr: string }

/// Failure result for bounded Git operations.
type BoundedFailure =
    | ProcessLaunchFailed of message: string
    | TimeoutExceeded of timeoutMs: int
    | StdoutLimitExceeded of limitBytes: int
    | StderrLimitExceeded of limitBytes: int
    | ProcessTerminationFailed of detail: string
    | OutputReadFailed of message: string

/// Default timeout for Git operations (30 seconds).
let defaultTimeoutMs = 30000

/// Maximum stdout/stderr buffer (1 MB).
let maxOutputBytes = 1024 * 1024

// ============================================================================
// Internal: Synchronous bounded byte reader (runs in background thread)
// ============================================================================

/// Reads from a stream in bounded byte chunks, blocking until EOF or limit.
let private readBoundedBytesSync
    (stream: Stream)
    (limitBytes: int)
    (ct: CancellationToken)
    : Result<byte [] * int, BoundedFailure> =
    let bufferSize = 4096
    let buffer = Array.zeroCreate<byte> bufferSize
    let collected = ResizeArray<byte>()
    
    let rec loop () =
        if ct.IsCancellationRequested || collected.Count >= limitBytes then
            ()
        else
            let bytesRead = stream.Read(buffer, 0, min bufferSize (limitBytes - collected.Count))
            if bytesRead > 0 then
                for i in 0 .. bytesRead - 1 do
                    collected.Add(buffer.[i])
                loop ()
    
    try
        loop ()
        
        if ct.IsCancellationRequested then
            Error(TimeoutExceeded 0)
        elif collected.Count >= limitBytes then
            Error(StdoutLimitExceeded limitBytes)
        else
            // EOF or completed normally
            Ok(collected.ToArray(), collected.Count)
    with
    | :? IOException -> Error(OutputReadFailed "IO error reading stream")
    | :? ObjectDisposedException -> Error(OutputReadFailed "Stream disposed")

/// Helper to run bounded read in background
let private readBoundedTask
    (stream: Stream)
    (limitBytes: int)
    (ct: CancellationToken)
    : Task<Result<byte [] * int, BoundedFailure>> =
    Task.Run<Result<byte [] * int, BoundedFailure>>(fun () -> readBoundedBytesSync stream limitBytes ct)

// ============================================================================
// Low-level bounded process runner
// ============================================================================

/// Run a command with bounded resources, returning raw completion.
/// All nonzero exits are preserved as successful completions.
let runBounded
    (fileName: string)
    (args: string list)
    (timeoutMs: int)
    (maxBytes: int)
    : Result<ProcessCompletion, BoundedFailure> =
    try
        let psi = ProcessStartInfo()
        psi.FileName <- fileName
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.RedirectStandardInput <- false

        // Use ArgumentList - no shell, no string concatenation
        for arg in args do
            psi.ArgumentList.Add(arg)

        use proc = new Process()
        proc.StartInfo <- psi

        // Get base streams for direct byte reading
        let stdoutStream = proc.StandardOutput.BaseStream
        let stderrStream = proc.StandardError.BaseStream

        // Cancellation token for all operations
        use cts = new CancellationTokenSource()
        let ct = cts.Token

        // Start process
        proc.Start() |> ignore

        // Start readers for both streams concurrently
        let stdoutTask = readBoundedTask stdoutStream maxBytes ct
        let stderrTask = readBoundedTask stderrStream maxBytes ct

        // Wait for process exit with timeout
        let exited = proc.WaitForExit(timeoutMs)

        if not exited then
            // Timeout - initiate termination
            cts.Cancel()

            try
                if not proc.HasExited then
                    proc.Kill(entireProcessTree = true)
            with _ -> ()

            // Wait for process termination confirmation
            let terminated = proc.WaitForExit(5000)

            if not terminated then
                // Cannot confirm termination - fail closed
                Error(ProcessTerminationFailed "process did not terminate within confirmation window")
            else
                // Check which stream triggered failure
                let stdoutResult = stdoutTask.Result
                match stdoutResult with
                | Error e -> Error e
                | Ok _ ->
                    let stderrResult = stderrTask.Result
                    match stderrResult with
                    | Error e -> Error e
                    | Ok _ -> Error(TimeoutExceeded timeoutMs)
        else
            // Process exited normally - get stream results
            let processExitCode = proc.ExitCode

            // Wait for both streams to complete
            try
                Task.WaitAll([| stdoutTask :> Task; stderrTask :> Task |], 5000) |> ignore
            with
            | :? AggregateException -> ()
            | :? OperationCanceledException -> ()

            // Check results
            let stdoutResult =
                if stdoutTask.IsCompleted then stdoutTask.Result
                else Ok([||], 0)
            let stderrResult =
                if stderrTask.IsCompleted then stderrTask.Result
                else Ok([||], 0)

            // Check for overflow failures first
            match stdoutResult with
            | Error e -> Error e
            | Ok (stdoutBytes, _) ->
                match stderrResult with
                | Error e -> Error e
                | Ok (stderrBytes, _) ->
                    // Both succeeded

                    let decodeBytes (bytes: byte []) =
                        try
                            Encoding.UTF8.GetString(bytes)
                        with
                        | _ -> String.Empty

                    Ok { ExitCode = processExitCode
                         Stdout = decodeBytes stdoutBytes
                         Stderr = decodeBytes stderrBytes }
    with
    | :? System.ComponentModel.Win32Exception as ex ->
        Error(ProcessLaunchFailed(sprintf "Failed to start process: %s" ex.Message))
    | :? System.InvalidOperationException as ex ->
        Error(ProcessLaunchFailed(sprintf "Invalid operation: %s" ex.Message))
    | :? OperationCanceledException ->
        Error(TimeoutExceeded timeoutMs)
    | ex ->
        Error(ProcessLaunchFailed(sprintf "Unexpected error: %s" ex.Message))

// ============================================================================
// Git-specific runner
// ============================================================================

/// Run git and capture output, returning raw completion.
/// Preserves all exit codes including 1, 2, 128+.
let runGitCapture
    (repoPath: string)
    (args: string list)
    (timeoutMs: int)
    : Result<ProcessCompletion, BoundedFailure> =
    runBounded "git" args timeoutMs maxOutputBytes

/// Run git for queries that should succeed, returning stdout.
/// Treats nonzero exit as failure.
let runGitQuery
    (repoPath: string)
    (args: string list)
    (timeoutMs: int)
    : Result<string, BoundedFailure> =
    match runGitCapture repoPath args timeoutMs with
    | Ok completion ->
        if completion.ExitCode = 0 then
            Ok(completion.Stdout.Trim())
        else
            Error(BoundedFailure.OutputReadFailed(
                sprintf "Git exited with %d: %s" completion.ExitCode completion.Stderr))
    | Error e -> Error e

/// Run git expecting success, returning stdout.
/// Treats nonzero exit as failure.
let runGitExpectSuccess
    (repoPath: string)
    (args: string list)
    (timeoutMs: int)
    : Result<string, BoundedFailure> =
    runGitQuery repoPath args timeoutMs

/// Run git with full completion result.
/// Preserves all exit codes.
let runGitWithExit
    (repoPath: string)
    (args: string list)
    (timeoutMs: int)
    : Result<ProcessCompletion, BoundedFailure> =
    runGitCapture repoPath args timeoutMs

/// Legacy wrapper - returns exit code directly.
/// Preserves all nonzero exits as successful Ok results.
let runGitWithExitCode
    (repoPath: string)
    (args: string list)
    (timeoutMs: int)
    : Result<int, BoundedFailure> =
    match runGitCapture repoPath args timeoutMs with
    | Ok completion -> Ok completion.ExitCode
    | Error e -> Error e

/// Legacy runGit for backward compatibility.
/// Returns (stdout, exitCode) tuple.
let runGit
    (repoPath: string)
    (args: string list)
    (timeoutMs: int)
    : Result<string * int, BoundedFailure> =
    match runGitCapture repoPath args timeoutMs with
    | Ok completion -> Ok(completion.Stdout.Trim(), completion.ExitCode)
    | Error e -> Error e
