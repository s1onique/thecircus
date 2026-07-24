module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

#nowarn "3511"

// =============================================================================
// Bounded-process core — CORRECTION 09
//
// Cleanup-and-classification follow-up to CORRECTION 08.
//
// P0 fixes from CORRECTION08 expert review:
// - Terminal cleanup awaits process exit and readers explicitly (bounded grace)
// - Timeout vs cancellation classification uses the winning tcs/task identity
// - Reader faults become ReadFailed with the actual exception
// - Process-wait failure becomes WaitFailed, not exit code -1
// - IncompleteOutput used when cleanup grace expires without completion
// - Disposal happens after tasks finish, not before
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
    | WaitFailed of detail: string
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
// Await helper - await a task with timeout, returns true if completed
// -----------------------------------------------------------------------------

let private tryAwait (t: Task) (timeout: TimeSpan) : bool =
    if t.IsCompleted then true
    else
        try
            t.Wait(timeout)
        with
        | _ -> false

let private tryAwaitOutcome (t: Task<ReadOutcome>) (timeout: TimeSpan) : bool =
    if t.IsCompleted then true
    else
        try
            t.Wait(timeout) |> ignore
            true
        with
        | _ -> false

// -----------------------------------------------------------------------------
// Inner event loop body — CORRECTION 09
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
        // Mutable state for event loop
        let stdoutOutcomeCell = { Value = None }
        let stderrOutcomeCell = { Value = None }
        let exitCodeCell = { Value = None }
        let killErrorCell = { Value = None }
        let mutable killRequested = false
        let mutable timedOut = false
        let mutable cancelled = false

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

        // Event loop - keep racing until natural completion or terminal cause
        let mutable loopDone = false
        while not loopDone do
            let stdoutOutcome = stdoutOutcomeCell.Value
            let stderrOutcome = stderrOutcomeCell.Value
            let exitCode = exitCodeCell.Value

            let mutable pending = ResizeArray<Task>()
            if stdoutOutcome.IsNone then pending.Add(stdoutTask)
            if stderrOutcome.IsNone then pending.Add(stderrTask)
            if exitCode.IsNone then pending.Add(exitTask)
            pending.Add(timeoutTcs.Task)
            pending.Add(cancelTcs.Task)

            let! winner = Task.WhenAny(pending.ToArray())

            if Object.ReferenceEquals(winner, timeoutTcs.Task) then
                timedOut <- true
                killNow()
            elif Object.ReferenceEquals(winner, cancelTcs.Task) then
                cancelled <- true
                killNow()
            elif stdoutOutcome.IsNone && Object.ReferenceEquals(winner, stdoutTask) then
                let outcome = tryReadOutcome stdoutTask
                stdoutOutcomeCell.Value <- Some outcome
                if isTerminal outcome then
                    killNow()
            elif stderrOutcome.IsNone && Object.ReferenceEquals(winner, stderrTask) then
                let outcome = tryReadOutcome stderrTask
                stderrOutcomeCell.Value <- Some outcome
                if isTerminal outcome then
                    killNow()
            elif exitCode.IsNone && Object.ReferenceEquals(winner, exitTask) then
                try
                    exitCodeCell.Value <- Some(procObj.ExitCode)
                with
                | _ -> ()
                // If ExitCode throws (process still running), leave as None

            let sOut = stdoutOutcomeCell.Value
            let sErr = stderrOutcomeCell.Value
            let eC = exitCodeCell.Value
            let stdoutTerminal = sOut.IsSome && isTerminal(sOut.Value)
            let stderrTerminal = sErr.IsSome && isTerminal(sErr.Value)
            // Natural completion: process exited (exit code captured) and both readers EOF
            let naturalComplete =
                eC.IsSome
                && sOut.IsSome && isEof(sOut.Value)
                && sErr.IsSome && isEof(sErr.Value)
            loopDone <-
                naturalComplete
                || timedOut
                || cancelled
                || stdoutTerminal
                || stderrTerminal

        // ---- Terminal cleanup: explicit await with bounded grace ----
        // Cancel linked token to stop readers
        try lcts.Cancel() with | _ -> ()

        // Best-effort await process exit (up to 5 seconds)
        let exitCompleted = tryAwait exitTask (TimeSpan.FromSeconds(5.0))

        // Capture exit code if not already captured
        if exitCodeCell.Value.IsNone then
            try
                if not procObj.HasExited then
                    procObj.WaitForExit(1000) |> ignore
            with
            | _ -> ()
            try
                exitCodeCell.Value <- Some(procObj.ExitCode)
            with
            | _ -> ()

        // Best-effort await readers (up to 2 seconds each)
        let stdoutCompleted = tryAwaitOutcome stdoutTask (TimeSpan.FromSeconds(2.0))
        let stderrCompleted = tryAwaitOutcome stderrTask (TimeSpan.FromSeconds(2.0))

        // Capture remaining reader outcomes if not already captured
        if stdoutOutcomeCell.Value.IsNone then
            stdoutOutcomeCell.Value <- Some(tryReadOutcome stdoutTask)
        if stderrOutcomeCell.Value.IsNone then
            stderrOutcomeCell.Value <- Some(tryReadOutcome stderrTask)

        let stdoutComplete = stdoutCompleted && stdoutTask.IsCompleted
        let stderrComplete = stderrCompleted && stderrTask.IsCompleted

        // Dispose everything now that all tasks have settled
        tReg.Dispose()
        cReg.Dispose()
        lcts.Dispose()
        tcts.Dispose()
        try procObj.Dispose() with | _ -> ()

        // ---- Classify ----
        if cancelled then
            match killErrorCell.Value with
            | Some msg -> return Error(BoundedProcessFailure.KillFailed msg)
            | None -> return Error BoundedProcessFailure.Cancelled
        elif timedOut then
            match killErrorCell.Value with
            | Some msg -> return Error(BoundedProcessFailure.KillFailed msg)
            | None -> return Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)
        elif exitCodeCell.Value.IsNone then
            // Process wait failed
            match killErrorCell.Value with
            | Some msg -> return Error(BoundedProcessFailure.KillFailed msg)
            | None -> return Error(BoundedProcessFailure.WaitFailed "process exit wait failed")
        elif not stdoutComplete || not stderrComplete then
            // Cleanup grace expired without completion
            return Error(BoundedProcessFailure.IncompleteOutput(stdoutComplete, stderrComplete))
        else
            let stdoutFinal = stdoutOutcomeCell.Value.Value
            let stderrFinal = stderrOutcomeCell.Value.Value
            let code = exitCodeCell.Value.Value
            match stdoutFinal with
            | Overflowed _ -> return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
            | ReadFailed msg -> return Error(BoundedProcessFailure.StdoutReaderFailed msg)
            | ReadCancelled -> return Error BoundedProcessFailure.Cancelled
            | EofReached b ->
                match stderrFinal with
                | Overflowed _ -> return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                | ReadFailed msg -> return Error(BoundedProcessFailure.StderrReaderFailed msg)
                | ReadCancelled -> return Error BoundedProcessFailure.Cancelled
                | EofReached eb ->
                    if code = 0 then
                        return Ok { ExitCode = code; Stdout = b; Stderr = eb }
                    else
                        return Error(BoundedProcessFailure.NonZeroExit(code, b, eb))
    }

// -----------------------------------------------------------------------------
// Process runner (public API) — CORRECTION 09
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