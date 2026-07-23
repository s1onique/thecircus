module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess

// =============================================================================
// Bounded-process core checkpoint — CORRECTION 02
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
    | ProcessExitedUnexpectedly of detail: string

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
// Bounded byte reader
// -----------------------------------------------------------------------------

type ReadOutcome =
    | BytesRead of bytes: byte array
    | LimitHit of bytes: byte array
    | ReadCancelled
    | ReadFailed of detail: string

let private readBounded
    (stream: Stream)
    (limitBytes: int)
    (cancellationToken: CancellationToken)
    : Task<ReadOutcome> =
    task {
        let bufferSize = min 4096 (max 1 (limitBytes + 1))
        let buffer = Array.zeroCreate<byte> bufferSize
        let collected = ResizeArray<byte>()

        try
            let mutable keepReading = true

            while keepReading && not cancellationToken.IsCancellationRequested do
                if collected.Count >= limitBytes then
                    keepReading <- false
                else
                    let bytesToRead = min bufferSize (limitBytes - collected.Count)
                    if bytesToRead <= 0 then
                        keepReading <- false
                    else
                        let! bytesRead = stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken)
                        if bytesRead = 0 then
                            keepReading <- false
                        else
                            for i = 0 to bytesRead - 1 do
                                collected.Add(buffer.[i])
                            if collected.Count > limitBytes then
                                keepReading <- false

            if cancellationToken.IsCancellationRequested then
                return ReadCancelled
            elif collected.Count > limitBytes then
                return LimitHit(collected.ToArray())
            else
                return BytesRead(collected.ToArray())

        with
        | :? OperationCanceledException ->
            return ReadCancelled
        | :? IOException as ex ->
            return ReadFailed ex.Message
        | :? ObjectDisposedException as ex ->
            return ReadFailed ex.Message
    }

// -----------------------------------------------------------------------------
// Helper: try kill process tree
// -----------------------------------------------------------------------------

let private tryKill (proc: Process) : BoundedProcessFailure option =
    try
        if not proc.HasExited then
            proc.Kill(entireProcessTree = true)
        proc.Dispose()
        None
    with
    | :? System.ComponentModel.Win32Exception as ex -> Some(BoundedProcessFailure.KillFailed ex.Message)
    | :? InvalidOperationException as ex -> Some(BoundedProcessFailure.KillFailed ex.Message)
    | :? System.NotSupportedException as ex -> Some(BoundedProcessFailure.KillFailed ex.Message)

// -----------------------------------------------------------------------------
// Helper: await remaining readers
// -----------------------------------------------------------------------------

let private awaitRemaining (stdoutTask: Task<ReadOutcome>) (stderrTask: Task<ReadOutcome>) : unit =
    try
        if not stdoutTask.IsCompleted then stdoutTask.Wait(1000) |> ignore
    with _ -> ()
    try
        if not stderrTask.IsCompleted then stderrTask.Wait(1000) |> ignore
    with _ -> ()

// -----------------------------------------------------------------------------
// Helper: extract bytes from ReadOutcome
// -----------------------------------------------------------------------------

