# ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION02

## Status

**READY — P0**

**Verdict: PARTIAL_CHECKPOINT**

## Parent epic

`EPIC-CIRCUS-FSHARP-DIAGNOSTIC-KNOWLEDGE-AND-HISTORY-SAFETY01`

## Predecessor

`ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION01`

## Problem statement

The review of CORRECTION01 closed two production changes (DisposeAsync
registration disposal and Deferred-ContinueWith removal) but routed a
single remaining detective step to the next slice:

* the canonical 36-test BoundedProcess suite still hangs in the test
  runner, and the dump from c69d189 is the only artefact that
  identifies the runner as the waiter (not the producer) of the
  pending task;
* dynamic FSI compilation is the dominant cost of every real-process
  test, and the registration-callback race hazard identified by the
  reviewer is not yet exercised by an explicit regression.

This correction ACT closes the remaining slice.

## Doctrine

The reviewer's guidance is preserved unchanged:

* the F# / FSharpDiagnostics subsystem continues to treat the
  upstream canonical production processor as the only authority;
* this slice contains zero Git subprocess work; the new fixture
  executable is a *test-side* F# project that is never loaded into
  the test process;
* BoundedProcess authority is not yet promoted: `git_adapter_allowed`
  remains `false`. The Git adapter is the next slice.

## Scope of changes

### 1. Precompiled F# fixture executable

A new test-side project
`tests/Circus.Tooling.ProcessTreeFixture/Circus.Tooling.ProcessTreeFixture.fsproj`
defines a standalone F# executable with a small, named mode grammar:

```text
empty
stdout <count>
stderr <count>
both <stdout-count> <stderr-count>
sleep <milliseconds>
exit <code>
exit-with-both <stdout-count> <stderr-count> <code>
echo-args <args...>
working-directory
```

The fixture writes raw bytes to the standard streams via
`Stream.Write` so the bytes the production reader observes are exactly
the bytes the fixture writes. The fixture is launched as a separate
`dotnet <fixture.dll> <mode> ...` process for every real-process test.
The fixture is **never** loaded into the test process.

`<UseAppHost>` is `false` so the canonical invocation path is
`dotnet <fixture.dll>`, identical across host operating systems.
A dedicated `FSharp.Core` package reference keeps the fixture's
runtimeconfig.json self-contained.

### 2. Test project wiring

`tests/Circus.Tooling.Tests.fsproj` adds a `ProjectReference` to the
fixture with:

```xml
<ProjectReference Include="..\Circus.Tooling.ProcessTreeFixture\Circus.Tooling.ProcessTreeFixture.fsproj"
                  PrivateAssets="all"
                  ReferenceOutputAssembly="false"
                  OutputItemType="None"
                  SkipGetTargetFrameworkProperties="true" />
```

The fixture is **built** as a dependency but **never loaded** into the
test process. A custom MSBuild target `CopyProcessTreeFixture` runs
after the test project's `Build` and copies the fixture's
`circus-process-tree-fixture.dll` plus its
`circus-process-tree-fixture.runtimeconfig.json` into the test's output
directory. The path is then resolvable via `AppContext.BaseDirectory`.

`tests/Circus.Tooling.Tests.fsproj` and `Circus.sln` are the only
non-fixture files touched by this slice.

### 3. Test refactor

`tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/BoundedProcessTests.fs`
is rewritten to:

* remove every FSI-script-generation helper (`createAndSmoke`,
  `createRawStdoutFixture`, `createRawStderrFixture`,
  `createRawBothFixture`, `createSleepFixture`, `createExitFixture`,
  `createEchoArgsFixture`, `createWorkingDirFixture`);
* remove the `smokeTestFixture` step that previously exercised
  `dotnet fsi --exec <script> --smoke` before the real test;
* add a single `runFixture` helper that resolves the precompiled
  fixture path and launches `dotnet <fixture.dll> <mode> ...`;
* update each of the 21 real-process tests (1-15, 16-21) to use the
  new pattern;
* update test 34 (sequential real-process invocations) to use the
  precompiled fixture.

Test 16 (pre-cancelled token) is preserved verbatim: it constructs
its own `BoundedProcessRequest` and asserts the public API never
launches the fixture.

### 4. Registration-callback race regression (test 36)

A new test 36 is appended to the existing seam-based regressions. It
injects a custom `CancellationTokenRegistration` whose callback:

1. signals that it started (`callbackStarted.TrySetResult(true)`);
2. blocks on a release task (`releaseCallback.Task.GetAwaiter().GetResult()`);
3. then returns.

The lifecycle is run with this blocking registration as `tReg`. The
test asserts:

* the callback signals started (the test thread awaits
  `callbackStarted.Task` before running the lifecycle);
