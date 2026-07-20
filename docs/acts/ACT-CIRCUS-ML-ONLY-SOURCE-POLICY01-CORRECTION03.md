# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION03

**Status:** PARTIAL CHECKPOINT — gate run red, test exit propagation
broken, parity proof incomplete, subprocess/Git failure paths not
fail-closed.  Mass operational-tooling migration remains owed to
`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`.

**Predecessor ACTs:**
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01` (closed PARTIAL)
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION01` (PARTIAL CHECKPOINT)
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02` (closed PARTIAL but
  claimed PASS — see blocking contradictions below)

## Blocking contradictions in CORRECTION02

### P0 — Canonical gate is red
The `.factory/gate-summary.json` produced by the tooling records
`overall_status=fail`, `checks_total=3`, `checks_passed=2`,
`checks_failed=1`, with the `action-pin-mutation-test` reporting
`status=fail`.  This directly contradicts the close report's claimed
`gate run: PASS`.  Wire compatibility is restored but the gate
itself is not green.

### P0 — Test executable always exits `0`
`tests/Circus.Tooling.Tests/Program.fs` discards the Expecto runner's
exit code (`|> ignore; 0`).  Expecto's
`runTestsInAssemblyWithCLIArgs` returns 0 on full pass and 1 when
any test fails.  `make test-source-policy` therefore reports
success even when the suite fails.

### P0 — Vacuous parity proof
The parity CSV marks all 31 rows `complete` but most rows only
reference the `writeMinimalRepo` fixture.  Dedicated negative
mutation tests exist for `CP-01`, `CP-03`, `CP-09`, `CP-13`,
`CP-22`, `CP-23` only.  The vacuous assertion
`Expect.isTrue (rc = 0 || rc = 1 || rc = 2) "exit 0 or 1"` is
trivially satisfied by every integer.

### P0 — `gate run` skips validation when a check fails
The control flow returns the regeneration exit code without
ever validating the (correctly-written) failed artefact.

### Further correctness defects
1. **Child-process deadlock risk** — `Process.WaitForExit()` is
   called without draining the redirected stdout/stderr streams.
2. **Secret scan fails open** — `gitTrackedFiles` returns `[]`
   when git fails, so CP-29 silently passes.
3. **Tree binding fails open** — `readExpectedTreeOid` returns
   `None` on git failure, `runVerify` skips the binding check.
4. **Failed-check accounting is wrong** — `ChecksFailed` counts
   violations, not failed checks.
5. **Makefile is not RID-neutral** — `CIRCUS_TOOLING` points at
   the native apphost.

## Scope

1. Return the actual Expecto exit code from the test runner.
2. Add a deliberately-failing fixture proving `make
   test-source-policy` exits non-zero.
3. Repair the failing action-pin mutation test (currently calls
   the deleted Python script).
4. Make `gate run` validate every artefact, including failed
   summaries; combine regeneration, validation and check
   verdicts.
5. Add real positive and negative tests for **every** CP-01…CP-31
   row.
6. Machine-validate that every parity CSV test reference exists.
7. Replace vacuous `0 || 1 || 2` assertions.
8. Drain redirected child stdout/stderr without deadlock
   (asynchronous reads with `WaitForExitAsync`).
9. Treat Git inventory and tree-OID failures as operational
   exit `2`, not policy pass.
10. Use NUL-delimited Git inventory.
11. Count failed checks separately from violations
    (`ViolationsTotal`).
12. Invoke the RID-neutral DLL through `dotnet` everywhere
    (Makefile).
13. Regenerate committed-tree evidence with
    `overall_status=pass`, `checks_passed=3`, `checks_failed=0`.

## Successor

* `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` (per
  CORRECTION01 § 13): mass shell-script migration.
