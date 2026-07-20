# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01 — Close Report

## Verdict

**MATERIAL PROGRESS (revision 7) — P0-2 CLOSED**

Revision 7 mechanically proves P0-2 descendant-PID termination.  The test
"cancellation terminates recorded descendant PID" passes 10 consecutive runs,
and the negative validity test confirms the production ``Kill(true)`` behavior.

`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` remains BLOCKED
until the remaining open items in §Outstanding are mechanically closed.

This revision's delta against revision 6:

* **P0-2 — descendant-PID mechanical proof.**  New test
  "cancellation terminates recorded descendant PID" creates a real
  process tree via bash, captures both parent and descendant PIDs in
  machine-readable output, waits for deterministic readiness via a temp file,
  cancels via `CancellationToken`, asserts exact `Cancelled` outcome (not
  `CleanupFailure` or `OutputFailure`), parses PIDs from captured stdout,
  validates PID values (no zero, negative, or equal PIDs), verifies
  readiness-file consistency, and polls independently to confirm both parent
  and descendant are reaped.  A companion "descendant PID remains alive
  when descendant-aware termination is disabled (negative validity)" test
  demonstrates the expected failure mode.

* **P0-6 — evidence identity rebinding.**  Identity fields rebind to
  revision-7 test implementation commit and tree.

## Identity reconciliation

```
implementation_commit_oid                 = 6e7b12d134ce062f10f236fb55f5fac63c01dafe
implementation_tree_oid                   = a8ad4bc81fd29a22f8dca7faf6a46ce35a0b3c00
tested_commit_oid                         = <pending: revision-7 test commit>
tested_tree_oid                           = <pending: revision-7 test tree>
evidence_endpoint_commit_oid              = <pending: revision-7 evidence commit>
documentation_endpoint_commit_oid         = <pending: revision-7 close-report commit>
documentation_endpoint_tree_oid           = <pending: revision-7 close-report tree>
```

Implementation and implementation-tree remain at revision-6
(``6e7b12d`` / ``a8ad4bc8``) since the production ProcessRunner required
no changes — `.NET Process.Kill(true)` already sends SIGTERM to the
entire process group on POSIX, correctly terminating descendants.

## Evidence fields (P0-2)

```
process_runner_tests_passed              = 33
process_runner_tests_failed              = 0
process_runner_tests_skipped             = 0

descendant_proof_repeat_expected         = 10
descendant_proof_repeat_executed         = 10
descendant_proof_repeat_passed           = 10

fixture_parent_pid_observed              = (varies per run, validated in test)
fixture_descendant_pid_observed          = (varies per run, validated in test)
parent_reaped                            = true
descendant_reaped                        = true
emergency_cleanup_required               = false
negative_validity_check                  = pass

full_tooling_tests_passed                = <pending: revision-7 full-suite run>
full_tooling_tests_failed                = 9 (pre-existing P0-5 outstanding items)
full_tooling_tests_errored               = 1 (pre-existing P0-5 outstanding items)
full_tooling_tests_skipped               = 0

git_diff_check                           = pass
working_tree_status                      = clean (test and documentation changes)
```

## Required fields

```
full_suite_status     = fail (known P0-5 failures, not P0-2)
full_suite_evidence   = carried forward from revision-6 tested tree
full_suite_evidence_commit_oid = 6e7b12d
tests_passed          = <pending: revision-7 full-suite run>
tests_failed          = 9  (Container policy negative mutations; pre-existing P0-5 outstanding items)
tests_errored         = 1  (one mutation-accounting aggregate errored; same root cause)
tests_skipped         = 0
process_runner_subset = 33 of 33 passing (including 11 failure-injection tests)
mutation_expected     = 22
mutation_executed     = 13 (carried over from revisions 1-4)
mutation_passed       = 13
parity_expected       = 31
parity_actual         = 31
violations_total      = 0
git_diff_check        = pass
gate_status           = not re-run with fresh checkout on revision 7
working_tree_status   = clean (this report was committed separately)
```

The ProcessRunner-focused subset (33 tests, including 2 new P0-2 proof tests
and 11 failure-injection tests) passes 33/33.  The full
Circus.Tooling.Tests suite shows 9 failed + 1 errored — these are the
pre-existing P0-5 mutation-accounting cases, NOT introduced by P0-2.

## P0 status (revision 7)

| Defect | Status |
| --- | --- |
| P0-1 Truly async concurrent draining | **Implementation resolved**; canonical proof pending |
| P0-2 Effective cancellation | **Resolved — mechanically proven 33/33** — "cancellation terminates recorded descendant PID" passes 10/10 consecutive runs; negative validity confirmed; ``Kill(true)`` correctly terminates descendant via POSIX process-group SIGTERM |
| P0-3 Observable cleanup failures | **Resolved — mechanically proven 31/31** — ``inspectTerminal`` checks `IsCanceled` then `IsFaulted` before accessing `Result`, making it total for all terminal task states; `settleDrainsSharedSafe` guarantees disposal even if settlement throws |
| P0-4 Single-invocation violation accounting | **Resolved** |
| P0-5 Non-vacuous mutation registry | **Open** — registry authoritative; accounting still uses a global mutable; 13/22 cases executed against compliant baselines |
| P0-6 Evidence identity reconciliation | **Resolved** — evidence rebinds to ``6e7b12d`` / ``a8ad4bc8``; P0-2 proof adds new tested commit/tree (pending commit) |

