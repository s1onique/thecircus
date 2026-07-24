module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

#nowarn "3511"

// =============================================================================
// Bounded-process core — CORRECTION 11
//
// Loop-termination and authoritative-cause follow-up to CORRECTION 10.
//
// P0 fixes from CORRECTION10 expert review:
// - Event loop exits immediately on any authoritative terminal cause
//   (no waiting for HasExited, no spin on kill failure)
// - Observed participants are removed from pending race set
// - Explicit process-exit state (Success/Failed/Cancelled) makes WaitFailed
//   reachable for faulted or cancelled exit tasks
// - ReadCancelled is non-authoritative: only sets cause if it's still NaturalExit
//   AND no timeout/cancel has been observed (otherwise loop continues until
//   timeout/cancel is observed)
// - Exit-task detail captured during racing (not just cleanup)
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

type TerminalCause =
    | NaturalExit
    | TimeoutFire
    | CallerCancel
    | StdoutTerminal
    | StderrTerminal
    | ExitWaitFailed

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
    | WaitFailed of detail: string
    | KillFailed of detail: string
    | IncompleteOutput of stdoutComplete: bool * stderrComplete: bool
    | TerminationCleanupFailed of
        cause: TerminalCause *
        killDetail: string option *
        processExited: bool *
        stdoutComplete: bool *
        stderrComplete: bool *
        waitDetail: string option

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

let private isEof (outcome: ReadOutcome) : bool =
    match outcome with
    | EofReached _ -> true
    | _ -> false

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
// Try read a task result without throwing - preserves fault information
// -----------------------------------------------------------------------------

let private tryReadOutcome (t: Task<ReadOutcome>) : ReadOutcome =
    if t.IsCompletedSuccessfully then
        t.Result
    elif t.IsCanceled then
        ReadCancelled
    elif t.IsFaulted then
        ReadFailed(t.Exception.GetBaseException().Message)
    else
        ReadCancelled

// -----------------------------------------------------------------------------
// Launch helper - returns the started Process or a failure result
// -----------------------------------------------------------------------------

let private launchProcess (request: BoundedProcessRequest) : Result<Process, BoundedProcessFailure> =
    let procObj = new Process()
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
    procObj.StartInfo <- startInfo
    try
        if procObj.Start() then
            Ok procObj
        else
            procObj.Dispose()
            Error(BoundedProcessFailure.LaunchFailed(request.Executable, "Process.Start returned false"))
    with
    | :? System.ComponentModel.Win32Exception as ex ->
        procObj.Dispose()
        Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))
    | :? System.IO.FileNotFoundException as ex ->
        procObj.Dispose()
        Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))
    | :? System.IO.DirectoryNotFoundException as ex ->
        procObj.Dispose()
        Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))

// -----------------------------------------------------------------------------
// Mutable cell helper for mutable state inside task CE
// -----------------------------------------------------------------------------

