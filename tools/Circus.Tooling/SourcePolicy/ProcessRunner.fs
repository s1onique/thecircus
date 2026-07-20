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
let internal DrainSettleTimeout : TimeSpan = TimeSpan.FromSeconds(2.0)

let private appendNote (note: string ref) (msg: string) : unit =
    if msg = "" then ()
    elif note.Value = "" then note.Value <- msg
    else note.Value <- sprintf "%s; %s" note.Value msg

// ---------------------------------------------------------------------------
// Failure-injection hooks and observation hooks.
//
// Production code never sets any of these; they exist solely for
// failure-injection tests.
// ---------------------------------------------------------------------------

let mutable internal InjectStartAsyncFailure : (unit -> string option) = fun () -> None
/// Inject an exception that fires immediately after the started
/// process has been assigned a PID but BEFORE any drain tasks are
/// created.
let mutable internal InjectStartAsyncAccessFailure : (unit -> exn option) = fun () -> None
/// Inject an exception that fires AFTER the stdout drain task has been
/// created but before stderr drain creation.  Used to exercise the
/// startAsync partial-acquisition window.
let mutable internal InjectStartAsyncStdoutDrainFailure : (unit -> exn option) = fun () -> None
/// Inject an exception that fires AFTER both drain tasks have been
/// created but before the observer publication.
let mutable internal InjectStartAsyncObserverFailure : (unit -> exn option) = fun () -> None
let mutable internal InjectWaitFailure : (unit -> exn option) = fun () -> None
let mutable internal InjectDrainFailure : (unit -> exn option) = fun () -> None

let mutable internal DisposeProcess : (Process -> unit) =
    fun (p: Process) -> p.Dispose()

let mutable internal ObserveStartedPid : int -> unit = fun _ -> ()
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

let private disposeProc (proc: Process) (note: string ref) : unit =
    try
        if not (isNull proc) then DisposeProcess proc
    with ex ->
        appendNote note (sprintf "dispose failed: %s" ex.Message)

/// Boundedly settle a single drain task.
///
/// Returns one of:
///   ``Settled``              — task completed (success or recorded error)
///                              inside the bound.
///   ``AlreadyTerminal``      — task was already terminal when checked,
///                              so the deadline was honoured without
///                              waiting.
///   ``TimeoutOccurred``      — wait timed out; task may still be
///                              running.  ``Task.Wait`` does NOT
///                              terminate the task.
///
/// ``settleDrainsShared`` aggregates the per-drain result and reports
/// whether any drain timed out so ``runCore`` can promote the outcome
/// to a structured ``CleanupFailure`` rather than silently swallowing
/// the timeout.
type private DrainSettleStatus =
    | Settled
    | AlreadyTerminal
    | TimeoutOccurred

