module Circus.Tooling.SourcePolicy.ProcessRunner

/// Governed child-process runner used by the source-policy verifier.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

type ProcessOutcome =
    | SpawnFailure of detail: string * cleanupNote: string
    | Exited of exitCode: int * cleanupNote: string
    | NonzeroExit of exitCode: int * cleanupNote: string
    | Cancelled of cleanupNote: string
    | CleanupFailure of detail: string
    | OutputFailure of detail: string * cleanupNote: string
    | BodyFailure of detail: string * cleanupNote: string

type BytesResult = {
    Outcome: ProcessOutcome
    Output: byte[]
    Stderr: string
    Pid: int option
    DescendantPid: int option
}

type TextResult = {
    Outcome: ProcessOutcome
    Output: string
    Stderr: string
    Pid: int option
    DescendantPid: int option
}

let internal CleanupTimeout : TimeSpan = TimeSpan.FromSeconds(5.0)
/// Shared bounded time the cleanup bracket waits for in-flight drain
/// tasks to settle.  Both stdout and stderr drains share this single
/// deadline so two drains do not independently consume two seconds each.
let internal DrainSettleTimeout : TimeSpan = TimeSpan.FromSeconds(2.0)

let private appendNote (note: string ref) (msg: string) : unit =
    if msg = "" then ()
    elif note.Value = "" then note.Value <- msg
    else note.Value <- sprintf "%s; %s" note.Value msg

// ---------------------------------------------------------------------------
// Failure-injection hooks and observation hooks.
//
// Each hook is a mutable ``internal`` function the test suite can flip
// from its default to exercise the corresponding lifecycle stage.  The
// observation hooks publish the drain tasks so a focused test can prove
// they have reached a terminal state.  Production code never sets any
// of these; they exist solely for failure-injection tests.
// ---------------------------------------------------------------------------

let mutable internal InjectStartAsyncFailure : (unit -> string option) = fun () -> None
let mutable internal InjectStartAsyncAccessFailure : (unit -> exn option) = fun () -> None
let mutable internal InjectWaitFailure : (unit -> exn option) = fun () -> None
let mutable internal InjectDrainFailure : (unit -> exn option) = fun () -> None

/// Test-only operation that performs the actual disposal.  Production
/// installs the real ``Process.Dispose``.  A test can replace this with
/// a throwing function to exercise the catch-and-record branch inside
/// ``disposeProc`` with a real exception.
let mutable internal DisposeProcess : (Process -> unit) =
    fun (p: Process) -> p.Dispose()

/// Test-only observation hook fired immediately after a started process
/// has been assigned a PID.
let mutable internal ObserveStartedPid : int -> unit = fun _ -> ()

/// Test-only observation hooks fired when the stdout / stderr drain
/// tasks are created inside ``startAsync``.  Tests can save the tasks
/// and assert they are terminal (``IsCompleted``) once ``runCore``
/// returns.
let mutable internal ObserveStdoutDrainTask : (Task<Result<byte[], exn>> -> unit) = ignore
let mutable internal ObserveStderrDrainTask : (Task<Result<string, exn>> -> unit) = ignore

let private killTree (proc: Process) (note: string ref) : unit =
    try
        if not (isNull proc) && not proc.HasExited then
            proc.Kill(true)
    with ex ->
        appendNote note (sprintf "kill failed: %s" ex.Message)

let private waitBounded (proc: Process) (note: string ref) : bool =
    try
        if not (isNull proc) then
            proc.WaitForExit(int CleanupTimeout.TotalMilliseconds)
        else true
    with ex ->
        appendNote note (sprintf "wait bounded failed: %s" ex.Message)
        false

/// Dispose the process via the injectable ``DisposeProcess`` operation
/// so a test can replace it with a throwing function and exercise the
/// real catch-and-record branch.
let private disposeProc (proc: Process) (note: string ref) : unit =
    try
        if not (isNull proc) then DisposeProcess proc
    with ex ->
        appendNote note (sprintf "dispose failed: %s" ex.Message)

