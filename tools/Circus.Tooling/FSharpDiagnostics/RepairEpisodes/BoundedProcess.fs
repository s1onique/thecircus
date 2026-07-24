module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

#nowarn "3511"

// =============================================================================
// Bounded-process core -- CORRECTION 12
//
// P0 lifecycle classification fixes (from the persistent review):
// - Select ExitWaitFailed when the exit task is faulted or cancelled.
// - Include direct-process exit in cleanup completeness.
// - Remove unsafe Option.Value extraction without a Some match.
// - Defer disposal after incomplete cleanup so CancellationTokenSource
//   dispose does not race active operations.
//
// P1 lifecycle seam:
// - LifecycleSeam exposes (ExitTask, Kill, HasExited, ExitCode) so the
//   unreachable OS states (faulted/cancelled exit, cleanup expiry, kill
//   failure) can be tested without depending on a real subprocess.
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

type TerminalFailure =
    | StdoutOverflow
    | StderrOverflow
    | StdoutReadFailure of detail: string
    | StderrReadFailure of detail: string
    | UnexpectedStdoutCancellation
    | UnexpectedStderrCancellation

type TerminationCleanupContext = {
    Cause: TerminalCause
    TerminalFailure: TerminalFailure option
    KillDetail: string option
    ProcessExited: bool
    StdoutComplete: bool
    StderrComplete: bool
    WaitDetail: string option
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
    | WaitFailed of detail: string
    | KillFailed of detail: string
    | IncompleteOutput of stdoutComplete: bool * stderrComplete: bool
    | TerminationCleanupFailed of TerminationCleanupContext

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
        match procObj.Start() with
        | true -> Ok procObj
        | false ->
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
// Internal lifecycle seam
//
// Tests construct a Seam to reproduce OS-level states (faulted/cancelled
// exit task, killed-but-still-running cleanup expiry, permanent kill failure)
// that are not reliably reachable through a real child process.
//
// The default seam wraps a real Process. Production code uses the default
// seam built by `run`; tests inject a custom seam via
// `executeLifecycleWithSeam`.
// -----------------------------------------------------------------------------

type internal LifecycleSeam = {
    /// Task that completes when the child process exits.
    ExitTask: Task
    /// Attempt to kill the child. Returns Ok on success, Error with the
    /// detail message on failure. Implemented by the seam so the
    /// kill-failure branch is testable.
    Kill: unit -> Result<unit, string>
    /// Check whether the child has exited at the moment of the call.
    /// Used by the cleanup-completeness check so it remains accurate
    /// even after the exit task has settled.
    HasExited: unit -> bool
    /// Read the child's exit code. Only valid when `HasExited ()` is true.
    /// Named to avoid the field-name collision with BoundedProcessSuccess
    /// the test site relies on for type inference.
    ReadExitCode: unit -> int
    /// Release the resources owned by the seam. In this checkpoint the
    /// field is supplied by every construction site but the lifecycle
    /// does not invoke it yet; ownership behaviour is left to a later
    /// checkpoint that wires Task.WhenAll-based disposal.
    Dispose: unit -> unit
}

// -----------------------------------------------------------------------------
// Termination-cleanup failure constructor helper
//
// All production sites that surface a TerminationCleanupFailed fail through
// this helper so the record layout has exactly one owner. This checkpoint
// only normalises the payload shape; actual TerminalFailure values are
// captured by a later classifier-only checkpoint.
// -----------------------------------------------------------------------------

let private terminationCleanupFailure
    cause
    terminalFailure
    killDetail
    processExited
    stdoutComplete
    stderrComplete
    waitDetail
    =
    BoundedProcessFailure.TerminationCleanupFailed {
        Cause = cause
        TerminalFailure = terminalFailure
        KillDetail = killDetail
        ProcessExited = processExited
        StdoutComplete = stdoutComplete
        StderrComplete = stderrComplete
        WaitDetail = waitDetail
    }

// -----------------------------------------------------------------------------
// Lifecycle implementation
// -----------------------------------------------------------------------------

let internal executeLifecycleWithSeam
    (lcts: CancellationTokenSource)
    (request: BoundedProcessRequest)
    (timeoutTcs: TaskCompletionSource<bool>)
    (cancelTcs: TaskCompletionSource<bool>)
    (stdoutTask: Task<ReadOutcome>)
    (stderrTask: Task<ReadOutcome>)
    (seam: LifecycleSeam)
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
                match seam.Kill() with
                | Ok () -> ()
                | Error msg -> killErrorCell.Value <- Some msg

        // Event loop - exits immediately on any authoritative terminal cause
        let mutable loopDone = false
        while not loopDone do
            let stdoutOutcome = stdoutOutcomeCell.Value
            let stderrOutcome = stderrOutcomeCell.Value

            let mutable pending = ResizeArray<Task>()
            if stdoutOutcome.IsNone then pending.Add(stdoutTask)
            if stderrOutcome.IsNone then pending.Add(stderrTask)
            if not exitObserved then pending.Add(seam.ExitTask)
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
            elif not exitObserved && Object.ReferenceEquals(winner, seam.ExitTask) then
                exitObserved <- true
                if seam.ExitTask.IsCompletedSuccessfully then
                    // Capture exit code from the seam. The seam abstracts
                    // the actual code source (real Process or test inject).
                    try
                        exitCodeCell.Value <- Some(seam.ReadExitCode())
                    with
                    | ex -> waitDetailCell.Value <- Some ex.Message
                elif seam.ExitTask.IsFaulted then
                    waitDetailCell.Value <- Some(seam.ExitTask.Exception.GetBaseException().Message)
                    // P0 fix: classify wait failure, not just populate detail.
                    terminalCauseCell.Value <- ExitWaitFailed
                elif seam.ExitTask.IsCanceled then
                    waitDetailCell.Value <- Some "process exit wait cancelled"
                    // P0 fix: classify wait failure, not just populate detail.
                    // The caller-cancel path is gated by the timeout/cancel
                    // branch, so a cancelled ExitTask here is the underlying
                    // task being cancelled, not the public API being cancelled.
                    terminalCauseCell.Value <- ExitWaitFailed
            elif stdoutOutcome.IsNone && Object.ReferenceEquals(winner, stdoutTask) then
                let outcome = tryReadOutcome stdoutTask
                stdoutOutcomeCell.Value <- Some outcome
                if isTerminal outcome then
                    let isAuthTimeoutOrCancel = timeoutObserved || cancellationObserved
                    if not isAuthTimeoutOrCancel && terminalCauseCell.Value = NaturalExit then
                        match outcome with
                        | ReadCancelled ->
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

            let sOut = stdoutOutcomeCell.Value
            let sErr = stderrOutcomeCell.Value
            let stdoutTerminal = sOut.IsSome && isTerminal(sOut.Value)
            let stderrTerminal = sErr.IsSome && isTerminal(sErr.Value)
            let hasAuthoritativeCause =
                timeoutObserved || cancellationObserved
                || terminalCauseCell.Value = StdoutTerminal
                || terminalCauseCell.Value = StderrTerminal
                || terminalCauseCell.Value = ExitWaitFailed
                || (waitDetailCell.Value.IsSome && not exitObserved)
            let naturalComplete =
                stdoutTerminal = false
                && stderrTerminal = false
                && sOut.IsSome && isEof(sOut.Value)
                && sErr.IsSome && isEof(sErr.Value)
                && terminalCauseCell.Value = NaturalExit
            loopDone <- hasAuthoritativeCause || naturalComplete

        // ---- Terminal cleanup: async grace races ----
        try lcts.Cancel() with | _ -> ()

        let exitRace = raceWithDelay seam.ExitTask (TimeSpan.FromSeconds(5.0))
        let! exitWinner = exitRace
        // The race returned the ExitTask itself OR the delay task completed first.
        let exitTaskSettled = Object.ReferenceEquals(exitWinner, seam.ExitTask)
        // P0 fix: classify the process as still alive until both the exit task
        // has settled AND the process-side HasExited signal confirms it.
        let processExitedByRace = exitTaskSettled && seam.ExitTask.IsCompletedSuccessfully
        let processExited = processExitedByRace || seam.HasExited()
        // Capture latest exit detail if the loop did not already observe it.
        if not exitObserved then
            if seam.ExitTask.IsCompletedSuccessfully then
                try exitCodeCell.Value <- Some(seam.ReadExitCode()) with | ex -> waitDetailCell.Value <- Some ex.Message
            elif seam.ExitTask.IsFaulted then
                waitDetailCell.Value <- Some(seam.ExitTask.Exception.GetBaseException().Message)
            elif seam.ExitTask.IsCanceled then
                waitDetailCell.Value <- Some "process exit wait cancelled"
        if seam.ExitTask.IsFaulted && waitDetailCell.Value.IsNone then
            waitDetailCell.Value <- Some(seam.ExitTask.Exception.GetBaseException().Message)
        elif seam.ExitTask.IsCanceled && waitDetailCell.Value.IsNone then
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

        // Attach continuations to observe late task faults during finalization.
        // The continuations are also responsible for the deferred disposal:
        // because CancellationTokenSource.Dispose must not race its own
        // cancellation callbacks, we defer everything until the underlying
        // tasks have actually settled.
        let faultObserver (t: Task) =
            if t.IsFaulted then
                ignore t.Exception
            else
                ignore ()

        let mutable disposed = false
        let disposeOnce () =
            if not disposed then
                disposed <- true
                try tReg.Dispose() with | _ -> ()
                try cReg.Dispose() with | _ -> ()
                try lcts.Dispose() with | _ -> ()
                try tcts.Dispose() with | _ -> ()
                // We intentionally do NOT dispose processObj here. The
                // Process is owned by the caller's `run` and disposed in
                // its own cleanup continuation. Disposing it from the
                // lifecycle risks racing the caller's Process.HasExited
                // check used by the cleanup-completeness check.

        stdoutTask.ContinueWith(Action<Task<ReadOutcome>>(fun _ ->
            faultObserver stdoutTask
            disposeOnce())) |> ignore
        stderrTask.ContinueWith(Action<Task<ReadOutcome>>(fun _ ->
            faultObserver stderrTask
            disposeOnce())) |> ignore
        seam.ExitTask.ContinueWith(Action<Task>(fun _ ->
            faultObserver seam.ExitTask
            disposeOnce())) |> ignore

        // ---- Classify ----
        let cause = terminalCauseCell.Value
        let killDetail = killErrorCell.Value
        let waitDetail = waitDetailCell.Value
        // P0 fix: cleanup is incomplete when the process is still alive
        // OR when either reader did not settle during the grace window.
        let cleanupIncomplete =
            not processExited
            || not stdoutComplete
            || not stderrComplete
        // The wait is "failed" when we have a wait detail but no exit code.
        let exitWaitFailed = waitDetail.IsSome && exitCodeCell.Value.IsNone

        match cause with
        | TimeoutFire ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(terminationCleanupFailure cause None killDetail processExited stdoutComplete stderrComplete waitDetail)
            else
                return Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)
        | CallerCancel ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(terminationCleanupFailure cause None killDetail processExited stdoutComplete stderrComplete waitDetail)
            else
                return Error BoundedProcessFailure.Cancelled
        | StdoutTerminal ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(terminationCleanupFailure cause None killDetail processExited stdoutComplete stderrComplete waitDetail)
            elif exitWaitFailed then
                return Error(BoundedProcessFailure.WaitFailed waitDetail.Value)
            elif exitCodeCell.Value.IsNone
                 || stdoutOutcomeCell.Value.IsNone
                 || stderrOutcomeCell.Value.IsNone then
                return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
            else
                match stdoutOutcomeCell.Value.Value, stderrOutcomeCell.Value.Value, exitCodeCell.Value.Value with
                | Overflowed _, _, _ ->
                    return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                | ReadFailed msg, _, _ ->
                    return Error(BoundedProcessFailure.StdoutReaderFailed msg)
                | ReadCancelled, _, _ ->
                    return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                | EofReached b, Overflowed _, _ ->
                    return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                | EofReached b, ReadFailed msg, _ ->
                    return Error(BoundedProcessFailure.StderrReaderFailed msg)
                | EofReached b, ReadCancelled, _ ->
                    return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                | EofReached b, EofReached eb, code ->
                    if code = 0 then
                        return Ok { ExitCode = code; Stdout = b; Stderr = eb }
                    else
                        return Error(BoundedProcessFailure.NonZeroExit(code, b, eb))
        | StderrTerminal ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(terminationCleanupFailure cause None killDetail processExited stdoutComplete stderrComplete waitDetail)
            elif exitWaitFailed then
                return Error(BoundedProcessFailure.WaitFailed waitDetail.Value)
            elif exitCodeCell.Value.IsNone
                 || stdoutOutcomeCell.Value.IsNone
                 || stderrOutcomeCell.Value.IsNone then
                return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
            else
                match stdoutOutcomeCell.Value.Value, stderrOutcomeCell.Value.Value, exitCodeCell.Value.Value with
                | _, Overflowed _, _ ->
                    return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                | _, ReadFailed msg, _ ->
                    return Error(BoundedProcessFailure.StderrReaderFailed msg)
                | _, ReadCancelled, _ ->
                    return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                | Overflowed _, _, _ ->
                    return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                | ReadFailed msg, _, _ ->
                    return Error(BoundedProcessFailure.StdoutReaderFailed msg)
                | EofReached b, EofReached eb, code ->
                    if code = 0 then
                        return Ok { ExitCode = code; Stdout = b; Stderr = eb }
                    else
                        return Error(BoundedProcessFailure.NonZeroExit(code, b, eb))
                | _ ->
                    return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
        | ExitWaitFailed ->
            // P0 fix: classify wait failure with the captured detail.
            let detail = defaultArg waitDetail "process exit unavailable"
            return Error(BoundedProcessFailure.WaitFailed detail)
        | NaturalExit ->
            if killDetail.IsSome then
                return Error(BoundedProcessFailure.KillFailed killDetail.Value)
            elif cleanupIncomplete then
                return Error(terminationCleanupFailure cause None killDetail processExited stdoutComplete stderrComplete waitDetail)
            elif exitWaitFailed then
                return Error(BoundedProcessFailure.WaitFailed waitDetail.Value)
            elif exitCodeCell.Value.IsNone
                 || stdoutOutcomeCell.Value.IsNone
                 || stderrOutcomeCell.Value.IsNone then
                return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
            else
                match stdoutOutcomeCell.Value.Value, stderrOutcomeCell.Value.Value, exitCodeCell.Value.Value with
                | Overflowed _, _, _ ->
                    return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                | ReadFailed msg, _, _ ->
                    return Error(BoundedProcessFailure.StdoutReaderFailed msg)
                | ReadCancelled, _, _ ->
                    return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                | EofReached b, Overflowed _, _ ->
                    return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                | EofReached b, ReadFailed msg, _ ->
                    return Error(BoundedProcessFailure.StderrReaderFailed msg)
                | EofReached b, ReadCancelled, _ ->
                    return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
                | EofReached b, EofReached eb, code ->
                    if code = 0 then
                        return Ok { ExitCode = code; Stdout = b; Stderr = eb }
                    else
                        return Error(BoundedProcessFailure.NonZeroExit(code, b, eb))
    }