let private settleOneDrain
    (t: Task<Result<'a, exn>> option)
    (label: string)
    (remaining: TimeSpan)
    (note: string ref)
    : DrainSettleStatus =
    match t with
    | None -> Settled
    | Some task ->
        // ``Task.Wait`` returns false on timeout without terminating
        // the task, and tasks that completed before the bound are
        // already terminal.  Check ``IsCompleted`` first so a
        // second-pass drain does not need a fresh wait when the
        // shared deadline has already elapsed.
        if task.IsCompleted then
            AlreadyTerminal
        else if remaining <= TimeSpan.Zero then
            appendNote note (sprintf "%s drain did not settle (shared deadline already exhausted)" label)
            TimeoutOccurred
        else
            try
                if task.Wait(remaining) then
                    match task.Result with
                    | Result.Ok _ -> Settled
                    | Result.Error e ->
                        appendNote note (sprintf "%s drain settled with error: %s" label e.Message)
                        Settled
                else
                    appendNote note (sprintf "%s drain did not settle within shared %O deadline" label DrainSettleTimeout)
                    TimeoutOccurred
            with
            | :? AggregateException as agg ->
                appendNote note
                    (sprintf "%s drain settle aggregate: %s" label
                        (agg.InnerExceptions
                         |> Seq.map (fun e -> e.Message)
                         |> String.concat "; "))
                Settled
            | ex ->
                appendNote note (sprintf "%s drain settle exception: %s" label ex.Message)
                Settled

/// Aggregate of per-drain settle results.  Used by ``runCore`` to
/// decide whether to promote the outcome to ``CleanupFailure`` when
/// any drain timed out.
type private DrainSettleAggregate =
    | AllDrainsTerminal
    | DrainTimeout of label: string

let private settleDrainsShared
    (stdout: Task<Result<byte[], exn>> option)
    (stderr: Task<Result<string, exn>> option)
    (note: string ref)
    : DrainSettleAggregate =
    let deadline = DateTime.UtcNow.Add DrainSettleTimeout
    let stdoutStatus = settleOneDrain stdout "stdout" (deadline - DateTime.UtcNow) note
    if stdoutStatus = TimeoutOccurred then DrainTimeout "stdout"
    else
        let stderrStatus = settleOneDrain stderr "stderr" (deadline - DateTime.UtcNow) note
        if stderrStatus = TimeoutOccurred then DrainTimeout "stderr"
        else AllDrainsTerminal

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

/// Start a child process with exception-safe partial-acquisition ownership.
///
/// The body is bracketed by an inner ``try/with`` that retains local
/// ``partialStdout`` / ``partialStderr`` task options so any drain
/// task that has been created before a later operation throws is still
/// settled (boundedly) inside the catch branch — alongside the
/// kill, bounded wait, and dispose that always run.
///
/// Failure-injection points are exposed after each acquisition step:
///   * ``InjectStartAsyncAccessFailure`` — fires right after ``proc.Id``
///     but BEFORE any drain is created.
///   * ``InjectStartAsyncStdoutDrainFailure`` — fires AFTER the stdout
///     drain has been created but BEFORE the stderr drain.
///   * ``InjectStartAsyncObserverFailure`` — fires AFTER both drain
///     tasks have been created but BEFORE the observer publication.
let private startAsync (argv: string list) (workingDir: string option) (ct: CancellationToken) (note: string ref) : Result<AsyncProcessContext, string> =
    if List.isEmpty argv then
        Result.Error "argv is empty"
    else
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
                // Acquisition bracket for partially-created resources.
                let mutable partialStdout : Task<Result<byte[], exn>> option = None
                let mutable partialStderr : Task<Result<string, exn>> option = None
                try
                    let pid = proc.Id
                    ObserveStartedPid pid
                    match InjectStartAsyncAccessFailure() with
                    | Some ex -> raise ex
                    | None -> ()

                    let stdoutBytes = drainBytesAsync proc.StandardOutput.BaseStream ct
                    partialStdout <- Some stdoutBytes
                    // Publish the stdout drain task BEFORE the post-stdout
                    // injection so a test can capture and assert the
                    // task's terminal state even when the injection
                    // throws.
                    ObserveStdoutDrainTask stdoutBytes

                    // Post-stdout-drain injection: exercises the window
                    // where one drain has been created and the partial
                    // bracket must settle it.
                    match InjectStartAsyncStdoutDrainFailure() with
                    | Some ex -> raise ex
                    | None -> ()

                    let stderrText = drainTextAsync proc.StandardError.BaseStream ct
                    partialStderr <- Some stderrText
                    ObserveStderrDrainTask stderrText

                    let stdoutText = task {
                        let! r = stdoutBytes
                        return
                            match r with
                            | Result.Ok b -> Result.Ok (Encoding.UTF8.GetString b)
                            | Result.Error e -> Result.Error e
                    }

                    // Post-both-drains injection: exercises the window
                    // where both drains exist but the observer
                    // publication has not yet happened.
                    match InjectStartAsyncObserverFailure() with
                    | Some ex -> raise ex
                    | None -> ()

                    Result.Ok { Proc = proc; Pid = pid
                                StdoutBytes = stdoutBytes
                                StderrText = stderrText
                                StdoutText = stdoutText }
                with ex ->
                    appendNote note (sprintf "context construction failed: %s" ex.Message)
                    // Partial-acquisition cleanup: kill + bounded wait
                    // + settle partially-created drains + dispose, so a
                    // drain task created before the throw is also
                    // settled (boundedly) before the caller is told the
                    // context failed to construct.
                    killTree proc note
                    let _ = waitBounded proc note
                    let _ = settleDrainsShared partialStdout partialStderr note
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
/// Cleanup order on every exit path (executed in the ``finally``):
///   1. ``killTree``            (Process.Kill(entireProcessTree=true)).
///   2. ``waitBounded``         (Process.WaitForExit bounded).
///   3. ``settleDrainsShared``  (drains share a single
///      ``DrainSettleTimeout``; the kill in step 1 closes streams so
///      ``ReadAsync`` returns naturally; the bounded wait in step 2
///      limits the kill itself).
///   4. ``disposeProc``         (calls the injectable ``DisposeProcess``).
///
/// The settle step now returns a structured result.  When a drain
/// times out, ``runCore`` promotes the outcome to ``CleanupFailure``
/// instead of silently appending text to the cleanup note, so callers
/// can mechanically distinguish "drain settled" from "drain timed
/// out, may still be running".
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

    let mutable drainSettle : DrainSettleAggregate = AllDrainsTerminal

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
        killTree proc cleanupNote
        let _ = waitBounded proc cleanupNote
        drainSettle <- settleDrainsShared stdoutDrain stderrDrain cleanupNote
        disposeProc proc cleanupNote

    let outcome =
        match bodyResult with
        | BodySpawnError msg -> SpawnFailure (msg, cleanupNote.Value)
        | BodyUnexpected msg ->
            match drainSettle with
            | DrainTimeout label ->
                // A drain timed out AND the body threw — promote to a
                // structured CleanupFailure so the partial-acquisition
                // or exceptional drain situation is not silently
                // collapsed into BodyFailure.
                CleanupFailure (sprintf "%s drain timed out during cleanup: %s [%s]" label msg cleanupNote.Value)
            | AllDrainsTerminal ->
                BodyFailure (sprintf "body exception: %s [%s]" msg cleanupNote.Value, cleanupNote.Value)
        | BodyCompleted (verdict, _stdoutRaw, _stdoutText, _stderrText, stdoutOk, stderrOk) ->
            match drainSettle with
            | DrainTimeout label ->
                CleanupFailure (sprintf "%s drain timed out during cleanup: %s" label cleanupNote.Value)
            | AllDrainsTerminal ->
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