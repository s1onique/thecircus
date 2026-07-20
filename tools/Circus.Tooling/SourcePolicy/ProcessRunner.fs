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
// ---------------------------------------------------------------------------

let mutable internal InjectStartAsyncFailure : (unit -> string option) = fun () -> None
let mutable internal InjectStartAsyncAccessFailure : (unit -> exn option) = fun () -> None
let mutable internal InjectStartAsyncStdoutDrainFailure : (unit -> exn option) = fun () -> None
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

/// Per-drain settle status.  The settle helper classifies each drain
/// independently so the aggregate can record every affected stream.
type private DrainSettleStatus =
    | SettledOk
    | SettledWithError
    | TimeoutOccurred

/// Aggregate drain-settlement result.  Carries the FULL list of
/// affected stream labels so the outcome can faithfully report which
/// streams timed out.
type private DrainSettleAggregate =
    | AllDrainsTerminal
    | DrainTimeout of labels: string list

/// Inspect the inner ``Result`` of an already-completed or just-waited
/// drain task.  ``Task.Wait`` returning ``true`` (or ``IsCompleted``
/// being ``true`` before a wait) does NOT mean the inner ``Result``
/// is ``Ok`` — a drain can ``RanToCompletion`` while reporting an
/// inner ``Result.Error``.  This helper records that error into the
/// cleanup note so it cannot be silently swallowed.
let private inspectTerminal
    (task: Task<Result<'a, exn>>)
    (label: string)
    (note: string ref)
    : DrainSettleStatus =
    match task.Result with
    | Result.Ok _ -> SettledOk
    | Result.Error ex ->
        appendNote note (sprintf "%s drain settled with error: %s" label ex.Message)
        SettledWithError

/// Boundedly settle a single drain task.
let private settleOneDrain
    (t: Task<Result<'a, exn>> option)
    (label: string)
    (remaining: TimeSpan)
    (note: string ref)
    : DrainSettleStatus =
    match t with
    | None -> SettledOk
    | Some task ->
        if task.IsCompleted then
            inspectTerminal task label note
        else if remaining <= TimeSpan.Zero then
            appendNote note (sprintf "%s drain did not settle (shared deadline already exhausted)" label)
            TimeoutOccurred
        else
            try
                if task.Wait(remaining) then
                    inspectTerminal task label note
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
                SettledWithError
            | ex ->
                appendNote note (sprintf "%s drain settle exception: %s" label ex.Message)
                SettledWithError

/// Settle BOTH stdout and stderr drains unconditionally.  The shared
/// deadline is captured up-front and threaded into each call; the
/// second call may see zero remaining time but the ``IsCompleted``
/// check in ``settleOneDrain`` still lets an already-terminal stderr
/// task be reported truthfully rather than as a timeout.
let private settleDrainsShared
    (stdout: Task<Result<byte[], exn>> option)
    (stderr: Task<Result<string, exn>> option)
    (note: string ref)
    : DrainSettleAggregate =
    let deadline = DateTime.UtcNow.Add DrainSettleTimeout
    let stdoutStatus = settleOneDrain stdout "stdout" (deadline - DateTime.UtcNow) note
    let stderrStatus = settleOneDrain stderr "stderr" (deadline - DateTime.UtcNow) note
    let timedOutLabels =
        [
            if stdoutStatus = TimeoutOccurred then yield "stdout"
            if stderrStatus = TimeoutOccurred then yield "stderr"
        ]
    match timedOutLabels with
    | [] -> AllDrainsTerminal
    | labels -> DrainTimeout labels

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

/// Structured failure returned by ``startAsync``.  Distinguishes:
///   * ``PreSpawnFailure`` — failure before ``Process.Start`` returned a
///     non-null process.
///   * ``ContextConstructionFailure`` — ``Process.Start`` succeeded but
///     a subsequent context-construction step threw; cleanup settled
///     successfully.
///   * ``ContextCleanupFailure`` — same, but cleanup also timed out.
///     The ``labels`` list carries every drain that timed out.
///
/// Without this distinction a post-``Process.Start`` failure with a
/// cleanup timeout would be mis-classified as ``SpawnFailure`` by
/// ``runCore``.
type private StartFailure =
    | PreSpawnFailure of detail: string
    | ContextConstructionFailure of detail: string
    | ContextCleanupFailure of detail: string * timedOutLabels: string list

/// Start a child process with exception-safe partial-acquisition ownership.
///
/// Uses a ``try/with`` block with explicit cleanup in the handler
/// (rather than a nested ``try/finally``) because the cleanup
/// sequence is conditional on whether any partial drain tasks exist.
///
/// Failure-injection points are exposed after each acquisition step:
///   * ``InjectStartAsyncAccessFailure`` — fires right after ``proc.Id``
///     but BEFORE any drain is created.
///   * ``InjectStartAsyncStdoutDrainFailure`` — fires AFTER the stdout
///     drain has been created but BEFORE the stderr drain.
///   * ``InjectStartAsyncObserverFailure`` — fires AFTER both drain
///     tasks have been created but BEFORE the observer publication.
let private startAsync (argv: string list) (workingDir: string option) (ct: CancellationToken) (note: string ref) : Result<AsyncProcessContext, StartFailure> =
    if List.isEmpty argv then
        Result.Error (PreSpawnFailure "argv is empty")
    else
        match InjectStartAsyncFailure() with
        | Some msg ->
            appendNote note (sprintf "spawn injected failure: %s" msg)
            Result.Error (PreSpawnFailure (sprintf "spawn injected failure: %s" msg))
        | None ->
            let psi = buildStartInfo argv workingDir
            let proc =
                try Process.Start(psi)
                with ex ->
                    appendNote note (sprintf "spawn failed: %s" ex.Message)
                    Unchecked.defaultof<Process>
            if isNull proc then
                appendNote note "Process.Start returned null"
                Result.Error (PreSpawnFailure "Process.Start returned null")
            else
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
                    ObserveStdoutDrainTask stdoutBytes

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

                    match InjectStartAsyncObserverFailure() with
                    | Some ex -> raise ex
                    | None -> ()

                    Result.Ok { Proc = proc; Pid = pid
                                StdoutBytes = stdoutBytes
                                StderrText = stderrText
                                StdoutText = stdoutText }
                with ex ->
                    appendNote note (sprintf "context construction failed: %s" ex.Message)
                    killTree proc note
                    let _ = waitBounded proc note
                    let detail = sprintf "context construction failed: %s" ex.Message
                    // Propagate the structured settlement result so
                    // ``runCore`` can produce a truthful
                    // ``BodyFailure`` / ``CleanupFailure`` outcome
                    // instead of collapsing every post-``Process.Start``
                    // failure into ``SpawnFailure``.
                    match settleDrainsShared partialStdout partialStderr note with
                    | AllDrainsTerminal ->
                        disposeProc proc note
                        Result.Error (ContextConstructionFailure detail)
                    | DrainTimeout labels ->
                        let timeoutDetail =
                            sprintf "%s AND %s drain(s) timed out during cleanup: %s"
                                detail
                                (String.concat ", " labels)
                                note.Value
                        disposeProc proc note
                        Result.Error (ContextCleanupFailure (timeoutDetail, labels))

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
/// Uses nested ``try ( try <body> with capture ) finally <cleanup>`` so
/// body exceptions are captured into ``bodyResult`` while the outer
/// ``finally`` guarantees that kill, bounded wait, drain-settle, and
/// dispose complete BEFORE the public outcome is constructed.
///
/// Cleanup order on every exit path (executed in the ``finally``):
///   1. ``killTree``            (Process.Kill(entireProcessTree=true)).
///   2. ``waitBounded``         (Process.WaitForExit bounded).
///   3. ``settleDrainsShared``  (BOTH drains inspected unconditionally;
///      a single shared deadline is threaded through both calls).
///   4. ``disposeProc``         (calls the injectable ``DisposeProcess``).
///
/// The settle step now returns a structured ``DrainSettleAggregate``
/// that carries every affected stream label.  ``runCore`` promotes
/// the outcome to ``CleanupFailure`` when a drain timed out, instead
/// of silently appending text and returning ``BodyFailure``.
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
            | Result.Error (PreSpawnFailure msg) ->
                bodyResult <- BodySpawnError msg
            | Result.Error (ContextConstructionFailure msg) ->
                // Post-``Process.Start`` failure but cleanup settled —
                // this is a body-stage failure, NOT a spawn failure.
                bodyResult <- BodyUnexpected msg
            | Result.Error (ContextCleanupFailure (msg, labels)) ->
                // Post-``Process.Start`` failure AND cleanup timed out.
                // Reflect the structured timeout in ``drainSettle``
                // so the post-finally outcome constructor sees it
                // even though startAsync's local drains are no longer
                // in scope here.
                drainSettle <- DrainTimeout labels
                bodyResult <- BodyUnexpected msg
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
        // Only re-run the settle if startAsync did not already
        // report a structured drain timeout; the startAsync path
        // already owns the partial drains and has settled them.
        if proc <> null then
            drainSettle <- settleDrainsShared stdoutDrain stderrDrain cleanupNote
        disposeProc proc cleanupNote

    let outcome =
        match bodyResult with
        | BodySpawnError msg -> SpawnFailure (msg, cleanupNote.Value)
        | BodyUnexpected msg ->
            match drainSettle with
            | DrainTimeout labels ->
                CleanupFailure (sprintf "%s drain(s) timed out during cleanup: %s [%s]"
                    (String.concat ", " labels) msg cleanupNote.Value)
            | AllDrainsTerminal ->
                BodyFailure (sprintf "body exception: %s [%s]" msg cleanupNote.Value, cleanupNote.Value)
        | BodyCompleted (verdict, _stdoutRaw, _stdoutText, _stderrText, stdoutOk, stderrOk) ->
            match drainSettle with
            | DrainTimeout labels ->
                CleanupFailure (sprintf "%s drain(s) timed out during cleanup: %s"
                    (String.concat ", " labels) cleanupNote.Value)
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