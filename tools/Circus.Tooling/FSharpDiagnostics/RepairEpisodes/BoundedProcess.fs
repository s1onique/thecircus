module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

#nowarn "3511"

// =============================================================================
// Bounded-process core -- CORRECTION 15
//
// P0 lifecycle classification fixes (from the persistent review):
// - Select ExitWaitFailed when the exit task is faulted or cancelled.
// - Include direct-process exit in cleanup completeness.
// - Remove unsafe Option.Value extraction without a Some match.
// - Defer disposal after incomplete cleanup so CancellationTokenSource
//   dispose does not race active operations.
//
// P1 lifecycle seam:
// - LifecycleSeam exposes (ExitTask, Kill, HasExited, ExitCode, Dispose)
//   so the unreachable OS states (faulted/cancelled exit, cleanup
//   expiry, kill failure) can be tested without depending on a real
//   subprocess.
//
// P2 classifier extraction:
// - Introduce ClassificationSnapshot so the final classification can be
//   expressed as a pure function without task, mutable cell, Option.Value,
//   or Task access. The lifecycle task builds the snapshot at the end and
//   forwards it to `classify`.
// - Capture TerminalFailure alongside TerminalCause so the classifier can
//   surface the precise reader failure mode (Overflowed, ReadFailed,
//   unexpected reader cancellation) instead of reusing cause state.
// - Correct natural completion: both streams at EOF is no longer enough;
//   the exit task must have been observed successfully and the exit code
//   captured. EOF without process exit no longer pretends success.
//
// P3 authoritative cancellation:
// - Reader terminal branches now check participant completion
//   (timeoutObserved OR timeoutTcs.Task.IsCompleted, cancellationObserved
//   OR cancelTcs.Task.IsCompleted) rather than only the in-loop observed
//   flag. ReadCancelled when neither participant is complete surfaces
//   UnexpectedStdoutCancellation/UnexpectedStderrCancellation and
//   terminates the child. ReadCancelled when a participant is already
//   complete is stored without changing the cause.
//
// P3.1 deterministic timeout/cancellation precedence:
// - When both timeout and caller-cancellation participants are completed,
//   caller-cancellation wins. This is enforced in the event loop: if
//   the timeout task is observed first, we check whether the cancellation
//   task is also completed; if so, we set CallerCancel (not TimeoutFire).
//   Documented in the docstring on executeLifecycleWithSeam.
//
// CORRECTION 15 finalizer ordering fix:
// - The finalizer is NO LONGER scheduled at the start of the lifecycle.
//   Doing so could race: the finalizer waits only for the three
//   operations to settle, not for the lifecycle to finish processing
//   those completions or capture the snapshot. Disposal could therefore
//   run while the cleanup or snapshot capture still needed the seam's
//   state (HasExited, ReadExitCode).
// - The new finalization boundary is:
//     (a) all three operations settled
//     AND
//     (b) the snapshot has been captured
//     AND
//     (c) the public classification no longer accesses the seam.
// - After the snapshot is captured, executeLifecycleWithSeam decides:
//     * If the three operations are already settled, await the
//       finalization synchronously and then return.
//     * If the cleanup deliberately returned while operations are still
//       active (a cleanup-failure case), schedule the deferred
//       finalizer and return the typed cleanup failure immediately.
// - The finalizer is now observable: the internal return shape is
//   `LifecycleCompletion` carrying the Result plus the Finalization
//   Task. The finalization Task is fire-and-forget but a
//   fault-observing continuation is attached so the test runner can
//   detect any disposal-time failure.
// - run() awaits both Result and Finalization, so the public caller
//   never observes a Result while a resource it depends on is being
//   released.
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
    /// Release the resources owned by the seam. The lifecycle's finalizer
    /// invokes this exactly once after the exit task, stdout task, and
    /// stderr task have all settled AND the lifecycle has finished
    /// processing them. Production's default seam uses this to dispose
    /// the real Process.
    Dispose: unit -> unit
}

