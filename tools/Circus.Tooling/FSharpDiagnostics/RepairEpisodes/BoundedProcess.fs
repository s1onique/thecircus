module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// =============================================================================
// Bounded-process core — CORRECTION 06
//
// Addresses P0 issues:
// - Cancellation sources disposed after task completion
// - Caller cancellation as race participant
// - Overflow kills process immediately
// - Async stream reading with ReadAsync
// - Async cleanup with await
// - ExitCode captured before disposal
// - All paths complete the TCS
// - RunContinuationsAsynchronously
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
// Read outcome
// -----------------------------------------------------------------------------

type ReadOutcome =
    | EofReached of bytes: byte array
    | Overflowed of bytes: byte array
    | ReadFailed of detail: string
    | ReadCancelled

let private extractBytes (outcome: ReadOutcome) : byte array =
    match outcome with
    | EofReached b -> b
    | Overflowed b -> b
    | _ -> [||]

let private isTerminal (outcome: ReadOutcome) : bool =
    match outcome with
    | EofReached _ -> false
    | _ -> true

// -----------------------------------------------------------------------------
// Async bounded byte reader using ReadAsync
// -----------------------------------------------------------------------------

let private readBoundedAsync
    (stream: Stream)
    (limitBytes: int)
    (cancellationToken: CancellationToken)
    : Async<ReadOutcome> =
    async {
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
                    let! bytesRead = Async.AwaitTask(stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken))
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

        if cancellationToken.IsCancellationRequested then
            if int64 collected.Count > int64 limitBytes then
                return Overflowed(collected.ToArray())
            else
                return ReadCancelled
        else
            match readError with
            | Some msg -> return ReadFailed msg
            | None ->
                if int64 collected.Count > int64 limitBytes then
                    return Overflowed(collected.ToArray())
                else
                    return EofReached(collected.ToArray())
    }

// -----------------------------------------------------------------------------
// Process runner (public API)
// -----------------------------------------------------------------------------

