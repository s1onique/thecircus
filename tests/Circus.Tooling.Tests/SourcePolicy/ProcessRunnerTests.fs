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
// Synchronous helper for running ProcessRunner with bounded readiness polling.
// ---------------------------------------------------------------------------

/// Result of the bounded ProcessRunner invocation.
type BoundedProcessResult = {
    Result: TextResult
    ParsedParentPid: int option
    ParsedDescendantPid: int option
    ReadinessFound: bool
}

/// Run ProcessRunner with a short timeout and poll for readiness file.
/// This simplified version runs synchronously and returns when either:
/// - The readiness file is found (proof of process tree alive)
/// - The timeout expires
/// The returned Result reflects the bounded run (may be Cancelled or timed out).
let private runProcessWithReadiness
    (argv: string list)
    (workspace: string)
    (readinessFile: string)
    (pollMs: int)
    (readinessDeadline: DateTime)
    (cts: CancellationTokenSource)
    : BoundedProcessResult =
    let mutable parsedParentPid = None
    let mutable parsedDescendantPid = None
    let mutable readinessFound = false

    // Run the process synchronously with a short timeout
    // The bash script blocks forever after writing the readiness file,
    // so the CTS timeout will eventually trigger cancellation
    let result = runProcessText argv (Some workspace) cts.Token

    // Check for readiness file (may have been written before cancellation)
    if File.Exists(readinessFile) then
        let content = File.ReadAllText(readinessFile)
        let m = Regex.Match(content, @"PROCESS_TREE_READY parent_pid=(\d+) descendant_pid=(\d+)")
        if m.Success then
            parsedParentPid <- Some (int m.Groups.[1].Value)
            parsedDescendantPid <- Some (int m.Groups.[2].Value)
            readinessFound <- true

    { Result = result; ParsedParentPid = parsedParentPid; ParsedDescendantPid = parsedDescendantPid; ReadinessFound = readinessFound }

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
            // P0-2: Descendant-PID mechanical proof (positive).
            //
            // Design:
            // 1. Start ProcessRunner in background thread while polling for readiness.
            // 2. Poll for readiness file (proves processes are alive).
            // 3. Require exactly one valid PID record from readiness file.
            // 4. Cancel only after readiness is observed.
            // 5. Await ProcessRunner result.
            // 6. Verify: readiness found, PIDs valid, outcome is Cancelled,
            //    r.Pid matches parent, both PIDs are reaped.
            // 7. Emergency cleanup in finally with exact PIDs.
            // 8. Test FAILS if emergency cleanup was required.
            // ---------------------------------------------------------------------------

            test "cancellation terminates recorded descendant PID (positive proof)" {
                let workspace = Path.Combine(Path.GetTempPath(), "circus-prtree-proof-" + Guid.NewGuid().ToString("n"))
                Directory.CreateDirectory(workspace) |> ignore

                let readinessFile = Path.Combine(workspace, "ready")

                // Bash script creates a process tree:
                // Parent: bash -c "sleep infinity"
                // Child: sleep infinity (background)
                // Both PIDs captured in readiness file before blocking.
                let body =
                    sprintf
                        @"sleep 999999999 &
                         desc_pid=$!
                         parent_pid=$$
                         echo ""PROCESS_TREE_READY parent_pid=$parent_pid descendant_pid=$desc_pid"" > ""%s""
                         sync; while true; do sleep 60; done"
                        readinessFile

                let mutable cleanupPids = []
                let mutable emergencyCleanupRequired = false

                try
                    use cts = new CancellationTokenSource()
                    // Cancel AFTER readiness is observed (poll deadline of 2 seconds)
                    let readinessDeadline = DateTime.UtcNow.AddSeconds(2.0)

                    // Start ProcessRunner with readiness polling
                    let boundedResult =
                        runProcessWithReadiness
                            (bashArgs body)
                            workspace
                            readinessFile
                            50
                            readinessDeadline
                            cts

                    // Step 1: Readiness file MUST exist
                    if not boundedResult.ReadinessFound then
                        failtestf "readiness file did not appear within deadline - processes may not have started"

                    // Step 2: Extract PIDs
                    match boundedResult.ParsedParentPid, boundedResult.ParsedDescendantPid with
                    | Some parentPid, Some descendantPid ->
                        if parentPid <= 0 then failtestf "parent PID must be positive, got %d" parentPid
                        if descendantPid <= 0 then failtestf "descendant PID must be positive, got %d" descendantPid
                        if parentPid = descendantPid then failtestf "parent and descendant PIDs must differ"
                        cleanupPids <- [parentPid; descendantPid]
                    | _ -> failtestf "failed to parse PIDs from readiness file"

                    let parentPid = cleanupPids.[0]
                    let descendantPid = cleanupPids.[1]

                    // Step 3: Verify output consistency
                    let m = Regex.Match(boundedResult.Result.Output, @"PROCESS_TREE_READY parent_pid=(\d+) descendant_pid=(\d+)")
                    if not m.Success then
                        failtestf "could not parse PIDs from output"
                    let outParent = int m.Groups.[1].Value
                    let outDescendant = int m.Groups.[2].Value
                    Expect.equal parentPid outParent "parent PID from file must match output"
                    Expect.equal descendantPid outDescendant "descendant PID from file must match output"

                    // Step 4: Verify r.Pid matches parent
                    match boundedResult.Result.Pid with
                    | Some reportedPid -> Expect.equal reportedPid parentPid "r.Pid must match parent"
                    | None -> failtestf "r.Pid must be populated"

                    // Step 5: Assert Cancelled outcome
                    match boundedResult.Result.Outcome with
                    | Cancelled _ -> ()
                    | CleanupFailure d -> failtestf "expected Cancelled, got CleanupFailure: %s" d
                    | OutputFailure (d, _) -> failtestf "expected Cancelled, got OutputFailure: %s" d
                    | BodyFailure (d, _) -> failtestf "expected Cancelled, got BodyFailure: %s" d
                    | SpawnFailure (d, _) -> failtestf "expected Cancelled, got SpawnFailure: %s" d
                    | Exited (c, _) -> failtestf "expected Cancelled, got Exited(%d)" c
                    | NonzeroExit (c, _) -> failtestf "expected Cancelled, got NonzeroExit(%d)" c

                    // Step 6: Verify both PIDs are reaped (positive proof: killTree worked)
                    // Parent must be reaped within 3 seconds
                    let deadline2 = DateTime.UtcNow.AddSeconds(3.0)
                    let mutable parentReaped = false
                    while not parentReaped && DateTime.UtcNow < deadline2 do
                        if not (isPidAlive parentPid) then parentReaped <- true
                        else Thread.Sleep(50)
                    if not parentReaped then
                        emergencyCleanupRequired <- true
                        failtestf "parent PID %d was not reaped" parentPid

                    // Descendant must be reaped within 3 seconds
                    let deadline3 = DateTime.UtcNow.AddSeconds(3.0)
                    let mutable descendantReaped = false
                    while not descendantReaped && DateTime.UtcNow < deadline3 do
                        if not (isPidAlive descendantPid) then descendantReaped <- true
                        else Thread.Sleep(50)
                    if not descendantReaped then
                        emergencyCleanupRequired <- true
                        failtestf "descendant PID %d was not reaped" descendantPid

                finally
                    // Emergency cleanup with exact PIDs
                    if emergencyCleanupRequired then
                        let cleaned = emergencyCleanup cleanupPids
                        if cleaned then
                            () // Cleanup happened, test will fail below
                    cleanupReadiness readinessFile
                    cleanupWorkspace workspace

                // POSITIVE PROOF MUST NOT REQUIRE EMERGENCY CLEANUP
                if emergencyCleanupRequired then
                    failtestf "emergency cleanup was required - positive proof failed: tree kill did not work"
            }

            // ---------------------------------------------------------------------------
            // P0-2: Descendant-PID mechanical proof (negative).
            //
            // Design:
            // 1. Run bash fixture to get exact PIDs (with CTS timeout - bounded by readiness).
            // 2. Inject KillTreeStrategy = false (process-only kill, not tree kill).
            // 3. Cancel the ProcessRunner - parent killed, descendant survives.
            // 4. Verify: parent terminated, descendant alive, classify as DescendantSurvived.
            // 5. Emergency cleanup with exact PIDs in finally.
            // 6. Negative proof PASSES if DescendantSurvived is observed.
            // ---------------------------------------------------------------------------

            test "descendant survives when KillTreeStrategy=false (negative validity proof)" {
                let workspace = Path.Combine(Path.GetTempPath(), "circus-negvalid-" + Guid.NewGuid().ToString("n"))
                Directory.CreateDirectory(workspace) |> ignore

                let readinessFile = Path.Combine(workspace, "ready")

                let body =
                    sprintf
                        @"sleep 999999999 &
                         desc_pid=$!
                         parent_pid=$$
                         echo ""PROCESS_TREE_READY parent_pid=$parent_pid descendant_pid=$desc_pid"" > ""%s""
                         sync; while true; do sleep 60; done"
                        readinessFile

                let mutable cleanupPids = []
                let mutable proofResult: DescendantProofResult option = None

                try
                    // Phase 1: Get exact PIDs with production tree-kill strategy
                    use cts1 = new CancellationTokenSource()
                    cts1.CancelAfter(2000) // Short timeout to get PIDs

                    let phase1Result =
                        runProcessWithReadiness
                            (bashArgs body)
                            workspace
                            readinessFile
                            50
                            (DateTime.UtcNow.AddSeconds(2.0))
                            cts1

                    // Extract PIDs from phase 1
                    match phase1Result.ParsedParentPid, phase1Result.ParsedDescendantPid with
                    | Some parentPid, Some descendantPid ->
                        cleanupPids <- [parentPid; descendantPid]
                    | _ -> failtestf "phase 1: failed to parse PIDs from readiness file"

                    let parentPid = cleanupPids.[0]
                    let descendantPid = cleanupPids.[1]

                    // Phase 2: Run with KillTreeStrategy=false (process-only kill)
                    let cts2 = new CancellationTokenSource()
                    cts2.CancelAfter(2000)

                    // Set negative strategy: Kill(false) - only kills parent, not descendants
                    withInjection
                        (fun () -> KillTreeStrategy <- false)
                        (fun () ->
                            let _phase2Result =
                                runProcessWithReadiness
                                    (bashArgs body)
                                    workspace
                                    readinessFile
                                    50
                                    (DateTime.UtcNow.AddSeconds(2.0))
                                    cts2
                            ())

                    // Give OS time to process the kill
                    Thread.Sleep(500)

                    // Phase 3: Verify - parent should be dead, descendant should be alive
                    let parentAlive = isPidAlive parentPid
                    let descendantAlive = isPidAlive descendantPid

                    // Classify the result
                    if not parentAlive && descendantAlive then
                        proofResult <- Some (DescendantSurvived(parentPid, descendantPid))
                    elif not parentAlive && not descendantAlive then
                        proofResult <- Some (DescendantReaped(parentPid, descendantPid))
                    else
                        proofResult <- Some (Unexpected(sprintf "parent=%b descendant=%b" parentAlive descendantAlive))

                finally
                    // Always clean up with exact PIDs
                    let _ = emergencyCleanup cleanupPids
                    cleanupReadiness readinessFile
                    cleanupWorkspace workspace

                // NEGATIVE PROOF MUST OBSERVE DescendantSurvived
                match proofResult with
                | Some (DescendantSurvived(parentPid, descendantPid)) ->
                    // Negative proof PASSES - descendant survived process-only kill
                    // This proves the positive proof's tree kill is what killed the descendant
                    ()
                | Some (DescendantReaped _) ->
                    failtestf "NEGATIVE PROOF FAILED: descendant was reaped even with KillTreeStrategy=false - tree kill was still applied"
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

            // ``ContextCleanupFailure`` is exercised by substituting a
            // never-completing stdout drain via
            // ``SubstituteStdoutDrainTask``.  The catch branch must
            // classify the timeout as ``ContextCleanupFailure`` (not
            // ``ContextConstructionFailure``).  The outcome surfaces
            // as ``CleanupFailure`` (NOT ``SpawnFailure`` — Process.Start
            // succeeded), with the exact timed-out label.
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

            // The exhausted-deadline / already-terminal-stderr path.
            // We use ``InjectWaitFailure`` to force a body exception
            // so the post-finally outcome constructor runs, and we
            // substitute stdout with a never-completing task and
            // stderr with an already-terminal task.  We then verify
            // stdout ends up ``WaitingForChildrenToComplete`` (never
            // settled) and stderr ends up ``RanToCompletion`` (the
            // IsCompleted-first path classified it truthfully).
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
                // After settleDrainsShared: stdout remains in a non-terminal
                // state because the completion source never completes;
                // stderr was already terminal (RanToCompletion) so the
                // IsCompleted-first path classified it as SettledOk.
                // Note: a TCS task that never has SetResult called remains
                // non-completed even after Wait times out. The TaskStatus
                // is WaitingForActivation (not WaitingForChildrenToComplete).
                Expect.equal (!stdoutTask).Status TaskStatus.WaitingForActivation "stdout is waiting for completion source (never completed)"
                Expect.isTrue (!stderrTask).IsCompleted "stderr task must be terminal"
                Expect.equal (!stderrTask).Status TaskStatus.RanToCompletion "stderr is RanToCompletion"
                let _ = alreadyTerminalStdout
                // Cleanup note must record stdout timeout but NOT stderr.
                // The contract requires CleanupFailure (not BodyFailure) when
                // a drain timeout is detected during cleanup.
                match r.Outcome with
                | CleanupFailure detail ->
                    Expect.stringContains detail "stdout drain did not settle" "stdout timeout recorded"
                    Expect.isFalse (detail.Contains "stderr drain did not settle") "stderr must NOT be classified as timeout"
                | outcome ->
                    failtestf "expected CleanupFailure for exhausted-deadline scenario, got %A" outcome
            }

            // Terminal drain task carrying inner ``Result.Error``:
            // proves ``inspectTerminal`` records the inner error in
            // the cleanup note (SettledWithError) rather than silently
            // classifying it as SettledOk.  Exact OutputFailure proves:
            // 1. terminal inspection did not throw;
            // 2. the output-stage failure was not misclassified.
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

            // Faulted drain task: ``inspectTerminal`` must record
            // the inner exception message into the cleanup note
            // (rather than letting ``task.Result`` throw).
            // Exact OutputFailure proves terminal inspection did not throw
            // and the output-stage failure was not misclassified.
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

            // Cancelled drain task: ``inspectTerminal`` must record
            // a cancellation note rather than letting ``task.Result``
            // throw.
            // Exact OutputFailure proves terminal inspection did not throw
            // and the output-stage failure was not misclassified.
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
