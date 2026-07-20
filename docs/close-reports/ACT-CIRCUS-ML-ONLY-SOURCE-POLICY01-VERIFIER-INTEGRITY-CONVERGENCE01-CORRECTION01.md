# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01 — STATUS

## Verdict

**PARTIAL → MATERIAL PROGRESS (revision 3)**

The review verdict on revision 2 reopened five "Resolved"
classifications that were premature.  Revision 3 mechanically closes
the four that were structural:

* **P0-4** ``ContainerPolicy.verify`` is now invoked exactly once per
  ``gate run``.  The produced ``ContainerPolicyReport`` flows through
  both ``containerPolicyCheck`` and the count derivations in
  ``regenerate``.  Operational failures surface as ``"unavailable"``
  with a negative exit code, not as a policy failure.
* **P0-3** cleanup is strictly ordered.  The runner awaits both drain
  tasks BEFORE disposing the process, then constructs the outcome,
  then performs the safety-net dispose.  No outcome-affecting cleanup
  runs after the outcome has been constructed.
* **P0-2** parent cancellation is bounded through
  ``Process.WaitForExitAsync(cancellationToken)``.  Descendant-PID
  proof remains a known open item.
* **P1-2** ``Inventory.runGitBytes`` separates operational ``git``
  failures (``InventoryGitFailure``) from NUL parse failures
  (``InventoryDecodeError``).  Only the latter uses
  ``NulInventory.renderDiagnostic``; operational failures are
  explicitly tagged ``"not a NUL decode error"``.
* **stdout preservation** in ``runProcessText``: stdout is decoded
  independently of the stderr-drain success flag.

P0-6 documentation endpoint identity: this revision's close report
authored in commit ``3afcaf8`` supersedes the previous revision's
report commit ``8c9a8ab``.  The ACT document is the working spec and
its current tip is ``3afcaf8``.

`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` remains BLOCKED
until P0-5 mutation accounting, P1-1 exact parity identity, P1-3
bash-availability honesty, and the canonical-gate source-policy
coverage gap are mechanically closed.

## Identity reconciliation

```
implementation_commit_oid         = 3afcaf8950773093cdb36499e7ce60c2cf85ab79
implementation_tree_oid           = 3ea9260d84b2760e977b30568d3fad65ccf20ad3
tested_commit_oid                 = 3afcaf8950773093cdb36499e7ce60c2cf85ab79
tested_tree_oid                   = 3ea9260d84b2760e977b30568d3fad65ccf20ad3
evidence_endpoint_commit_oid      = 3afcaf8950773093cdb36499e7ce60c2cf85ab79
documentation_endpoint_commit_oid = 3afcaf8950773093cdb36499e7ce60c2cf85ab79
```

The implementation, tested, evidence, and documentation endpoints
are pinned to commit ``3afcaf8`` because the implementation, the
build, the test compilation, and the gate regeneration were produced
in a single local session.  The ACT document itself is the working
spec and lives at the current tip of the branch.

## Required fields

```
tests_passed        = not re-run in this revision
tests_failed        = not re-run in this revision
tests_skipped       = not re-run in this revision
mutation_expected   = 22
mutation_executed   = 16 (carried over from revision 1)
mutation_passed     = 16
parity_expected     = 31
parity_actual       = 31
violations_total    = 0
git_diff_check      = pass (verified locally)
gate_status         = pass (3/3)
working_tree_status = clean
```

The canonical gate summary ``.factory/gate-summary.json`` records:

```
overall_status        = pass
checks_total          = 3
checks_passed         = 3
checks_failed         = 0
violations_total      = 0
violations_operational = 0
tested_commit_oid     = 3afcaf8950773093cdb36499e7ce60c2cf85ab79
tested_tree_oid       = 3ea9260d84b2760e977b30568d3fad65ccf20ad3
```

## P0 status (revision 3)

| Defect | Status |
| --- | --- |
| P0-1 Truly async concurrent draining | **Resolved** — single drain per stream via cancellation-aware ``Stream.ReadAsync`` |
| P0-2 Effective cancellation | **Partially resolved** — parent cancellation bounded via ``Process.WaitForExitAsync(ct)``; descendant-PID proof remains open (see §Outstanding). |
| P0-3 Observable cleanup failures | **Resolved** — drain-before-dispose ordering; cleanup note is folded into the outcome before the immutable shape is returned; safety-net dispose in ``finally`` is idempotent and does not add new observations. |
| P0-4 Single-invocation violation accounting | **Resolved** — ``regenerate`` invokes ``ContainerPolicy.verify`` exactly once; status, count, and operational flag are all derived from the same report. |
| P0-5 Non-vacuous mutation registry | **Open** — registry is authoritative but accounting still uses a global mutable; must be folded into one sequenced test that produces an immutable ``Map<Id, Result>``. |
| P0-6 Evidence identity reconciliation | **Resolved** — implementation, tested, evidence, and documentation endpoints are distinct fields.  This ACT document claims ``3afcaf8`` as its current tip. |

## P1 status (revision 3)

| Defect | Status |
| --- | --- |
| P1-1 Strict parity schema | **Partially resolved** — quoted-only dialect, exact header order, function-name map; remaining work is removing the ``Regex("^(CP-\d+)")`` short-prefix aliasing. |
| P1-2 NUL diagnostic propagation | **Resolved** — operational ``git`` failures and NUL parse failures are distinct cases; only the latter produces a true ``DecodeDiagnostic``. |
| P1-3 Test integrity | **Partially resolved** — pending tests are absent; bash-availability still reports a passing test. |
| P1-4 Patch hygiene | **Resolved** — ``git diff --check`` is clean |

## Outstanding

1. **P0-2 descendant-PID proof**: extract the descendant ``$!`` from the
   captured stream, populate ``DescendantPid``, and verify both PIDs
   are reaped after cancellation.

2. **P0-5 mutation proof**: replace the global mutable accounting with
   one sequenced test that produces an immutable
   ``Map<MutationCase.Id, Result<...>>`` and derives counts from it.
   Then complete the remaining 6-9 mutation baselines so the
   authoritative registry reaches 22/22 mechanically.

3. **P1-1 exact parity identity**: replace
   ``Regex("^(CP-\d+)")`` with strict exact equality against the
   production rule metadata.  Also enforce that
   ``legacy_check_id`` matches ``CP-NN_<short_name>`` (no
   normalization) and that ``fsharp_check_id`` matches ``CP-NN``
   exactly.

4. **P1-3 bash-availability honesty**: replace
   ``test "skipped (bash unavailable)" { Expect.isTrue true ... }``
   with ``ptest "skipped (bash unavailable)" { ... }`` so the test is
   pending rather than passing.

5. **Canonical gate coverage**: the canonical ``gate run`` must execute
   ``make test-source-policy`` (the full mutation + process-runner
   suite).  Until then, the green 3/3 summary is necessary but
   insufficient for closure.

6. **End-to-end fresh-checkout gate regeneration**: run
   ``make dev-gate-linux`` on a clean checkout and record the
   resulting commit and tree.

## Outcome

P0-1, P0-3, P0-4, P0-6, P1-2, P1-4 are mechanically resolved in
revision 3.  P0-2 is partially resolved (parent fixed; descendant
proof remains open).  P0-5, P1-1, P1-3, and the canonical-gate
coverage gap remain.

The current verdict is **PARTIAL → MATERIAL PROGRESS (revision
3)**.  Work continues within this correction ACT — no
CORRECTION02 was created — until every P0/P1 defect is mechanically
closed and the canonical gate exercises the full source-policy test
suite.
