module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// =============================================================================
// Bounded-process core checkpoint
// =============================================================================
//
// This module implements a deliberately smaller bounded-process core that
// returns a .NET task.  The Git adapter is deferred to a later checkpoint.
//
// Architectural constraint: one outer ``task { }`` block, ordinary local
// functions and ``match`` expressions inside.  No ``async { }``,
// ``Async.StartChild``, ``Async.Parallel``, or nested task/async conversions.

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

// -----------------------------------------------------------------------------
// Public types
// -----------------------------------------------------------------------------

/// Resource limits for a bounded process execution.
type BoundedProcessLimits = {
    Timeout: TimeSpan
    StdoutLimitBytes: int
    StderrLimitBytes: int
}

/// A request to run a process with bounded resources.
type BoundedProcessRequest = {
    Executable: string
    WorkingDirectory: string
    Arguments: string list
    Environment: (string * string) list
    Limits: BoundedProcessLimits
}

/// Successful process completion with captured output.
type BoundedProcessSuccess = {
    ExitCode: int
    Stdout: byte array
    Stderr: byte array
}

/// Failure modes for bounded process execution.
type BoundedProcessFailure =
    | InvalidRequest of detail: string
    | LaunchFailed of executable: string * detail: string
    | TimedOut of timeout: TimeSpan
    | Cancelled
    | StdoutLimitExceeded of limitBytes: int
    | StderrLimitExceeded of limitBytes: int
    | NonZeroExit of
        exitCode: int *
        stdout: byte array *
        stderr: byte array
    | WaitFailed of detail: string
    | KillFailed of detail: string

// -----------------------------------------------------------------------------
// Internal types
// -----------------------------------------------------------------------------

/// Result of a bounded read operation.
type BoundedReadResult =
    | ReadComplete of byte array
    | ReadOverflow

// -----------------------------------------------------------------------------
// Request validation
// -----------------------------------------------------------------------------

let private validateRequest (request: BoundedProcessRequest) : BoundedProcessFailure option =
    if String.IsNullOrWhiteSpace request.Executable then
        Some(InvalidRequest "executable must not be empty")
    elif String.IsNullOrWhiteSpace request.WorkingDirectory then
        Some(InvalidRequest "working directory must not be empty")
    elif not (Directory.Exists request.WorkingDirectory) then
        Some(InvalidRequest "working directory does not exist")
    elif request.Limits.Timeout <= TimeSpan.Zero then
        Some(InvalidRequest "timeout must be greater than zero")
    elif request.Limits.StdoutLimitBytes < 0 then
        Some(InvalidRequest "stdout limit must not be negative")
    elif request.Limits.StderrLimitBytes < 0 then
        Some(InvalidRequest "stderr limit must not be negative")
    else
        let envKeys = request.Environment |> List.map fst
        let uniqueKeys = Set.ofList envKeys
        if Set.count uniqueKeys <> List.length envKeys then
            Some(InvalidRequest "environment contains duplicate keys")
        else
            None

// -----------------------------------------------------------------------------
// Bounded byte reader
// -----------------------------------------------------------------------------

let private readBounded
    (stream: Stream)
    (limitBytes: int)
    (cancellationToken: CancellationToken)
    : Task<BoundedReadResult> =
    task {
        let bufferSize = min 4096 (limitBytes + 1)
        let buffer = Array.zeroCreate<byte> bufferSize
        let collected = ResizeArray<byte>()

        let mutable keepReading = true

        while keepReading && not cancellationToken.IsCancellationRequested do
            let bytesToRead = min bufferSize (limitBytes + 1 - collected.Count)
            if bytesToRead <= 0 then
                keepReading <- false
            else
                let! bytesRead = stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken)
                if bytesRead = 0 then
                    keepReading <- false
                else
                    for i in 0 .. bytesRead - 1 do
                        collected.Add(buffer.[i])
                    if collected.Count > limitBytes then
                        keepReading <- false

        if cancellationToken.IsCancellationRequested then
            return ReadComplete(collected.ToArray())
        elif collected.Count > limitBytes then
            return ReadOverflow
        else
            return ReadComplete(collected.ToArray())
    }

// -----------------------------------------------------------------------------
// Helper: try kill process tree
// -----------------------------------------------------------------------------

let private tryKill (proc: Process) : BoundedProcessFailure option =
    try
        if not proc.HasExited then
            proc.Kill(entireProcessTree = true)
        None
    with
    | :? System.ComponentModel.Win32Exception as ex -> Some(KillFailed ex.Message)
    | :? InvalidOperationException as ex -> Some(KillFailed ex.Message)

// -----------------------------------------------------------------------------
// Synchronous runner (all logic, no task)
// -----------------------------------------------------------------------------

let private runSync
    (request: BoundedProcessRequest)
    : Result<BoundedProcessSuccess, BoundedProcessFailure> =
    // Validate request
    match validateRequest request with
    | Some failure -> Error failure
    | None ->
        // Construct ProcessStartInfo
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- request.Executable
        startInfo.WorkingDirectory <- request.WorkingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.CreateNoWindow <- true

        for arg in request.Arguments do
            startInfo.ArgumentList.Add(arg)

        for key, value in request.Environment do
            startInfo.Environment.[key] <- value

        // Construct and start Process
        let proc = Process.Start(startInfo)
        if isNull proc then
            Error(LaunchFailed(request.Executable, "Start returned null"))
        else
            try
                // After successful start - close StandardInput
                proc.StandardInput.Close()

                let stdoutStream = proc.StandardOutput.BaseStream
                let stderrStream = proc.StandardError.BaseStream

                // Create cancellation source for timeout
                use timeoutCts = new CancellationTokenSource(request.Limits.Timeout)

                // Start reader tasks (these are background tasks)
                let stdoutTask = readBounded stdoutStream request.Limits.StdoutLimitBytes timeoutCts.Token
                let stderrTask = readBounded stderrStream request.Limits.StderrLimitBytes timeoutCts.Token

                // Wait for process exit (synchronous wait)
                proc.WaitForExit()

                // Get results
                let stdoutRes = stdoutTask.Result
                let stderrRes = stderrTask.Result
                let exitCode = proc.ExitCode

                // Compute result based on timeout and output limits
                if timeoutCts.IsCancellationRequested then
                    match tryKill proc with
                    | Some f -> Error f
                    | None -> Error(TimedOut request.Limits.Timeout)
                else
                    match stdoutRes with
                    | ReadOverflow ->
                        match tryKill proc with
                        | Some f -> Error f
                        | None -> Error(StdoutLimitExceeded request.Limits.StdoutLimitBytes)

                    | ReadComplete stdoutBytes ->
                        match stderrRes with
                        | ReadOverflow ->
                            match tryKill proc with
                            | Some f -> Error f
                            | None -> Error(StderrLimitExceeded request.Limits.StderrLimitBytes)

                        | ReadComplete stderrBytes ->
                            if exitCode = 0 then
                                Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
                            else
                                Error(NonZeroExit(exitCode, stdoutBytes, stderrBytes))
            finally
                if not proc.HasExited then
                    proc.Kill() |> ignore
                proc.Dispose()

// -----------------------------------------------------------------------------
// Process runner (public API - returns Task)
// -----------------------------------------------------------------------------

let run
    (request: BoundedProcessRequest)
    (cancellationToken: CancellationToken)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    Task.FromResult(runSync request)
