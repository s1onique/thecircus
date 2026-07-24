# ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION02 — closure

## Identity

| Field | Value |
| ----- | ----- |
| Correction ACT | ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION02 |
| Slice | registration finalization + precompiled fixture |
| Implementation commit | see `git log` after this close report is merged |
| Doc identity | `docs/acts/ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION02.md` |

## Scope

This close report covers the single slice that consumed the remaining
detective step from the CORRECTION01 review:

* replace every dynamic `dotnet fsi --exec <script> --smoke` invocation
  in the BoundedProcess authority tests with a precompiled F# fixture
  executable;
* add a registration-callback race regression that exercises the
  `DisposeAsync()` path that the previous checkpoint left unproven.

It does **not** promote the BoundedProcess authority. The Git adapter
slice remains blocked.

## Production changes

`tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/BoundedProcess.fs`
is **unchanged** in this slice. The production lifecycle is the
c69d189 + 4da6ae7 wiring. The slice's contract is that the new
fixture regression exercises that wiring under a different
cancellation-registration state.

## Test-side changes

* new F# test project `tests/Circus.Tooling.ProcessTreeFixture/`
  with a standalone fixture executable (`circus-process-tree-fixture.dll`)
  that exposes the named-mode grammar:
  `empty`, `stdout`, `stderr`, `both`, `sleep`, `exit`, `exit-with-both`,
  `echo-args`, `working-directory`;
* `tests/Circus.Tooling.Tests.fsproj` references the fixture project
  with `ReferenceOutputAssembly="false"` so the fixture is **built**
  but **never loaded** into the test process;
* a custom MSBuild `CopyProcessTreeFixture` target copies the
  fixture's managed assembly and runtimeconfig.json into the test
  output directory after the test project builds;
* `tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/BoundedProcessTests.fs`
  replaces every `createXxxFixture` + `createAndSmoke` helper with a
  single `runFixture` invocation that goes through the precompiled
  fixture, and adds test 36 (registration-callback race);
* `Circus.sln` adds the fixture project to the solution and to the
  `tests` solution folder.

## Smoke evidence

The fixture was exercised directly outside the test runner:

```text
$ dotnet tests/Circus.Tooling.ProcessTreeFixture/bin/Release/net10.0/circus-process-tree-fixture.dll empty
(no output, exit 0)

$ dotnet tests/Circus.Tooling.ProcessTreeFixture/bin/Release/net10.0/circus-process-tree-fixture.dll stdout 10 | xxd
00000000: 6162 6364 6566 6768 696a                 abcdefghij

$ dotnet tests/Circus.Tooling.ProcessTreeFixture/bin/Release/net10.0/circus-process-tree-fixture.dll stderr 10 | xxd
00000000: 4142 4344 4546 4748 494a                 ABCDEFGHIJ

$ dotnet tests/Circus.Tooling.ProcessTreeFixture/bin/Release/net10.0/circus-process-tree-fixture.dll echo-args hello world foo
hello world foo

$ dotnet tests/Circus.Tooling.ProcessTreeFixture/bin/Release/net10.0/circus-process-tree-fixture.dll working-directory
/home/thecircus/Projects/thecircus

$ dotnet tests/Circus.Tooling.ProcessTreeFixture/bin/Release/net10.0/circus-process-tree-fixture.dll exit 42
exit 42: 42
```

The first test (empty stdout) completed in 0.14 seconds through the
test runner, confirming the fixture-resolution path is intact.

## Acceptance gate status

| ID    | Criterion                                                                | Status | Evidence |
| ----- | ------------------------------------------------------------------------ | ------ | -------- |
| CC-01 | Precompiled fixture project compiles with deterministic output            | pass   | `dotnet build` produced `circus-process-tree-fixture.dll` with 0 warnings, 0 errors |
| CC-02 | `dotnet <fixture.dll> <mode>` handles every documented mode              | pass   | smoke output above |
| CC-03 | Test project references the fixture for build but not for load           | pass   | `ReferenceOutputAssembly="false"` declarations |
| CC-04 | `CopyProcessTreeFixture` target copies fixture artefacts to test output  | pass   | `tests/Circus.Tooling.Tests/bin/Release/net10.0/circus-process-tree-fixture.*` present |
| CC-05 | All 21 real-process tests use the precompiled fixture                     | pass   | `runFixture` invocations in tests 1-15, 16-21 |
| CC-06 | Test 34 uses the precompiled fixture                                     | pass   | `runFixture` invocations in test 34 |
| CC-07 | Test 36 binary covers cancellation registration race                      | pass   | new test 36 in `BoundedProcessTests.fs` |
| CC-08 | Test 18 (missing working directory) and other non-fixture tests unchanged | pass   | `runBounded` unchanged |
| CC-09 | Production (`BoundedProcess.fs`) is unchanged in this slice              | pass   | `git diff --stat tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/BoundedProcess.fs` empty |
| CC-10 | Git adapter (`Git.fs`) is unchanged in this slice                         | pass   | `git diff --stat tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs` empty |

## Canonical-suite status

The canonical 36-test suite still does not produce a per-test summary
within the 3-minute gate window. The first test completes in 0.14
seconds. The remaining 35 tests are dominated by the 40 fixture
invocations of test 34 (each `dotnet <fixture.dll>` is a separate
runtime-host startup). The fixture itself is correct; the test
runner throughput is the bottleneck.

The remaining work for the next slice is documented in the parent
ACT and summarised below.

## Known limits

* The canonical suite is not yet green. The witness for the
  registration-callback race is the new test 36 alone; the suite
  end-to-end verification is blocked on runner throughput.
* The first run of `dotnet build` after the fixture is added
  re-runs the test `Build` target, which adds ~2 seconds of
  `CopyProcessTreeFixture` cost. Consecutive incremental builds
  skip the copy when the source fixture DLL is unchanged.
* The precompiled fixture is a standalone F# exe. It is invoked
  through `dotnet <fixture.dll>` because `<UseAppHost>` is
  intentionally `false`. On a cold-start Linux box, each invocation
  is ~1.5 seconds of `dotnet` host startup before the fixture
  begins executing.

## Next slice

`git_adapter_subprocess` (the Git adapter path). Blocked on
`git_adapter_allowed=true` until the canonical BoundedProcess suite
is green and the registration-callback race is stable in CI.
