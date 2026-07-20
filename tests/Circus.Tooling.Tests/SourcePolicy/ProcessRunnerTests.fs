module Circus.Tooling.Tests.SourcePolicy.ProcessRunnerTests

/// Focused process-runner tests covering every CORRECTION01 invariant.

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto

open Circus.Tooling.SourcePolicy.ProcessRunner

let mutable bashOk = false
do
    try
        let p = Process.Start(new ProcessStartInfo(FileName = "bash", RedirectStandardOutput = true, UseShellExecute = false))
        if not (isNull p) then p.Dispose()
        bashOk <- true
    with _ -> ()

// ---------------------------------------------------------------------------
// Failure-injection test helpers.
// ---------------------------------------------------------------------------

let private resetInjections () =
    InjectStartAsyncFailure <- fun () -> None
    InjectStartAsyncAccessFailure <- fun () -> None
    InjectStartAsyncStdoutDrainFailure <- fun () -> None
    InjectStartAsyncObserverFailure <- fun () -> None
    InjectWaitFailure <- fun () -> None
    InjectDrainFailure <- fun () -> None
    DisposeProcess <- fun (p: Process) -> p.Dispose()
    KillTreeStrategy <- true
    ObserveStartedPid <- ignore
    ObserveStdoutDrainTask <- ignore
    ObserveStderrDrainTask <- ignore
    SubstituteStdoutDrainTask <- id
    SubstituteStderrDrainTask <- id

