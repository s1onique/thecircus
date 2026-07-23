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

/// Reader task result with bytes and fault state.
type ReaderTaskResult = {
    Bytes: byte array
    IsOverflow: bool
    IsCancelled: bool
    IsFaulted: bool
    FaultException: exn option
}

/// Terminal condition for process lifecycle.
type TerminalCondition =
    | ProcessExited
    | Timeout
    | CallerCancelled
    | StdoutOverflow
    | StderrOverflow
    | StdoutCancelled
    | StderrCancelled
    | StdoutFaulted of exn
    | StderrFaulted of exn
    | WaitFaulted of exn

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
    : Task<ReaderTaskResult> =
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
                return { Bytes = collected.ToArray(); IsOverflow = false; IsCancelled = true; IsFaulted = false; FaultException = None }
            elif isOverflow then
                return { Bytes = collected.ToArray(); IsOverflow = true; IsCancelled = false; IsFaulted = false; FaultException = None }
            else
                return { Bytes = collected.ToArray(); IsOverflow = false; IsCancelled = false; IsFaulted = false; FaultException = None }
        with
        | :? OperationCanceledException ->
            return { Bytes = collected.ToArray(); IsOverflow = false; IsCancelled = true; IsFaulted = false; FaultException = None }
        | ex ->
            return { Bytes = collected.ToArray(); IsOverflow = false; IsCancelled = false; IsFaulted = true; FaultException = Some ex }
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
// Helper: wait for task with timeout
// -----------------------------------------------------------------------------

let private awaitTask (t: Task) (timeoutMs: int) =
    try t.Wait(timeoutMs) with _ -> false

// -----------------------------------------------------------------------------
// Helper: poll until a task completes (synchronous, no nested tasks)
// -----------------------------------------------------------------------------

let private pollForCompletion
    (waitTask: Task)
    (stdoutTask: Task<ReaderTaskResult>)
    (stderrTask: Task<ReaderTaskResult>)
    (timeoutCts: CancellationTokenSource)
    (linkedCts: CancellationTokenSource)
    (pollIntervalMs: int)
    : TerminalCondition =
    let mutable isComplete = false
    let mutable result = Timeout

    while not isComplete do
        if linkedCts.IsCancellationRequested then
            result <- CallerCancelled
            isComplete <- true
        elif timeoutCts.IsCancellationRequested then
            result <- Timeout
            isComplete <- true
        elif stdoutTask.IsFaulted && not (isNull stdoutTask.Exception) then
            result <- StdoutFaulted(stdoutTask.Exception.InnerException)
            isComplete <- true
        elif stdoutTask.IsCompleted && stdoutTask.Result.IsFaulted then
            result <- StdoutFaulted(stdoutTask.Result.FaultException.Value)
            isComplete <- true
        elif stdoutTask.IsCompleted && stdoutTask.Result.IsCancelled then
            result <- StdoutCancelled
            isComplete <- true
        elif stdoutTask.IsCompleted && stdoutTask.Result.IsOverflow then
            result <- StdoutOverflow
            isComplete <- true
        elif stderrTask.IsFaulted && not (isNull stderrTask.Exception) then
            result <- StderrFaulted(stderrTask.Exception.InnerException)
            isComplete <- true
        elif stderrTask.IsCompleted && stderrTask.Result.IsFaulted then
            result <- StderrFaulted(stderrTask.Result.FaultException.Value)
            isComplete <- true
        elif stderrTask.IsCompleted && stderrTask.Result.IsCancelled then
            result <- StderrCancelled
            isComplete <- true
        elif stderrTask.IsCompleted && stderrTask.Result.IsOverflow then
            result <- StderrOverflow
            isComplete <- true
        elif waitTask.IsFaulted && not (isNull waitTask.Exception) then
            result <- WaitFaulted(waitTask.Exception.InnerException)
            isComplete <- true
        elif waitTask.IsCompleted then
            result <- ProcessExited
            isComplete <- true
        else
            Thread.Sleep(pollIntervalMs)

    result

// -----------------------------------------------------------------------------
// Helper: convert terminal condition to failure
// -----------------------------------------------------------------------------

let private terminalToFailure
    (terminal: TerminalCondition)
    (timeout: TimeSpan)
    (stdoutLimit: int)
    (stderrLimit: int)
    : BoundedProcessFailure =
    match terminal with
    | Timeout -> TimedOut timeout
    | CallerCancelled -> Cancelled
    | StdoutOverflow -> StdoutLimitExceeded stdoutLimit
    | StderrOverflow -> StderrLimitExceeded stderrLimit
    | StdoutCancelled | StderrCancelled -> Cancelled
    | StdoutFaulted exn | StderrFaulted exn | WaitFaulted exn -> WaitFailed exn.Message
    | ProcessExited -> failwith "ProcessExited is not a failure"

// -----------------------------------------------------------------------------
// Helper: check if terminal condition requires kill
// -----------------------------------------------------------------------------

