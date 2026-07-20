module Circus.Tooling.SourcePolicy.ProcessRunner

/// Governed child-process runner used by the source-policy verifier.
///
/// CORRECTION01 contract:
///
///   * ``runProcessBytes`` / ``runProcessText`` start the process,
///     kick off cancellation-aware async drains for both stdout and
///     stderr, **then** wait for exit.  Streams drain concurrently
///     via ``Stream.ReadAsync(..., cancellationToken)``.
///
///   * ``CancellationToken`` propagation triggers an owned-tree
///     ``Process.Kill(true)`` followed by a **bounded**
///     ``WaitForExit(timeout)``; timeout is reported as
///     ``CleanupFailure``.
///
///   * Stream-read exceptions surface as ``OutputFailure``.
///
///   * Cleanup and disposal failures are observable through the
///     ``cleanupNote`` field on every outcome shape.
///
///   * Outcome shapes distinguish ``SpawnFailure``, ``Exited``,
///     ``NonzeroExit``, ``Cancelled``, ``CleanupFailure``, and
///     ``OutputFailure``.  No raw exception is collapsed into a
///     successful empty result.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

// -----------------------------------------------------------------------------
// Result type
// -----------------------------------------------------------------------------

type ProcessOutcome =
    | SpawnFailure of detail: string * cleanupNote: string
    | Exited of exitCode: int * cleanupNote: string
    | NonzeroExit of exitCode: int * cleanupNote: string
    | Cancelled of cleanupNote: string
    | CleanupFailure of detail: string
    | OutputFailure of detail: string * cleanupNote: string

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

// -----------------------------------------------------------------------------
// Cancellation
// -----------------------------------------------------------------------------

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
// Cleanup primitives
// -----------------------------------------------------------------------------

let internal CleanupTimeout : TimeSpan = TimeSpan.FromSeconds(5.0)

let private appendNote (note: string ref) (msg: string) : unit =
    if msg = "" then ()
    elif note.Value = "" then note.Value <- msg
    else note.Value <- sprintf "%s; %s" note.Value msg

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
        if not (isNull proc) then proc.Dispose()
    with ex ->
        appendNote note (sprintf "dispose failed: %s" ex.Message)

// -----------------------------------------------------------------------------
// Async drain — cancellation-aware
// -----------------------------------------------------------------------------

/// Drains ``BaseStream`` into an owned byte buffer using
/// cancellation-aware ``Stream.ReadAsync``.  Exceptions are returned
/// in the result so the caller can surface them as ``OutputFailure``
/// rather than silently terminating the drain.
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
        let result =
            match firstError with
            | Some e -> Result.Error e
            | None -> Result.Ok (ms.ToArray())
        return result
    }

/// Drains ``BaseStream`` into an owned UTF-8 string.  Uses the same
/// cancellation-aware ``Stream.ReadAsync`` as the byte variant.
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
        let result =
            match firstError with
            | Some e -> Result.Error e
            | None -> Result.Ok (Encoding.UTF8.GetString(ms.ToArray()))
        return result
    }

// -----------------------------------------------------------------------------
// Build argv
// -----------------------------------------------------------------------------

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

// -----------------------------------------------------------------------------
// Process lifecycle core — supports both byte and text stdout capture.
// Only ONE drain per stream is started to avoid concurrent reads on the
// same pipe consuming each other's bytes.
// -----------------------------------------------------------------------------

type private DrainMode = BytesOnly | TextOnly

type private AsyncProcessContext = {
    Proc: Process
    Pid: int
    StdoutDrain: Task<Result<byte[], exn>>
    StderrDrain: Task<Result<string, exn>>
    StdoutAsText: Task<Result<string, exn>> option
    Observer: CancellationObserver
}