let private extractBytes (outcome: ReadOutcome) : byte array =
    match outcome with
    | BytesRead b -> b
    | LimitHit b -> b
    | _ -> [||]

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

        // STEP 3: Setup start info
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

        // STEP 4: Start process
        let procOpt =
            try
                let proc = new Process()
                proc.StartInfo <- startInfo
                if proc.Start() then Some proc
                else
                    proc.Dispose()
                    None
            with
            | :? System.ComponentModel.Win32Exception -> None
            | :? System.IO.FileNotFoundException -> None
            | :? System.IO.DirectoryNotFoundException -> None

        match procOpt with
        | None ->
            return Error(BoundedProcessFailure.LaunchFailed(request.Executable, "Could not start process"))
        | Some proc ->

            proc.StandardInput.Close()

            use timeoutCts = new CancellationTokenSource(request.Limits.Timeout)
            use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)

            let stdoutTask = readBounded proc.StandardOutput.BaseStream request.Limits.StdoutLimitBytes linkedCts.Token
            let stderrTask = readBounded proc.StandardError.BaseStream request.Limits.StderrLimitBytes linkedCts.Token

            let! firstCompleted =
                Task.WhenAny(
                    stdoutTask,
                    stderrTask,
                    proc.WaitForExitAsync(CancellationToken.None)
                )

            // Determine what happened
            if firstCompleted = stdoutTask then
                let! out = stdoutTask
                match out with
                | LimitHit _ ->
                    tryKill proc |> ignore
                    awaitRemaining stdoutTask stderrTask
                    return Error(BoundedProcessFailure.StdoutLimitExceeded request.Limits.StdoutLimitBytes)
                | ReadFailed msg ->
                    tryKill proc |> ignore
                    awaitRemaining stdoutTask stderrTask
                    return Error(BoundedProcessFailure.StdoutReaderFailed msg)
                | ReadCancelled ->
                    tryKill proc |> ignore
                    awaitRemaining stdoutTask stderrTask
                    if timeoutCts.IsCancellationRequested then
                        return Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)
                    else
                        return Error(BoundedProcessFailure.Cancelled)
                | BytesRead _ ->
                    // Normal EOF on stdout, wait for process if needed
                    if not proc.HasExited then
                        do! proc.WaitForExitAsync(CancellationToken.None)
                    let exitCode = proc.ExitCode
                    tryKill proc |> ignore
                    let stdoutBytes = if stdoutTask.IsCompleted then extractBytes stdoutTask.Result else [||]
                    let stderrBytes = if stderrTask.IsCompleted then extractBytes stderrTask.Result else [||]
                    if exitCode = 0 then
                        return Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
                    else
                        return Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))

            elif firstCompleted = stderrTask then
                let! err = stderrTask
                match err with
                | LimitHit _ ->
                    tryKill proc |> ignore
                    awaitRemaining stdoutTask stderrTask
                    return Error(BoundedProcessFailure.StderrLimitExceeded request.Limits.StderrLimitBytes)
                | ReadFailed msg ->
                    tryKill proc |> ignore
                    awaitRemaining stdoutTask stderrTask
                    return Error(BoundedProcessFailure.StderrReaderFailed msg)
                | ReadCancelled ->
                    tryKill proc |> ignore
                    awaitRemaining stdoutTask stderrTask
                    if timeoutCts.IsCancellationRequested then
                        return Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)
                    else
                        return Error(BoundedProcessFailure.Cancelled)
                | BytesRead _ ->
                    // Normal EOF on stderr, wait for process if needed
                    if not proc.HasExited then
                        do! proc.WaitForExitAsync(CancellationToken.None)
                    let exitCode = proc.ExitCode
                    tryKill proc |> ignore
                    let stdoutBytes = if stdoutTask.IsCompleted then extractBytes stdoutTask.Result else [||]
                    let stderrBytes = if stderrTask.IsCompleted then extractBytes stderrTask.Result else [||]
                    if exitCode = 0 then
                        return Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
                    else
                        return Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))

            else
                // Process exited first
                if timeoutCts.IsCancellationRequested then
                    tryKill proc |> ignore
                    awaitRemaining stdoutTask stderrTask
                    return Error(BoundedProcessFailure.TimedOut request.Limits.Timeout)
                elif linkedCts.IsCancellationRequested then
                    tryKill proc |> ignore
                    awaitRemaining stdoutTask stderrTask
                    return Error(BoundedProcessFailure.Cancelled)
                else
                    // Normal exit
                    let exitCode = proc.ExitCode
                    tryKill proc |> ignore
                    let stdoutBytes = if stdoutTask.IsCompleted then extractBytes stdoutTask.Result else [||]
                    let stderrBytes = if stderrTask.IsCompleted then extractBytes stderrTask.Result else [||]
                    if exitCode = 0 then
                        return Ok { ExitCode = exitCode; Stdout = stdoutBytes; Stderr = stderrBytes }
                    else
                        return Error(BoundedProcessFailure.NonZeroExit(exitCode, stdoutBytes, stderrBytes))
    }