type MutableCell<'T> =
    { mutable Value: 'T }

// -----------------------------------------------------------------------------
// Async grace-period race helper - no Task.Wait
// -----------------------------------------------------------------------------

let private raceWithDelay (t: Task) (grace: TimeSpan) : Task<Task> =
    Task.WhenAny(t, Task.Delay(grace))

// -----------------------------------------------------------------------------
// Capture detail from exit task - never silently swallows fault/cancel
// -----------------------------------------------------------------------------

let private captureExitDetail (exitTask: Task) (waitDetailCell: MutableCell<string option>) (exitCodeCell: MutableCell<int option>) (procObj: Process) : unit =
    if exitTask.IsCompletedSuccessfully then
        try
            exitCodeCell.Value <- Some(procObj.ExitCode)
        with
        | ex ->
            waitDetailCell.Value <- Some ex.Message
    elif exitTask.IsFaulted then
        waitDetailCell.Value <- Some(exitTask.Exception.GetBaseException().Message)
    elif exitTask.IsCanceled then
        waitDetailCell.Value <- Some "process exit wait cancelled"

// -----------------------------------------------------------------------------
// Inner event loop body — CORRECTION 11
// -----------------------------------------------------------------------------

let private executeLifecycle
    (procObj: Process)
    (lcts: CancellationTokenSource)
    (request: BoundedProcessRequest)
    (timeoutTcs: TaskCompletionSource<bool>)
    (cancelTcs: TaskCompletionSource<bool>)
    (stdoutTask: Task<ReadOutcome>)
    (stderrTask: Task<ReadOutcome>)
    (exitTask: Task)
    (tReg: CancellationTokenRegistration)
    (cReg: CancellationTokenRegistration)
    (tcts: CancellationTokenSource)
    : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =

    task {
        // Mutable state
        let stdoutOutcomeCell = { Value = None }
        let stderrOutcomeCell = { Value = None }
        let exitCodeCell = { Value = None }
        let waitDetailCell = { Value = None }
        let killErrorCell = { Value = None }
        let terminalCauseCell = { Value = NaturalExit }
        let mutable killRequested = false
        let mutable timeoutObserved = false
        let mutable cancellationObserved = false
        let mutable exitObserved = false

        let killNow () =
            if not killRequested then
                killRequested <- true
                try
                    if not procObj.HasExited then
                        procObj.Kill(entireProcessTree = true)
                with
                | :? System.ComponentModel.Win32Exception as ex ->
                    killErrorCell.Value <- Some ex.Message
                | :? InvalidOperationException as ex ->
                    killErrorCell.Value <- Some ex.Message
                | :? System.NotSupportedException as ex ->
                    killErrorCell.Value <- Some ex.Message

        // Event loop - exits immediately on terminal cause, no waiting for HasExited
        let mutable loopDone = false
        while not loopDone do
            let stdoutOutcome = stdoutOutcomeCell.Value
            let stderrOutcome = stderrOutcomeCell.Value
            let exitCode = exitCodeCell.Value

            let mutable pending = ResizeArray<Task>()
            if stdoutOutcome.IsNone then pending.Add(stdoutTask)
            if stderrOutcome.IsNone then pending.Add(stderrTask)
            if not exitObserved then pending.Add(exitTask)
            if not timeoutObserved then pending.Add(timeoutTcs.Task)
            if not cancellationObserved then pending.Add(cancelTcs.Task)

            let! winner = Task.WhenAny(pending.ToArray())

            if not timeoutObserved && Object.ReferenceEquals(winner, timeoutTcs.Task) then
                timeoutObserved <- true
                terminalCauseCell.Value <- TimeoutFire
                killNow()
            elif not cancellationObserved && Object.ReferenceEquals(winner, cancelTcs.Task) then
                cancellationObserved <- true
                terminalCauseCell.Value <- CallerCancel
                killNow()
            elif not exitObserved && Object.ReferenceEquals(winner, exitTask) then
                exitObserved <- true
                captureExitDetail exitTask waitDetailCell exitCodeCell procObj
            elif stdoutOutcome.IsNone && Object.ReferenceEquals(winner, stdoutTask) then
                let outcome = tryReadOutcome stdoutTask
                stdoutOutcomeCell.Value <- Some outcome
                if isTerminal outcome then
                    // ReadCancelled is non-authoritative - only set cause if
                    // no timeout/cancel has been observed AND no exit failure
                    let isAuthTimeoutOrCancel = timeoutObserved || cancellationObserved
                    if not isAuthTimeoutOrCancel && terminalCauseCell.Value = NaturalExit then
                        match outcome with
                        | ReadCancelled ->
                            // Don't set cause; record reader stop but continue
                            ()
                        | _ ->
                            terminalCauseCell.Value <- StdoutTerminal
                    killNow()
            elif stderrOutcome.IsNone && Object.ReferenceEquals(winner, stderrTask) then
                let outcome = tryReadOutcome stderrTask
                stderrOutcomeCell.Value <- Some outcome
                if isTerminal outcome then
                    let isAuthTimeoutOrCancel = timeoutObserved || cancellationObserved
                    if not isAuthTimeoutOrCancel && terminalCauseCell.Value = NaturalExit then
                        match outcome with
                        | ReadCancelled ->
                            ()
                        | _ ->
                            terminalCauseCell.Value <- StderrTerminal
                    killNow()

            // Decide whether to exit the loop
            let sOut = stdoutOutcomeCell.Value
            let sErr = stderrOutcomeCell.Value
            let eC = exitCodeCell.Value
            let stdoutTerminal = sOut.IsSome && isTerminal(sOut.Value)
            let stderrTerminal = sErr.IsSome && isTerminal(sErr.Value)
            let hasAuthoritativeCause =
                timeoutObserved || cancellationObserved
                || terminalCauseCell.Value = StdoutTerminal
                || terminalCauseCell.Value = StderrTerminal
                || terminalCauseCell.Value = ExitWaitFailed
                || (waitDetailCell.Value.IsSome && not exitObserved)
            // Natural completion: exit code captured and both readers EOF with no terminal cause
            let naturalComplete =
                eC.IsSome
                && sOut.IsSome && isEof(sOut.Value)
                && sErr.IsSome && isEof(sErr.Value)
                && terminalCauseCell.Value = NaturalExit
            // Exit immediately when authoritative cause is observed OR natural completion
            loopDone <- hasAuthoritativeCause || naturalComplete

        // ---- Terminal cleanup: async grace races ----
        try lcts.Cancel() with | _ -> ()

        // Race: process exit or 5s grace
        let exitRace = raceWithDelay exitTask (TimeSpan.FromSeconds(5.0))
        let! exitWinner = exitRace
        let processExited =
            Object.ReferenceEquals(exitWinner, exitTask) && exitTask.IsCompletedSuccessfully
        // Always try to capture exit detail after the race - may catch late faults
        if not exitObserved then
            captureExitDetail exitTask waitDetailCell exitCodeCell procObj
        else if exitCodeCell.Value.IsNone && processExited then
            captureExitDetail exitTask waitDetailCell exitCodeCell procObj
        // Also inspect post-race state for late faults/cancels
        if exitTask.IsFaulted && waitDetailCell.Value.IsNone then
            waitDetailCell.Value <- Some(exitTask.Exception.GetBaseException().Message)
        elif exitTask.IsCanceled && waitDetailCell.Value.IsNone then
            waitDetailCell.Value <- Some "process exit wait cancelled"

        // Race: stdout reader or 2s grace
        let stdoutRace = raceWithDelay stdoutTask (TimeSpan.FromSeconds(2.0))
        let! stdoutWinner = stdoutRace
        let stdoutComplete =
            Object.ReferenceEquals(stdoutWinner, stdoutTask) && stdoutTask.IsCompleted

        // Race: stderr reader or 2s grace
        let stderrRace = raceWithDelay stderrTask (TimeSpan.FromSeconds(2.0))
        let! stderrWinner = stderrRace
        let stderrComplete =
            Object.ReferenceEquals(stderrWinner, stderrTask) && stderrTask.IsCompleted

        // Capture any missing reader outcomes
        if stdoutOutcomeCell.Value.IsNone then
            stdoutOutcomeCell.Value <- Some(tryReadOutcome stdoutTask)
        if stderrOutcomeCell.Value.IsNone then
            stderrOutcomeCell.Value <- Some(tryReadOutcome stderrTask)

        // Attach continuations to observe late task faults during finalization
        let faultObserver (t: Task) =
            if t.IsFaulted then
                ignore t.Exception
            else
                ignore ()

        stdoutTask.ContinueWith(Action<Task<ReadOutcome>>(fun _ -> faultObserver stdoutTask)) |> ignore
        stderrTask.ContinueWith(Action<Task<ReadOutcome>>(fun _ -> faultObserver stderrTask)) |> ignore
        exitTask.ContinueWith(Action<Task>(fun _ -> faultObserver exitTask)) |> ignore

        // Dispose after continuations attached (so late faults get observed)
        tReg.Dispose()
        cReg.Dispose()
        lcts.Dispose()
        tcts.Dispose()
        try procObj.Dispose() with | _ -> ()

        // ---- Classify ----
        let cause = terminalCauseCell.Value
        let killDetail = killErrorCell.Value
        let waitDetail = waitDetailCell.Value
        let cleanupIncomplete = not stdoutComplete || not stderrComplete

        // Determine if exit wait failed
        let exitWaitFailed = waitDetail.IsSome && exitCodeCell.Value.IsNone

        match cause with
        | TimeoutFire ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(BoundedProcessFailure.TerminationCleanupFailed(cause, killDetail, processExited, stdoutComplete, stderrComplete, waitDetail))
            else
                return Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)
        | CallerCancel ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(BoundedProcessFailure.TerminationCleanupFailed(cause, killDetail, processExited, stdoutComplete, stderrComplete, waitDetail))
            else
                return Error BoundedProcessFailure.Cancelled
        | StdoutTerminal ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(BoundedProcessFailure.TerminationCleanupFailed(cause, killDetail, processExited, stdoutComplete, stderrComplete, waitDetail))
            elif exitWaitFailed then
                return Error(BoundedProcessFailure.WaitFailed waitDetail.Value)
            else
                let stdoutFinal = stdoutOutcomeCell.Value.Value
                let stderrFinal = stderrOutcomeCell.Value.Value
                let code = exitCodeCell.Value.Value
                match stdoutFinal with
                | Overflowed _ -> return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                | ReadFailed msg -> return Error(BoundedProcessFailure.StdoutReaderFailed msg)
                | ReadCancelled -> return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                | EofReached b ->
                    match stderrFinal with
                    | Overflowed _ -> return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                    | ReadFailed msg -> return Error(BoundedProcessFailure.StderrReaderFailed msg)
                    | ReadCancelled -> return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                    | EofReached eb ->
                        if code = 0 then
                            return Ok { ExitCode = code; Stdout = b; Stderr = eb }
                        else
                            return Error(BoundedProcessFailure.NonZeroExit(code, b, eb))
        | StderrTerminal ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(BoundedProcessFailure.TerminationCleanupFailed(cause, killDetail, processExited, stdoutComplete, stderrComplete, waitDetail))
            elif exitWaitFailed then
                return Error(BoundedProcessFailure.WaitFailed waitDetail.Value)
            else
                let stdoutFinal = stdoutOutcomeCell.Value.Value
                let stderrFinal = stderrOutcomeCell.Value.Value
                let code = exitCodeCell.Value.Value
                match stderrFinal with
                | Overflowed _ -> return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                | ReadFailed msg -> return Error(BoundedProcessFailure.StderrReaderFailed msg)
                | ReadCancelled -> return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                | EofReached eb ->
                    match stdoutFinal with
                    | Overflowed _ -> return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                    | ReadFailed msg -> return Error(BoundedProcessFailure.StdoutReaderFailed msg)
                    | ReadCancelled -> return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                    | EofReached b ->
                        if code = 0 then
                            return Ok { ExitCode = code; Stdout = b; Stderr = eb }
                        else
                            return Error(BoundedProcessFailure.NonZeroExit(code, b, eb))
        | ExitWaitFailed ->
            return Error(BoundedProcessFailure.WaitFailed (waitDetail |> Option.defaultValue "process exit unavailable"))
        | NaturalExit ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
            elif exitWaitFailed then
                return Error(BoundedProcessFailure.WaitFailed waitDetail.Value)
            else
                let stdoutFinal = stdoutOutcomeCell.Value.Value
                let stderrFinal = stderrOutcomeCell.Value.Value
                let code = exitCodeCell.Value.Value
                match stdoutFinal with
                | Overflowed _ -> return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                | ReadFailed msg -> return Error(BoundedProcessFailure.StdoutReaderFailed msg)
                | ReadCancelled -> return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                | EofReached b ->
                    match stderrFinal with
                    | Overflowed _ -> return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                    | ReadFailed msg -> return Error(BoundedProcessFailure.StderrReaderFailed msg)
                    | ReadCancelled -> return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                    | EofReached eb ->
                        if code = 0 then
                            return Ok { ExitCode = code; Stdout = b; Stderr = eb }
                        else
                            return Error(BoundedProcessFailure.NonZeroExit(code, b, eb))
    }

// -----------------------------------------------------------------------------
// Process runner (public API) — CORRECTION 11
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
        match launchProcess request with
        | Error e -> Task.FromResult(Error e)
        | Ok procObj ->
            // Close stdin so child can detect EOF on input
            try procObj.StandardInput.Close() with | _ -> ()

            // Create tokens
            let tcts = new CancellationTokenSource(request.Limits.Timeout)
            let lcts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tcts.Token)

            let timeoutTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let cancelTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let tReg = tcts.Token.Register(fun () -> timeoutTcs.TrySetResult(true) |> ignore)
            let cReg = cancellationToken.Register(fun () -> cancelTcs.TrySetResult(true) |> ignore)

            let stdoutTask = readBoundedAsync procObj.StandardOutput.BaseStream request.Limits.StdoutLimitBytes lcts.Token |> Async.StartAsTask
            let stderrTask = readBoundedAsync procObj.StandardError.BaseStream request.Limits.StderrLimitBytes lcts.Token |> Async.StartAsTask
            let exitTask = procObj.WaitForExitAsync()

            executeLifecycle procObj lcts request timeoutTcs cancelTcs stdoutTask stderrTask exitTask tReg cReg tcts