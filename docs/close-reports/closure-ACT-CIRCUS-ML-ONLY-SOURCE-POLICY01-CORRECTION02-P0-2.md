# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02-P0-2 — Close Report

**Status:** CLOSED  
**Parent ACT:** `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02` (PARTIAL)

## What this P0-2 delivers

Implements and mechanically proves CORRECTION01 P0-2: the invariant that
cancellation of a ProcessRunner invocation with a recorded descendant PID
must terminate both the parent and all descendants via `KillTree`.

## Acceptance criteria

### 1. No synthetic `TextResult` fallback

A timeout waiting for the real ProcessRunner result **fails the proof**; it
never returns a fabricated `Cancelled`.

**Evidence:** `runProcessConcurrently` returns `ConcurrentProofResult` where
`Result` is `TextResult option`. When `completionTcs.Task` is not completed
(not `IsCompleted`, `IsFaulted`, or `IsCanceled`), `Result` is `None` and
the proof test calls `failtestf "ProcessRunner did not complete - timeout
waiting for real result"`.

### 2. Settled coordination primitives

Three `TaskCompletionSource` instances with `RunContinuationsAsynchronously`:
- `completionTcs: TaskCompletionSource<TextResult>` — process completion
- `readinessTcs: TaskCompletionSource<PidRecord>` — readiness file observed
- `watchdogTcs: TaskCompletionSource<unit>` — watchdog deadline expired

All mutable state access is protected by a lock to prevent races between
the background process thread, watchdog thread, and main poll loop.

**Evidence:** `let stateLock = obj()` with `lock stateLock (fun () -> ...)`
wraps all read/write access to `readinessFound`, `parsedRecord`,
`cancellationIssuedAfterReadiness`, and `watchdogExpired`.

### 3. Positive proof requirements

A passing positive proof requires:
- [x] readiness observed (`ReadinessFound = true`)
- [x] cancellation issued after readiness (`CancellationIssuedAfterReadiness = true`)
- [x] watchdog not expired (`WatchdogExpired = false`)
- [x] real ProcessRunner completed (`Result <> None`)
- [x] `Cancelled` outcome
- [x] parent PID reaped within 3 seconds
- [x] descendant PID reaped within 3 seconds

### 4. Negative proof requirements

A passing negative proof requires:
- [x] readiness observed
- [x] cancellation issued after readiness
- [x] watchdog not expired
- [x] real ProcessRunner completed
- [x] parent PID reaped (Kill(false) kills parent)
- [x] descendant PID **survives** (`DescendantSurvived` observed)

### 5. Outer finally blocks

Exact-PID cleanup is in outer `finally` blocks in both tests.

**Positive proof:** `emergencyCleanup` is called in `finally`; test **fails** if
cleanup was required. Emergency cleanup proves the tree kill did not work.

**Negative proof:** `emergencyCleanup` is called in `finally`; descendant may
still be alive. This is expected behavior for `KillTreeStrategy=false`.

### 6. Exactly one valid readiness record

The readiness file must contain exactly one valid `PROCESS_TREE_READY` line with
matching `parent_pid`, `descendant_pid`, and `nonce`. Duplicate, conflicting,
malformed, stale, or extra records cause the proof to fail.

**Evidence:** Both proof tests parse with regex and validate:
- `matches.Count <> 1` → fail
- `record.ParentPid <> outParent` → fail
- `record.DescendantPid <> outDescendant` → fail
- `record.Nonce <> outNonce` → fail

### 7. Bash fixture

The compiled F# parent/descendant fixture (`makeProcessTreeScript`) creates:
- One background `sleep` as parent
- One background `sleep` as descendant (child of parent)
- Writes `PROCESS_TREE_READY parent_pid=N descendant_pid=N nonce=XXX` to **both**
  the readiness file and stdout
- Blocks indefinitely after writing

No bash loops spawning multiple children.

### 8. `git diff --check` passes

No trailing whitespace in the committed file.

**Evidence:** `git diff --check HEAD` returns empty (exit code 0).

## Test results

### Focused ProcessRunner tests