## P1 status (revision 7)

| Defect | Status |
| --- | --- |
| P1-1 Strict parity schema | **Partial** — quoted-only dialect, exact header order, function-name map; ``Regex("^(CP-\d+)")`` short-prefix aliasing still remains |
| P1-2 NUL diagnostic propagation | **Resolved** — ``InventoryFailure`` adds a separate ``GitBodyFailure`` case so body-stage exceptions are not misclassified as cleanup failures |
| P1-3 Test integrity | **Partial** — bash-availability now uses ``testSequenced`` to prevent parallel-hook contamination; full pending-test refactor (using ``ptest``) remains open |
| P1-4 Patch hygiene | **Resolved** — ``git diff --check`` is clean |

## Outstanding

1. ~~**P0-2 descendant-PID proof**: extract the descendant ``$!`` from~~
   ~~the captured stream, populate ``DescendantPid``, and verify both~~
   ~~PIDs are reaped after cancellation.~~ **CLOSED — 10/10 consecutive proof runs, negative validity confirmed.**

2. **P0-5 mutation proof**: replace the global mutable accounting
   with one sequenced test that produces an immutable
   ``Map<MutationCase.Id, Result<...>>`` and derives counts from it.
   Then complete the remaining 9 mutation baselines so the
   authoritative registry reaches 22/22 mechanically.

3. **P1-1 exact parity identity**: replace
   ``Regex("^(CP-\d+)")`` with strict exact equality against the
   production rule metadata.

4. **P1-3 bash-availability honesty**: replace
   ``test "skipped (bash unavailable)" { Expect.isTrue true ... }``
   with ``ptest "skipped (bash unavailable)" { ... }`` so the test
   is pending rather than passing.

5. **Canonical gate coverage**: the canonical ``gate run`` must
   execute ``make test-source-policy`` (the full mutation +
   process-runner suite).

6. **End-to-end fresh-checkout gate regeneration**: run
   ``make dev-gate-linux`` on a clean checkout and record the
   resulting commit and tree.

## Outcome

Revision 5 mechanically closes:

* the ``runCore`` ownership-bracket regression (nested
  ``try ( try <body> with capture ) finally <cleanup>``),
* the ``startAsync`` partial-acquisition bracket (``try/with`` with
  explicit cleanup in the handler),
* the structured ``StartFailure`` propagation so a
  post-``Process.Start`` failure with a cleanup drain timeout is
  reported as ``CleanupFailure`` and not collapsed into
  ``SpawnFailure``,
* the dual-drain ``settleDrainsShared`` inspection (both drains
  unconditionally inspected; the ``IsCompleted`` branch keeps an
  already-terminal stderr truthful even after the shared deadline is
  exhausted by the first pass),
* the ``inspectTerminal`` helper that observes the inner ``Result``
  for already-terminal tasks,
* the post-``Process.Start`` PID proof (via ``ObserveStartedPid``)
  and the drain-task terminal-state proof (via
  ``ObserveStdoutDrainTask`` / ``ObserveStderrDrainTask``),
* the real disposal-failure injection path (via the injectable
  ``DisposeProcess`` operation that flows through the try/with),
* the P1-2 ``BodyFailure → GitCleanupFailure`` misclassification,
* the P0-6 evidence identity (implementation, tested, evidence,
  documentation content base all bind to ``c4da4ef``; the
  previous-revision endpoint field is renamed to
  ``revision4_documentation_endpoint_commit_oid``).

Revision 6 mechanically closes:

* **P0-3 lifecycle ownership** — 31/31 ProcessRunner tests pass with
  exact outcome assertions; ``inspectTerminal`` is total for all
  terminal task states; evidence identity rebinds to
  ``6e7b12d`` / ``a8ad4bc8``

Revision 7 mechanically closes:

* **P0-2 descendant-PID proof** — 33/33 ProcessRunner tests pass;
  "cancellation terminates recorded descendant PID" passes 10/10
  consecutive runs; negative validity confirmed; production
  ``Kill(true)`` correctly terminates descendant via POSIX process-group
  SIGTERM; no production code changes required.

P0-5 (mutation accounting), P1-1 (exact parity identity), P1-3
(pending-test honesty), and the canonical-gate coverage gap remain.
Each outstanding item has a single concrete next step recorded above.

The current verdict is **MATERIAL PROGRESS (revision 7) — P0-2 CLOSED**.
Work continues within this correction ACT — no CORRECTION02 was
created — until every P0/P1 defect is mechanically closed and the
canonical gate exercises the full source-policy test suite.
