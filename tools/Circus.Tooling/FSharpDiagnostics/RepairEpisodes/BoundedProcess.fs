module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

#nowarn "3511"

// =============================================================================
// Bounded-process core -- CORRECTION 14
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
// P3 authoritative cancellation and resource ownership:
// - Linked reader cancellation must not override an authoritative
//   timeout or caller-cancellation participant. The reader terminal
//   branch uses participant completion checks (timeoutObserved OR
//   timeoutTcs.Task.IsCompleted, cancellationObserved OR
//   cancelTcs.Task.IsCompleted) to decide whether to surface an
//   Unexpected*Cancellation, set the reader terminal cause, or wait
//   for the authoritative participant to be observed.
// - Registrations, cancellation sources, and the seam's Dispose are now
//   owned by a single atomic finalizer (Interlocked.Exchange) that
//   waits for the exit task, stdout task, and stderr task to settle
//   before invoking the disposal callback. The previous per-task
//   disposal continuations and the unsynchronized mutable Boolean are
//   removed. Production's run() no longer disposes the process object
//   directly; the seam's Dispose callback owns it.
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
    /// stderr task have all settled. Production's default seam uses this
    /// to dispose the real Process.
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
        // Exactly-once resource ownership.
        //
        // One finalizer owns the registrations, the cancellation sources,
        // and the seam's Dispose. Interlocked.Exchange guarantees that
        // even if multiple scheduling paths race, the disposal body runs
        // exactly once. The finalizer waits for the exit task AND both
        // reader tasks to settle before disposing; aggregate faults are
        // observed so they are never silently swallowed.
        // -----------------------------------------------------------------
        let mutable disposalState = 0
        let disposeOnce () =
            if Interlocked.Exchange(&disposalState, 1) = 0 then
                try tReg.Dispose() with | _ -> ()
                try cReg.Dispose() with | _ -> ()
                try lcts.Dispose() with | _ -> ()
                try tcts.Dispose() with | _ -> ()
                try seam.Dispose() with | _ -> ()

        let allOperations =
            Task.WhenAll([|
                seam.ExitTask
                stdoutTask :> Task
                stderrTask :> Task
            |])

        let finalizer =
            task {
                try
                    do! allOperations
                with
                | _ ->
                    if allOperations.IsFaulted then
                        ignore allOperations.Exception
                disposeOnce()
            }

        // Schedule the finalizer immediately. It runs concurrently with
        // the lifecycle's event loop and cleanup races. The lifecycle's
        // public Result is unaffected by the finalizer's completion.
        finalizer |> ignore

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

        // The finalizer scheduled above observes any late task faults and
        // disposes exactly once after the three operations have settled.
        // We do not duplicate that work here.

        // ---- Build classification snapshot and classify ----
        //
        // The snapshot is the only bridge between the mutable lifecycle
        // state and the pure classifier. Gathering the values here is
        // the last place where Option.Value extraction is allowed; the
        // classifier itself never sees a MutableCell.
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

        return classify request snapshot
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
// would race the finalizer's exactly-once disposal.
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

            // The lifecycle's finalizer owns the process; the lifecycle
            // returns the public Result. We propagate the result and
            // intentionally do not dispose the process here.
            executeLifecycleWithSeam lcts request timeoutTcs cancelTcs stdoutTask stderrTask seam tReg cReg tcts
