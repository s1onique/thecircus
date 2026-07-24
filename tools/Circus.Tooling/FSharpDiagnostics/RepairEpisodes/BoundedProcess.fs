module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess
#nowarn "3511"

// =============================================================================
// Bounded-process core — CORRECTION 08
//
// Addresses P0 issues from CORRECTION 07:
// - Discarded WaitAny winner → exact task identity with Object.ReferenceEquals
// - Normal EOF disables timeout/cancellation → event loop continues racing
// - Cleanup can block forever → all sync waits replaced with direct awaits
// - Thread-blocking Task.Run → one task {} expression with let! await
// - Reader faults misclassified → specific try/catch on Result access
// - Launch failure can leak Process → process disposed on all paths
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
// Try read a task result without throwing
// -----------------------------------------------------------------------------

let private tryReadResult (t: Task<'T>) : Result<'T, exn> =
    if t.IsCompletedSuccessfully then
        Ok t.Result
    elif t.IsCanceled then
        Error (OperationCanceledException())
    elif t.IsFaulted then
        Error t.Exception
    else
        Error (InvalidOperationException("task not completed"))

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
    | ex ->
        procObj.Dispose()
        Error(BoundedProcessFailure.LaunchFailed(request.Executable, ex.Message))

// -----------------------------------------------------------------------------
// Mutable cell helper for mutable state inside task CE
// -----------------------------------------------------------------------------

type MutableCell<'T> =
    { mutable Value: 'T }

// -----------------------------------------------------------------------------
// Inner event loop body (helper to avoid indentation issues)
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

        // Event loop
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
                match tryReadResult stdoutTask with
                | Ok outcome ->
                    stdoutOutcomeCell.Value <- Some outcome
                    if isTerminal outcome then killNow()
                | Error _ ->
                    stdoutOutcomeCell.Value <- Some ReadCancelled
                    killNow()
            elif stderrOutcome.IsNone && Object.ReferenceEquals(winner, stderrTask) then
                match tryReadResult stderrTask with
                | Ok outcome ->
                    stderrOutcomeCell.Value <- Some outcome
                    if isTerminal outcome then killNow()
                | Error _ ->
                    stderrOutcomeCell.Value <- Some ReadCancelled
                    killNow()
            elif exitCode.IsNone && Object.ReferenceEquals(winner, exitTask) then
                try
                    exitCodeCell.Value <- Some(procObj.ExitCode)
                with
                | _ -> exitCodeCell.Value <- Some(-1)

            let sOut = stdoutOutcomeCell.Value
            let sErr = stderrOutcomeCell.Value
            let eC = exitCodeCell.Value
            let stdoutTerminal = sOut.IsSome && isTerminal(sOut.Value)
            let stderrTerminal = sErr.IsSome && isTerminal(sErr.Value)
            loopDone <-
                (eC.IsSome && sOut.IsSome && sErr.IsSome)
                || stdoutTerminal
                || stderrTerminal
                || timedOut
                || cancelled

        // Drain remaining readers and exit if not yet observed
        if stdoutOutcomeCell.Value.IsNone then
            stdoutOutcomeCell.Value <-
                match tryReadResult stdoutTask with
                | Ok outcome -> Some outcome
                | Error _ -> Some ReadCancelled
        if stderrOutcomeCell.Value.IsNone then
            stderrOutcomeCell.Value <-
                match tryReadResult stderrTask with
                | Ok outcome -> Some outcome
                | Error _ -> Some ReadCancelled
        if exitCodeCell.Value.IsNone then
            try
                exitCodeCell.Value <- Some(procObj.ExitCode)
            with
            | _ -> exitCodeCell.Value <- Some(-1)

        // Dispose
        tReg.Dispose()
        cReg.Dispose()
        lcts.Dispose()
        tcts.Dispose()
        procObj.Dispose()

        // Classify
        if cancelled then
            match killErrorCell.Value with
            | Some msg -> return Error(BoundedProcessFailure.KillFailed msg)
            | None -> return Error BoundedProcessFailure.Cancelled
        elif timedOut then
            match killErrorCell.Value with
            | Some msg -> return Error(BoundedProcessFailure.KillFailed msg)
            | None -> return Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)
        elif killErrorCell.Value.IsSome then
            return Error(BoundedProcessFailure.KillFailed killErrorCell.Value.Value)
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
// Process runner (public API) — CORRECTION 08
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
            let lcts : CancellationTokenSource option =
                try
                    Some(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tcts.Token))
                with
                | _ ->
                    tcts.Dispose()
                    procObj.Dispose()
                    None

            let cancelledResult : Task<Result<BoundedProcessSuccess, BoundedProcessFailure>> =
                Task.FromResult(Error BoundedProcessFailure.Cancelled)
            match lcts with
            | None -> cancelledResult
            | Some lcts ->
                let timeoutTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                let cancelTcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                let tReg = tcts.Token.Register(fun () -> timeoutTcs.TrySetResult(true) |> ignore)
                let cReg = cancellationToken.Register(fun () -> cancelTcs.TrySetResult(true) |> ignore)

                let stdoutTask = readBoundedAsync procObj.StandardOutput.BaseStream request.Limits.StdoutLimitBytes lcts.Token |> Async.StartAsTask
                let stderrTask = readBoundedAsync procObj.StandardError.BaseStream request.Limits.StderrLimitBytes lcts.Token |> Async.StartAsTask
                let exitTask = procObj.WaitForExitAsync()

                executeLifecycle procObj lcts request timeoutTcs cancelTcs stdoutTask stderrTask exitTask tReg cReg tcts