let private needsKill (terminal: TerminalCondition) =
    match terminal with
    | Timeout | StdoutOverflow | StderrOverflow | StdoutCancelled | StderrCancelled
    | StdoutFaulted _ | StderrFaulted _ | WaitFaulted _ -> true
    | CallerCancelled | ProcessExited -> false

// -----------------------------------------------------------------------------
// Helper: unwrap Option (None -> false, Some x -> true)
// -----------------------------------------------------------------------------

let private ( |? ) (opt: 'a option) (defaultValue: 'a) : 'a =
    match opt with
    | Some v -> v
    | None -> defaultValue

// -----------------------------------------------------------------------------
// Process runner (internal async helper)
// -----------------------------------------------------------------------------

let private runAsync
    (request: BoundedProcessRequest)
    (cancellationToken: CancellationToken)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    // Step 1: validate
    match validateRequest request with
    | Some e -> Task.FromResult(Error e)
    | None ->
        // Step 2: setup process
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

        let proc = new Process()
        proc.StartInfo <- startInfo

        // Step 3: start process
        try
            if not (proc.Start()) then
                proc.Dispose()
                Task.FromResult(Error(LaunchFailed(request.Executable, "Start returned false")))
            else
                // Step 4: start background tasks
                proc.StandardInput.Close()
                use timeoutCts = new CancellationTokenSource(request.Limits.Timeout)
                use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                let stdoutTask = readBounded proc.StandardOutput.BaseStream request.Limits.StdoutLimitBytes linkedCts.Token
                let stderrTask = readBounded proc.StandardError.BaseStream request.Limits.StderrLimitBytes linkedCts.Token
                let waitTask = proc.WaitForExitAsync()

                // Step 5: poll for completion
                let terminal = pollForCompletion waitTask stdoutTask stderrTask timeoutCts linkedCts 10

                // Step 6: handle kill if needed
                if needsKill terminal then
                    let killResult = tryKill proc
                    awaitTask waitTask 5000 |> ignore
                    awaitTask stdoutTask 5000 |> ignore
                    awaitTask stderrTask 5000 |> ignore
                    proc.Dispose()
                    let failure = killResult |? terminalToFailure terminal request.Limits.Timeout request.Limits.StdoutLimitBytes request.Limits.StderrLimitBytes
                    Task.FromResult(Error failure)
                else
                    // Step 7: wait for all tasks
                    if not proc.HasExited then
                        awaitTask waitTask 5000 |> ignore
                    awaitTask stdoutTask 5000 |> ignore
                    awaitTask stderrTask 5000 |> ignore

                    // Step 8: collect results
                    let exitCode = if proc.HasExited then proc.ExitCode else 0

                    let stdoutResult =
                        if stdoutTask.IsCompleted then stdoutTask.Result
                        elif stdoutTask.IsFaulted then
                            { Bytes = [||]; IsOverflow = false; IsCancelled = false; IsFaulted = true;
                              FaultException = if isNull stdoutTask.Exception then None else Some stdoutTask.Exception.InnerException }
                        else
                            { Bytes = [||]; IsOverflow = false; IsCancelled = false; IsFaulted = false; FaultException = None }

                    let stderrResult =
                        if stderrTask.IsCompleted then stderrTask.Result
                        elif stderrTask.IsFaulted then
                            { Bytes = [||]; IsOverflow = false; IsCancelled = false; IsFaulted = true;
                              FaultException = if isNull stderrTask.Exception then None else Some stderrTask.Exception.InnerException }
                        else
                            { Bytes = [||]; IsOverflow = false; IsCancelled = false; IsFaulted = false; FaultException = None }

                    let stdoutBytes = if stdoutResult.IsCancelled || stdoutResult.IsFaulted then [||] else stdoutResult.Bytes
                    let stderrBytes = if stderrResult.IsCancelled || stderrResult.IsFaulted then [||] else stderrResult.Bytes

                    // Step 9: cleanup
                    try
                        if not proc.HasExited then
                            proc.Kill() |> ignore
                        proc.Dispose()
                    with _ -> ()

                    // Step 10: return result
                    if exitCode = 0 then
                        Task.FromResult(Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes })
                    else
                        Task.FromResult(Error(NonZeroExit(exitCode, stdoutBytes, stderrBytes)))
        with
        | :? System.ComponentModel.Win32Exception as ex ->
            proc.Dispose()
            Task.FromResult(Error(LaunchFailed(request.Executable, ex.Message)))
        | :? System.InvalidOperationException as ex ->
            proc.Dispose()
            Task.FromResult(Error(LaunchFailed(request.Executable, ex.Message)))
        | :? System.IO.FileNotFoundException as ex ->
            proc.Dispose()
            Task.FromResult(Error(LaunchFailed(request.Executable, ex.Message)))
        | :? System.IO.DirectoryNotFoundException as ex ->
            proc.Dispose()
            Task.FromResult(Error(LaunchFailed(request.Executable, ex.Message)))

// -----------------------------------------------------------------------------
// Process runner (public API)
// -----------------------------------------------------------------------------

let run
    (request: BoundedProcessRequest)
    (cancellationToken: CancellationToken)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
    runAsync request cancellationToken