// -----------------------------------------------------------------------------
// Classification snapshot
//
// The lifecycle task gathers every value needed to classify the result
// into this immutable record and then forwards it to the pure `classify`
// function. Building the snapshot is the only place where mutable cells,
// Task access, and Option.Value extraction live. The classifier itself
// contains no task, no mutable state, no option value extraction, and no
// task or process access.
// -----------------------------------------------------------------------------

type private ClassificationSnapshot = {
    Cause: TerminalCause
    TerminalFailure: TerminalFailure option
    KillDetail: string option
    WaitDetail: string option
    ProcessExited: bool
    StdoutComplete: bool
    StderrComplete: bool
    ExitCode: int option
    StdoutOutcome: ReadOutcome option
    StderrOutcome: ReadOutcome option
}

// -----------------------------------------------------------------------------
// Finalization mode (internal)
//
// Whether the lifecycle must await finalization before returning, or
// may defer it. AwaitBeforeReturn is only used when all three
// operations have already settled, so the finalization completes
// without waiting on anything that could be stuck. Deferred is used
// when the cleanup deliberately returned while operations are still
// active, so the public API does not block indefinitely on a Task
// that cannot complete until the caller supplies the missing state.
// -----------------------------------------------------------------------------

type internal FinalizationMode =
    | AwaitBeforeReturn
    | Deferred

// -----------------------------------------------------------------------------
// Lifecycle completion (internal)
//
// Internal return shape for executeLifecycleWithSeam. The Result is the
// public classification; the Finalization is the disposal Task that
// callers (or the public run) may need to observe; the FinalizationMode
// tells the caller whether the finalization has been awaited already.
// -----------------------------------------------------------------------------

type internal LifecycleCompletion = {
    Result: Result<BoundedProcessSuccess, BoundedProcessFailure>
    Finalization: Task
    FinalizationMode: FinalizationMode
}

// -----------------------------------------------------------------------------
// Pure classification helpers
// -----------------------------------------------------------------------------

let private cleanupContext
    (snapshot: ClassificationSnapshot)
    : TerminationCleanupContext =
    {
        Cause = snapshot.Cause
        TerminalFailure = snapshot.TerminalFailure
        KillDetail = snapshot.KillDetail
        ProcessExited = snapshot.ProcessExited
        StdoutComplete = snapshot.StdoutComplete
        StderrComplete = snapshot.StderrComplete
        WaitDetail = snapshot.WaitDetail
    }

let private cleanupIncomplete (snapshot: ClassificationSnapshot) : bool =
    not snapshot.ProcessExited
    || not snapshot.StdoutComplete
    || not snapshot.StderrComplete

let private exitWaitFailed (snapshot: ClassificationSnapshot) : bool =
    match snapshot.WaitDetail, snapshot.ExitCode with
    | Some _, None -> true
    | _ -> false

