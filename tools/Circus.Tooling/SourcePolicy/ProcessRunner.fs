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
/// Bounded time the cleanup bracket waits for in-flight drain tasks to
/// settle.  Must be finite so a damaged stream cannot make cleanup
/// indefinite.
let internal DrainSettleTimeout : TimeSpan = TimeSpan.FromSeconds(2.0)

let private appendNote (note: string ref) (msg: string) : unit =
    if msg = "" then ()
    elif note.Value = "" then note.Value <- msg
    else note.Value <- sprintf "%s; %s" note.Value msg

// ---------------------------------------------------------------------------
// Failure injection hooks.
//
// Each hook is a mutable ``internal`` function the test suite can flip from
// its default ``None`` to ``Some`` to force the corresponding lifecycle
// stage to fail.  Production code never sets them; they exist solely so
// failure-injection tests can exercise the exception-safe ownership bracket
// inside ``runCore`` and ``startAsync``.  All hooks are reset-safe because
// they are functions, not flags.
// ---------------------------------------------------------------------------

let mutable internal InjectStartAsyncFailure : (unit -> string option) = fun () -> None
let mutable internal InjectStartAsyncAccessFailure : (unit -> exn option) = fun () -> None
let mutable internal InjectWaitFailure : (unit -> exn option) = fun () -> None
let mutable internal InjectDrainFailure : (unit -> exn option) = fun () -> None
let mutable internal InjectDisposeFailure : (unit -> exn option) = fun () -> None

/// Test-only observation hook fired immediately after a started process
/// has been assigned a PID.  Production code never installs anything
/// other than ``ignore``.  Tests use this to capture the PID of a
/// process that was started and then immediately released by the
/// context-construction cleanup bracket, so they can assert the OS
/// reaped it (rather than accepting ``None`` and silently passing).
let mutable internal ObserveStartedPid : int -> unit = fun _ -> ()

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

/// Dispose the process.  Honours the ``InjectDisposeFailure`` hook so a
/// test can exercise the real catch-and-record branch instead of merely
/// appending a synthetic note and then calling the real dispose.  When
/// the injection is active the real ``Process.Dispose`` call is skipped
/// to faithfully reproduce a dispose-time exception.
let private disposeProc (proc: Process) (note: string ref) : unit =
    match InjectDisposeFailure() with
    | Some ex ->
        appendNote note (sprintf "dispose injected failure: %s" ex.Message)
    | None ->
        try
            if not (isNull proc) then proc.Dispose()
        with ex ->
            appendNote note (sprintf "dispose failed: %s" ex.Message)

/// Boundedly settle a single drain task.  Returns ``true`` when the
/// task completed inside the bound; ``false`` when the wait timed out
/// or the task threw while being awaited.  Either way, cleanup is
/// guaranteed to finish inside ``DrainSettleTimeout`` so a damaged
/// stream cannot make the cleanup bracket indefinite.
let private settleDrain (t: Task<Result<'a, exn>> option) (label: string) (note: string ref) : bool =
    match t with
    | None -> true
    | Some task ->
        try
            if task.Wait(DrainSettleTimeout) then
                match task.Result with
                | Result.Ok _ -> true
                | Result.Error e ->
                    appendNote note (sprintf "%s drain settled with error: %s" label e.Message)
                    false
            else
                appendNote note (sprintf "%s drain did not settle within %O" label DrainSettleTimeout)
                false
        with ex ->
            appendNote note (sprintf "%s drain settle exception: %s" label ex.Message)
            false

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
                    // Publish the started PID so a focused test can
                    // observe and assert reaping — the owned context is
                    // never returned in this branch, so ``r.Pid``
                    // would otherwise be ``None``.
                    ObserveStartedPid pid
                    // Failure injection for the post-Start context
                    // construction window.  Throwing here exercises the
                    // ownership-bracket cleanup path.
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

/// The body result captured during the lifecycle bracket.  This type is
/// ``private`` because callers consume ``ProcessOutcome`` values, not raw
/// bracket state.
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
/// process that ``runCore`` causes to be created is released (drained,
/// killed, boundedly reaped, and disposed) BEFORE the public outcome is
/// constructed.  No cleanup runs after the outcome exists.  Exceptions
/// raised inside the body are captured into the cleanup note and reported
/// as ``BodyFailure``; the runner itself never propagates an exception to
/// its callers — every code path returns a structured ``Result`` or
/// outcome shape.
///
/// F# does not allow combining ``try/with`` and ``try/finally`` in a single
/// expression, so the bracket is structured as nested
/// ``try ( try <body> with capture ) finally <cleanup>`` so that cleanup
/// runs after the body has settled regardless of whether it threw.
///
/// Cleanup order on every exit path:
///   1. Boundedly settle stdout drain (drainSettleTimeout).
///   2. Boundedly settle stderr drain (drainSettleTimeout).
///   3. Kill the process tree (kill-tree).
///   4. Boundedly wait for process exit (cleanupTimeout).
///   5. Dispose the process (honours InjectDisposeFailure).
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

    // Body result placeholder.  Defaults to a sentinel that the
    // post-finally outcome constructor can recognise if the body
    // never executes (which is impossible in practice, but F# requires
    // an initial value for ``mutable`` bindings).
    let mutable bodyResult : BodyResult = BodySpawnError "not started"

    // Mutable observation buffers populated inside the body so the
    // post-finally outcome constructor can read them.
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

                // Failure injection for the wait stage.
                match InjectWaitFailure() with
                | Some ex -> raise ex
                | None -> ()

                let verdict =
                    waitForExitAsync ctx cancellationToken cleanupNote
                    |> Async.RunSynchronously

                // Failure injection for the drain stage.
                match InjectDrainFailure() with
                | Some ex -> raise ex
                | None -> ()

                // Drain operations: any failure is recorded into the
                // cleanup note and surfaced via the stdoutOk / stderrOk
                // flags in ``BodyCompleted``.
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
        // Cleanup runs BEFORE the public outcome is constructed.
        //
        // The order is:
        //   1. Boundedly settle stdout drain (records settle errors).
        //   2. Boundedly settle stderr drain (records settle errors).
        //   3. Kill the process tree.
        //   4. Boundedly wait for the process to exit.
        //   5. Dispose the process (honours InjectDisposeFailure).
        //
        // Steps 1 and 2 ensure that any drain task still responding to
        // stream closure has either finished or been recorded as
        // timed-out before the process is killed.  Without this, the
        // exceptional path could return a ``BodyFailure`` outcome while
        // drain tasks were still observing stream closure.
        let _ = settleDrain stdoutDrain "stdout" cleanupNote
        let _ = settleDrain stderrDrain "stderr" cleanupNote
        killTree proc cleanupNote
        let _ = waitBounded proc cleanupNote
        disposeProc proc cleanupNote

    // Post-finally outcome construction.  The outcome is built from
    // the captured body result and the now-final cleanup note.  No
    // further cleanup may run after this point.
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
        // Stdout is always decoded from the raw buffer; the
        // stderrOk flag controls only stderr reporting, not the
        // preservation of successfully-captured stdout.
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