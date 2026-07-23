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
type public BoundedProcessLimits = {
    Timeout: TimeSpan
    StdoutLimitBytes: int
    StderrLimitBytes: int
}

/// A request to run a process with bounded resources.
type public BoundedProcessRequest = {
    Executable: string
    WorkingDirectory: string
    Arguments: string list
    Environment: (string * string) list
    Limits: BoundedProcessLimits
}

/// Successful process completion with captured output.
type public BoundedProcessSuccess = {
    ExitCode: int
    Stdout: byte array
    Stderr: byte array
}

/// Failure modes for bounded process execution.
type public BoundedProcessFailure =
    | InvalidRequest of detail: string
    | LaunchFailed of executable: string * detail: string
    | TimedOut of timeout: TimeSpan
    | Cancelled
    | StdoutLimitExceeded of limitBytes: int
    | StderrLimitExceeded of limitBytes: int
    | NonZeroExit of exitCode: int * stdout: byte array * stderr: byte array
    | StdoutReaderFailed of detail: string
    | StderrReaderFailed of detail: string
    | KillFailed of detail: string

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

/// Result of a bounded read operation.
type ReadResult =
    | Completed of bytes: byte array
    | Overflow of bytes: byte array
    | ReadCancelled
    | ReadFailed of detail: string

let private readBounded
    (stream: Stream)
    (limitBytes: int)
    (cancellationToken: CancellationToken)
    : Task<ReadResult> =
    task {
        let bufferSize = min 4096 (max 1 (limitBytes + 1))
        let buffer = Array.zeroCreate<byte> bufferSize
        let collected = ResizeArray<byte>()
        let mutable isOverflow = false

        try
            let mutable keepReading = true

            while keepReading && not cancellationToken.IsCancellationRequested do
                let bytesToRead = min bufferSize (max 0 (limitBytes + 1 - collected.Count))
                if bytesToRead <= 0 then
                    keepReading <- false
                    isOverflow <- true
                else
                    let! bytesRead = stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken)
                    if bytesRead = 0 then
                        keepReading <- false
                    else
                        for i = 0 to bytesRead - 1 do
                            collected.Add(buffer.[i])
                        if collected.Count > limitBytes then
                            keepReading <- false
                            isOverflow <- true

            if cancellationToken.IsCancellationRequested then
                return ReadCancelled
            elif isOverflow then
                return Overflow(collected.ToArray())
            else
                return Completed(collected.ToArray())
        with
        | :? OperationCanceledException ->
            return ReadCancelled
        | :? IOException as ex ->
            return ReadFailed ex.Message
        | ex ->
            return ReadFailed ex.Message
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
// Helper: unwrap Option (None -> fallback, Some x -> x)
// -----------------------------------------------------------------------------

let private ( |?> ) (opt: BoundedProcessFailure option) (fallback: BoundedProcessFailure) : BoundedProcessFailure =
    match opt with
    | Some v -> v
    | None -> fallback

// -----------------------------------------------------------------------------
// Helper: start process and return launch result
// -----------------------------------------------------------------------------

let private startProcess
    (startInfo: ProcessStartInfo)
    : Result<Process, BoundedProcessFailure> =
    try
        let proc = new Process()
        proc.StartInfo <- startInfo
        if proc.Start() then Ok(proc)
        else Error(LaunchFailed(startInfo.FileName, "Start returned false"))
    with
    | :? System.ComponentModel.Win32Exception as ex -> Error(LaunchFailed(startInfo.FileName, ex.Message))
    | :? System.InvalidOperationException as ex -> Error(LaunchFailed(startInfo.FileName, ex.Message))
    | :? System.IO.FileNotFoundException as ex -> Error(LaunchFailed(startInfo.FileName, ex.Message))
    | :? System.IO.DirectoryNotFoundException as ex -> Error(LaunchFailed(startInfo.FileName, ex.Message))

// -----------------------------------------------------------------------------
// Private execution helper
// -----------------------------------------------------------------------------

let private runWithProcess
    (request: BoundedProcessRequest)
    (proc: Process)
    (cancellationToken: CancellationToken)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    task {
        proc.StandardInput.Close()

        // Link cancellation token with timeout
        use timeoutCts = new CancellationTokenSource(request.Limits.Timeout)
        use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)

        // Start reader tasks
        let stdoutTask = readBounded proc.StandardOutput.BaseStream request.Limits.StdoutLimitBytes linkedCts.Token
        let stderrTask = readBounded proc.StandardError.BaseStream request.Limits.StderrLimitBytes linkedCts.Token
        let waitTask = proc.WaitForExitAsync(linkedCts.Token)

        // Race all tasks using Task.WhenAny
        let! _completedTask =
            Task.WhenAny(
                Task.WhenAll(stdoutTask, stderrTask) :> Task,
                waitTask
            )

        // Determine terminal condition BEFORE awaiting
        let terminalFailure =
            if timeoutCts.IsCancellationRequested then
                Some(TimedOut request.Limits.Timeout)
            elif linkedCts.IsCancellationRequested then
                Some BoundedProcessFailure.Cancelled
            else
                None

        // If we have a terminal failure, kill and return
        match terminalFailure with
        | Some failure ->
            let killResult = tryKill proc
            do! Task.Delay(100, CancellationToken.None)
            return Error(killResult |?> failure)
        | None ->
            // Await all tasks
            let! stdoutResult = stdoutTask
            let! stderrResult = stderrTask
            let exitCode = proc.ExitCode

            // Analyze results
            let stdoutFailure =
                match stdoutResult with
                | Overflow _ -> Some(StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                | ReadFailed d -> Some(StdoutReaderFailed d)
                | ReadCancelled -> Some BoundedProcessFailure.Cancelled
                | Completed _ -> None

            let stderrFailure =
                match stderrResult with
                | Overflow _ -> Some(StderrLimitExceeded request.Limits.StderrLimitBytes)
                | ReadFailed d -> Some(StderrReaderFailed d)
                | ReadCancelled -> Some BoundedProcessFailure.Cancelled
                | Completed _ -> None

            // Return result
            match stdoutFailure with
            | Some f -> return Error f
            | None ->
                match stderrFailure with
                | Some f -> return Error f
                | None ->
                    let stdoutBytes =
                        match stdoutResult with
                        | Completed b -> b
                        | Overflow b -> b
                        | _ -> [||]
                    let stderrBytes =
                        match stderrResult with
                        | Completed b -> b
                        | Overflow b -> b
                        | _ -> [||]
                    if exitCode = 0 then
                        return Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
                    else
                        return Error(NonZeroExit(exitCode, stdoutBytes, stderrBytes))
    }

// -----------------------------------------------------------------------------
// Process runner (public API - genuine async task)
// -----------------------------------------------------------------------------

let run
    (request: BoundedProcessRequest)
    (cancellationToken: CancellationToken)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    task {
        // Check validation
        let validationResult = validateRequest request

        // Check cancellation
        let cancelledResult =
            if cancellationToken.IsCancellationRequested then
                Some BoundedProcessFailure.Cancelled
            else
                None

        // Setup process start info
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

        // Start process
        let launchResult = startProcess startInfo

        // Compute final result
        match validationResult with
        | Some e -> return Error e
        | None ->
            match cancelledResult with
            | Some e -> return Error e
            | None ->
                match launchResult with
                | Error e -> return Error e
                | Ok proc ->
                    use _proc = proc
                    return! runWithProcess request proc cancellationToken
    }