/// Boundedly settle a single drain task.  Returns ``true`` when the
/// task completed inside the bound; ``false`` when the wait timed out
/// or the task threw while being awaited.  Either way, cleanup is
/// guaranteed to finish inside ``remaining`` so a damaged stream
/// cannot make the cleanup bracket indefinite.
let private settleOneDrain
    (t: Task<Result<'a, exn>> option)
    (label: string)
    (remaining: TimeSpan)
    (note: string ref)
    : bool =
    match t with
    | None -> true
    | Some task ->
        try
            if remaining > TimeSpan.Zero && task.Wait(remaining) then
                match task.Result with
                | Result.Ok _ -> true
                | Result.Error e ->
                    appendNote note (sprintf "%s drain settled with error: %s" label e.Message)
                    false
            else
                appendNote note (sprintf "%s drain did not settle within shared %O deadline" label DrainSettleTimeout)
                false
        with
        | :? AggregateException as agg ->
            appendNote note
                (sprintf "%s drain settle aggregate: %s" label
                    (agg.InnerExceptions
                     |> Seq.map (fun e -> e.Message)
                     |> String.concat "; "))
            false
        | ex ->
            appendNote note (sprintf "%s drain settle exception: %s" label ex.Message)
            false

/// Settle both stdout and stderr drains under a single shared deadline
/// so two slow drains do not independently consume ``DrainSettleTimeout``
/// each.  The deadline is captured up-front so the second drain sees the
/// remaining time.
let private settleDrainsShared
    (stdout: Task<Result<byte[], exn>> option)
    (stderr: Task<Result<string, exn>> option)
    (note: string ref)
    : unit =
    let deadline = DateTime.UtcNow.Add DrainSettleTimeout
    let stdoutOk = settleOneDrain stdout "stdout" (deadline - DateTime.UtcNow) note
    let stderrOk = settleOneDrain stderr "stderr" (deadline - DateTime.UtcNow) note
    let _ = stdoutOk
    let _ = stderrOk
    ()

let private drainBytesAsync (stream: Stream) (cancellationToken: CancellationToken) : Task<Result<byte[], exn>> =
    task {
        use ms = new MemoryStream()
        let buffer : byte[] = Array.zeroCreate 8192
        let mutable finished = false
        let mutable firstError : exn option = None
        while not finished do
            try
                let! read = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                if read <= 0 then finished <- true
                else ms.Write(buffer, 0, read) |> ignore
            with
            | :? OperationCanceledException ->
                finished <- true
            | ex ->
                firstError <- Some ex
                finished <- true
        return
            match firstError with
            | Some e -> Result.Error e
            | None -> Result.Ok (ms.ToArray())
    }

let private drainTextAsync (stream: Stream) (cancellationToken: CancellationToken) : Task<Result<string, exn>> =
    task {
        use ms = new MemoryStream()
        let buffer : byte[] = Array.zeroCreate 8192
        let mutable finished = false
        let mutable firstError : exn option = None
        while not finished do
            try
                let! read = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                if read <= 0 then finished <- true
                else ms.Write(buffer, 0, read) |> ignore
            with
            | :? OperationCanceledException ->
                finished <- true
            | ex ->
                firstError <- Some ex
                finished <- true
        return
            match firstError with
            | Some e -> Result.Error e
            | None -> Result.Ok (Encoding.UTF8.GetString(ms.ToArray()))
    }

let buildStartInfo (argv: string list) (workingDir: string option) : ProcessStartInfo =
    match argv with
    | [] -> invalidArg "argv" "argv must contain at least the executable name"
    | exe :: rest ->
        let psi = ProcessStartInfo()
        psi.FileName <- exe
        psi.ArgumentList.Clear()
        for a in rest do psi.ArgumentList.Add(a)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        match workingDir with
        | Some d -> psi.WorkingDirectory <- d
        | None -> ()
        psi

type private AsyncProcessContext = {
    Proc: Process
    Pid: int
    StdoutBytes: Task<Result<byte[], exn>>
    StderrText: Task<Result<string, exn>>
    StdoutText: Task<Result<string, exn>>
}

let private startAsync (argv: string list) (workingDir: string option) (ct: CancellationToken) (note: string ref) : Result<AsyncProcessContext, string> =
    if List.isEmpty argv then
        Result.Error "argv is empty"
    else
        // Failure injection hook for the "spawn failed" branch.
        match InjectStartAsyncFailure() with
        | Some msg ->
            appendNote note (sprintf "spawn injected failure: %s" msg)
            Result.Error (sprintf "spawn injected failure: %s" msg)
        | None ->
            let psi = buildStartInfo argv workingDir
            let proc =
                try Process.Start(psi)
                with ex ->
                    appendNote note (sprintf "spawn failed: %s" ex.Message)
                    Unchecked.defaultof<Process>
            if isNull proc then
                appendNote note "Process.Start returned null"
                Result.Error "Process.Start returned null"
            else
                // After ``Process.Start`` succeeds but before the owned
                // context is returned, an exception (real or injected)
                // would historically leak the freshly-started process.
                // That whole window is now bracketed: if anything throws,
                // we still dispose the started process before propagating
                // the error to the caller.
                try
                    let pid = proc.Id
                    ObserveStartedPid pid
                    match InjectStartAsyncAccessFailure() with
                    | Some ex -> raise ex
                    | None -> ()
                    let stdoutBytes = drainBytesAsync proc.StandardOutput.BaseStream ct
                    let stderrText = drainTextAsync proc.StandardError.BaseStream ct
                    let stdoutText = task {
                        let! r = stdoutBytes
                        return
                            match r with
                            | Result.Ok b -> Result.Ok (Encoding.UTF8.GetString b)
                            | Result.Error e -> Result.Error e
                    }
                    // Publish the drain tasks so a focused test can prove
                    // they reach a terminal state once ``runCore`` returns.
                    ObserveStdoutDrainTask stdoutBytes
                    ObserveStderrDrainTask stderrText
                    Result.Ok { Proc = proc; Pid = pid
                                StdoutBytes = stdoutBytes
                                StderrText = stderrText
                                StdoutText = stdoutText }
                with ex ->
                    appendNote note (sprintf "context construction failed: %s" ex.Message)
                    // Exception-safe: kill, boundedly wait, dispose the
                    // already-started process before propagating the
                    // error to the caller.
                    killTree proc note
                    let _ = waitBounded proc note
                    disposeProc proc note
                    Result.Error (sprintf "context construction failed: %s" ex.Message)

let private waitForExitAsync (ctx: AsyncProcessContext) (cancellationToken: CancellationToken) (note: string ref) : Async<Result<int, string>> =
    async {
        try
            do! ctx.Proc.WaitForExitAsync(cancellationToken) |> Async.AwaitTask
            let mutable code = 0
            try code <- ctx.Proc.ExitCode with _ -> ()
            return Result.Ok code
        with
        | :? OperationCanceledException ->
            killTree ctx.Proc note
            let ok = waitBounded ctx.Proc note
            if ok then
                appendNote note "cancelled by token"
                return Result.Error "cancelled"
            else
                appendNote note "bounded cleanup wait timed out"
                return Result.Error "cleanup_timeout"
        | ex ->
            killTree ctx.Proc note
            let _ = waitBounded ctx.Proc note
            appendNote note (sprintf "wait exception: %s" ex.Message)
            return Result.Error (sprintf "wait_failed: %s" ex.Message)
    }

type private CleanupObservation = {
    Notes: string
}

type private BodyResult =
    | BodySpawnError of detail: string
    | BodyCompleted of
        verdict : Result<int, string> *
        stdoutRaw : byte[] *
        stdoutText : string *
        stderrText : string *
        stdoutOk : bool *
        stderrOk : bool
    | BodyUnexpected of detail: string

let private finalize (verdict: Result<int, string>) (stdoutOk: bool) (stderrOk: bool) (cleanup: CleanupObservation) : ProcessOutcome =
    let note = cleanup.Notes
    if not stdoutOk then
        OutputFailure (sprintf "stdout drain failed: %s" (if note = "" then "<no note>" else note), note)
    elif not stderrOk then
        OutputFailure (sprintf "stderr drain failed: %s" (if note = "" then "<no note>" else note), note)
    else
        match verdict with
        | Result.Ok 0 -> Exited (0, note)
        | Result.Ok n -> NonzeroExit (n, note)
        | Result.Error "cancelled" -> Cancelled note
        | Result.Error "cleanup_timeout" ->
            CleanupFailure (sprintf "bounded cleanup wait timed out: %s" note)
        | Result.Error other ->
            CleanupFailure (sprintf "%s: %s" other note)

/// Run the child process with exception-safe ownership.
///
/// The lifecycle is bracketed by ``try``/``finally`` so that every
/// process that ``runCore`` causes to be created is released (killed,
/// boundedly reaped, drain-settled, and disposed) BEFORE the public
/// outcome is constructed.  No cleanup runs after the outcome exists.
///
/// F# does not allow combining ``try/with`` and ``try/finally`` in a
/// single expression, so the bracket is structured as nested
/// ``try ( try <body> with capture ) finally <cleanup>`` so that cleanup
/// runs after the body has settled regardless of whether it threw.
///
/// Cleanup order on every exit path (executed in the ``finally``):
///   1. ``killTree``         (Process.Kill(entireProcessTree=true)).
///   2. ``waitBounded``      (Process.WaitForExit bounded to ``CleanupTimeout``).
///   3. ``settleDrainsShared`` (drain tasks share a single
///      ``DrainSettleTimeout``; the kill in step 1 closes the streams
///      so drain ``ReadAsync`` returns naturally, and the bounded wait
///      in step 2 limits the kill itself).
///   4. ``disposeProc``      (calls the injectable ``DisposeProcess``;
///      any throw is caught into the cleanup note).
///
/// The kill-then-settle order is essential: settling drains before the
/// process is killed leaves the drains blocked on ``ReadAsync`` waiting
/// for stream closure, which only happens after the kill anyway, so a
/// pre-kill settle would just consume the shared deadline for no
/// benefit.  By killing first the streams close, the drain tasks reach
/// a terminal state quickly, and the bounded settle only needs to mop
/// up any straggling work.
let private runCore
    (argv: string list)
    (workingDir: string option)
    (cancellationToken: CancellationToken)
    : Result<ProcessOutcome * byte[] * string * int * bool * string, string> =
    let cleanupNote = ref ""
    let mutable proc : Process = null
    let mutable stdoutDrain : Task<Result<byte[], exn>> option = None
    let mutable stderrDrain : Task<Result<string, exn>> option = None
    let mutable pid : int option = None

    let mutable bodyResult : BodyResult = BodySpawnError "not started"

    let mutable capturedStdoutRaw : byte[] = [||]
    let mutable capturedStderrText : string = ""
    let mutable capturedStderrOk : bool = true

    try
        try
            match startAsync argv workingDir cancellationToken cleanupNote with
            | Result.Error msg ->
                bodyResult <- BodySpawnError msg
            | Result.Ok ctx ->
                proc <- ctx.Proc
                pid <- Some ctx.Pid
                stdoutDrain <- Some ctx.StdoutBytes
                stderrDrain <- Some ctx.StderrText

                match InjectWaitFailure() with
                | Some ex -> raise ex
                | None -> ()

                let verdict =
                    waitForExitAsync ctx cancellationToken cleanupNote
                    |> Async.RunSynchronously

                match InjectDrainFailure() with
                | Some ex -> raise ex
                | None -> ()

                let mutable stdoutRaw : byte[] = [||]
                let mutable stderrText : string = ""
                let mutable stdoutOk = true
                let mutable stderrOk = true
                (match stdoutDrain with
                 | Some t ->
                     try
                         match t.Result with
                         | Result.Ok data -> stdoutRaw <- data
                         | Result.Error e ->
                             stdoutOk <- false
                             appendNote cleanupNote (sprintf "stdout drain failed: %s" e.Message)
                     with ex ->
                         stdoutOk <- false
                         appendNote cleanupNote (sprintf "stdout drain await failed: %s" ex.Message)
                 | None -> ())
                (match stderrDrain with
                 | Some t ->
                     try
                         match t.Result with
                         | Result.Ok s -> stderrText <- s
                         | Result.Error e ->
                             stderrOk <- false
                             appendNote cleanupNote (sprintf "stderr drain failed: %s" e.Message)
                     with ex ->
                         stderrOk <- false
                         appendNote cleanupNote (sprintf "stderr drain await failed: %s" ex.Message)
                 | None -> ())

                let stdoutText = Encoding.UTF8.GetString stdoutRaw
                capturedStdoutRaw <- stdoutRaw
                capturedStderrText <- stderrText
                capturedStderrOk <- stderrOk
                bodyResult <- BodyCompleted (verdict, stdoutRaw, stdoutText, stderrText, stdoutOk, stderrOk)
        with ex ->
            appendNote cleanupNote (sprintf "body exception: %s" ex.Message)
            bodyResult <- BodyUnexpected ex.Message
    finally
        // Exceptional cleanup order: kill first (which closes streams),
        // then boundedly wait for the parent exit, then drain-settle the
        // redirected streams under a single shared deadline, then
        // dispose.  This guarantees the drain tasks reach a terminal
        // state BEFORE the public outcome is constructed.
        killTree proc cleanupNote
        let _ = waitBounded proc cleanupNote
        settleDrainsShared stdoutDrain stderrDrain cleanupNote
        disposeProc proc cleanupNote

    let outcome =
        match bodyResult with
        | BodySpawnError msg -> SpawnFailure (msg, cleanupNote.Value)
        | BodyUnexpected msg ->
            BodyFailure (sprintf "body exception: %s [%s]" msg cleanupNote.Value, cleanupNote.Value)
        | BodyCompleted (verdict, _stdoutRaw, _stdoutText, _stderrText, stdoutOk, stderrOk) ->
            let cleanup = { Notes = cleanupNote.Value }
            finalize verdict stdoutOk stderrOk cleanup

    Ok (outcome, capturedStdoutRaw, capturedStderrText, (defaultArg pid 0), capturedStderrOk, cleanupNote.Value)

let runProcessBytes (argv: string list) (workingDir: string option) (cancellationToken: CancellationToken) : BytesResult =
    match runCore argv workingDir cancellationToken with
    | Result.Ok (outcome, output, stderr, pid, _, _) ->
        { Outcome = outcome
          Output = output
          Stderr = stderr
          Pid = if pid = 0 then None else Some pid
          DescendantPid = None }
    | Result.Error _ ->
        { Outcome = SpawnFailure ("internal: runCore returned error", "")
          Output = [||]
          Stderr = ""
          Pid = None
          DescendantPid = None }

let runProcessText (argv: string list) (workingDir: string option) (cancellationToken: CancellationToken) : TextResult =
    match runCore argv workingDir cancellationToken with
    | Result.Ok (outcome, output, stderr, pid, _, _) ->
        let text = Encoding.UTF8.GetString(output)
        { Outcome = outcome
          Output = text
          Stderr = stderr
          Pid = if pid = 0 then None else Some pid
          DescendantPid = None }
    | Result.Error _ ->
        { Outcome = SpawnFailure ("internal: runCore returned error", "")
          Output = ""
          Stderr = ""
          Pid = None
          DescendantPid = None }

let isPidAlive (pid: int) : bool =
    try
        let p = Process.GetProcessById(pid)
        let alive = not p.HasExited
        p.Dispose()
        alive
    with _ -> false