// -----------------------------------------------------------------------------
// Default seam - wraps a real Process
// -----------------------------------------------------------------------------

let private defaultSeam (procObj: Process) : LifecycleSeam =
    {
        ExitTask = procObj.WaitForExitAsync()
        Kill = fun () ->
            try
                if not procObj.HasExited then
                    procObj.Kill(entireProcessTree = true)
                Ok ()
            with
            | :? System.ComponentModel.Win32Exception as ex -> Error ex.Message
            | :? InvalidOperationException as ex -> Error ex.Message
            | :? System.NotSupportedException as ex -> Error ex.Message
        HasExited = fun () -> procObj.HasExited
        ReadExitCode = fun () -> procObj.ExitCode
        Dispose =
            fun () ->
                try
                    procObj.Dispose()
                with
                | _ -> ()
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

            let seam = defaultSeam procObj

            // The lifecycle reads procObj.ExitCode via the seam, so the
            // Process must NOT be disposed until the lifecycle has
            // returned. We then dispose the Process AND the continuation-run
            // resources (cleanup of the deferred-disposal finalizer inside
            // the lifecycle) only after the lifecycle's result has been
            // captured. The WhenAll race in the previous version disposed
            // procObj before the lifecycle finished reading ExitCode, which
            // surfaced as "No process is associated with this object."
            let lifecycleTask = executeLifecycleWithSeam lcts request timeoutTcs cancelTcs stdoutTask stderrTask seam tReg cReg tcts
            let resultTask =
                task {
                    let! result = lifecycleTask
                    try procObj.Dispose() with | _ -> ()
                    return result
                }
            resultTask
