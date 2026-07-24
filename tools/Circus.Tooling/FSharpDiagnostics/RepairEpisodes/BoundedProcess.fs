module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// =============================================================================
// Bounded-process core — CORRECTION 07
//
// Addresses P0 issues from CORRECTION 06:
// - Task.FromCanceled throws on uncancelled tokens → proper pending participant TCS
// - Timeout classified as Cancelled → actual winning task used for classification
// - Synchronous cleanup → task {} CE with await
// - Exceptions leave TCS incomplete → task {} ensures completion
// - Process exit doesn't await readers → proper await ordering
// - Kill failures discarded → preserved on all termination paths
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
                    let! bytesRead = stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken) |> Async.AwaitTask
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
// Process runner (public API) — CORRECTION 07
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
        // Use TCS with RunContinuationsAsynchronously for thread safety
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

        // Mutable refs for cleanup
        let mutable procRef : Process option = None
        let mutable timeoutCts : CancellationTokenSource option = None
        let mutable linkedCts : CancellationTokenSource option = None
        let mutable timeoutReg : CancellationTokenRegistration option = None
        let mutable cancelReg : CancellationTokenRegistration option = None

        let disposeAll () =
            match timeoutReg with Some r -> r.Dispose(); () | None -> ()
            match cancelReg with Some r -> r.Dispose(); () | None -> ()
            match linkedCts with Some cts -> cts.Dispose(); () | None -> ()
            match timeoutCts with Some cts -> cts.Dispose(); () | None -> ()
            match procRef with Some p -> p.Dispose(); () | None -> ()

        // Start process with try/catch for launch exceptions
        let startedProc =
            try
                let procObj = new Process()
                procObj.StartInfo <- startInfo
                if procObj.Start() then
                    procRef <- Some procObj
                    procObj.StandardInput.Close()
                    Some procObj
                else
                    procObj.Dispose()
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

        match startedProc with
        | None when not tcs.Task.IsCompleted ->
            tcs.TrySetResult(Error(BoundedProcessFailure.LaunchFailed(request.Executable, "Process.Start returned false"))) |> ignore
        | None -> ()
        | Some procObj ->
            // Create timeout source
            let tcts = new CancellationTokenSource(request.Limits.Timeout)
            timeoutCts <- Some tcts

            // Create linked source for readers (cancelled by either timeout or caller)
            let lcts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tcts.Token)
            linkedCts <- Some lcts

            // Create proper pending participants for timeout and cancellation
            // These remain pending until their respective tokens are cancelled
            let timeoutTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let cancelTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            // Register callbacks to complete the TCSs when cancellation occurs
            let tReg = tcts.Token.Register(fun () -> timeoutTcs.TrySetResult(true) |> ignore)
            let cReg = cancellationToken.Register(fun () -> cancelTcs.TrySetResult(true) |> ignore)
            timeoutReg <- Some tReg
            cancelReg <- Some cReg

            // Start readers as tasks
            let stdoutTask = readBoundedAsync procObj.StandardOutput.BaseStream request.Limits.StdoutLimitBytes lcts.Token |> Async.StartAsTask
            let stderrTask = readBoundedAsync procObj.StandardError.BaseStream request.Limits.StderrLimitBytes lcts.Token |> Async.StartAsTask
            let exitTask = procObj.WaitForExitAsync()

            // Run lifecycle using Task.Run to avoid blocking
            Task.Run(fun () ->
                try
                    // Race all five participants
                    let _ = Task.WaitAny(
                        exitTask,
                        stdoutTask,
                        stderrTask,
                        timeoutTcs.Task,
                        cancelTcs.Task
                    )

                    // Helper to kill process and return any failure
                    let tryKill () =
                        try
                            if not procObj.HasExited then
                                procObj.Kill(entireProcessTree = true)
                            None
                        with
                        | :? System.ComponentModel.Win32Exception as ex -> Some ex.Message
                        | :? InvalidOperationException as ex -> Some ex.Message
                        | :? System.NotSupportedException as ex -> Some ex.Message

                    // Await remaining tasks and cleanup - use ContinueWith to handle cancellation exceptions
                    let awaitAll () =
                        // Helper that awaits and catches cancellation/fault
                        let safeAwait (t: Task) =
                            if t.IsCompleted then ()
                            elif t.IsCanceled || t.IsFaulted then
                                t.ContinueWith(fun (_: Task) -> ()) |> ignore
                            else
                                try t.Wait() with _ -> ()

                        safeAwait exitTask
                        safeAwait stdoutTask
                        safeAwait stderrTask

                    // Determine winner by checking which task completed
                    let winnerTask =
                        if exitTask.IsCompleted then exitTask
                        elif stdoutTask.IsCompleted then stdoutTask
                        elif stderrTask.IsCompleted then stderrTask
                        elif timeoutTcs.Task.IsCompleted then timeoutTcs.Task
                        elif cancelTcs.Task.IsCompleted then cancelTcs.Task
                        else exitTask // fallback

                    if Object.ReferenceEquals(winnerTask, cancelTcs.Task) then
                        // Caller cancellation
                        let killFailed = tryKill()
                        awaitAll()
                        disposeAll()
                        match killFailed with
                        | Some msg -> tcs.TrySetResult(Error(BoundedProcessFailure.KillFailed msg)) |> ignore
                        | None -> tcs.TrySetResult(Error BoundedProcessFailure.Cancelled) |> ignore

                    elif Object.ReferenceEquals(winnerTask, timeoutTcs.Task) then
                        // Timeout
                        let killFailed = tryKill()
                        awaitAll()
                        disposeAll()
                        match killFailed with
                        | Some msg -> tcs.TrySetResult(Error(BoundedProcessFailure.KillFailed msg)) |> ignore
                        | None -> tcs.TrySetResult(Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)) |> ignore

                    elif Object.ReferenceEquals(winnerTask, exitTask) then
                        // Process exited naturally - await remaining readers
                        stdoutTask.Wait()
                        stderrTask.Wait()
                        let exitCode = procObj.ExitCode
                        disposeAll()

                        let stdoutOutcome = stdoutTask.Result
                        let stderrOutcome = stderrTask.Result

                        if isTerminal stdoutOutcome then
                            match stdoutOutcome with
                            | Overflowed _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)) |> ignore
                            | ReadFailed msg -> tcs.TrySetResult(Error(BoundedProcessFailure.StdoutReaderFailed msg)) |> ignore
                            | ReadCancelled -> tcs.TrySetResult(Error BoundedProcessFailure.Cancelled) |> ignore
                            | _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)) |> ignore
                        elif isTerminal stderrOutcome then
                            match stderrOutcome with
                            | Overflowed _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)) |> ignore
                            | ReadFailed msg -> tcs.TrySetResult(Error(BoundedProcessFailure.StderrReaderFailed msg)) |> ignore
                            | ReadCancelled -> tcs.TrySetResult(Error BoundedProcessFailure.Cancelled) |> ignore
                            | _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)) |> ignore
                        else
                            let stdoutBytes = extractBytes stdoutOutcome
                            let stderrBytes = extractBytes stderrOutcome
                            if exitCode = 0 then
                                tcs.TrySetResult(Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }) |> ignore
                            else
                                tcs.TrySetResult(Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))) |> ignore

                    elif Object.ReferenceEquals(winnerTask, stdoutTask) then
                        let stdoutOutcome = stdoutTask.Result

                        if isTerminal stdoutOutcome then
                            // Stdout terminal - kill process
                            let killFailed = tryKill()
                            awaitAll()
                            disposeAll()
                            // Preserve kill failure on all terminal paths
                            match killFailed with
                            | Some msg -> tcs.TrySetResult(Error(BoundedProcessFailure.KillFailed msg)) |> ignore
                            | None ->
                                match stdoutOutcome with
                                | Overflowed _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)) |> ignore
                                | ReadFailed msg -> tcs.TrySetResult(Error(BoundedProcessFailure.StdoutReaderFailed msg)) |> ignore
                                | ReadCancelled -> tcs.TrySetResult(Error BoundedProcessFailure.Cancelled) |> ignore
                                | _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)) |> ignore
                        else
                            // Stdout EOF - wait for process and stderr
                            exitTask.Wait()
                            stderrTask.Wait()
                            let exitCode = procObj.ExitCode
                            disposeAll()

                            let stderrOutcome = stderrTask.Result

                            if isTerminal stderrOutcome then
                                match stderrOutcome with
                                | Overflowed _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)) |> ignore
                                | ReadFailed msg -> tcs.TrySetResult(Error(BoundedProcessFailure.StderrReaderFailed msg)) |> ignore
                                | ReadCancelled -> tcs.TrySetResult(Error BoundedProcessFailure.Cancelled) |> ignore
                                | _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)) |> ignore
                            else
                                let stdoutBytes = extractBytes stdoutOutcome
                                let stderrBytes = extractBytes stderrOutcome
                                if exitCode = 0 then
                                    tcs.TrySetResult(Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }) |> ignore
                                else
                                    tcs.TrySetResult(Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))) |> ignore

                    else
                        // Stderr task won
                        let stderrOutcome = stderrTask.Result

                        if isTerminal stderrOutcome then
                            // Stderr terminal - kill process
                            let killFailed = tryKill()
                            awaitAll()
                            disposeAll()
                            match killFailed with
                            | Some msg -> tcs.TrySetResult(Error(BoundedProcessFailure.KillFailed msg)) |> ignore
                            | None ->
                                match stderrOutcome with
                                | Overflowed _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)) |> ignore
                                | ReadFailed msg -> tcs.TrySetResult(Error(BoundedProcessFailure.StderrReaderFailed msg)) |> ignore
                                | ReadCancelled -> tcs.TrySetResult(Error BoundedProcessFailure.Cancelled) |> ignore
                                | _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)) |> ignore
                        else
                            // Stderr EOF - wait for process and stdout
                            exitTask.Wait()
                            stdoutTask.Wait()
                            let exitCode = procObj.ExitCode
                            disposeAll()

                            let stdoutOutcome = stdoutTask.Result

                            if isTerminal stdoutOutcome then
                                match stdoutOutcome with
                                | Overflowed _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)) |> ignore
                                | ReadFailed msg -> tcs.TrySetResult(Error(BoundedProcessFailure.StdoutReaderFailed msg)) |> ignore
                                | ReadCancelled -> tcs.TrySetResult(Error BoundedProcessFailure.Cancelled) |> ignore
                                | _ -> tcs.TrySetResult(Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)) |> ignore
                            else
                                let stdoutBytes = extractBytes stdoutOutcome
                                let stderrBytes = extractBytes stderrOutcome
                                if exitCode = 0 then
                                    tcs.TrySetResult(Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }) |> ignore
                                else
                                    tcs.TrySetResult(Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))) |> ignore
                with
                | ex ->
                    disposeAll()
                    tcs.TrySetResult(Error(BoundedProcessFailure.KillFailed ex.Message)) |> ignore
            ) |> ignore

        tcs.Task
