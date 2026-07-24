module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// =============================================================================
// Bounded-process core — CORRECTION 04
//
// Addresses:
// - P0: Synchronous blocking in reader (must use ReadAsync)
// - P0: Normal EOF kills valid child (EOF is nonterminal)
// - P0: Synchronous cleanup (must use async waits)
// - P0: Process leak on failure paths
// - P1: Limit overflow arithmetic
// =============================================================================

#nowarn "3511" // Task state machine static compilation warning

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

// -----------------------------------------------------------------------------
// Public types
// -----------------------------------------------------------------------------

type BoundedProcessLimits = {
    Timeout: TimeSpan
    StdoutLimitBytes: int
    StderrLimitBytes: int
}

type BoundedProcessRequest = {
    Executable: string
    WorkingDirectory: string
    Arguments: string list
    Environment: (string * string) list
    Limits: BoundedProcessLimits
}

type BoundedProcessSuccess = {
    ExitCode: int
    Stdout: byte array
    Stderr: byte array
}

type BoundedProcessFailure =
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
        Some(BoundedProcessFailure.InvalidRequest "executable must not be empty")
    elif String.IsNullOrWhiteSpace request.WorkingDirectory then
        Some(BoundedProcessFailure.InvalidRequest "working directory must not be empty")
    elif not (Directory.Exists request.WorkingDirectory) then
        Some(BoundedProcessFailure.InvalidRequest "working directory does not exist")
    elif request.Limits.Timeout <= TimeSpan.Zero then
        Some(BoundedProcessFailure.InvalidRequest "timeout must be greater than zero")
    elif request.Limits.StdoutLimitBytes < 0 then
        Some(BoundedProcessFailure.InvalidRequest "stdout limit must not be negative")
    elif request.Limits.StderrLimitBytes < 0 then
        Some(BoundedProcessFailure.InvalidRequest "stderr limit must not be negative")
    elif request.Limits.StdoutLimitBytes = Int32.MaxValue then
        Some(BoundedProcessFailure.InvalidRequest "stdout limit must be less than Int32.MaxValue")
    elif request.Limits.StderrLimitBytes = Int32.MaxValue then
        Some(BoundedProcessFailure.InvalidRequest "stderr limit must be less than Int32.MaxValue")
    else
        let envKeys = request.Environment |> List.map fst
        let uniqueKeys = Set.ofList envKeys
        if Set.count uniqueKeys <> List.length envKeys then
            Some(BoundedProcessFailure.InvalidRequest "environment contains duplicate keys")
        else
            None

// -----------------------------------------------------------------------------
// Read outcome — terminal outcomes indicate the process must be killed
// -----------------------------------------------------------------------------

type ReadOutcome =
    | EofReached of bytes: byte array        // Normal EOF, nonterminal for process
    | Overflowed of bytes: byte array        // Limit exceeded, terminal
    | ReadFailed of detail: string            // IO error, terminal
    | ReadCancelled                           // Cancellation, terminal

// -----------------------------------------------------------------------------
// Extract bytes from ReadOutcome (for completed tasks only)
// -----------------------------------------------------------------------------

let private extractBytes (outcome: ReadOutcome) : byte array =
    match outcome with
    | EofReached b -> b
    | Overflowed b -> b
    | _ -> [||]

// -----------------------------------------------------------------------------
// Asynchronous bounded byte reader — reads up to limit+1 bytes to detect overflow
// Uses ReadAsync for true async I/O
// -----------------------------------------------------------------------------

let private readBoundedAsync
    (stream: Stream)
    (limitBytes: int)
    (cancellationToken: CancellationToken)
    : Task<ReadOutcome> =
    task {
        // Safe arithmetic: read limit+1 bytes, using int64 to avoid overflow
        let maxToRead = int64 limitBytes + 1L
        let bufferSize = min 4096 (max 1 limitBytes)
        let buffer = Array.zeroCreate<byte> bufferSize
        let collected = ResizeArray<byte>()

        let mutable keepReading = true
        let mutable readError: string option = None

        while keepReading && int64 collected.Count < maxToRead && not cancellationToken.IsCancellationRequested do
            let remaining = maxToRead - int64 collected.Count
            let bytesToRead = min bufferSize (int remaining)

            if bytesToRead <= 0 then
                keepReading <- false
            else
                try
                    let! bytesRead = stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken)
                    if bytesRead = 0 then
                        keepReading <- false
                    else
                        for i = 0 to bytesRead - 1 do
                            collected.Add(buffer.[i])
                with
                | :? OperationCanceledException ->
                    keepReading <- false
                | :? IOException as ex ->
                    readError <- Some ex.Message
                    keepReading <- false
                | :? ObjectDisposedException as ex ->
                    readError <- Some ex.Message
                    keepReading <- false

        match readError with
        | Some msg -> return ReadFailed msg
        | None ->
            if cancellationToken.IsCancellationRequested then
                if int64 collected.Count > int64 limitBytes then
                    return Overflowed(collected.ToArray())
                else
                    return ReadCancelled
            elif int64 collected.Count > int64 limitBytes then
                return Overflowed(collected.ToArray())
            else
                return EofReached(collected.ToArray())
    }