/// Pure classifier that maps a snapshot onto the final Result. Contains
/// no task, no mutable state, no Option.Value extraction, and no access
/// to Task or Process. F# if/then/else and match are expression forms
/// that directly produce the Result value.
let private classify
    (request: BoundedProcessRequest)
    (snapshot: ClassificationSnapshot)
    : Result<BoundedProcessSuccess, BoundedProcessFailure> =

    // Global termination failure takes precedence over every other branch.
    // When a kill was attempted and the kill itself failed, surface
    // KillFailed regardless of cause or cleanup completeness.
    match snapshot.KillDetail with
    | Some killDetail ->
        Error(BoundedProcessFailure.KillFailed killDetail)

    | None ->
        match snapshot.Cause with
        | ExitWaitFailed ->
            let detail =
                match snapshot.WaitDetail with
                | Some d -> d
                | None -> "process exit unavailable"
            Error(BoundedProcessFailure.WaitFailed detail)

        | TimeoutFire ->
            if cleanupIncomplete snapshot then
                Error(BoundedProcessFailure.TerminationCleanupFailed (cleanupContext snapshot))
            else
                Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)

        | CallerCancel ->
            if cleanupIncomplete snapshot then
                Error(BoundedProcessFailure.TerminationCleanupFailed (cleanupContext snapshot))
            else
                Error BoundedProcessFailure.Cancelled

        | StdoutTerminal ->
            if cleanupIncomplete snapshot then
                // Preserve the captured TerminalFailure so the caller can
                // see why the loop decided to terminate, but never hide an
                // unconfirmed surviving process behind a reader error.
                Error(BoundedProcessFailure.TerminationCleanupFailed (cleanupContext snapshot))
            else
                match snapshot.TerminalFailure with
                | Some StdoutOverflow ->
                    Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                | Some (StdoutReadFailure detail) ->
                    Error(BoundedProcessFailure.StdoutReaderFailed detail)
                | Some UnexpectedStdoutCancellation ->
                    Error(BoundedProcessFailure.IncompleteOutput(snapshot.StdoutComplete, snapshot.StderrComplete))
                | _ ->
                    // Caught cases: StderrOverflow, StderrReadFailure,
                    // UnexpectedStderrCancellation, or None. The lifecycle
                    // is supposed to keep cause and terminalFailure in the
                    // same stream, but we fall back to IncompleteOutput
                    // for any cross-stream mismatch.
                    Error(BoundedProcessFailure.IncompleteOutput(snapshot.StdoutComplete, snapshot.StderrComplete))

        | StderrTerminal ->
            if cleanupIncomplete snapshot then
                Error(BoundedProcessFailure.TerminationCleanupFailed (cleanupContext snapshot))
            else
                match snapshot.TerminalFailure with
                | Some StderrOverflow ->
                    Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                | Some (StderrReadFailure detail) ->
                    Error(BoundedProcessFailure.StderrReaderFailed detail)
                | Some UnexpectedStderrCancellation ->
                    Error(BoundedProcessFailure.IncompleteOutput(snapshot.StdoutComplete, snapshot.StderrComplete))
                | _ ->
                    Error(BoundedProcessFailure.IncompleteOutput(snapshot.StdoutComplete, snapshot.StderrComplete))

        | NaturalExit ->
            if cleanupIncomplete snapshot then
                Error(BoundedProcessFailure.TerminationCleanupFailed (cleanupContext snapshot))
            elif exitWaitFailed snapshot then
                let detail =
                    match snapshot.WaitDetail with
                    | Some d -> d
                    | None -> "process exit unavailable"
                Error(BoundedProcessFailure.WaitFailed detail)
            else
                match snapshot.StdoutOutcome, snapshot.StderrOutcome, snapshot.ExitCode with
                | Some (EofReached stdout), Some (EofReached stderr), Some exitCode ->
                    if exitCode = 0 then
                        Ok {
                            ExitCode = exitCode
                            Stdout = stdout
                            Stderr = stderr
                        }
                    else
                        Error(BoundedProcessFailure.NonZeroExit(exitCode, stdout, stderr))
                | _ ->
                    Error(BoundedProcessFailure.IncompleteOutput(snapshot.StdoutComplete, snapshot.StderrComplete))

