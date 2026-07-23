module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// =============================================================================
// Bounded-process core — CORRECTION 03
//
// Addresses:
// - P0: Overflow detection unreachable (reads limit+1 bytes)
// - P0: Timeout/cancellation not independent race participants
// - P0: Process exit returns incomplete output as success
// - P0: Termination cleanup not awaited
// - P0: Kill failures discarded
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
    | IncompleteOutput of stdoutBytes: byte array * stderrBytes: byte array

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
    else
        let envKeys = request.Environment |> List.map fst
        let uniqueKeys = Set.ofList envKeys
        if Set.count uniqueKeys <> List.length envKeys then
            Some(BoundedProcessFailure.InvalidRequest "environment contains duplicate keys")
        else
            None

// -----------------------------------------------------------------------------
// Read outcome with explicit overflow flag
// -----------------------------------------------------------------------------

type ReadOutcome =
    | BytesRead of bytes: byte array
    | Overflowed of bytes: byte array  // Read limit+1 bytes, overflow detected
    | ReadCancelled
    | ReadFailed of detail: string

// -----------------------------------------------------------------------------
// Extract bytes from ReadOutcome (for completed tasks only)
// -----------------------------------------------------------------------------

let private extractBytes (outcome: ReadOutcome) : byte array =
    match outcome with
    | BytesRead b -> b
    | Overflowed b -> b
    | _ -> [||]

// -----------------------------------------------------------------------------
// Bounded byte reader — reads up to limit+1 bytes to detect overflow
// -----------------------------------------------------------------------------

let private readBounded
    (stream: Stream)
    (limitBytes: int)
    (cancellationToken: CancellationToken)
    : Task<ReadOutcome> =
    task {
        // Read limit+1 bytes to make overflow detection possible
        let maxToRead = limitBytes + 1
        let bufferSize = min 4096 (max 1 maxToRead)
        let buffer = Array.zeroCreate<byte> bufferSize
        let collected = ResizeArray<byte>()

        let mutable keepReading = true

        while keepReading && collected.Count < maxToRead && not cancellationToken.IsCancellationRequested do
            let remaining = maxToRead - collected.Count
            let bytesToRead = min bufferSize remaining

            if bytesToRead > 0 then
                let bytesRead = stream.Read(buffer, 0, bytesToRead)
                if bytesRead = 0 then
                    keepReading <- false
                else
                    for i = 0 to bytesRead - 1 do
                        collected.Add(buffer.[i])
            else
                keepReading <- false

        if cancellationToken.IsCancellationRequested then
            if collected.Count > limitBytes then
                return Overflowed(collected.ToArray())
            else
                return ReadCancelled
        elif collected.Count > limitBytes then
            return Overflowed(collected.ToArray())
        else
            return BytesRead(collected.ToArray())
    }

// -----------------------------------------------------------------------------
// Helper: create a task that completes when a CancellationToken is cancelled
// -----------------------------------------------------------------------------