* `c.Finalization.IsCompleted` is `false` after the lifecycle returns
  (the finalizer is awaiting the callback's `DisposeAsync()`);
* the test thread is still responsive (`Task.Delay(100)` does not
  observe the finalizer completing);
* releasing the callback (`releaseCallback.SetResult(true)`) lets the
  finalizer complete;
* the seam's `Dispose` callback runs exactly once.

This directly exercises the strongest remaining registration-callback
hazard the previous slice left un-proven.

### 5. Tests 1-35 are unchanged in semantics

Tests 22-35 (seam-based regressions for faulty exit, cancelled exit,
timer-with-pending-exit, reader-failure cleanup, EOF-with-pending-exit,
authority races, resource ownership, disposal ordering, and
deterministic timeout/cancellation precedence) are unchanged.

## Scope isolation

The correction ACT does not modify:

* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/BoundedProcess.fs`
  (production lifecycle is c69d189 + 4da6ae7 — unchanged by this slice);
* `tools/Circus.Tooling/NoForcePush/`;
* `src/Circus.Persistence.Postgres/`;
* `tests/Circus.Persistence.Postgres.Tests/`;
* any foundation capture-extraction or normalization code;
* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs`
  (the Git adapter is still blocked by the same
  `git_adapter_allowed=false` rule).

## Acceptance criteria (summary)

| ID    | Criterion                                                                | Status |
| ----- | ------------------------------------------------------------------------ | ------ |
| CC-01 | Precompiled fixture project compiles with deterministic output            | pass   |
| CC-02 | `dotnet <fixture.dll> <mode>` handles every documented mode              | pass   |
| CC-03 | Test project references the fixture for build but not for load           | pass   |
| CC-04 | `CopyProcessTreeFixture` target copies fixture artefacts to test output  | pass   |
| CC-05 | All 21 real-process tests use the precompiled fixture                     | pass   |
| CC-06 | Test 34 uses the precompiled fixture                                     | pass   |
| CC-07 | Test 36 binary covers cancellation registration race                      | pass   |
| CC-08 | Test 18 (missing working directory) and other non-fixture tests unchanged | pass   |
| CC-09 | Production (`BoundedProcess.fs`) is unchanged in this slice              | pass   |
| CC-10 | Git adapter (`Git.fs`) is unchanged in this slice                         | pass   |

## Board state

```yaml
bounded_process:
  commit: <see git log at CORRECTION02 commit>
  build: PASS
  individual_tests: PASS_REPORTED

  registration_race_test:
    status: IN_PROGRESS
    defects:
      - CancelAsync_await_cycle: FIXED
      - release_finally_scope: EXTENDED_TO_COVER_COMPLETION_OBSERVATION

  production_async_registration_disposal:
    status: IMPLEMENTED
    note: disposeOnceAsync uses tReg.DisposeAsync().AsTask() and cReg.DisposeAsync().AsTask()

  canonical_suite:
    status: UNVERIFIED
    blocker: test36_verification_pending_build_and_run

  test36:
    cancel_async_fire_and_forget: CORRECTION18
    bounded_completion_observation: IN_PLACE
    release_guarantee: COVERS_COMPLETION_AND_FINALIZATION

  hygiene:
    fixture_lock_terminal_lf: FIXED

  authority_ready: false
  git_adapter_allowed: false

next_slice:
  name: test36_verification_and_suite_run
  test34_redesign: deferred_until_suite_terminates
```

**Correction applied**:
- Production async registration disposal was already implemented in CORRECTION17 via
  `disposeOnceAsync()` using `tReg.DisposeAsync().AsTask()` and `cReg.DisposeAsync().AsTask()`
- Test 36 had two issues fixed by CORRECTION18:
  1. `CancelAsync()` await cycle: now stores task and awaits after callback release
  2. `try/finally` scope: now covers bounded completion observation via `Task.WhenAny`
- Added bounded timeout to `completionTask` observation so synchronous disposal regression
  produces assertion failure rather than hang
- Bounded callback-start wait to prevent hang before reaching protected block
- All cleanup awaits bounded with `Task.WhenAny` to ensure test completes on all paths

## Remaining work

The canonical 36-test suite still hangs in the test runner without
producing per-test output. The first test (empty stdout) completes
in 0.14 seconds, but the suite never reaches the expected per-test
progress messages within the 3-minute gate window. The fixture in
test 34 (40 invocations of `dotnet <fixture.dll>`) is the most likely
suspect: the .NET runtime host startup cost per invocation dominates
the suite's wall clock.

The next slice must either:

1. collapse test 34 to a smaller iteration count and replace the
   `dotnet <fixture.dll>` invocation with a direct P/Invoke seam
   that exercises the same lifecycle paths, or
2. pre-launch a single fixture host and stream mode invocations
   over a child-process protocol, or
3. prove that the test runner's flush behaviour is consuming
   per-test output before the runner can print it.

The Git adapter (`tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs`)
remains blocked on `git_adapter_allowed=false` until the canonical
suite is green and the registration-callback race is stable.