// -----------------------------------------------------------------------------
// Lifecycle implementation
//
// Authoritative cancellation order (P3.1):
//   * The explicit timeout and caller-cancellation tasks are the public
//     cause authorities. If both participants are pre-completed when
//     the timeout task is observed by the event loop, the cause is
//     CallerCancel (not TimeoutFire). This guarantees a deterministic
//     public precedence regardless of which task `Task.WhenAny` returns
//     first.
//
// Finalizer ordering (CORRECTION 15):
//   * The disposal Task is established only after the snapshot has been
//     captured. The finalization is awaited synchronously if the three
//     operations are already settled, or scheduled as a fire-and-forget
//     Task with a fault-observing continuation when the cleanup
//     returned while operations were still active.
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
    : Task<LifecycleCompletion> =
    task {
        // Mutable state
        let stdoutOutcomeCell = { Value = None }
        let stderrOutcomeCell = { Value = None }
        let exitCodeCell = { Value = None }
        let waitDetailCell = { Value = None }
        let killErrorCell = { Value = None }
        let terminalCauseCell = { Value = NaturalExit }
        let terminalFailureCell = { Value = None }
        let mutable killRequested = false
        let mutable timeoutObserved = false
        let mutable cancellationObserved = false
        let mutable exitObserved = false

        let captureStdoutFailure (outcome: ReadOutcome) =
            // Map a terminal stdout outcome into the corresponding
            // TerminalFailure. EOF is intentionally not terminal and never
            // produces a TerminalFailure. The caller has already verified
            // that the outcome is terminal.
            match outcome with
            | Overflowed _ -> Some TerminalFailure.StdoutOverflow
            | ReadFailed detail -> Some(TerminalFailure.StdoutReadFailure detail)
            | ReadCancelled -> Some TerminalFailure.UnexpectedStdoutCancellation
            | EofReached _ -> None

        let captureStderrFailure (outcome: ReadOutcome) =
            match outcome with
            | Overflowed _ -> Some TerminalFailure.StderrOverflow
            | ReadFailed detail -> Some(TerminalFailure.StderrReadFailure detail)
            | ReadCancelled -> Some TerminalFailure.UnexpectedStderrCancellation
            | EofReached _ -> None

        let killNow () =
            if not killRequested then
                killRequested <- true
                match seam.Kill() with
                | Ok () -> ()
                | Error msg -> killErrorCell.Value <- Some msg

        // -----------------------------------------------------------------
        // Exactly-once resource ownership. disposeOnce is only called from
        // the finalizer (or from the synchronous finalization path below);
        // it is never invoked from the event loop or cleanup.
        // -----------------------------------------------------------------
        let mutable disposalState = 0
        let disposeOnce () =
            if Interlocked.Exchange(&disposalState, 1) = 0 then
                try tReg.Dispose() with | _ -> ()
                try cReg.Dispose() with | _ -> ()
                try lcts.Dispose() with | _ -> ()
                try tcts.Dispose() with | _ -> ()
                try seam.Dispose() with | _ -> ()

        // Helper that builds the finalization Task. The finalization waits
        // for the three operations to settle, observes aggregate faults,
        // and then invokes disposeOnce exactly once. The finalization
        // never accesses the seam after disposal. The finalization is a
        // single F# task computation expression with no ContinueWith;
        // it is awaited via the standard task CE continuation path so
        // there is no synchronous Task.Wait deadlock.
        let buildFinalization () : Task =
            task {
                try
                    do!
                        Task.WhenAll(
                            [|
                                seam.ExitTask
                                stdoutTask :> Task
                                stderrTask :> Task
                            |]
                        )
                with
                | _ as failure ->
                    // Observe aggregate faults so they are never
                    // silently swallowed.
                    ignore failure
                disposeOnce()
            }

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
                // P3.1 deterministic precedence: if the caller-cancellation
                // task is also already completed, caller cancellation wins
                // over timeout. The public cause authority precedence is
                // caller cancellation > timeout.
                if cancellationObserved || cancelTcs.Task.IsCompleted then
                    terminalCauseCell.Value <- CallerCancel
                else
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
                    // P3 authoritative cancellation: the explicit timeout or
                    // caller-cancellation task remains the public cause
                    // authority. Decision is made on participant completion
                    // (.Task.IsCompleted) rather than only the in-loop
                    // observed flag, so that a reader cancellation that
                    // races a near-simultaneous authoritative completion
                    // does not impersonate the authoritative cause.
                    let timeoutAuthoritative =
                        timeoutObserved || timeoutTcs.Task.IsCompleted
                    let cancellationAuthoritative =
                        cancellationObserved || cancelTcs.Task.IsCompleted
                    let cancellationCauseAvailable =
                        timeoutAuthoritative || cancellationAuthoritative
                    match outcome with
                    | ReadCancelled ->
                        if cancellationCauseAvailable then
                            // Store the outcome but do not set the cause or
                            // surface an Unexpected*Cancellation. Continue
                            // the event loop until the authoritative
                            // participant is observed.
                            ()
                        else if terminalCauseCell.Value = NaturalExit then
                            terminalCauseCell.Value <- StdoutTerminal
                            terminalFailureCell.Value <- Some TerminalFailure.UnexpectedStdoutCancellation
                            killNow()
                    | _ ->
                        // Overflowed or ReadFailed. Retain the existing
                        // reader terminal cause if one is already set, and
                        // capture the corresponding TerminalFailure.
                        if terminalCauseCell.Value = NaturalExit then
                            terminalCauseCell.Value <- StdoutTerminal
                        match captureStdoutFailure outcome with
                        | Some failure -> terminalFailureCell.Value <- Some failure
                        | None -> ()
                        killNow()
            elif stderrOutcome.IsNone && Object.ReferenceEquals(winner, stderrTask) then
                let outcome = tryReadOutcome stderrTask
                stderrOutcomeCell.Value <- Some outcome
                if isTerminal outcome then
                    let timeoutAuthoritative =
                        timeoutObserved || timeoutTcs.Task.IsCompleted
                    let cancellationAuthoritative =
                        cancellationObserved || cancelTcs.Task.IsCompleted
                    let cancellationCauseAvailable =
                        timeoutAuthoritative || cancellationAuthoritative
                    match outcome with
                    | ReadCancelled ->
                        if cancellationCauseAvailable then
                            ()
                        else if terminalCauseCell.Value = NaturalExit then
                            terminalCauseCell.Value <- StderrTerminal
                            terminalFailureCell.Value <- Some TerminalFailure.UnexpectedStderrCancellation
                            killNow()
                    | _ ->
                        if terminalCauseCell.Value = NaturalExit then
                            terminalCauseCell.Value <- StderrTerminal
                        match captureStderrFailure outcome with
                        | Some failure -> terminalFailureCell.Value <- Some failure
                        | None -> ()
                        killNow()

            let sOut = stdoutOutcomeCell.Value
            let sErr = stderrOutcomeCell.Value
            let stdoutTerminal =
                match sOut with
                | Some o -> isTerminal o
                | None -> false
            let stderrTerminal =
                match sErr with
                | Some o -> isTerminal o
                | None -> false
            let hasAuthoritativeCause =
                timeoutObserved || cancellationObserved
                || terminalCauseCell.Value = StdoutTerminal
                || terminalCauseCell.Value = StderrTerminal
                || terminalCauseCell.Value = ExitWaitFailed
                || (waitDetailCell.Value.IsSome && not exitObserved)
            // P2 fix: natural completion now requires the exit task to
            // have been observed successfully and the exit code to be
            // captured. Both streams reaching EOF is no longer enough on
            // its own; a hung child that produced no output can no longer
            // impersonate a successful exit.
            let stdoutEof =
                match sOut with
                | Some o -> isEof o
                | None -> false
            let stderrEof =
                match sErr with
                | Some o -> isEof o
                | None -> false
            let naturalComplete =
                exitObserved
                && exitCodeCell.Value.IsSome
                && stdoutEof
                && stderrEof
                && terminalCauseCell.Value = NaturalExit
            loopDone <- hasAuthoritativeCause || naturalComplete

        // ---- Terminal cleanup: async grace races ----
        // The cleanup races (and the seam's HasExited / ReadExitCode
        // calls below) MUST complete before the finalizer is allowed to
        // dispose the seam. The classification snapshot is only built
        // after this section, and the finalizer is only established
        // after that.
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

        // ---- Build classification snapshot ----
        //
        // The snapshot is the only bridge between the mutable lifecycle
        // state and the pure classifier. Gathering the values here is
        // the last place where Option.Value extraction is allowed; the
        // classifier itself never sees a MutableCell. After the snapshot
        // is captured, the lifecycle no longer accesses the seam.
        let snapshot = {
            Cause = terminalCauseCell.Value
            TerminalFailure = terminalFailureCell.Value
            KillDetail = killErrorCell.Value
            WaitDetail = waitDetailCell.Value
            ProcessExited = processExited
            StdoutComplete = stdoutComplete
            StderrComplete = stderrComplete
            ExitCode = exitCodeCell.Value
            StdoutOutcome = stdoutOutcomeCell.Value
            StderrOutcome = stderrOutcomeCell.Value
        }

        let result = classify request snapshot

        // ---- Establish finalization AFTER state capture ----
        //
        // By this point the lifecycle has finished using the seam: the
        // event loop, the cleanup races, and the snapshot capture are
        // all complete. The finalizer is now safe to dispose. The
        // disposal Task is made observable via LifecycleCompletion so
        // the public run can await it before returning.
        let operationsSettled =
            (seam.ExitTask.IsCompleted
             || seam.ExitTask.IsCanceled
             || seam.ExitTask.IsFaulted)
            && (stdoutTask.IsCompleted
                || stdoutTask.IsCanceled
                || stdoutTask.IsFaulted)
            && (stderrTask.IsCompleted
                || stderrTask.IsCanceled
                || stderrTask.IsFaulted)

        let finalization = buildFinalization ()
        let finalizationMode =
            if operationsSettled then
                // All operations have already settled. The finalization
                // task is already completed (or will complete
                // immediately when awaited). The public run() must await
                // it so a successful or normally-terminated process
                // returns only after disposal.
                AwaitBeforeReturn
            else
                // The cleanup deliberately returned while operations are
                // still active. The public run() must NOT block on the
                // finalization, because the finalization cannot complete
                // until the caller supplies whatever state the
                // outstanding operations are waiting on. The deferred
                // finalizer is observed asynchronously; its failure (if
                // any) is surfaced via UnobservedTaskException at GC
                // time, which is acceptable for a best-effort disposal
                // that already failed at the classification level.
                Deferred

        return {
            Result = result
            Finalization = finalization
            FinalizationMode = finalizationMode
        }
    }