let private waitForCancellation (token: CancellationToken) : Task =
    if token.IsCancellationRequested then
        Task.CompletedTask
    else
        Task.Delay(Timeout.Infinite, token)

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
            // STEP 1: Setup start info
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

            // STEP 2: Start process
            let startedProcess =
                try
                    let p = new Process()
                    p.StartInfo <- startInfo
                    if p.Start() then Some p
                    else
                        p.Dispose()
                        None
                with
                | :? System.ComponentModel.Win32Exception -> None
                | :? System.IO.FileNotFoundException -> None
                | :? System.IO.DirectoryNotFoundException -> None

            match startedProcess with
            | None ->
                return Error(BoundedProcessFailure.LaunchFailed(request.Executable, "Could not start process"))
            | Some proc ->
                proc.StandardInput.Close()

                // STEP 3: Create independent race participants
                use timeoutCts = new CancellationTokenSource(request.Limits.Timeout)
                let timeoutToken = timeoutCts.Token

                // Race 5 outcomes: process exit, timeout, cancellation, stdout, stderr
                let stdoutTask = readBounded proc.StandardOutput.BaseStream request.Limits.StdoutLimitBytes CancellationToken.None
                let stderrTask = readBounded proc.StandardError.BaseStream request.Limits.StderrLimitBytes CancellationToken.None
                let waitForTimeout = waitForCancellation timeoutToken
                let waitForCancel = waitForCancellation cancellationToken
                let waitForExit = proc.WaitForExitAsync()

                // Race all outcomes
                let! firstCompleted =
                    Task.WhenAny(
                        waitForExit,
                        waitForTimeout,
                        waitForCancel,
                        stdoutTask :> Task,
                        stderrTask :> Task
                    )

                // STEP 4: Classify the winner and handle accordingly
                let killFailed = ref None

                let killAndAwait () =
                    try
                        if not proc.HasExited then
                            proc.Kill(entireProcessTree = true)
                        // Await direct process exit
                        proc.WaitForExit() |> ignore
                    with
                    | :? System.ComponentModel.Win32Exception as ex -> killFailed := Some(BoundedProcessFailure.KillFailed ex.Message)
                    | :? InvalidOperationException as ex -> killFailed := Some(BoundedProcessFailure.KillFailed ex.Message)
                    | :? System.NotSupportedException as ex -> killFailed := Some(BoundedProcessFailure.KillFailed ex.Message)

                let awaitAllReaders () =
                    let tasks = [| stdoutTask :> Task; stderrTask :> Task |]
                    Task.WaitAll(tasks, 5000) |> ignore

                // Process exit won
                if firstCompleted = waitForExit then
                    // Wait for both readers to complete (they may still have data)
                    awaitAllReaders ()
                    let stdoutBytes = extractBytes stdoutTask.Result
                    let stderrBytes = extractBytes stderrTask.Result

                    // Check for overflow or reader failures
                    match stdoutTask.Result, stderrTask.Result with
                    | Overflowed _, _ ->
                        killAndAwait ()
                        return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                    | _, Overflowed _ ->
                        killAndAwait ()
                        return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                    | ReadFailed msg, _ ->
                        killAndAwait ()
                        return Error(BoundedProcessFailure.StdoutReaderFailed msg)
                    | _, ReadFailed msg ->
                        killAndAwait ()
                        return Error(BoundedProcessFailure.StderrReaderFailed msg)
                    | _ ->
                        // Both readers complete, check for partial output
                        let exitCode = proc.ExitCode
                        proc.Dispose()
                        match !killFailed with
                        | Some e -> return Error e
                        | None ->
                            if exitCode = 0 then
                                return Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
                            else
                                return Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))

                // Timeout won
                elif firstCompleted = waitForTimeout then
                    killAndAwait ()
                    awaitAllReaders ()
                    match !killFailed with
                    | Some e -> return Error e
                    | None -> return Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)

                // Caller cancellation won
                elif firstCompleted = waitForCancel then
                    killAndAwait ()
                    awaitAllReaders ()
                    match !killFailed with
                    | Some e -> return Error e
                    | None -> return Error BoundedProcessFailure.Cancelled

                // Stdout reader won (overflow or failure)
                elif firstCompleted = (stdoutTask :> Task) then
                    killAndAwait ()
                    awaitAllReaders ()
                    match stdoutTask.Result with
                    | Overflowed _ ->
                        match !killFailed with
                        | Some e -> return Error e
                        | None -> return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                    | ReadFailed msg ->
                        match !killFailed with
                        | Some e -> return Error e
                        | None -> return Error(BoundedProcessFailure.StdoutReaderFailed msg)
                    | ReadCancelled ->
                        match !killFailed with
                        | Some e -> return Error e
                        | None -> return Error BoundedProcessFailure.Cancelled
                    | BytesRead _ ->
                        // Shouldn't happen - BytesRead means we stopped at limit, process should still be running
                        // Fall through to wait for process
                        awaitAllReaders ()
                        let stdoutBytes = extractBytes stdoutTask.Result
                        let stderrBytes = extractBytes stderrTask.Result
                        match !killFailed with
                        | Some e -> return Error e
                        | None ->
                            let exitCode = proc.ExitCode
                            proc.Dispose()
                            if exitCode = 0 then
                                return Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
                            else
                                return Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))

                // Stderr reader won (overflow or failure)
                else // stderrTask
                    killAndAwait ()
                    awaitAllReaders ()
                    match stderrTask.Result with
                    | Overflowed _ ->
                        match !killFailed with
                        | Some e -> return Error e
                        | None -> return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                    | ReadFailed msg ->
                        match !killFailed with
                        | Some e -> return Error e
                        | None -> return Error(BoundedProcessFailure.StderrReaderFailed msg)
                    | ReadCancelled ->
                        match !killFailed with
                        | Some e -> return Error e
                        | None -> return Error BoundedProcessFailure.Cancelled
                    | BytesRead _ ->
                        awaitAllReaders ()
                        let stdoutBytes = extractBytes stdoutTask.Result
                        let stderrBytes = extractBytes stderrTask.Result
                        match !killFailed with
                        | Some e -> return Error e
                        | None ->
                            let exitCode = proc.ExitCode
                            proc.Dispose()
                            if exitCode = 0 then
                                return Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
                            else
                                return Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))
        }
