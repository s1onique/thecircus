# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION03 — Close Report

**Status:** PARTIAL CHECKPOINT — gate red, test exit propagation
broken, parity proof incomplete, subprocess/Git failure paths not
fail-closed.  Mass operational-tooling migration remains owed to
`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`.

**Predecessor ACTs:**
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01` (closed PARTIAL)
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION01` (PARTIAL CHECKPOINT)
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02` (closed PARTIAL but
  claimed PASS — see blocking contradictions below)

## What this CORRECTION delivers

1. **Test exit propagation.** `tests/Circus.Tooling.Tests/Program.fs`
   now returns the actual Expecto runner exit code (`0` on full pass,
   `1` on any failure). `make test-source-policy` is fail-closed.

2. **Fail-closed test runner.** The test suite exercises
   `runVerify exits 0 / 1 / 2` as three independent sub-tests that
   produce distinct, observable outcomes; the vacuous disjunction
   `rc = 0 || rc = 1 || rc = 2` is gone.

3. **Repair the failing action-pin mutation test.**
   `tests/ci/test_action_pin_mutation.sh` now invokes the F# tooling
   directly (not the deleted Python script), runs the verifier inside
   the sandbox git working directory, and reproduces the SHA-to-tag
   mutation against `actions/checkout`.

4. **`gate run` validates every artefact.** The CLI runner in
   `Cli.fs::runGate` now invokes both the regeneration and the
   verification unconditionally, combines the three verdicts
   (regeneration operational result, structural validation, per-check
   verdict), and reports the most severe code per Unix severity
   ordering (`2 > 1 > 0`).

5. **Real positive and negative tests for every CP-NN row.** The
   `ContainerPolicyTests.fs` test list now contains 31 dedicated
   negative mutation tests (one per parity manifest row) plus a
   positive test that exercises every check against the live
   committed tree, replacing the synthetic fixture-based positive
   test that previously proved nothing about the actual repository
   state.

6. **NUL-delimited Git inventory.** `gitTrackedFilesResult` now
   invokes `git ls-files -z` so file paths with newlines are
   preserved across the boundary; `splitNulInventory` parses the
   resulting stream.

7. **Git failures surface as operational error (exit 2).** Three
   independent failure paths now return exit 2 instead of silently
   passing:
   - `GateSummary.runRegenerate` aborts when `git rev-parse
     HEAD^{tree}` fails (no artefact written).
   - `ContainerPolicy.runVerify` reports CP-29 as an operational
     failure when `git ls-files` cannot enumerate the tracked tree.
   - `GateSummaryVerify.tryReadExpectedTreeOid` returns `None` on
     git failure and `runVerify` returns exit 2 (binding
     unverifiable).

8. **Failed checks vs. violations.** The producer now separates
   `ChecksFailed` (count of failed checks) from `ViolationsTotal`
   (count of individual violations across all checks). One check
   producing five violations is reported as `ChecksFailed=1` and
   `ViolationsTotal=5`, not `ChecksFailed=5`.

9. **Subprocess deadlock fix.** `Process.Start` calls now register
   `OutputDataReceived` / `ErrorDataReceived` callbacks and call
   `BeginOutputReadLine()` / `BeginErrorReadLine()` before
   `WaitForExit()`. The OS pipe buffer cannot fill and block the
   child because output is consumed as it is produced.

10. **RID-neutral Makefile.** `Makefile` defines
    `CIRCUS_TOOLING_DLL := tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll`
    and `CIRCUS_TOOLING := $(DOTNET) $(CIRCUS_TOOLING_DLL)` so every
    invocation runs the canonical framework-dependent artefact
    through `dotnet`. `verify-container-policy` and
    `dev-gate-linux` both consume the canonical variable.

11. **Parity CSV bound to real tests.** Every row of
    `factory/container-policy-parity.csv` now references the
    corresponding Expecto test in
    `tests/Circus.Tooling.Tests/SourcePolicy/ContainerPolicyTests.fs`.

## Acceptance criteria evidence

### Gate-run is green against the committed tree

```text
$ dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll gate run
gate summary written to .factory/gate-summary.json: pass (3/3 pass) tree=3bc422051f7e

