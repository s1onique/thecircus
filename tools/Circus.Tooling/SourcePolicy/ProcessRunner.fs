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
            let pid = proc.Id
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

let private observeCleanup (proc: Process) (note: string ref) : CleanupObservation =
    killTree proc note
    let _ = waitBounded proc note
    disposeProc proc note
    { Notes = note.Value }

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

/// Run the child process with strict lifecycle ordering:
///   1. Wait for process exit (cancellation-aware);
///   2. If cancelled, kill the tree and boundedly reap (already done inside waitForExitAsync);
///   3. Await both drain tasks so stdout and stderr are completely consumed;
///   4. Dispose the process;
///   5. Construct the outcome from verdict + drain results + cleanup note.
///
/// No cleanup is performed after the outcome is constructed.
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
    let mutable verdict : Result<int, string> = Result.Ok 0
    match startAsync argv workingDir cancellationToken cleanupNote with
        | Result.Error msg ->
            Ok (SpawnFailure (msg, cleanupNote.Value), [||], "", 0, true, cleanupNote.Value)
        | Result.Ok ctx ->
            proc <- ctx.Proc
            pid <- Some ctx.Pid
            stdoutDrain <- Some ctx.StdoutBytes
            stderrDrain <- Some ctx.StderrText
            // 1. Wait for exit (cancellation-aware).  Cancellation
            //    already triggered the bounded kill + wait internally.
            verdict <-
                waitForExitAsync ctx cancellationToken cleanupNote
                |> Async.RunSynchronously
            // 2 & 3. Await drain tasks BEFORE disposing the process.
            //      Reading redirected streams after Dispose can fail.
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
            // 4. Dispose the process (idempotent; we never disposed before).
            disposeProc proc cleanupNote
            // 5. Construct the outcome from the verdict, drain flags,
            //    and cleanup note — all observations already collected.
            let cleanup = { Notes = cleanupNote.Value }
            let outcome = finalize verdict stdoutOk stderrOk cleanup
            Ok (outcome, stdoutRaw, stderrText, (defaultArg pid 0), stderrOk, cleanupNote.Value)
    // Note: every code path that constructs a Process has already
    // disposed via ``disposeProc proc cleanupNote`` before the
    // outcome was built.  No post-outcome cleanup runs.

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