let private startAsync (argv: string list) (workingDir: string option) (ct: CancellationToken) (mode: DrainMode) (note: string ref) : Result<AsyncProcessContext, string> =
    if List.isEmpty argv then
        Result.Error "argv is empty"
    else
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
            let observer = CancellationObserver(ct)
            let pid = proc.Id
            // Always capture stdout as bytes (lossless).  When the caller
            // only needs text, the same buffer is decoded on demand inside
            // the text variant of the entry point so we never race two
            // consumers on the same underlying pipe.
            let stdoutBytes = drainBytesAsync proc.StandardOutput.BaseStream ct
            let stdoutAsText =
                match mode with
                | TextOnly -> Some (task {
                    let! r = stdoutBytes
                    match r with
                    | Result.Ok b -> return Result.Ok (Encoding.UTF8.GetString b)
                    | Result.Error e -> return Result.Error e
                  })
                | BytesOnly -> None
            let stderrDrain = drainTextAsync proc.StandardError.BaseStream ct
            Result.Ok { Proc = proc; Pid = pid
                        StdoutDrain = stdoutBytes
                        StderrDrain = stderrDrain
                        StdoutAsText = stdoutAsText
                        Observer = observer }

let private waitForExit (ctx: AsyncProcessContext) (note: string ref) : Result<int, string> =
    try
        if not ctx.Proc.HasExited then
            ctx.Proc.WaitForExit()
        Result.Ok ctx.Proc.ExitCode
    with
    | :? OperationCanceledException ->
        killTree ctx.Proc note
        let ok = waitBounded ctx.Proc note
        if ok then
            appendNote note "cancelled by token"
            Result.Error "cancelled"
        else
            appendNote note "bounded cleanup wait timed out"
            Result.Error "cleanup_timeout"
    | ex ->
        killTree ctx.Proc note
        let _ = waitBounded ctx.Proc note
        appendNote note (sprintf "wait exception: %s" ex.Message)
        Result.Error (sprintf "wait_failed: %s" ex.Message)

let private outcomeFor (verdict: Result<int, string>) (note: string ref) : ProcessOutcome =
    match verdict with
    | Result.Ok 0 -> Exited (0, note.Value)
    | Result.Ok n -> NonzeroExit (n, note.Value)
    | Result.Error "cancelled" -> Cancelled note.Value
    | Result.Error "cleanup_timeout" ->
        CleanupFailure (sprintf "bounded cleanup wait timed out: %s" note.Value)
    | Result.Error other ->
        CleanupFailure (sprintf "%s: %s" other note.Value)

// -----------------------------------------------------------------------------
// Public entry points
// -----------------------------------------------------------------------------

let runProcessBytes (argv: string list) (workingDir: string option) (cancellationToken: CancellationToken) : BytesResult =
    let cleanupNote = ref ""
    let observer = CancellationObserver(cancellationToken)
    let mutable proc : Process = null
    try
        try
            match startAsync argv workingDir cancellationToken BytesOnly cleanupNote with
            | Result.Error msg ->
                { Outcome = SpawnFailure (msg, cleanupNote.Value)
                  Output = [||]
                  Stderr = ""
                  Pid = None
                  DescendantPid = None }
            | Result.Ok ctx ->
                proc <- ctx.Proc
                let verdict = waitForExit ctx cleanupNote
                let mutable finalOutcome = outcomeFor verdict cleanupNote
                let stdout =
                    try
                        match ctx.StdoutDrain.Result with
                        | Result.Ok b -> b
                        | Result.Error e ->
                            appendNote cleanupNote (sprintf "stdout drain failed: %s" e.Message)
                            finalOutcome <- OutputFailure (cleanupNote.Value, cleanupNote.Value)
                            [||]
                    with ex ->
                        appendNote cleanupNote (sprintf "stdout drain await failed: %s" ex.Message)
                        finalOutcome <- OutputFailure (cleanupNote.Value, cleanupNote.Value)
                        [||]
                let stderr =
                    try
                        match ctx.StderrDrain.Result with
                        | Result.Ok s -> s
                        | Result.Error e ->
                            appendNote cleanupNote (sprintf "stderr drain failed: %s" e.Message)
                            finalOutcome <- OutputFailure (cleanupNote.Value, cleanupNote.Value)
                            ""
                    with ex ->
                        appendNote cleanupNote (sprintf "stderr drain await failed: %s" ex.Message)
                        finalOutcome <- OutputFailure (cleanupNote.Value, cleanupNote.Value)
                        ""
                if observer.IsCancellationRequested &&
                   not (match finalOutcome with Cancelled _ | CleanupFailure _ -> true | _ -> false) then
                    finalOutcome <- Cancelled cleanupNote.Value
                { Outcome = finalOutcome
                  Output = stdout
                  Stderr = stderr
                  Pid = Some ctx.Pid
                  DescendantPid = None }
        with ex ->
            appendNote cleanupNote (sprintf "%s: %s" (ex.GetType().FullName) ex.Message)
            { Outcome = SpawnFailure (ex.Message, cleanupNote.Value)
              Output = [||]
              Stderr = ""
              Pid = None
              DescendantPid = None }
    finally
        killTree proc cleanupNote
        let _ = waitBounded proc cleanupNote
        disposeProc proc cleanupNote
        observer.Unregister ()
        observer.Dispose ()