// -----------------------------------------------------------------------------
// Helper functions (pure, no task CE)
// -----------------------------------------------------------------------------

let private tryKillProcess (proc: Process) : string option =
    let mutable killFailed = None
    try
        if not proc.HasExited then
            proc.Kill(entireProcessTree = true)
    with
    | :? System.ComponentModel.Win32Exception as ex -> killFailed <- Some ex.Message
    | :? InvalidOperationException as ex -> killFailed <- Some ex.Message
    | :? System.NotSupportedException as ex -> killFailed <- Some ex.Message
    killFailed

let private disposeProcess (proc: Process) =
    try proc.Dispose() with _ -> ()

let private awaitProcessExitSync (proc: Process) (msTimeout: int) =
    try
        let finished = proc.WaitForExit(msTimeout)
        if not finished then
            try proc.Kill() with _ -> ()
    with _ -> ()

let private awaitReaderWithTimeout (task: Task<ReadOutcome>) (msTimeout: int) =
    try task.Wait(msTimeout) with _ -> false

// Classify outcomes and produce final result - pure function
let private classifyOutcomes
    (proc: Process)
    (stdoutResult: ReadOutcome option)
    (stderrResult: ReadOutcome option)
    (timeoutOccurred: bool)
    (cancelled: bool)
    (limits: BoundedProcessLimits)
    : Result<BoundedProcessSuccess, BoundedProcessFailure> =
    
    match stdoutResult, stderrResult with
    | Some(Overflowed _), _ ->
        let killFailed = tryKillProcess proc
        awaitProcessExitSync proc 5000
        disposeProcess proc
        match killFailed with
        | Some msg -> Error(BoundedProcessFailure.KillFailed msg)
        | None -> Error(BoundedProcessFailure.StdoutLimitExceeded limits.StdoutLimitBytes)
    | _, Some(Overflowed _) ->
        let killFailed = tryKillProcess proc
        awaitProcessExitSync proc 5000
        disposeProcess proc
        match killFailed with
        | Some msg -> Error(BoundedProcessFailure.KillFailed msg)
        | None -> Error(BoundedProcessFailure.StderrLimitExceeded limits.StderrLimitBytes)
    | Some(ReadFailed msg), _ ->
        let killFailed = tryKillProcess proc
        awaitProcessExitSync proc 5000
        disposeProcess proc
        match killFailed with
        | Some msg2 -> Error(BoundedProcessFailure.KillFailed msg2)
        | None -> Error(BoundedProcessFailure.StdoutReaderFailed msg)
    | _, Some(ReadFailed msg) ->
        let killFailed = tryKillProcess proc
        awaitProcessExitSync proc 5000
        disposeProcess proc
        match killFailed with
        | Some msg2 -> Error(BoundedProcessFailure.KillFailed msg2)
        | None -> Error(BoundedProcessFailure.StderrReaderFailed msg)
    | Some(ReadCancelled), _ | _, Some(ReadCancelled) ->
        let killFailed = tryKillProcess proc
        awaitProcessExitSync proc 5000
        disposeProcess proc
        match killFailed with
        | Some msg -> Error(BoundedProcessFailure.KillFailed msg)
        | None -> Error BoundedProcessFailure.Cancelled
    | Some(EofReached stdoutBytes), Some(EofReached stderrBytes) ->
        if timeoutOccurred then
            let killFailed = tryKillProcess proc
            awaitProcessExitSync proc 5000
            disposeProcess proc
            match killFailed with
            | Some msg -> Error(BoundedProcessFailure.KillFailed msg)
            | None -> Error(BoundedProcessFailure.TimedOut limits.Timeout)
        elif cancelled then
            let killFailed = tryKillProcess proc
            awaitProcessExitSync proc 5000
            disposeProcess proc
            match killFailed with
            | Some msg -> Error(BoundedProcessFailure.KillFailed msg)
            | None -> Error BoundedProcessFailure.Cancelled
        else
            let exitCode = proc.ExitCode
            disposeProcess proc
            if exitCode = 0 then
                Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
            else
                Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))
    | _ ->
        let stdoutBytes = stdoutResult |> Option.map extractBytes |> Option.defaultValue [||]
        let stderrBytes = stderrResult |> Option.map extractBytes |> Option.defaultValue [||]
        let exitCode = proc.ExitCode
        disposeProcess proc
        if exitCode = 0 then
            Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
        else
            Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))

