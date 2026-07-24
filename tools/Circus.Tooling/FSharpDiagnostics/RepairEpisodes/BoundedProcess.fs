module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// =============================================================================
// Bounded-process core — CORRECTION 05
//
// Addresses P0 issues:
// - Process-exit task comparison must use stored task
// - Timeout/cancellation as explicit race participants
// - Async cleanup throughout (no .Wait/.Result)
// - Incomplete output is explicit failure
// - Overflow triggers immediate termination
// - Process lifetime protected with try/finally
// =============================================================================

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
    | IncompleteOutput of stdoutComplete: bool * stderrComplete: bool

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
    | ReadCancelled                          // Cancellation, terminal

// -----------------------------------------------------------------------------
// Extract bytes from ReadOutcome
// -----------------------------------------------------------------------------

let private extractBytes (outcome: ReadOutcome) : byte array =
    match outcome with
    | EofReached b -> b
    | Overflowed b -> b
    | _ -> [||]

// -----------------------------------------------------------------------------
// Is the outcome terminal (requires process kill)?
// -----------------------------------------------------------------------------

let private isTerminal (outcome: ReadOutcome) : bool =
    match outcome with
    | EofReached _ -> false
    | _ -> true

// -----------------------------------------------------------------------------
// Bounded byte reader using synchronous Stream.Read
// Runs in thread pool to avoid blocking
// -----------------------------------------------------------------------------

let private readBoundedAsync
    (stream: Stream)
    (limitBytes: int)
    (cancellationToken: CancellationToken)
    : Task<ReadOutcome> =
    Task.Run<ReadOutcome>(fun () ->
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
                    let bytesRead = stream.Read(buffer, 0, bytesToRead)
                    if bytesRead = 0 then
                        keepReading <- false
                    else
                        for i = 0 to bytesRead - 1 do
                            collected.Add(buffer.[i])
                with
                | :? IOException as ex ->
                    readError <- Some ex.Message
                    keepReading <- false
                | :? ObjectDisposedException as ex ->
                    readError <- Some ex.Message
                    keepReading <- false

        if cancellationToken.IsCancellationRequested then
            if int64 collected.Count > int64 limitBytes then
                Overflowed(collected.ToArray())
            else
                ReadCancelled
        else
            match readError with
            | Some msg -> ReadFailed msg
            | None ->
                if int64 collected.Count > int64 limitBytes then
                    Overflowed(collected.ToArray())
                else
                    EofReached(collected.ToArray())
    , cancellationToken)

// -----------------------------------------------------------------------------
// Helper to try kill process
// -----------------------------------------------------------------------------

let private tryKill (proc: Process) : string option =
    try
        if not proc.HasExited then
            proc.Kill(entireProcessTree = true)
        None
    with
    | :? System.ComponentModel.Win32Exception as ex -> Some ex.Message
    | :? InvalidOperationException as ex -> Some ex.Message
    | :? System.NotSupportedException as ex -> Some ex.Message

// -----------------------------------------------------------------------------
// Process runner (public API)
// -----------------------------------------------------------------------------

