# closure-ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION01-P0-3 — Close Report

## Verdict

**P0-3 lifecycle ownership — CLOSED**

P0-3 is mechanically resolved. The implementation correctly handles all drain task
terminal states (success, faulted, cancelled, and inner `Result.Error`) without
allowing exceptions to escape the cleanup boundary.

## Immutable implementation boundary

| field | value |
| --- | --- |
| implementation commit / detached `HEAD` | `6e7b12d134ce062f10f236fb55f5fac63c01dafe` |
| implementation tree / detached `HEAD^{tree}` | `a8ad4bc81fd29a22f8dca7faf6a46ce35a0b3c00` |
| evidence test commit | `6e7b12d134ce062f10f236fb55f5fac63c01dafe` |
| evidence test tree | `a8ad4bc81fd29a22f8dca7faf6a46ce35a0b3c00` |
| ProcessRunnerTests | 31/31 pass |

## Implementation summary

### `inspectTerminal` totality

`Task.IsCompleted` is true for all three terminal states (`RanToCompletion`,
`Faulted`, and `Canceled`), while accessing `Task<TResult>.Result` throws for
faulted and cancelled tasks. The implementation checks these states before
accessing `Result`:

```fsharp
let private inspectTerminal (task: Task<Result<'a, exn>>) (label: string) (note: string ref) =
    try
        if task.IsCanceled then
            appendNote note (sprintf "%s drain task was cancelled" label)
            SettledWithError
        elif task.IsFaulted then
            // ... extract inner exception messages
            SettledWithError
        else
            match task.Result with
            | Ok _ -> SettledOk
            | Error ex ->
                appendNote note (sprintf "%s drain settled with error: %s" label ex.Message)
                SettledWithError
    with ex ->
        appendNote note (sprintf "%s terminal inspection failed: %s" label ex.Message)
        SettledWithError
```

### `settleDrainsSharedSafe` containment

Settlement exceptions cannot bypass disposal:

```fsharp
let private settleDrainsSharedSafe (...) =
    try
        settleDrainsShared stdout stderr note
    with ex ->
        appendNote note (sprintf "settle threw, classified as drain timeout: %s" ex.Message)
        DrainTimeout [ "settle" ]
```

### Corrected test fixtures

* `Task.FromCanceled<TResult>` requires a `CancellationToken` for which cancellation
  has already been requested. `CancellationToken(true)` supplies this.
* Never-completing `TaskCompletionSource` tasks remain in `WaitingForActivation`
  state, not `WaitingForChildrenToComplete`.

## Mechanical proof

| Test | Required outcome | Status |
| --- | --- | --- |
| `ContextCleanupFailure: stdout never completes inside startAsync` | `CleanupFailure` | ✓ exact |
| `exhausted deadline: stdout never completes, stderr is already terminal` | `CleanupFailure` | ✓ exact |
| `terminal drain carrying inner Result.Error` | `OutputFailure` | ✓ exact |
| `faulted drain task via IsFaulted branch` | `OutputFailure` | ✓ exact |
| `cancelled drain task via IsCanceled branch` | `OutputFailure` | ✓ exact |

## Working tree status

```
commit = 6e7b12d134ce062f10f236fb55f5fac63c01dafe
tree   = a8ad4bc81fd29a22f8dca7faf6a46ce35a0b3c00
status = clean
```

## Updated ACT status

| Defect | Status |
| --- | --- |
| P0-1 asynchronous concurrent draining | Resolved; canonical-gate inclusion pending |
| P0-2 cancellation | Partial — descendant-PID proof remains |
| **P0-3 lifecycle ownership and cleanup** | **Resolved — mechanically proven 31/31** |
| P0-4 single-invocation accounting | Resolved |
| P0-5 mutation proof | Open |
| P0-6 evidence identity | Needs rebinding to `6e7b12d` / `a8ad4bc8` |
| P1-1 exact parity identity | Open |
| P1-2 failure-domain distinction | Resolved |
| P1-3 Bash availability honesty | Open |
| Canonical gate coverage | Open |

## Next work inside CORRECTION01

1. Record new implementation/test identity in the close report (this commit)
2. Complete descendant-PID assertion (P0-2)
3. Replace mutation global state with immutable sequenced 22-case proof (P0-5)
4. Remove parity prefix aliasing (P1-1)
5. Replace Bash substitute pass with `ptest` (P1-3)
6. Put full source-policy suite into canonical gate and regenerate from fresh checkout

## References

* [Task.IsCompleted Property](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.iscompleted)
* [Task.FromCanceled Method](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.fromcanceled)