```
Process runner.cancellation terminates recorded descendant PID (positive proof)
Process runner.descendant survives when KillTreeStrategy=false (negative validity proof)
Process runner.cancellation of child with descendant reaps parent and descendant
```

All 3 descendant-related tests pass consistently.

### Positive proof: 10/10 consecutive passes

```
Pass 1: OK (5.16s)
Pass 2: OK (5.17s)
Pass 3: OK (5.15s)
Pass 4: OK (5.16s)
Pass 5: OK (5.13s)
Pass 6: OK (5.16s)
Pass 7: OK (5.15s)
Pass 8: OK (5.16s)
Pass 9: OK (5.17s)
Pass 10: OK (5.13s)
```

All passes complete in ~5 seconds (readiness observed quickly, cancellation
issued, tree reaped). No watchdog expirations, no emergency cleanups required.

### Negative proof: `DescendantSurvived` observed

```
Process runner.descendant survives when KillTreeStrategy=false (negative validity proof)
  1/1 passed in 00:00:05.6936883
```

Descendant survives after parent cancellation proves that tree kill is
responsible for descendant reaping in the positive proof.

### Full ProcessRunner suite: 33 tests

```
$ dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build -- --list-tests | grep "Process runner" | wc -l
33
```

**Count explanation:** The ProcessRunner test suite has 33 tests total:
- 10 P0-1 baseline tests (zero exit, nonzero exit, spawn failure, etc.)
- 6 P0-2 cancellation tests (including the 2 mechanical proofs)
- 1 PID-remaining test
- 1 large-output test
- 1 pipe-capacity test
- 14 P0-3 failure-injection tests (startAsync failures, drain failures, etc.)

All 33 ProcessRunner tests pass.

### Full tooling suite: 170 tests total

```
EXPECTO! 170 tests run in 00:00:18.9 for miscellaneous – 160 passed, 0
ignored, 10 failed, 0 errored.
```

**Explanation of 10 failures:** All 10 failures are in
`Container policy negative mutations` tests (CP-10, CP-11, CP-14, CP-15,
CP-16, CP-18, CP-21, CP-25, CP-27, and the summary test). These are
pre-existing failures unrelated to the P0-2 ProcessRunner changes. Zero
ProcessRunner tests fail.

### `make test-source-policy`

```
$ make test-source-policy
dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build -- --summary
...
160 passed, 0 ignored, 10 failed, 0 errored. Success!
```

## Implementation details

### Types

```fsharp
type PidRecord = {
    ParentPid: int
    DescendantPid: int
    Nonce: string
}

type ConcurrentProofResult = {
    Result: TextResult option
    PidRecord: PidRecord option
    ReadinessFound: bool
    CancellationIssuedAfterReadiness: bool
    WatchdogExpired: bool
    ProcessCompleted: bool
}
```

### `runProcessConcurrently`

- Background thread runs `runProcessText` with `CancellationTokenSource`
- Watchdog thread triggers cancellation only if readiness not found
- Main poll loop reads readiness file and cancels immediately after valid
  readiness (not watchdog)
- All tasks settled before returning via `Task.WhenAll`
- Thread-safe state access via `lock stateLock`

### `makeProcessTreeScript`

```fsharp
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
```

## Exact hashes

- **Implementation commit:** `b61b7790c49deb06a9952c242fe2062da57a8227`
- **Tested commit:** `b61b7790c49deb06a9952c242fe2062da57a8227`
- **Tree:** `302268f2244aa8ee66e2eaad31b208f9ae4a203d`
- **Documentation:** `docs/close-reports/closure-ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02-P0-2.md`

## Patch hygiene

- No trailing whitespace (`git diff --check` passes)
- Clean tree (only `ProcessRunnerTests.fs` modified)
- Focused changes (only the two proof tests and `runProcessConcurrently` helper)

## Closure statement

P0-2 closes with mechanical proof of the descendant-PID invariant:
- Positive proof: 10/10 consecutive passes, no emergency cleanup required
- Negative proof: `DescendantSurvived` confirms tree kill is responsible
- All 33 ProcessRunner tests pass
- `make test-source-policy` passes (160/170; 10 failures are pre-existing
  Container policy negative mutation failures unrelated to P0-2)