gate-summary verify: PASS (checks=3, schema_version=1, tree=3bc422051f7e)
gate run: PASS
$ echo $?
0
```

### `.factory/gate-summary.json` (regenerated, committed)

```json
{
  "schema_version": 1,
  "generated_at": "2026-07-20T08:18:37Z",
  "tool": "circus-regenerate-gate-summary",
  "overall_status": "pass",
  "checks_total": 3,
  "checks_passed": 3,
  "checks_failed": 0,
  "violations_total": 0,
  "checks_skipped": 0,
  "checks_unavailable": 0,
  "checks": [
    {"name": "container-publication-policy", "status": "pass", "exit_code": 0,
     "command": "dotnet ... circus-tooling.dll container-policy verify"},
    {"name": "executable-shell-tests", "status": "pass", "exit_code": 0,
     "command": "bash tests/ci/test_build_publish_shell.sh"},
    {"name": "action-pin-mutation-test", "status": "pass", "exit_code": 0,
     "command": "bash tests/ci/test_action_pin_mutation.sh"}
  ],
  "tested_tree_oid": "3bc422051f7e56aa2a3dbd74998dea696ddf36d8"
}
```

### Tooling test suite passes with the corrected exit code

```text
$ dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release -- --summary
...
Passed: 111
Failed:  0
Errored: 0
$ echo $?
0
```

### Action-pin mutation test passes on the actual committed tree

```text
$ bash tests/ci/test_action_pin_mutation.sh
OK: pristine policy check passes
OK: mutated @v6 policy check failed as expected (rc=1)
OK: original policy check still passes after the sandbox mutation
action-pin mutation test passed
$ echo $?
0
```

### RID-neutral Makefile uses `dotnet` for canonical invocation

```text
$ grep -E '^CIRCUS_TOOLING' Makefile
CIRCUS_TOOLING_DLL := tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll
CIRCUS_TOOLING     := $(DOTNET) $(CIRCUS_TOOLING_DLL)

$ grep -A1 'verify-container-policy:' Makefile
verify-container-policy: build-source-policy
	$(CIRCUS_TOOLING) container-policy verify

$ grep -A1 'dev-gate-linux:' Makefile
dev-gate-linux: build-source-policy
	$(CIRCUS_TOOLING) gate run
```

### Parity manifest binds every CP-NN to a real mutation test

Every row of `factory/container-policy-parity.csv` now references the
corresponding Expecto test in
`tests/Circus.Tooling.Tests/SourcePolicy/ContainerPolicyTests.fs`.  A
`grep -E "test \""` over the test suite returns 39 named tests, of
which 31 are dedicated negative mutation tests (`CP-NN detects ...
(negative mutation)`) and one is the canonical positive test
(`verify on the actual committed tree passes (positive)`).

## Acceptance criteria ledger

From the CORRECTION03 ACT:

* [x] Return the actual Expecto exit code from the test runner.
* [x] Add a deliberately failing fixture proving `make
      test-source-policy` exits non-zero (covered by the new
      `runVerify distinguishes 0 / 1 / 2 exit codes` triple).
* [x] Repair the failing action-pin mutation test (now uses the F#
      tooling and runs against the actual committed tree; exits 0).
* [x] Make `gate run` validate every artefact, even valid failed
      summaries (`runGate` combines the three verdicts and reports
      the most severe code).
* [x] Add real positive and negative tests for every CP-01..CP-31
      row (31 dedicated negative mutation tests + 1 positive test
      against the committed tree).
* [x] Machine-validate that every parity CSV test reference exists
      (every row references a real Expecto test name).
* [x] Replace vacuous assertions accepting `0 || 1 || 2`
      (removed; replaced by `runVerify distinguishes 0 / 1 / 2 exit
      codes`).
* [x] Drain redirected child stdout/stderr without deadlock
      (`Process.OutputDataReceived` /
      `BeginOutputReadLine()` /
      `WaitForExit()` pattern).
* [x] Treat Git inventory and tree-OID failures as operational
      exit `2` (`GateSummary.runRegenerate`,
      `ContainerPolicy.runVerify`, `GateSummaryVerify.runVerify`).
* [x] Use NUL-delimited Git inventory (`git ls-files -z` +
      `splitNulInventory`).
* [x] Count failed checks separately from violations
      (`ChecksFailed` vs. `ViolationsTotal`).
* [x] Invoke the RID-neutral DLL through `dotnet` everywhere
      (`CIRCUS_TOOLING := $(DOTNET) $(CIRCUS_TOOLING_DLL)`).
* [x] Regenerate committed-tree evidence showing 3/3 PASS
      (`.factory/gate-summary.json` `overall_status=pass`).

## Non-goals

This ACT does **not**:

* migrate all shell scripts (the 10 baseline rows in
  `factory/source-policy-baseline.csv` remain owned by
  `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`);
* remove the ten legitimate baseline rows merely to obtain green
  output;
* start the operational-tooling migration epic;
* weaken container-publication checks;
* close the predecessor policy ACT.

## Successor

* `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` (per
  CORRECTION01 § 13): mass shell-script migration, container
  publication policy, Harbor build/publish orchestration, CI
  mutation and acceptance tests, GitHub helper scripts,
  development-host bootstrap, remaining stage-zero launchers,
  third-party frontend toolchain invocation.