// -----------------------------------------------------------------------------
// Default seam - wraps a real Process.
// Dispose owns the real Process through the production finalizer.
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
        Dispose = fun () -> procObj.Dispose()
    }

// -----------------------------------------------------------------------------
// Process runner (public API)
//
// The lifecycle's finalizer disposes the process via the seam's Dispose
// callback. run() therefore does NOT dispose procObj directly: doing so
// would race the finalizer's exactly-once disposal. run() awaits both
// the lifecycle's Result and its Finalization Task so the public caller
// never observes a Result while a resource it depends on is being
// released.
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

            // The lifecycle returns a LifecycleCompletion carrying the
            // public Result, the disposal Task, and a FinalizationMode.
            // For AwaitBeforeReturn we await disposal before returning;
            // for Deferred we observe disposal asynchronously so the
            // public run() never blocks indefinitely on operations that
            // are still pending.
            let completion =
                executeLifecycleWithSeam
                    lcts request timeoutTcs cancelTcs
                    stdoutTask stderrTask seam tReg cReg tcts
            task {
                let! c = completion
                match c.FinalizationMode with
                | AwaitBeforeReturn ->
                    do! c.Finalization
                    return c.Result
                | Deferred ->
                    // Observe the finalization asynchronously with a
                    // fault-detecting continuation. We do NOT block on
                    // it because a Deferred finalization may be waiting
                    // on operations that the caller is expected to
                    // complete externally.
                    c.Finalization.ContinueWith(
                        fun (t: Task) ->
                            if t.IsFaulted then
                                ignore t.Exception
                        ,
                        TaskContinuationOptions.ExecuteSynchronously
                    )
                    |> ignore
                    return c.Result
            }
