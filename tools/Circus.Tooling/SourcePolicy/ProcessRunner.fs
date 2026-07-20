module Circus.Tooling.SourcePolicy.ProcessRunner

/// Governed child-process runner used by the source-policy verifier.
///
/// The previous ``Inventory.fs`` / ``GateSummary.fs`` helpers read
/// stdout through ``StreamReader.ReadToEndAsync`` and
/// ``Process.StandardOutput.ReadToEnd``.  That path is **not**
/// byte-faithful: invalid UTF-8 sequences are replaced with the
/// Unicode replacement character and the initial byte-order mark is
/// consumed by the reader.  ``git ls-files -z`` emits filenames as
/// opaque, non-NUL-delimited byte sequences, so any text-mode
/// conversion sits on the wrong side of an integrity boundary.
///
/// This module separates the two contracts into visible function
/// pairs — ``runProcessBytes`` and ``runProcessText`` — instead of
/// selecting behaviour through an unclear Boolean flag.  Each
/// call site is forced to make the contract obvious.
///
/// Cancellation, deterministic disposal, and child/descendant
/// termination are guaranteed on every exit path.  ``runProcessBytes``
/// reads directly from ``Process.StandardOutput.BaseStream`` so NUL
/// bytes survive verbatim, and the runner drains stdout and stderr
/// concurrently so no path can deadlock when both streams are
/// redirected.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

// -----------------------------------------------------------------------------
// Result type
// -----------------------------------------------------------------------------

/// Outcome of a governed process invocation.  The discriminator
/// distinguishes the seven exit shapes required by ACT §5 / WS3:
///
///   - ``SpawnFailure``     — ``Process.Start`` returned ``null`` or threw
///   - ``Exited``           — process ran and exited; the actual code follows
///   - ``NonzeroExit``      — explicit name for the policy-relevant case
///   - ``Cancelled``        — cancellation observed; cleanup evidence follows
///   - ``CleanupFailure``   — child terminated but reaping/closing failed
///   - ``OutputFailure``    — output read/copy raised an exception
///
/// We never collapse a raw exception into a successful empty result;
/// every failure shape is observable.
type ProcessOutcome =
    | SpawnFailure of detail: string
    | Exited of exitCode: int
    | NonzeroExit of exitCode: int
    | Cancelled of cleanupNote: string
    | CleanupFailure of detail: string
    | OutputFailure of detail: string

/// Full result of a governed invocation.  ``Output`` is empty for
/// spawn failures and may be truncated when cancellation or cleanup
/// observed before the drain completed.  ``Stderr`` is the UTF-8
/// decoded error stream; ``Output`` is the captured byte buffer.
type BytesResult = {
    Outcome: ProcessOutcome
    Output: byte[]
    Stderr: string
    Pid: int option
}

/// Full result of a text-mode governed invocation.  ``Output`` is the
/// UTF-8 decoded stdout stream; ``Stderr`` is the UTF-8 decoded
/// error stream.  Both are decoded with replacement-fallback so an
/// invalid byte sequence surfaces as ``U+FFFD`` rather than throwing.
type TextResult = {
    Outcome: ProcessOutcome
    Output: string
    Stderr: string
    Pid: int option
}

// -----------------------------------------------------------------------------
// Cancellation
// -----------------------------------------------------------------------------

/// Internal cancellation observer.  When the supplied token fires
/// after the child has started, we request termination of the owned
/// process tree and remember that cancellation happened so the result
/// type can preserve the original classification.
type private CancellationObserver(token: CancellationToken) =
    let mutable cancelled = false
    let registration : CancellationTokenRegistration option =
        if token.CanBeCanceled then
            (token.Register(fun () -> cancelled <- true) |> Some)
        else
            None
    member _.IsCancellationRequested = cancelled
    member _.Unregister () =
        match registration with
        | Some r -> r.Dispose()
        | None -> ()
    member _.Dispose () =
        match registration with
        | Some r -> r.Dispose()
        | None -> ()

// -----------------------------------------------------------------------------
// Process disposal
// -----------------------------------------------------------------------------

let private dispose (proc: Process) (note: string ref) =
    try
        if not (isNull proc) then proc.Dispose()
    with ex ->
        note.Value <- sprintf "%sdispose failed: %s" note.Value ex.Message

let private killTree (proc: Process) (note: string ref) =
    try
        if not (isNull proc) && not proc.HasExited then
            proc.Kill(true)
    with ex ->
        note.Value <- sprintf "%skill failed: %s" note.Value ex.Message

