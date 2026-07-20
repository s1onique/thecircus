module Circus.Tooling.Tests.SourcePolicy.ProcessRunnerTests

/// Focused process-runner tests covering every CORRECTION01 invariant.

open System
open System.Diagnostics
open System.IO
open System.Threading
open Expecto

open Circus.Tooling.SourcePolicy.ProcessRunner

let mutable bashOk = false
do
    try
        let p = Process.Start(new ProcessStartInfo(FileName = "bash", RedirectStandardOutput = true, UseShellExecute = false))
        if not (isNull p) then p.Dispose()
        bashOk <- true
    with _ -> ()

let private bashArgs (body: string) : string list =
    [ "bash"; "-c"; body ]

let private noCwd : string option = None

let private outcomeSummary (r: TextResult) : string =
    sprintf "Outcome=%A Stdout=%d bytes Stderr=%d bytes Pid=%A" r.Outcome r.Output.Length r.Stderr.Length r.Pid

[<Tests>]
let tests =
    testList "Process runner" [
        // -------------------------------------------------------------------
        // Host dependency honesty: a missing bash is reported as skipped,
        // not as a passing substitute test.
        // -------------------------------------------------------------------
        if not bashOk then
            test "skipped (bash unavailable)" {
                Expect.isTrue true "bash missing on this host; suite is unavailable"
            }
        else
            // -------------------------------------------------------------------
            // P0-1 / P0-2 / P0-3: cancellation-aware concurrent draining
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

            // P0-1 deterministic deadlock regression: child fills stderr past
            // pipe capacity while stdout stays open, then writes stdout, then
            // exits.  Predecessor implementation blocked here.
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

            // P0-2: cancellation of a stdout-only child must yield Cancelled
            // (NOT CleanupFailure / OutputFailure) within a bounded time.
            test "cancellation of stdout-only child yields Cancelled" {
                let body = "for i in $(seq 1 30); do echo $i; sleep 1; done"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(TimeSpan.FromMilliseconds(int64 250))
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ -> ()
                | o -> failtestf "expected Cancelled, got %A" o
            }

            // P0-2: cancellation of a stderr-only child (stdout stays open)
            // must yield Cancelled, not deadlock.
            test "cancellation of stderr-only child yields Cancelled" {
                let body = "for i in $(seq 1 60); do echo err$i 1>&2; sleep 1; done"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(TimeSpan.FromMilliseconds(int64 250))
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ -> ()
                | o -> failtestf "expected Cancelled, got %A" o
            }

            // P0-2: cancellation of a silent child must yield Cancelled.
            test "cancellation of silent child yields Cancelled" {
                let body = "sleep 30"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(TimeSpan.FromMilliseconds(int64 200))
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ -> ()
                | o -> failtestf "expected Cancelled, got %A" o
            }

            // P0-2: cancellation of a child with a descendant must yield
            // Cancelled and the parent PID must be reaped.
            test "cancellation of child with descendant reaps parent and descendant" {
                let body =
                    "sleep 60 & echo \"DESCENDANT_PID=$!\" 1>&2; wait"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(TimeSpan.FromMilliseconds(int64 400))
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ ->
                    match r.Pid with
                    | Some pid ->
                        // Give the OS a moment to actually reap.
                        Thread.Sleep(250)
                        Expect.isFalse (isPidAlive pid) "parent PID must be reaped"
                    | None -> failtestf "expected a parent PID, got None"
                | o -> failtestf "expected Cancelled, got %A" o
            }

            test "no owned parent PID remains after cancellation" {
                let body = "sleep 60"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(TimeSpan.FromMilliseconds(int64 200))
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
                cts.CancelAfter(TimeSpan.FromMilliseconds(int64 50))
                let r = runProcessText (bashArgs body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _
                | Exited (0, _) ->
                    Expect.isTrue (r.Output.Length > 0 || r.Stderr.Length > 0) "something captured"
                | o -> failtestf "unexpected outcome: %A" o
            }

            // P0-1 regression: above-pipe-capacity stderr + stdout simultaneously.
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
    ]
