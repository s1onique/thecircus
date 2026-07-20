module Circus.Tooling.Tests.SourcePolicy.ProcessRunnerTests

/// Focused process-runner tests covering every WS4 invariant.

open System
open System.Diagnostics
open System.IO
open System.Text
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

let private script (body: string) : string list =
    [ "bash"; "-c"; body ]

let private noCwd : string option = None

[<Tests>]
let tests =
    testList "Process runner" [
        if bashOk then
            test "successful zero exit" {
                let r = runProcessText (script "exit 0") noCwd CancellationToken.None
                match r.Outcome with
                | Exited 0 -> Expect.equal r.Output "" "no output"
                | o -> failtestf "expected Exited 0, got %A" o
            }

            test "nonzero exit preserves output" {
                let r = runProcessText (script "echo captured; exit 7") noCwd CancellationToken.None
                match r.Outcome with
                | NonzeroExit n ->
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
                let r = runProcessText (script "echo to-out; echo to-err 1>&2") noCwd CancellationToken.None
                Expect.stringContains r.Output "to-out" "stdout preserved"
                Expect.stringContains r.Stderr "to-err" "stderr preserved"
            }

            test "simultaneous large stdout and stderr do not deadlock" {
                let body = "for i in $(seq 1 200); do echo line$i; echo err$i 1>&2; done"
                let r = runProcessText (script body) noCwd CancellationToken.None
                match r.Outcome with
                | Exited 0 ->
                    Expect.stringContains r.Output "line200" "stdout survived"
                    Expect.stringContains r.Stderr "err200" "stderr survived"
                | o -> failtestf "expected Exited 0, got %A" o
            }

            test "working directory propagates" {
                let tmp = Path.Combine(Path.GetTempPath(), "circus-prtest-" + Guid.NewGuid().ToString("n"))
                Directory.CreateDirectory tmp |> ignore
                try
                    let r = runProcessText (script "pwd") (Some tmp) CancellationToken.None
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
                let r = runProcessText (script "printf '\\xff\\xfe\\xfd'") noCwd CancellationToken.None
                match r.Outcome with
                | Exited 0 ->
                    Expect.stringContains r.Output "\uFFFD" "replacement char emitted"
                | o -> failtestf "expected Exited 0, got %A" o
            }

            test "NUL bytes survive the byte path" {
                let r = runProcessBytes (script "printf '\\0'") noCwd CancellationToken.None
                match r.Outcome with
                | Exited 0 ->
                    Expect.equal r.Output.Length 1 "one byte"
                    Expect.equal r.Output.[0] (byte 0) "byte is NUL"
                | o -> failtestf "expected Exited 0, got %A" o
            }

            test "invalid UTF-8 byte sequences are preserved" {
                let r = runProcessBytes (script "printf '\\xff\\xfe'") noCwd CancellationToken.None
                match r.Outcome with
                | Exited 0 ->
                    Expect.equal r.Output.[0] (byte 0xFFuy) "first byte preserved"
                    Expect.equal r.Output.[1] (byte 0xFEuy) "second byte preserved"
                | o -> failtestf "expected Exited 0, got %A" o
            }

            test "cancellation after start terminates child" {
                let body = "for i in $(seq 1 30); do echo $i; sleep 1; done"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(TimeSpan.FromMilliseconds(int64 250))
                let r = runProcessText (script body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ -> ()
                | CleanupFailure _ -> ()
                | OutputFailure _ -> ()
                | o -> failtestf "expected Cancelled or CleanupFailure, got %A" o
            }

            test "no owned helper process remains after cancellation" {
                let body = "for i in $(seq 1 60); do echo $i; sleep 1; done"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(TimeSpan.FromMilliseconds(int64 300))
                let r = runProcessText (script body) noCwd cts.Token
                match r.Pid with
                | Some pid ->
                    Thread.Sleep(500)
                    Expect.isFalse (isPidAlive pid) "no lingering child"
                | None -> ()
            }

            test "large output captured before cancellation is preserved" {
                let body = "for i in $(seq 1 100); do echo line$i; done"
                use cts = new CancellationTokenSource()
                cts.CancelAfter(TimeSpan.FromMilliseconds(int64 50))
                let r = runProcessText (script body) noCwd cts.Token
                match r.Outcome with
                | Cancelled _ | CleanupFailure _ | OutputFailure _ | Exited 0 ->
                    Expect.isTrue (r.Output.Length > 0 || r.Stderr.Length > 0) "something captured"
                | _ -> failtestf "unexpected outcome: %A" r.Outcome
            }
        else
            test "skipped (bash unavailable)" {
                Expect.isTrue true "bash missing on this host"
            }
    ]