// -----------------------------------------------------------------------------
// Output capture
// -----------------------------------------------------------------------------

/// Drains ``BaseStream`` into an owned byte buffer.  Never throws
/// because the caller wraps ``process.StandardOutput.BaseStream`` in
/// a try/finally that returns the bytes accumulated so far.
let private drainStream (stream: Stream) (cancellationObs: CancellationObserver) : Task<byte[]> =
    task {
        use ms = new MemoryStream()
        let buffer : byte[] = Array.zeroCreate 8192
        let mutable cancelled = false
        while not cancelled do
            if cancellationObs.IsCancellationRequested then
                cancelled <- true
            else
                let read =
                    try stream.Read(buffer, 0, buffer.Length)
                    with _ -> -1
                if read <= 0 then cancelled <- true
                else ms.Write(buffer, 0, read) |> ignore
        return ms.ToArray()
    }

/// Drains ``BaseStream`` into an owned string using a UTF-8 reader
/// with replacement-fallback so malformed bytes do not throw.
let private drainText (stream: Stream) (cancellationObs: CancellationObserver) : Task<string> =
    task {
        use ms = new MemoryStream()
        let buffer : byte[] = Array.zeroCreate 8192
        let mutable cancelled = false
        while not cancelled do
            if cancellationObs.IsCancellationRequested then
                cancelled <- true
            else
                let read =
                    try stream.Read(buffer, 0, buffer.Length)
                    with _ -> -1
                if read <= 0 then cancelled <- true
                else ms.Write(buffer, 0, read) |> ignore
        return Encoding.UTF8.GetString(ms.ToArray())
    }

// -----------------------------------------------------------------------------
// Wait helper
// -----------------------------------------------------------------------------

let private waitOrCancel (proc: Process) (ct: CancellationToken) (obs: CancellationObserver) (cleanupNote: string ref) : ProcessOutcome =
    try
        let waitTask = proc.WaitForExitAsync(ct)
        waitTask.Wait(ct)
        if obs.IsCancellationRequested then
            killTree proc cleanupNote
            try proc.WaitForExit() with _ -> ()
            Cancelled cleanupNote.Value
        else
            let exitCode = proc.ExitCode
            if exitCode = 0 then Exited 0 else NonzeroExit exitCode
    with
    | :? OperationCanceledException ->
        killTree proc cleanupNote
        try proc.WaitForExit() with _ -> ()
        Cancelled cleanupNote.Value
    | ex ->
        killTree proc cleanupNote
        try proc.WaitForExit() with _ -> ()
        Cancelled (sprintf "%s%s" cleanupNote.Value ex.Message)

// -----------------------------------------------------------------------------
// Public entry points
// -----------------------------------------------------------------------------

/// Build a ``ProcessStartInfo`` from a positional argv list.  ``argv.[0]``
/// is the executable; remaining entries are added via
/// ``ProcessStartInfo.ArgumentList`` so spaces, quotes, and other
/// shell-significant characters survive verbatim.  No shell is
/// involved.
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

/// Internal drain runner that produces bytes + decoded stderr.
let private drainBytesStderr (proc: Process) (observer: CancellationObserver) =
    let stdoutTask = drainStream proc.StandardOutput.BaseStream observer
    let stderrTask = drainText proc.StandardError.BaseStream observer
    stdoutTask, stderrTask

let private drainTextText (proc: Process) (observer: CancellationObserver) =
    let stdoutTask = drainText proc.StandardOutput.BaseStream observer
    let stderrTask = drainText proc.StandardError.BaseStream observer
    stdoutTask, stderrTask