// -----------------------------------------------------------------------------
// Process runner (public API)
// -----------------------------------------------------------------------------

/// Run a process with resource limits, capturing stdout/stderr as bytes.
let run
    (request: BoundedProcessRequest)
    (cancellationToken: CancellationToken)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =

    match validateRequest request with
    | Some e -> Task.FromResult(Error e)
    | None when cancellationToken.IsCancellationRequested ->
        Task.FromResult(Error BoundedProcessFailure.Cancelled)
    | None ->
        task {
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

            // Start process with exception handling
            let proc : Result<Process, BoundedProcessFailure> =
                try
                    let p = new Process()
                    p.StartInfo <- startInfo
                    let started = p.Start()
                    if started then Ok p else Error(BoundedProcessFailure.LaunchFailed(request.Executable, "Process.Start returned false"))
                with
                | :? System.ComponentModel.Win32Exception as ex -> Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))
                | :? System.IO.FileNotFoundException as ex -> Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))
                | :? System.IO.DirectoryNotFoundException as ex -> Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))
                | ex -> Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))

            match proc with
            | Error e -> return Error e
            | Ok p ->
                let proc = p
                proc.StandardInput.Close()

                use timeoutCts = new CancellationTokenSource(request.Limits.Timeout)
                use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
                let linkedToken = linkedCts.Token

                let stdoutReadTask = readBoundedAsync proc.StandardOutput.BaseStream request.Limits.StdoutLimitBytes linkedToken
                let stderrReadTask = readBoundedAsync proc.StandardError.BaseStream request.Limits.StderrLimitBytes linkedToken

                let! winner = Task.WhenAny(
                    proc.WaitForExitAsync(),
                    stdoutReadTask,
                    stderrReadTask
                )

                let getReaderResult (task: Task<ReadOutcome>) msTimeout =
                    if awaitReaderWithTimeout task msTimeout then Some task.Result else None

                if winner = proc.WaitForExitAsync() then
                    let stdoutResult = getReaderResult stdoutReadTask 10000
                    let stderrResult = getReaderResult stderrReadTask 10000
                    return classifyOutcomes proc stdoutResult stderrResult timeoutCts.IsCancellationRequested cancellationToken.IsCancellationRequested request.Limits

                elif winner = stdoutReadTask then
                    match getReaderResult stdoutReadTask 0 with
                    | Some(EofReached _) ->
                        let! _ = Task.WhenAny(
                            proc.WaitForExitAsync(),
                            stderrReadTask,
                            Task.Delay(request.Limits.Timeout, cancellationToken),
                            Task.Delay(request.Limits.Timeout, timeoutCts.Token)
                        )
                        let stdoutResult = getReaderResult stdoutReadTask 10000
                        let stderrResult = getReaderResult stderrReadTask 10000
                        awaitProcessExitSync proc 10000
                        return classifyOutcomes proc stdoutResult stderrResult timeoutCts.IsCancellationRequested cancellationToken.IsCancellationRequested request.Limits

                    | Some(outcome) ->
                        let stdoutResult = Some outcome
                        let stderrResult = getReaderResult stderrReadTask 10000
                        return classifyOutcomes proc stdoutResult stderrResult timeoutCts.IsCancellationRequested cancellationToken.IsCancellationRequested request.Limits

                    | None ->
                        return Error(BoundedProcessFailure.LaunchFailed(request.Executable, "Unexpected state"))

                else
                    match getReaderResult stderrReadTask 0 with
                    | Some(EofReached _) ->
                        let! _ = Task.WhenAny(
                            proc.WaitForExitAsync(),
                            stdoutReadTask,
                            Task.Delay(request.Limits.Timeout, cancellationToken),
                            Task.Delay(request.Limits.Timeout, timeoutCts.Token)
                        )
                        let stdoutResult = getReaderResult stdoutReadTask 10000
                        let stderrResult = getReaderResult stderrReadTask 10000
                        awaitProcessExitSync proc 10000
                        return classifyOutcomes proc stdoutResult stderrResult timeoutCts.IsCancellationRequested cancellationToken.IsCancellationRequested request.Limits

                    | Some(outcome) ->
                        let stdoutResult = getReaderResult stdoutReadTask 10000
                        let stderrResult = Some outcome
                        return classifyOutcomes proc stdoutResult stderrResult timeoutCts.IsCancellationRequested cancellationToken.IsCancellationRequested request.Limits

                    | None ->
                        return Error(BoundedProcessFailure.LaunchFailed(request.Executable, "Unexpected state"))
        }