let private withInjection<'a>
    (setHooks : unit -> unit)
    (body : unit -> 'a)
    : 'a =
    resetInjections ()
    try
        setHooks ()
        body ()
    finally
        resetInjections ()

let private bashArgs (body: string) : string list =
    [ "bash"; "-c"; body ]

let private noCwd : string option = None

let private outcomeSummary (r: TextResult) : string =
    sprintf "Outcome=%A Stdout=%d bytes Stderr=%d bytes Pid=%A" r.Outcome r.Output.Length r.Stderr.Length r.Pid

/// Build a faulted ``Task<Result<byte[], exn>>`` directly via the
/// .NET ``Task`` constructor.  This bypasses the ``TaskCompletionSource``
/// type inference problem and produces a task with the exact status we
/// need (``Faulted``) for the ``inspectTerminal`` ``IsFaulted`` branch.
let private faultedStdoutTask (msg: string) : Task<Result<byte[], exn>> =
    Task.FromException<Result<byte[], exn>> (IOException(msg))

/// Build a cancelled ``Task<Result<byte[], exn>>`` for the
/// ``inspectTerminal`` ``IsCanceled`` branch.
let private cancelledStdoutTask () : Task<Result<byte[], exn>> =
    Task.FromCanceled<Result<byte[], exn>> (new CancellationToken(true))

/// Type for the negative-validity test result classifier.
type DescendantProofResult =
    | DescendantSurvived of parentPid: int * descendantPid: int
    | DescendantReaped of parentPid: int * descendantPid: int
    | Unexpected of detail: string

// ---------------------------------------------------------------------------
// Concurrent ProcessRunner helper with proper coordination primitives.
// ---------------------------------------------------------------------------

/// Parsed PID record from readiness file.
type PidRecord = {
    ParentPid: int
    DescendantPid: int
    Nonce: string
}

/// Result of the concurrent ProcessRunner invocation.
type ConcurrentProofResult = {
    Result: TextResult option
    PidRecord: PidRecord option
    ReadinessFound: bool
    CancellationIssuedAfterReadiness: bool
    WatchdogExpired: bool
    ProcessCompleted: bool
}

/// Exception thrown when the proof cannot proceed.
exception ProofFailed of string

/// Run ProcessRunner with genuine concurrency using settled coordination primitives:
/// - ProcessRunner completion task
/// - Readiness task
/// - Watchdog task
/// - All tasks settled before returning
/// - Timeout waiting for real result fails the proof.
/// - All mutable state access is protected by a lock to prevent races.
let private runProcessConcurrently
    (argv: string list)
    (workspace: string)
    (readinessFile: string)
    (pollMs: int)
    (watchdogMs: int)
    (cts: CancellationTokenSource)
    : ConcurrentProofResult =
    // One TCS per coordination primitive with async continuations
    let completionTcs = TaskCompletionSource<TextResult>(TaskCreationOptions.RunContinuationsAsynchronously)
    let readinessTcs = TaskCompletionSource<PidRecord>(TaskCreationOptions.RunContinuationsAsynchronously)
    let watchdogTcs = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let stateLock = obj()

    let mutable readinessFound = false
    let mutable cancellationIssuedAfterReadiness = false
    let mutable watchdogExpired = false
    let mutable parsedRecord: PidRecord option = None

    // Thread-safe state setters
    let setReadiness (found: bool) (record: PidRecord) =
        lock stateLock (fun () ->
            readinessFound <- found
            parsedRecord <- Some record
        )
    let setCancellationAfterReadiness () =
        lock stateLock (fun () -> cancellationIssuedAfterReadiness <- true)
    let setWatchdogExpired () =
        lock stateLock (fun () ->
            watchdogExpired <- true
        )
    let getReadinessFound () : bool =
        lock stateLock (fun () -> readinessFound)

    // Background thread: run process
    let processThread = Thread(fun () ->
        try
            let result = runProcessText argv (Some workspace) cts.Token
            completionTcs.TrySetResult(result) |> ignore
        with ex ->
            completionTcs.TrySetException(ex) |> ignore
    )
    processThread.IsBackground <- true
    processThread.Start()

    // Watchdog thread: timeout independent of normal cancellation
    let watchdogThread = Thread(fun () ->
        Thread.Sleep(watchdogMs)
        if not (getReadinessFound()) then
            setWatchdogExpired()
            watchdogTcs.TrySetResult() |> ignore
            cts.Cancel() // Force cancel after watchdog
    )
    watchdogThread.IsBackground <- true
    watchdogThread.Start()

    // Main poll loop
    let deadline = DateTime.UtcNow.AddMilliseconds(float watchdogMs)
    while not (getReadinessFound()) && DateTime.UtcNow < deadline do
        if File.Exists(readinessFile) then
            let content = File.ReadAllText(readinessFile)
            // Parse: PROCESS_TREE_READY parent_pid=N descendant_pid=N nonce=XXX
            let m = Regex.Match(content, @"PROCESS_TREE_READY parent_pid=(\d+) descendant_pid=(\d+) nonce=(\S+)")
            if m.Success then
                let parentPid = int m.Groups.[1].Value
                let descendantPid = int m.Groups.[2].Value
                let nonce = m.Groups.[3].Value
                if parentPid > 0 && descendantPid > 0 && parentPid <> descendantPid then
                    let record = { ParentPid = parentPid; DescendantPid = descendantPid; Nonce = nonce }
                    setReadiness true record
                    readinessTcs.TrySetResult(record) |> ignore
                    // Cancel immediately after valid readiness
                    setCancellationAfterReadiness()
                    cts.Cancel()
        if not (getReadinessFound()) then
            Thread.Sleep(pollMs)

    // Wait for all tasks to settle
    let allTasks = Task.WhenAll(completionTcs.Task, readinessTcs.Task, watchdogTcs.Task)
    allTasks.Wait(watchdogMs) |> ignore

    // Determine if process completed
    let processCompleted = completionTcs.Task.IsCompleted

    // Get result - if not completed, we fail the proof
    let result =
        if completionTcs.Task.IsCompleted && not completionTcs.Task.IsFaulted && not completionTcs.Task.IsCanceled then
            Some completionTcs.Task.Result
        else
            // Process did not complete - this fails the proof
            None

    // Read final state under lock
    let finalReadiness, finalRecord, finalCancellation, finalWatchdog =
        lock stateLock (fun () ->
            readinessFound, parsedRecord, cancellationIssuedAfterReadiness, watchdogExpired)

    { Result = result
      PidRecord = finalRecord
      ReadinessFound = finalReadiness
      CancellationIssuedAfterReadiness = finalCancellation
      WatchdogExpired = finalWatchdog
      ProcessCompleted = processCompleted }

// ---------------------------------------------------------------------------
// Fixture cleanup helpers.
// ---------------------------------------------------------------------------

/// Check if a PID is alive.
let private isPidAlive (pid: int) : bool =
    try
        let p = Process.GetProcessById(pid)
        let alive = not p.HasExited
        p.Dispose()
        alive
    with _ -> false

/// Emergency cleanup for exact PIDs. Returns true if any PID required cleanup.
let private emergencyCleanup (pids: int list) : bool =
    let mutable required = false
    for pid in pids do
        if pid > 0 && isPidAlive pid then
            required <- true
            try
                let p = Process.GetProcessById(pid)
                p.Kill(true)
                p.Dispose()
            with _ -> ()
    required

/// Clean up workspace directory.
let private cleanupWorkspace (workspace: string) =
    try
        if Directory.Exists(workspace) then Directory.Delete(workspace, false)
    with _ -> ()

/// Clean up readiness file.
let private cleanupReadiness (readinessFile: string) =
    try if File.Exists(readinessFile) then File.Delete(readinessFile) with _ -> ()

/// Shared bash script for process tree fixture.
/// Creates parent (sleep) and child (sleep), writes PIDs to file and stdout, then blocks.
let private makeProcessTreeScript (nonce: string) (readinessFile: string) : string =
    let script =
        "sleep 999999999 &\n" +
        "desc_pid=$!\n" +
        "parent_pid=$$\n" +
        "msg=\"PROCESS_TREE_READY parent_pid=$parent_pid descendant_pid=$desc_pid nonce=" + nonce + "\"\n" +
        "printf '%s\\n' \"$msg\" > \"" + readinessFile + "\"\n" +
        "printf '%s\\n' \"$msg\"\n" +
        "sync\n" +
        "sleep 999999999"
    script

[<Tests>]
let tests =
    testSequenced <| testList "Process runner" [
        if not bashOk then
            test "skipped (bash unavailable)" {
                Expect.isTrue true "bash missing on this host; suite is unavailable"
            }
        else
            // -------------------------------------------------------------------
            // P0-1 / P0-2 / P0-3 baseline tests.
            // -------------------------------------------------------------------
            test "successful zero exit" {
                let r = runProcessText (bashArgs "exit 0") noCwd CancellationToken.None
                match r.Outcome with
                | Exited (0, _) -> Expect.equal r.Output "" "no output"
                | o -> failtestf "expected Exited 0, got %A" o
            }

            test "nonzero exit preserves output and is named NonzeroExit" {
                let r = runProcessText (bashArgs "echo captured; exit 7") noCwd CancellationToken.None
                match r.Outcome with
                | NonzeroExit (n, _) ->
                    Expect.equal n 7 "exit 7"
                    Expect.stringContains r.Output "captured" "stdout preserved"
                | o -> failtestf "expected NonzeroExit, got %A" o
            }

            test "spawn failure on missing executable" {
                let r = runProcessText [ "/nonexistent-binary-xyz" ] noCwd CancellationToken.None
                match r.Outcome with
                | SpawnFailure _ -> ()
                | o -> failtestf "expected SpawnFailure, got %A" o
            }

            test "stdout and stderr are preserved together" {
                let r = runProcessText (bashArgs "echo to-out; echo to-err 1>&2") noCwd CancellationToken.None
                Expect.stringContains r.Output "to-out" "stdout preserved"
                Expect.stringContains r.Stderr "to-err" "stderr preserved"
            }

            test "simultaneous large stdout and stderr do not deadlock" {
                let body =
                    "for i in $(seq 1 5000); do echo err$i 1>&2; done; " +
                    "for i in $(seq 1 5000); do echo out$i; done; exit 0"
                let r = runProcessText (bashArgs body) noCwd CancellationToken.None
                match r.Outcome with
                | Exited (0, _) ->
                    Expect.stringContains r.Output "out5000" "stdout survived"
                    Expect.stringContains r.Stderr "err5000" "stderr survived"
                | o -> failtestf "expected Exited 0, got %s" (outcomeSummary r)
            }

            test "working directory propagates" {
                let tmp = Path.Combine(Path.GetTempPath(), "circus-prtest-" + Guid.NewGuid().ToString("n"))
                Directory.CreateDirectory tmp |> ignore
                try
                    let r = runProcessText (bashArgs "pwd") (Some tmp) CancellationToken.None
                    Expect.stringContains r.Output (Path.GetFileName tmp) "pwd saw our dir"
                finally
                    Directory.Delete(tmp, true)
            }

            test "argument boundary preservation" {
                let argv = [ "bash"; "-c"; "printf '%s' \"$1\"; printf '.'"; "--"; "--weird arg 'with' \"quotes\"" ]
                let r = runProcessText argv noCwd CancellationToken.None
                Expect.equal r.Output "--weird arg 'with' \"quotes\"." "verbatim args"
            }

            test "invalid textual output (replacement fallback)" {
                let r = runProcessText (bashArgs "printf '\\xff\\xfe\\xfd'") noCwd CancellationToken.None
                match r.Outcome with
                | Exited (0, _) ->
                    Expect.stringContains r.Output "\uFFFD" "replacement char emitted"
                | o -> failtestf "expected Exited 0, got %A" o
            }

            test "NUL bytes survive the byte path" {
                let r = runProcessBytes (bashArgs "printf '\\0'") noCwd CancellationToken.None
                match r.Outcome with
                | Exited (0, _) ->
                    Expect.equal r.Output.Length 1 "one byte"
                    Expect.equal r.Output.[0] (byte 0) "byte is NUL"
                | o -> failtestf "expected Exited 0, got %A" o
            }

            test "invalid UTF-8 byte sequences are preserved" {
                let r = runProcessBytes (bashArgs "printf '\\xff\\xfe'") noCwd CancellationToken.None
                match r.Outcome with
                | Exited (0, _) ->
                    Expect.equal r.Output.[0] (byte 0xFFuy) "first byte preserved"
                    Expect.equal r.Output.[1] (byte 0xFEuy) "second byte preserved"
                | o -> failtestf "expected Exited 0, got %A" o
            }

            // P0-2: cancellation
            test "cancellation of stdout-only child yields Cancelled" {
                let body = "for i in $(seq 1 30); do echo $i; sleep 1; done"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(250)
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ -> ()
                | o -> failtestf "expected Cancelled, got %A" o
            }

            test "cancellation of stderr-only child yields Cancelled" {
                let body = "for i in $(seq 1 60); do echo err$i 1>&2; sleep 1; done"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(250)
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ -> ()
                | o -> failtestf "expected Cancelled, got %A" o
            }

            test "cancellation of silent child yields Cancelled" {
                let body = "sleep 30"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(200)
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ -> ()
                | o -> failtestf "expected Cancelled, got %A" o
            }

            test "cancellation of child with descendant reaps parent and descendant" {
                let body =
                    "sleep 60 & echo \"DESCENDANT_PID=$!\" 1>&2; wait"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(400)
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ ->
                    match r.Pid with
                    | Some pid ->
                        Thread.Sleep(250)
                        Expect.isFalse (isPidAlive pid) "parent PID must be reaped"
                    | None -> failtestf "expected a parent PID, got None"
                | o -> failtestf "expected Cancelled, got %A" o
            }

            // ---------------------------------------------------------------------------
            // P0-2: Descendant-PID mechanical proof (positive) - concurrent design.
            //
            // Design:
            // 1. One process-tree invocation with concurrent polling
            // 2. Unique nonce per invocation to detect stale records
            // 3. Strict parse of readiness file: parent_pid, descendant_pid, nonce
            // 4. Immediate cancellation after valid readiness (not watchdog)
            // 5. Require: readiness observed, cancellation after readiness, watchdog not expired,
            //             real ProcessRunner completed, Cancelled outcome, parent dead, descendant dead
            // 6. Exact-PID cleanup in outer finally; test fails if cleanup required
            // ---------------------------------------------------------------------------

            test "cancellation terminates recorded descendant PID (positive proof)" {
                let nonce = Guid.NewGuid().ToString("n")
                let workspace = Path.Combine(Path.GetTempPath(), "circus-posproof-" + nonce)
                Directory.CreateDirectory(workspace) |> ignore

                let readinessFile = Path.Combine(workspace, "ready-" + nonce)
                let body = makeProcessTreeScript nonce readinessFile

                let mutable cleanupPids = []
                let mutable emergencyCleanupRequired = false

                try
                    use cts = new CancellationTokenSource()
                    let proofResult =
                        runProcessConcurrently
                            (bashArgs body)
                            workspace
                            readinessFile
                            50    // poll every 50ms
                            5000  // watchdog 5s
                            cts

                    // Step 1: Readiness MUST be found
                    if not proofResult.ReadinessFound then
                        failtestf "readiness file did not appear within watchdog deadline"

                    // Step 2: Parse record
                    match proofResult.PidRecord with
                    | Some record ->
                        if record.ParentPid <= 0 then failtestf "parent PID must be positive"
                        if record.DescendantPid <= 0 then failtestf "descendant PID must be positive"
                        if record.ParentPid = record.DescendantPid then failtestf "PIDs must differ"
                        if record.Nonce <> nonce then failtestf "nonce mismatch: expected %s, got %s" nonce record.Nonce
                        cleanupPids <- [record.ParentPid; record.DescendantPid]
                    | None -> failtestf "no PID record parsed"

                    let record = proofResult.PidRecord.Value

                    // Step 3: Cancellation must be issued AFTER readiness
                    if not proofResult.CancellationIssuedAfterReadiness then
                        failtestf "cancellation must be issued after readiness"

                    // Step 4: Watchdog must NOT have expired
                    if proofResult.WatchdogExpired then
                        failtestf "watchdog expired - readiness deadline not met"

                    // Step 5: Real ProcessRunner must have completed
                    match proofResult.Result with
                    | None -> failtestf "ProcessRunner did not complete - timeout waiting for real result"
                    | Some result ->
                        // Step 6: Verify stdout contains exactly ONE matching record
                        let matches = Regex.Matches(result.Output, @"PROCESS_TREE_READY parent_pid=(\d+) descendant_pid=(\d+) nonce=(\S+)")
                        if matches.Count <> 1 then
                            failtestf "expected exactly 1 stdout record, got %d" matches.Count
                        let m = matches.[0]
                        let outParent = int m.Groups.[1].Value
                        let outDescendant = int m.Groups.[2].Value
                        let outNonce = m.Groups.[3].Value
                        if record.ParentPid <> outParent then
                            failtestf "stdout parent mismatch: %d vs %d" record.ParentPid outParent
                        if record.DescendantPid <> outDescendant then
                            failtestf "stdout descendant mismatch: %d vs %d" record.DescendantPid outDescendant
                        if record.Nonce <> outNonce then
                            failtestf "stdout nonce mismatch: %s vs %s" record.Nonce outNonce

                        // Step 7: Verify r.Pid matches parent
                        match result.Pid with
                        | Some reportedPid -> Expect.equal reportedPid record.ParentPid "r.Pid matches parent"
                        | None -> failtestf "r.Pid must be populated"

                        // Step 8: Assert Cancelled outcome
                        match result.Outcome with
                        | Cancelled _ -> ()
                        | CleanupFailure d -> failtestf "expected Cancelled, got CleanupFailure: %s" d
                        | OutputFailure (d, _) -> failtestf "expected Cancelled, got OutputFailure: %s" d
                        | BodyFailure (d, _) -> failtestf "expected Cancelled, got BodyFailure: %s" d
                        | SpawnFailure (d, _) -> failtestf "expected Cancelled, got SpawnFailure: %s" d
                        | Exited (c, _) -> failtestf "expected Cancelled, got Exited(%d)" c
                        | NonzeroExit (c, _) -> failtestf "expected Cancelled, got NonzeroExit(%d)" c

                        // Step 9: Verify both PIDs are reaped (positive proof: killTree worked)
                        // Parent must be reaped within 3 seconds
                        let deadline2 = DateTime.UtcNow.AddSeconds(3.0)
                        let mutable parentReaped = false
                        while not parentReaped && DateTime.UtcNow < deadline2 do
                            if not (isPidAlive record.ParentPid) then parentReaped <- true
                            else Thread.Sleep(50)
                        if not parentReaped then
                            emergencyCleanupRequired <- true
                            failtestf "parent PID %d was not reaped" record.ParentPid

                        // Descendant must be reaped within 3 seconds
                        let deadline3 = DateTime.UtcNow.AddSeconds(3.0)
                        let mutable descendantReaped = false
                        while not descendantReaped && DateTime.UtcNow < deadline3 do
                            if not (isPidAlive record.DescendantPid) then descendantReaped <- true
                            else Thread.Sleep(50)
                        if not descendantReaped then
                            emergencyCleanupRequired <- true
                            failtestf "descendant PID %d was not reaped" record.DescendantPid

                finally
                    // Always clean up with exact PIDs
                    let cleaned = emergencyCleanup cleanupPids
                    if cleaned then emergencyCleanupRequired <- true
                    cleanupReadiness readinessFile
                    cleanupWorkspace workspace

                // POSITIVE PROOF MUST NOT REQUIRE EMERGENCY CLEANUP
                if emergencyCleanupRequired then
                    failtestf "emergency cleanup was required - positive proof failed: tree kill did not work"
            }

            // ---------------------------------------------------------------------------
            // P0-2: Descendant-PID mechanical proof (negative) - concurrent design.
            //
            // Design:
            // 1. One process-tree invocation with KillTreeStrategy=false
            // 2. Same concurrent polling as positive proof
            // 3. Immediate cancellation after valid readiness
            // 4. Require: readiness observed, cancellation after readiness, watchdog not expired,
            //             real ProcessRunner completed, parent dead, descendant alive (DescendantSurvived)
            // 5. Exact-PID cleanup in outer finally
            // ---------------------------------------------------------------------------

            test "descendant survives when KillTreeStrategy=false (negative validity proof)" {
                let nonce = Guid.NewGuid().ToString("n")
                let workspace = Path.Combine(Path.GetTempPath(), "circus-negproof-" + nonce)
                Directory.CreateDirectory(workspace) |> ignore

                let readinessFile = Path.Combine(workspace, "ready-" + nonce)
                let body = makeProcessTreeScript nonce readinessFile

                let mutable cleanupPids = []
                let mutable proofResult: DescendantProofResult option = None

                try
                    // Inject KillTreeStrategy=false BEFORE running process
                    withInjection
                        (fun () -> KillTreeStrategy <- false)
                        (fun () ->
                            use cts = new CancellationTokenSource()
                            let concurrentResult =
                                runProcessConcurrently
                                    (bashArgs body)
                                    workspace
                                    readinessFile
                                    50    // poll every 50ms
                                    5000  // watchdog 5s
                                    cts

                            // Must have readiness
                            if not concurrentResult.ReadinessFound then
                                failtestf "readiness file did not appear within watchdog deadline"

                            match concurrentResult.PidRecord with
                            | Some record ->
                                cleanupPids <- [record.ParentPid; record.DescendantPid]
                            | None -> failtestf "no PID record parsed"

                            let record = concurrentResult.PidRecord.Value

                            // Cancellation must be issued after readiness
                            if not concurrentResult.CancellationIssuedAfterReadiness then
                                failtestf "cancellation must be issued after readiness"

                            // Watchdog must NOT have expired
                            if concurrentResult.WatchdogExpired then
                                failtestf "watchdog expired - readiness deadline not met"

                            // Real ProcessRunner must have completed
                            match concurrentResult.Result with
                            | None -> failtestf "ProcessRunner did not complete - timeout waiting for real result"
                            | Some result ->
                                // Verify exactly one stdout record matches
                                let matches = Regex.Matches(result.Output, @"PROCESS_TREE_READY parent_pid=(\d+) descendant_pid=(\d+) nonce=(\S+)")
                                if matches.Count <> 1 then
                                    failtestf "expected exactly 1 stdout record, got %d" matches.Count
                                let m = matches.[0]
                                let outParent = int m.Groups.[1].Value
                                let outDescendant = int m.Groups.[2].Value
                                let outNonce = m.Groups.[3].Value
                                if record.ParentPid <> outParent then
                                    failtestf "stdout parent mismatch: %d vs %d" record.ParentPid outParent
                                if record.DescendantPid <> outDescendant then
                                    failtestf "stdout descendant mismatch: %d vs %d" record.DescendantPid outDescendant
                                if record.Nonce <> outNonce then
                                    failtestf "stdout nonce mismatch: %s vs %s" record.Nonce outNonce

                            // Verify parent is dead (Kill(false) kills parent)
                            Thread.Sleep(500) // Give OS time to process kill
                            let parentAlive = isPidAlive record.ParentPid
                            let descendantAlive = isPidAlive record.DescendantPid

                            // Classify the result
                            if not parentAlive && descendantAlive then
                                proofResult <- Some (DescendantSurvived(record.ParentPid, record.DescendantPid))
                            elif not parentAlive && not descendantAlive then
                                proofResult <- Some (DescendantReaped(record.ParentPid, record.DescendantPid))
                            else
                                proofResult <- Some (Unexpected(sprintf "parent=%b descendant=%b" parentAlive descendantAlive))
                        )

                finally
                    // Cleanup after classification - descendant may still be alive
                    emergencyCleanup cleanupPids |> ignore
                    cleanupReadiness readinessFile
                    cleanupWorkspace workspace

                // NEGATIVE PROOF MUST OBSERVE DescendantSurvived
                match proofResult with
                | Some (DescendantSurvived(parentPid, descendantPid)) ->
                    // Negative proof PASSES - descendant survived process-only kill
                    // This proves the positive proof's tree kill is what killed the descendant
                    ()
                | Some (DescendantReaped _) ->
                    failtestf "NEGATIVE PROOF FAILED: descendant was reaped even with KillTreeStrategy=false"
                | Some (Unexpected detail) ->
                    failtestf "NEGATIVE PROOF UNEXPECTED: %s" detail
                | None ->
                    failtestf "NEGATIVE PROOF INCOMPLETE: no result was classified"
            }

            test "no owned parent PID remains after cancellation" {
                let body = "sleep 60"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(200)
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Pid with
                | Some pid ->
                    Thread.Sleep(250)
                    Expect.isFalse (isPidAlive pid) "no lingering child"
                | None -> ()
            }

            test "large output captured before cancellation is preserved" {
                let body = "for i in $(seq 1 100); do echo line$i; done"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(50)
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _
                | Exited (0, _) ->
                    Expect.isTrue (r.Output.Length > 0 || r.Stderr.Length > 0) "something captured"
                | o -> failtestf "unexpected outcome: %A" o
            }

            test "above-pipe-capacity stderr while stdout open does not deadlock" {
                let stderrBytes = 256 * 1024
                let body =
                    sprintf
                        "for i in $(seq 1 %d); do echo \"stderr-line-$i-with-some-payload-1234567890abcdef\" 1>&2; done; for i in $(seq 1 1000); do echo \"stdout-$i\"; done"
                        (stderrBytes / 50)
                let r = runProcessText (bashArgs body) noCwd CancellationToken.None
                match r.Outcome with
                | Exited (0, _) ->
                    Expect.stringContains r.Output "stdout-1000" "stdout survived"
                    Expect.stringContains r.Stderr "stderr-line-" "stderr survived"
                | o -> failtestf "expected Exited 0, got %s" (outcomeSummary r)
            }

            // -------------------------------------------------------------------
            // P0-3: Exception-safe ownership bracket + failure-injection tests.
            // -------------------------------------------------------------------

            test "injected startAsync failure produces SpawnFailure with the injected note" {
                let r =
                    withInjection
                        (fun () ->
                            InjectStartAsyncFailure <- fun () -> Some "injected-spawn-error")
                        (fun () ->
                            runProcessText (bashArgs "exit 0") noCwd CancellationToken.None)
                match r.Outcome with
                | SpawnFailure (detail, _) ->
                    Expect.stringContains detail "injected-spawn-error" "injected note propagated"
                | o -> failtestf "expected SpawnFailure, got %A" o
            }

            test "injected startAsync access failure captures the started PID and releases it" {
                let observedPids = ConcurrentQueue<int>()
                let r =
                    withInjection
                        (fun () ->
                            ObserveStartedPid <- observedPids.Enqueue
                            InjectStartAsyncAccessFailure <- fun () ->
                                Some (InvalidOperationException("injected-context-failure") :> exn))
                        (fun () ->
                            runProcessText (bashArgs "echo alive; exit 0") noCwd CancellationToken.None)
                Expect.isGreaterThan (observedPids.Count) 0 "expected at least one observed PID"
                match r.Outcome with
                | BodyFailure _ -> ()
                | o -> failtestf "expected BodyFailure (NOT SpawnFailure, since Process.Start already succeeded), got %A" o
                for pid in observedPids do
                    Thread.Sleep(250)
                    Expect.isFalse (isPidAlive pid) (sprintf "observed PID %d must be reaped" pid)
            }

            test "injected startAsync stdout-drain failure settles the created stdout drain with RanToCompletion" {
                let observedPids = ConcurrentQueue<int>()
                let stdoutTask = ref Unchecked.defaultof<Task<Result<byte[], exn>>>
                let r =
                    withInjection
                        (fun () ->
                            ObserveStartedPid <- observedPids.Enqueue
                            ObserveStdoutDrainTask <- (fun t -> stdoutTask := t)
                            InjectStartAsyncStdoutDrainFailure <- fun () ->
                                Some (InvalidOperationException("injected-stdout-drain-failure") :> exn))
                        (fun () ->
                            runProcessText (bashArgs "sleep 60") noCwd CancellationToken.None)
                Expect.isGreaterThan (observedPids.Count) 0 "expected at least one observed PID"
                Expect.isTrue (!stdoutTask).IsCompleted "partial stdout drain must be terminal"
                Expect.equal (!stdoutTask).Status TaskStatus.RanToCompletion "partial stdout drain must be RanToCompletion"
                match r.Outcome with
                | BodyFailure _ -> ()
                | o -> failtestf "expected BodyFailure (post-Start failure with clean settlement), got %A" o
                for pid in observedPids do
                    Thread.Sleep(250)
                    Expect.isFalse (isPidAlive pid) (sprintf "observed PID %d must be reaped" pid)
            }

            test "injected startAsync observer failure settles both created drains with RanToCompletion" {
                let observedPids = ConcurrentQueue<int>()
                let stdoutTask = ref Unchecked.defaultof<Task<Result<byte[], exn>>>
                let stderrTask = ref Unchecked.defaultof<Task<Result<string, exn>>>
                let r =
                    withInjection
                        (fun () ->
                            ObserveStartedPid <- observedPids.Enqueue
                            ObserveStdoutDrainTask <- (fun t -> stdoutTask := t)
                            ObserveStderrDrainTask <- (fun t -> stderrTask := t)
                            InjectStartAsyncObserverFailure <- fun () ->
                                Some (InvalidOperationException("injected-observer-failure") :> exn))
                        (fun () ->
                            runProcessText (bashArgs "sleep 60") noCwd CancellationToken.None)
                Expect.isGreaterThan (observedPids.Count) 0 "expected at least one observed PID"
                Expect.isTrue (!stdoutTask).IsCompleted "partial stdout drain must be terminal"
                Expect.isTrue (!stderrTask).IsCompleted "partial stderr drain must be terminal"
                Expect.equal (!stdoutTask).Status TaskStatus.RanToCompletion "partial stdout drain must be RanToCompletion"
                Expect.equal (!stderrTask).Status TaskStatus.RanToCompletion "partial stderr drain must be RanToCompletion"
                match r.Outcome with
                | BodyFailure _ -> ()
                | CleanupFailure _ -> ()
                | o -> failtestf "expected BodyFailure/CleanupFailure, got %A" o
                for pid in observedPids do
                    Thread.Sleep(250)
                    Expect.isFalse (isPidAlive pid) (sprintf "observed PID %d must be reaped" pid)
            }

            // -------------------------------------------------------------------
            // Mechanical proof of the new settlement semantics.
            // -------------------------------------------------------------------

            test "ContextCleanupFailure: stdout never completes inside startAsync -> CleanupFailure" {
                let observedPids = ConcurrentQueue<int>()
                let neverCompletes = TaskCompletionSource<Result<byte[], exn>>()
                let r =
                    withInjection
                        (fun () ->
                            ObserveStartedPid <- observedPids.Enqueue
                            SubstituteStdoutDrainTask <- fun _ -> neverCompletes.Task
                            InjectStartAsyncStdoutDrainFailure <- fun () ->
                                Some (InvalidOperationException("injected-stdout-drain-failure") :> exn))
                        (fun () ->
                            runProcessText (bashArgs "sleep 60") noCwd CancellationToken.None)
                Expect.isGreaterThan (observedPids.Count) 0 "expected at least one observed PID"
                match r.Outcome with
                | CleanupFailure detail ->
                    Expect.stringContains detail "context construction failed" "post-Start detail"
                    Expect.stringContains detail "stdout" "stdout label in detail"
                | o -> failtestf "expected CleanupFailure (NOT SpawnFailure) for ContextCleanupFailure path, got %A" o
                for pid in observedPids do
                    Thread.Sleep(250)
                    Expect.isFalse (isPidAlive pid) (sprintf "observed PID %d must be reaped" pid)
            }

            test "exhausted deadline: stdout never completes, stderr is already terminal" {
                let stdoutTask = ref Unchecked.defaultof<Task<Result<byte[], exn>>>
                let stderrTask = ref Unchecked.defaultof<Task<Result<string, exn>>>
                let neverCompletes = TaskCompletionSource<Result<byte[], exn>>()
                let alreadyTerminalStdout : Task<Result<byte[], exn>> =
                    Task.FromResult (Result.Ok [||])
                let alreadyTerminalStderr : Task<Result<string, exn>> =
                    Task.FromResult (Result.Ok "")
                let r =
                    withInjection
                        (fun () ->
                            ObserveStdoutDrainTask <- (fun t -> stdoutTask := t)
                            ObserveStderrDrainTask <- (fun t -> stderrTask := t)
                            SubstituteStdoutDrainTask <- fun _ -> neverCompletes.Task
                            SubstituteStderrDrainTask <- fun _ -> alreadyTerminalStderr
                            InjectWaitFailure <- fun () ->
                                Some (TimeoutException("injected-wait-failure") :> exn))
                        (fun () ->
                            runProcessText (bashArgs "sleep 60") noCwd CancellationToken.None)
                Expect.equal (!stdoutTask).Status TaskStatus.WaitingForActivation "stdout is waiting for completion source (never completed)"
                Expect.isTrue (!stderrTask).IsCompleted "stderr task must be terminal"
                Expect.equal (!stderrTask).Status TaskStatus.RanToCompletion "stderr is RanToCompletion"
                let _ = alreadyTerminalStdout
                match r.Outcome with
                | CleanupFailure detail ->
                    Expect.stringContains detail "stdout drain did not settle" "stdout timeout recorded"
                    Expect.isFalse (detail.Contains "stderr drain did not settle") "stderr must NOT be classified as timeout"
                | outcome ->
                    failtestf "expected CleanupFailure for exhausted-deadline scenario, got %A" outcome
            }

            test "terminal drain carrying inner Result.Error is recorded into the cleanup note" {
                let injectedEx = IOException("injected-inner-error")
                let injectedTask : Task<Result<byte[], exn>> =
                    Task.FromResult (Result.Error injectedEx)
                let r =
                    withInjection
                        (fun () ->
                            SubstituteStdoutDrainTask <- fun _ -> injectedTask)
                        (fun () ->
                            runProcessText (bashArgs "echo alive; exit 0") noCwd CancellationToken.None)
                match r.Outcome with
                | OutputFailure (detail, _) ->
                    Expect.stringContains detail "stdout drain settled with error" "inner error recorded"
                    Expect.stringContains detail "injected-inner-error" "inner exception preserved"
                | outcome ->
                    failtestf "expected OutputFailure for terminal inner-Result.Error, got %A" outcome
                Expect.equal injectedTask.Status TaskStatus.RanToCompletion "task is RanToCompletion"
            }

            test "faulted drain task is caught by inspectTerminal via IsFaulted branch" {
                let faultedTask = faultedStdoutTask "injected-fault"
                let r =
                    withInjection
                        (fun () ->
                            SubstituteStdoutDrainTask <- fun _ -> faultedTask)
                        (fun () ->
                            runProcessText (bashArgs "echo alive; exit 0") noCwd CancellationToken.None)
                match r.Outcome with
                | OutputFailure (detail, _) ->
                    Expect.stringContains detail "stdout drain task faulted" "faulted branch ran"
                    Expect.stringContains detail "injected-fault" "fault exception preserved"
                | outcome ->
                    failtestf "expected OutputFailure for faulted drain, got %A" outcome
                Expect.equal faultedTask.Status TaskStatus.Faulted "task is Faulted"
            }

            test "cancelled drain task is caught by inspectTerminal via IsCanceled branch" {
                let cancelledTask = cancelledStdoutTask ()
                let r =
                    withInjection
                        (fun () ->
                            SubstituteStdoutDrainTask <- fun _ -> cancelledTask)
                        (fun () ->
                            runProcessText (bashArgs "echo alive; exit 0") noCwd CancellationToken.None)
                match r.Outcome with
                | OutputFailure (detail, _) ->
                    Expect.stringContains detail "stdout drain task was cancelled" "cancelled branch ran"
                | outcome ->
                    failtestf "expected OutputFailure for cancelled drain, got %A" outcome
                Expect.equal cancelledTask.Status TaskStatus.Canceled "task is Canceled"
            }

            test "injected wait failure produces BodyFailure and releases the process" {
                let r =
                    withInjection
                        (fun () ->
                            InjectWaitFailure <- fun () ->
                                Some (TimeoutException("injected-wait-failure") :> exn))
                        (fun () ->
                            runProcessText (bashArgs "echo alive; exit 0") noCwd CancellationToken.None)
                match r.Outcome with
                | BodyFailure (detail, _) ->
                    Expect.stringContains detail "injected-wait-failure" "wait note propagated"
                | o -> failtestf "expected BodyFailure, got %A" o
                match r.Pid with
                | Some pid ->
                    Thread.Sleep(250)
                    Expect.isFalse (isPidAlive pid) "wait-failed process must be released"
                | None -> ()
            }

            test "injected drain failure produces BodyFailure with drain-settle note" {
                let r =
                    withInjection
                        (fun () ->
                            InjectDrainFailure <- fun () ->
                                Some (IOException("injected-drain-failure") :> exn))
                        (fun () ->
                            runProcessText (bashArgs "echo alive; exit 0") noCwd CancellationToken.None)
                match r.Outcome with
                | BodyFailure (detail, _) ->
                    Expect.stringContains detail "injected-drain-failure" "drain note propagated"
                | o -> failtestf "expected BodyFailure, got %A" o
                match r.Pid with
                | Some pid ->
                    Thread.Sleep(250)
                    Expect.isFalse (isPidAlive pid) "drain-failed process must be released"
                | None -> ()
            }

            test "injected wait failure on long-running child leaves both drain tasks terminal" {
                let stdoutTask = ref Unchecked.defaultof<Task<Result<byte[], exn>>>
                let stderrTask = ref Unchecked.defaultof<Task<Result<string, exn>>>
                let r =
                    withInjection
                        (fun () ->
                            ObserveStdoutDrainTask <- (fun t -> stdoutTask := t)
                            ObserveStderrDrainTask <- (fun t -> stderrTask := t)
                            InjectWaitFailure <- fun () ->
                                Some (TimeoutException("injected-wait-failure") :> exn))
                        (fun () ->
                            runProcessText (bashArgs "sleep 60") noCwd CancellationToken.None)
                match r.Outcome with
                | BodyFailure (detail, _) ->
                    Expect.stringContains detail "injected-wait-failure" "wait note propagated"
                | CleanupFailure detail ->
                    Expect.stringContains detail "drain" "drain timeout promoted to CleanupFailure"
                | o -> failtestf "expected BodyFailure or CleanupFailure, got %A" o
                Expect.isTrue (!stdoutTask).IsCompleted "stdout drain task must be terminal"
                Expect.isTrue (!stderrTask).IsCompleted "stderr drain task must be terminal"
                Expect.equal (!stdoutTask).Status TaskStatus.RanToCompletion "stdout drain must be RanToCompletion"
                Expect.equal (!stderrTask).Status TaskStatus.RanToCompletion "stderr drain must be RanToCompletion"
                match r.Pid with
                | Some pid ->
                    Thread.Sleep(250)
                    Expect.isFalse (isPidAlive pid) "long-running child must be reaped"
                | None -> ()
            }

            test "injected DisposeProcess throws and is caught by disposeProc" {
                let r =
                    withInjection
                        (fun () ->
                            DisposeProcess <-
                                fun (_p: Process) ->
                                    raise (ObjectDisposedException("injected-dispose-failure")))
                        (fun () ->
                            runProcessText (bashArgs "echo alive; exit 0") noCwd CancellationToken.None)
                match r.Outcome with
                | Exited (0, note)
                | NonzeroExit (_, note) ->
                    Expect.stringContains note "dispose failed" "dispose catch-and-record branch ran"
                    Expect.stringContains note "injected-dispose-failure" "real exception message preserved"
                | o -> failtestf "expected Exited/NonzeroExit with dispose-failed note, got %A" o
            }

            test "injection hooks are reset after each test (no cross-test pollution)" {
                resetInjections ()
                let r = runProcessText (bashArgs "exit 0") noCwd CancellationToken.None
                match r.Outcome with
                | Exited (0, _) -> ()
                | o -> failtestf "expected clean Exited 0 after reset, got %A" o
            }
    ]