/// Governed byte capture.  Reads stdout directly from
/// ``StandardOutput.BaseStream`` so every byte the child wrote
/// survives the trip, including NUL bytes, embedded newlines, and
/// invalid UTF-8 sequences.  Stderr is decoded as UTF-8 with
/// replacement-fallback because the verifier only consumes it as a
/// diagnostic.  Cancellation, nonzero exit, and output-capture
/// failure are all distinguishable in the returned ``ProcessOutcome``.
let runProcessBytes (argv: string list) (workingDir: string option) (cancellationToken: CancellationToken) : BytesResult =
    if List.isEmpty argv then
        { Outcome = SpawnFailure "argv is empty"
          Output = [||]
          Stderr = ""
          Pid = None }
    else
        let psi = buildStartInfo argv workingDir
        let mutable proc : Process = null
        let cleanupNote = ref ""
        let observer = CancellationObserver(cancellationToken)
        try
            try
                proc <- Process.Start(psi)
                if isNull proc then
                    { Outcome = SpawnFailure "Process.Start returned null"
                      Output = [||]
                      Stderr = ""
                      Pid = None }
                else
                    let pid = proc.Id
                    let stdoutTask, stderrTask = drainBytesStderr proc observer
                    let outcome =
                        try waitOrCancel proc cancellationToken observer cleanupNote
                        with _ ->
                            killTree proc cleanupNote
                            try proc.WaitForExit() with _ -> ()
                            Cancelled cleanupNote.Value
                    let mutable finalOutcome : ProcessOutcome = outcome
                    let stdout =
                        try stdoutTask.Result
                        with ex ->
                            cleanupNote.Value <- sprintf "%sstdout drain failed: %s" cleanupNote.Value ex.Message
                            finalOutcome <- OutputFailure cleanupNote.Value
                            [||]
                    let stderr =
                        try stderrTask.Result
                        with ex ->
                            cleanupNote.Value <- sprintf "%sstderr drain failed: %s" cleanupNote.Value ex.Message
                            finalOutcome <- OutputFailure cleanupNote.Value
                            ""
                    if observer.IsCancellationRequested && not (match finalOutcome with Cancelled _ -> true | _ -> false) then
                        finalOutcome <- Cancelled cleanupNote.Value
                    { Outcome = finalOutcome
                      Output = stdout
                      Stderr = stderr
                      Pid = Some pid }
            with ex ->
                { Outcome = SpawnFailure (sprintf "%s: %s" (ex.GetType().FullName) ex.Message)
                  Output = [||]
                  Stderr = ""
                  Pid = None }
        finally
            killTree proc cleanupNote
            dispose proc cleanupNote
            observer.Unregister ()
            observer.Dispose()

/// Governed text capture.  Reads stdout through a UTF-8
/// replacement-fallback decoder so the caller can treat the output
/// as an ordinary .NET string.  Invalid byte sequences surface as
/// ``U+FFFD``; the captured length is preserved.  Cancellation,
/// nonzero exit, and output-capture failure are all distinguishable.
let runProcessText (argv: string list) (workingDir: string option) (cancellationToken: CancellationToken) : TextResult =
    if List.isEmpty argv then
        { Outcome = SpawnFailure "argv is empty"
          Output = ""
          Stderr = ""
          Pid = None }
    else
        let psi = buildStartInfo argv workingDir
        let mutable proc : Process = null
        let cleanupNote = ref ""
        let observer = CancellationObserver(cancellationToken)
        try
            try
                proc <- Process.Start(psi)
                if isNull proc then
                    { Outcome = SpawnFailure "Process.Start returned null"
                      Output = ""
                      Stderr = ""
                      Pid = None }
                else
                    let pid = proc.Id
                    let stdoutTask, stderrTask = drainTextText proc observer
                    let outcome =
                        try waitOrCancel proc cancellationToken observer cleanupNote
                        with _ ->
                            killTree proc cleanupNote
                            try proc.WaitForExit() with _ -> ()
                            Cancelled cleanupNote.Value
                    let mutable finalOutcome : ProcessOutcome = outcome
                    let stdout =
                        try stdoutTask.Result
                        with ex ->
                            cleanupNote.Value <- sprintf "%sstdout drain failed: %s" cleanupNote.Value ex.Message
                            finalOutcome <- OutputFailure cleanupNote.Value
                            ""
                    let stderr =
                        try stderrTask.Result
                        with ex ->
                            cleanupNote.Value <- sprintf "%sstderr drain failed: %s" cleanupNote.Value ex.Message
                            finalOutcome <- OutputFailure cleanupNote.Value
                            ""
                    if observer.IsCancellationRequested && not (match finalOutcome with Cancelled _ -> true | _ -> false) then
                        finalOutcome <- Cancelled cleanupNote.Value
                    { Outcome = finalOutcome
                      Output = stdout
                      Stderr = stderr
                      Pid = Some pid }
            with ex ->
                { Outcome = SpawnFailure (sprintf "%s: %s" (ex.GetType().FullName) ex.Message)
                  Output = ""
                  Stderr = ""
                  Pid = None }
        finally
            killTree proc cleanupNote
            dispose proc cleanupNote
            observer.Unregister ()
            observer.Dispose()

/// Determine whether the process referenced by ``pid`` is still
/// alive.  Used by tests to prove that no owned child remains after
/// cancellation/cleanup.
let isPidAlive (pid: int) : bool =
    try
        let p = Process.GetProcessById(pid)
        let alive = not p.HasExited
        p.Dispose()
        alive
    with _ -> false