let run
    (request: BoundedProcessRequest)
    (cancellationToken: CancellationToken)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =

    match validateRequest request with
    | Some e -> Task.FromResult(Error e)
    | None when cancellationToken.IsCancellationRequested ->
        Task.FromResult(Error BoundedProcessFailure.Cancelled)
    | None ->
        let tcs = TaskCompletionSource<Result<BoundedProcessSuccess, BoundedProcessFailure>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        )

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
                tcs.TrySetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))) |> ignore
                None
            | :? System.IO.FileNotFoundException as ex ->
                tcs.TrySetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))) |> ignore
                None
            | :? System.IO.DirectoryNotFoundException as ex ->
                tcs.TrySetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))) |> ignore
                None

        match procOpt with
        | None when not tcs.Task.IsCompleted ->
            tcs.TrySetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, "Process.Start returned false"))) |> ignore
        | None -> ()
        | Some proc ->
            try
                proc.StandardInput.Close()

                let timeoutCts = new CancellationTokenSource(request.Limits.Timeout)
                let linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
                let linkedToken = linkedCts.Token

                // Start async readers as tasks
                let stdoutAsync = readBoundedAsync proc.StandardOutput.BaseStream request.Limits.StdoutLimitBytes linkedToken
                let stderrAsync = readBoundedAsync proc.StandardError.BaseStream request.Limits.StderrLimitBytes linkedToken

                let stdoutTask = Async.StartAsTask(stdoutAsync)
                let stderrTask = Async.StartAsTask(stderrAsync)

                // Race participants
                let processExitTask = proc.WaitForExitAsync()
                let timeoutTask = Task.Delay(request.Limits.Timeout)
                let cancellationTask = Task.FromCanceled(cancellationToken)

                // Race all participants
                let winnerTask = Task.WhenAny(
                    processExitTask,
                    stdoutTask,
                    stderrTask,
                    timeoutTask,
                    cancellationTask
                )

                // Continue with result handling
                winnerTask.ContinueWith(fun (_: Task<Task>) ->
                    let winner = winnerTask.Result
                    let tryKill () =
                        try
                            if not proc.HasExited then
                                proc.Kill(entireProcessTree = true)
                            None
                        with
                        | :? System.ComponentModel.Win32Exception as ex -> Some ex.Message
                        | :? InvalidOperationException as ex -> Some ex.Message
                        | :? System.NotSupportedException as ex -> Some ex.Message

                    let completeWith result =
                        try
                            timeoutCts.Dispose()
                            linkedCts.Dispose()
                            proc.Dispose()
                        with _ -> ()
                        tcs.TrySetResult(result) |> ignore

                    if Object.ReferenceEquals(winner, cancellationTask) then
                        let killFailed = tryKill()
                        Task.WaitAll(processExitTask, stdoutTask, stderrTask)
                        match killFailed with
                        | Some msg -> completeWith (Error(BoundedProcessFailure.KillFailed msg))
                        | None -> completeWith (Error BoundedProcessFailure.Cancelled)

                    elif Object.ReferenceEquals(winner, timeoutTask) then
                        let killFailed = tryKill()
                        Task.WaitAll(processExitTask, stdoutTask, stderrTask)
                        match killFailed with
                        | Some msg -> completeWith (Error(BoundedProcessFailure.KillFailed msg))
                        | None -> completeWith (Error(BoundedProcessFailure.TimedOut request.Limits.Timeout))

                    elif Object.ReferenceEquals(winner, processExitTask) then
                        let stdoutOutcome = stdoutTask.Result
                        let stderrOutcome = stderrTask.Result
                        let exitCode = proc.ExitCode
                        timeoutCts.Dispose()
                        linkedCts.Dispose()
                        proc.Dispose()

                        if isTerminal stdoutOutcome then
                            match stdoutOutcome with
                            | Overflowed _ -> completeWith (Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes))
                            | ReadFailed msg -> completeWith (Error(BoundedProcessFailure.StdoutReaderFailed msg))
                            | ReadCancelled -> completeWith (Error BoundedProcessFailure.Cancelled)
                            | _ -> completeWith (Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes))
                        elif isTerminal stderrOutcome then
                            match stderrOutcome with
                            | Overflowed _ -> completeWith (Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes))
                            | ReadFailed msg -> completeWith (Error(BoundedProcessFailure.StderrReaderFailed msg))
                            | ReadCancelled -> completeWith (Error BoundedProcessFailure.Cancelled)
                            | _ -> completeWith (Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes))
                        else
                            let stdoutBytes = extractBytes stdoutOutcome
                            let stderrBytes = extractBytes stderrOutcome
                            if exitCode = 0 then
                                completeWith (Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes })
                            else
                                completeWith (Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes)))

                    elif Object.ReferenceEquals(winner, stdoutTask) then
                        let stdoutOutcome = stdoutTask.Result

                        if isTerminal stdoutOutcome then
                            let killFailed = tryKill()
                            Task.WaitAll(processExitTask, stderrTask)
                            timeoutCts.Dispose()
                            linkedCts.Dispose()
                            proc.Dispose()
                            match stdoutOutcome with
                            | Overflowed _ -> completeWith (Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes))
                            | ReadFailed msg -> completeWith (Error(BoundedProcessFailure.StdoutReaderFailed msg))
                            | ReadCancelled ->
                                match killFailed with
                                | Some msg -> completeWith (Error(BoundedProcessFailure.KillFailed msg))
                                | None -> completeWith (Error BoundedProcessFailure.Cancelled)
                            | _ -> completeWith (Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes))
                        else
                            processExitTask.Wait()
                            let stderrOutcome = stderrTask.Result
                            let exitCode = proc.ExitCode
                            timeoutCts.Dispose()
                            linkedCts.Dispose()
                            proc.Dispose()

                            if isTerminal stderrOutcome then
                                match stderrOutcome with
                                | Overflowed _ -> completeWith (Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes))
                                | ReadFailed msg -> completeWith (Error(BoundedProcessFailure.StderrReaderFailed msg))
                                | ReadCancelled -> completeWith (Error BoundedProcessFailure.Cancelled)
                                | _ -> completeWith (Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes))
                            else
                                let stdoutBytes = extractBytes stdoutOutcome
                                let stderrBytes = extractBytes stderrOutcome
                                if exitCode = 0 then
                                    completeWith (Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes })
                                else
                                    completeWith (Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes)))

                    else
                        let stderrOutcome = stderrTask.Result

                        if isTerminal stderrOutcome then
                            let killFailed = tryKill()
                            Task.WaitAll(processExitTask, stdoutTask)
                            timeoutCts.Dispose()
                            linkedCts.Dispose()
                            proc.Dispose()
                            match stderrOutcome with
                            | Overflowed _ -> completeWith (Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes))
                            | ReadFailed msg -> completeWith (Error(BoundedProcessFailure.StderrReaderFailed msg))
                            | ReadCancelled ->
                                match killFailed with
                                | Some msg -> completeWith (Error(BoundedProcessFailure.KillFailed msg))
                                | None -> completeWith (Error BoundedProcessFailure.Cancelled)
                            | _ -> completeWith (Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes))
                        else
                            processExitTask.Wait()
                            let stdoutOutcome = stdoutTask.Result
                            let exitCode = proc.ExitCode
                            timeoutCts.Dispose()
                            linkedCts.Dispose()
                            proc.Dispose()

                            if isTerminal stdoutOutcome then
                                match stdoutOutcome with
                                | Overflowed _ -> completeWith (Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes))
                                | ReadFailed msg -> completeWith (Error(BoundedProcessFailure.StdoutReaderFailed msg))
                                | ReadCancelled -> completeWith (Error BoundedProcessFailure.Cancelled)
                                | _ -> completeWith (Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes))
                            else
                                let stdoutBytes = extractBytes stdoutOutcome
                                let stderrBytes = extractBytes stderrOutcome
                                if exitCode = 0 then
                                    completeWith (Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes })
                                else
                                    completeWith (Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes)))
                ) |> ignore

            with
            | ex ->
                tcs.TrySetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))) |> ignore

        tcs.Task