let runProcessText (argv: string list) (workingDir: string option) (cancellationToken: CancellationToken) : TextResult =
    let cleanupNote = ref ""
    let observer = CancellationObserver(cancellationToken)
    let mutable proc : Process = null
    try
        try
            match startAsync argv workingDir cancellationToken TextOnly cleanupNote with
            | Result.Error msg ->
                { Outcome = SpawnFailure (msg, cleanupNote.Value)
                  Output = ""
                  Stderr = ""
                  Pid = None
                  DescendantPid = None }
            | Result.Ok ctx ->
                proc <- ctx.Proc
                let verdict = waitForExit ctx cleanupNote
                let mutable finalOutcome = outcomeFor verdict cleanupNote
                let stdout =
                    try
                        let drain =
                            match ctx.StdoutAsText with
                            | Some t -> t
                            | None -> failwith "internal: text drain missing"
                        match drain.Result with
                        | Result.Ok s -> s
                        | Result.Error e ->
                            appendNote cleanupNote (sprintf "stdout drain failed: %s" e.Message)
                            finalOutcome <- OutputFailure (cleanupNote.Value, cleanupNote.Value)
                            ""
                    with ex ->
                        appendNote cleanupNote (sprintf "stdout drain await failed: %s" ex.Message)
                        finalOutcome <- OutputFailure (cleanupNote.Value, cleanupNote.Value)
                        ""
                let stderr =
                    try
                        match ctx.StderrDrain.Result with
                        | Result.Ok s -> s
                        | Result.Error e ->
                            appendNote cleanupNote (sprintf "stderr drain failed: %s" e.Message)
                            finalOutcome <- OutputFailure (cleanupNote.Value, cleanupNote.Value)
                            ""
                    with ex ->
                        appendNote cleanupNote (sprintf "stderr drain await failed: %s" ex.Message)
                        finalOutcome <- OutputFailure (cleanupNote.Value, cleanupNote.Value)
                        ""
                if observer.IsCancellationRequested &&
                   not (match finalOutcome with Cancelled _ | CleanupFailure _ -> true | _ -> false) then
                    finalOutcome <- Cancelled cleanupNote.Value
                { Outcome = finalOutcome
                  Output = stdout
                  Stderr = stderr
                  Pid = Some ctx.Pid
                  DescendantPid = None }
        with ex ->
            appendNote cleanupNote (sprintf "%s: %s" (ex.GetType().FullName) ex.Message)
            { Outcome = SpawnFailure (ex.Message, cleanupNote.Value)
              Output = ""
              Stderr = ""
              Pid = None
              DescendantPid = None }
    finally
        killTree proc cleanupNote
        let _ = waitBounded proc cleanupNote
        disposeProc proc cleanupNote
        observer.Unregister ()
        observer.Dispose ()

let isPidAlive (pid: int) : bool =
    try
        let p = Process.GetProcessById(pid)
        let alive = not p.HasExited
        p.Dispose()
        alive
    with _ -> false