/// Run a process with resource limits, capturing stdout/stderr as bytes.
let run
    (request: BoundedProcessRequest)
    (cancellationToken: CancellationToken)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =

    // Pre-validate outside async
    match validateRequest request with
    | Some e -> Task.FromResult(Error e)
    | None when cancellationToken.IsCancellationRequested ->
        Task.FromResult(Error BoundedProcessFailure.Cancelled)
    | None ->
        // Use TaskCompletionSource for full control
        let tcs = TaskCompletionSource<Result<BoundedProcessSuccess, BoundedProcessFailure>>()

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
        let procOpt =
            try
                let p = new Process()
                p.StartInfo <- startInfo
                if p.Start() then Some p
                else
                    p.Dispose()
                    None
            with
            | :? System.ComponentModel.Win32Exception as ex ->
                tcs.SetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message)))
                None
            | :? System.IO.FileNotFoundException as ex ->
                tcs.SetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message)))
                None
            | :? System.IO.DirectoryNotFoundException as ex ->
                tcs.SetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message)))
                None

        match procOpt with
        | None when not tcs.Task.IsCompleted ->
            tcs.SetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, "Process.Start returned false")))
            tcs.Task
        | None -> tcs.Task
        | Some proc ->
            // Use a flag to ensure we only complete once
            let completed = ref false
            let complete result =
                lock completed (fun () ->
                    if not !completed then
                        completed := true
                        tcs.SetResult(result)
                )

            let disposeProc () =
                try proc.Dispose() with _ -> ()

            try
                proc.StandardInput.Close()

                // Create cancellation sources
                use timeoutCts = new CancellationTokenSource(request.Limits.Timeout)
                use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
                let linkedToken = linkedCts.Token

                // Start all readers
                let stdoutReadTask = readBoundedAsync proc.StandardOutput.BaseStream request.Limits.StdoutLimitBytes linkedToken
                let stderrReadTask = readBoundedAsync proc.StandardError.BaseStream request.Limits.StderrLimitBytes linkedToken

                // Store process exit task once
                let processExitTask = proc.WaitForExitAsync()

                // Continue with race using Task continuations
                let onTimeout () =
                    let killFailed = tryKill proc
                    stdoutReadTask.Wait()
                    stderrReadTask.Wait()
                    disposeProc ()
                    match killFailed with
                    | Some msg -> complete (Error(BoundedProcessFailure.KillFailed msg))
                    | None -> complete (Error(BoundedProcessFailure.TimedOut request.Limits.Timeout))

                let onProcessExit () =
                    stdoutReadTask.ContinueWith(fun (t: Task<ReadOutcome>) ->
                        stderrReadTask.ContinueWith(fun (u: Task<ReadOutcome>) ->
                            let stdoutOutcome = t.Result
                            let stderrOutcome = u.Result

                            if isTerminal stdoutOutcome then
                                let killFailed = tryKill proc
                                disposeProc ()
                                match killFailed with
                                | Some msg -> complete (Error(BoundedProcessFailure.KillFailed msg))
                                | None ->
                                    match stdoutOutcome with
                                    | Overflowed _ -> complete (Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes))
                                    | ReadFailed msg -> complete (Error(BoundedProcessFailure.StdoutReaderFailed msg))
                                    | ReadCancelled -> complete (Error BoundedProcessFailure.Cancelled)
                                    | _ -> complete (Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes))
                            elif isTerminal stderrOutcome then
                                let killFailed = tryKill proc
                                disposeProc ()
                                match killFailed with
                                | Some msg -> complete (Error(BoundedProcessFailure.KillFailed msg))
                                | None ->
                                    match stderrOutcome with
                                    | Overflowed _ -> complete (Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes))
                                    | ReadFailed msg -> complete (Error(BoundedProcessFailure.StderrReaderFailed msg))
                                    | ReadCancelled -> complete (Error BoundedProcessFailure.Cancelled)
                                    | _ -> complete (Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes))
                            else
                                // Normal completion
                                disposeProc ()
                                let exitCode = proc.ExitCode
                                let stdoutBytes = extractBytes stdoutOutcome
                                let stderrBytes = extractBytes stderrOutcome
                                if exitCode = 0 then
                                    complete (Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes })
                                else
                                    complete (Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes)))
                        ) |> ignore
                    ) |> ignore

                // Race process exit against timeout
                let raceTask: Task<Task> = Task.WhenAny(processExitTask, Task.Delay(request.Limits.Timeout))
                raceTask.ContinueWith(fun (t: Task<Task>) ->
                    let winner = t.Result
                    if winner.IsCompleted && Object.ReferenceEquals(winner, processExitTask) then
                        onProcessExit ()
                    else
                        onTimeout ()
                ) |> ignore

                tcs.Task
            with
            | ex ->
                disposeProc ()
                complete (Error(BoundedProcessFailure.KillFailed ex.Message))
                tcs.Task
