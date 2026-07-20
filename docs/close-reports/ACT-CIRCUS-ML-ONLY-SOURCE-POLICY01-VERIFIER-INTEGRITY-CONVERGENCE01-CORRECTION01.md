# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01 — Close Report

## Verdict

**PARTIAL → MATERIAL PROGRESS (revision 5)**

Revision 5 is the convergence point for the P0-3 and P0-6 review
findings.  It does NOT claim that every defect is closed.

`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` remains BLOCKED
until the remaining open items in §Outstanding are mechanically closed.

This revision's delta against revision 4:

* **P0-3 — exception-safe ownership.**  ``runCore`` and ``startAsync``
  each use a try/finally ownership bracket.  ``runCore`` executes the
  body inside an inner ``try/with`` that captures exceptions into a
  mutable ``bodyResult``, while an outer ``try/finally`` guarantees
  that **kill → bounded wait → drain-settle → dispose** complete
  BEFORE the public outcome is constructed.  ``startAsync`` retains
  local ``partialStdout`` / ``partialStderr`` task options so any
  drain task that has been created before a subsequent acquisition
  step throws is still settled (boundedly) inside the catch branch —
  alongside the kill, bounded wait, and dispose that always run.
  ``settleDrainsShared`` returns a structured ``DrainSettleAggregate``
  (``AllDrainsTerminal`` | ``DrainTimeout`` of label).  ``runCore``
  promotes the outcome to ``CleanupFailure`` when a drain timed out,
  instead of silently appending text and returning ``BodyFailure``.
  Five new injection / observation hooks are exposed:
  * ``InjectStartAsyncStdoutDrainFailure``, ``InjectStartAsyncObserverFailure``
    — exercise the partial-acquisition window inside ``startAsync``.
  * ``ObserveStdoutDrainTask``, ``ObserveStderrDrainTask`` — publish
    drain tasks immediately after creation (BEFORE the subsequent
    injection point) so a test can capture them and assert they are
    ``RanToCompletion`` even when a downstream injection throws.

* **P1-2 — inventory failure distinction.**  ``InventoryFailure``
  gains a new ``GitBodyFailure of detail: string`` case.  ``fromOutcome``
  maps ``BodyFailure`` to ``GitBodyFailure`` (truthful) instead of
  ``GitCleanupFailure``.

* **P0-6 — evidence identity.**  ``implementation_commit_oid`` rebinds
  to this revision's implementation commit.  ``tested_commit_oid``
  and ``tested_tree_oid`` identify the commit actually fully executed
  against.  ``documentation_content_base_commit_oid`` rebinds to
  the implementation commit whose tree the docs evaluate.  The
  unresolved ``<run git rev-parse ...>`` placeholder is removed;
  the field is renamed from ``previous_documentation_endpoint_commit_oid``
  to the precise ``revision4_documentation_endpoint_commit_oid`` so
  the label is no longer ambiguous as the history grows.

## Identity reconciliation

```
implementation_commit_oid                 = e2b33677f2db2503dd730475cffde0f9f9f14808
implementation_tree_oid                   = 07c0701d0be99b8cc876382db5bdd4f19bc9c34d
tested_commit_oid                         = e2b33677f2db2503dd730475cffde0f9f9f14808
tested_tree_oid                           = 07c0701d0be99b8cc876382db5bdd4f19bc9c34d
evidence_endpoint_commit_oid              = e2b33677f2db2503dd730475cffde0f9f9f14808
documentation_content_base_commit_oid     = e2b33677f2db2503dd730475cffde0f9f9f14808
revision4_documentation_endpoint_commit_oid = f117929 (revision-4 close-report commit)
```

Implementation, tested, evidence, and documentation content base are
pinned to the same commit because the implementation, build, test
compilation, and test execution were produced in a single local
session.

The ``revision4_documentation_endpoint_commit_oid`` field is renamed
(formerly ``previous_documentation_endpoint_commit_oid``) so the
label is precise and does not become ambiguous as additional
documentation commits land in the history.

## Required fields

```
full_suite_status     = fail
tests_passed          = 153 (Circus.Tooling.Tests; revision-5 implementation)
tests_failed          = 9  (Container policy negative mutations; pre-existing P0-5 outstanding items)
tests_errored         = 1  (one mutation-accounting aggregate errored; same root cause)
tests_skipped         = 0
process_runner_subset = 26 of 26 passing (including seven failure-injection tests:
                       - injected startAsync access failure (PID proof),
                       - injected startAsync stdout-drain partial acquisition,
                       - injected startAsync observer partial acquisition,
                       - injected wait failure (long-running child, terminal-state proof),
                       - injected drain failure,
                       - injected DisposeProcess catch-and-record)
mutation_expected     = 22
mutation_executed     = 13 (carried over from revisions 1-4)
mutation_passed       = 13
parity_expected       = 31
parity_actual         = 31
violations_total      = 0
git_diff_check        = pass (verified locally for the implementation commit range)
gate_status           = not re-run with fresh checkout on revision 5
working_tree_status   = clean (this report was committed separately)
```

The ProcessRunner-focused subset (26 tests, including the seven
failure-injection tests) passes 26/26 against the implementation
tree ``07c0701d0be99b8cc876382db5bdd4f19bc9c34d``.  The full
Circus.Tooling.Tests suite runs 153/163 — the 10 non-passing entries
(9 failed + 1 errored) are the pre-existing P0-5
mutation-accounting cases that this ACT acknowledges as outstanding.

## P0 status (revision 5)

| Defect | Status |
| --- | --- |
| P0-1 Truly async concurrent draining | **Implementation resolved**; canonical proof pending |
| P0-2 Effective cancellation | **Partial** — parent cancellation bounded via ``Process.WaitForExitAsync(ct)``; descendant-PID proof remains open |
| P0-3 Observable cleanup failures | **Partial — ``runCore`` ownership bracket, kill-then-settle drain order, shared-deadline settle, real ``DisposeProcess`` injection, partial-acquisition bracket inside ``startAsync``, structured drain-timeout promotion to ``CleanupFailure``, and the PID / drain-task observation hooks are all in place; descendant-PID proof remains open (see Outstanding)** |
| P0-4 Single-invocation violation accounting | **Resolved** |
| P0-5 Non-vacuous mutation registry | **Open** — registry authoritative; accounting still uses a global mutable; 13/22 cases executed against compliant baselines |
| P0-6 Evidence identity reconciliation | **Resolved** — distinct fields populated; documentation content base is the implementation commit ``e2b3367``; the previous-revision endpoint field is renamed to ``revision4_documentation_endpoint_commit_oid``; the self-referential ``<run git rev-parse ...>`` placeholder is removed |

## P1 status (revision 5)

| Defect | Status |
| --- | --- |
| P1-1 Strict parity schema | **Partial** — quoted-only dialect, exact header order, function-name map; ``Regex("^(CP-\d+)")`` short-prefix aliasing still remains |
| P1-2 NUL diagnostic propagation | **Resolved** — ``InventoryFailure`` adds a separate ``GitBodyFailure`` case so body-stage exceptions are not misclassified as cleanup failures |
| P1-3 Test integrity | **Partial** — bash-availability now uses ``testSequenced`` to prevent parallel-hook contamination; full pending-test refactor (using ``ptest``) remains open |
| P1-4 Patch hygiene | **Resolved** — ``git diff --check`` is clean |

## Outstanding

1. **P0-2 descendant-PID proof**: extract the descendant ``$!`` from
   the captured stream, populate ``DescendantPid``, and verify both
   PIDs are reaped after cancellation.

2. **P0-3 never-throw result semantics**: ``runCore`` currently
   converts every body exception into a ``BodyFailure`` outcome, but
   the inner drain blocks (stdout and stderr) still each contain a
   ``try/with`` that swallows exceptions locally.  An explicit
   assertion that no F# exception propagates past ``runProcessText``
   / ``runProcessBytes`` (e.g. a stress test that injects an exception
   inside the inner drain await) is still missing.

3. **P0-5 mutation proof**: replace the global mutable accounting
   with one sequenced test that produces an immutable
   ``Map<MutationCase.Id, Result<...>>`` and derives counts from it.
   Then complete the remaining 9 mutation baselines so the
   authoritative registry reaches 22/22 mechanically.

4. **P1-1 exact parity identity**: replace
   ``Regex("^(CP-\d+)")`` with strict exact equality against the
   production rule metadata.

5. **P1-3 bash-availability honesty**: replace
   ``test "skipped (bash unavailable)" { Expect.isTrue true ... }``
   with ``ptest "skipped (bash unavailable)" { ... }`` so the test
   is pending rather than passing.

6. **Canonical gate coverage**: the canonical ``gate run`` must
   execute ``make test-source-policy`` (the full mutation +
   process-runner suite).

7. **End-to-end fresh-checkout gate regeneration**: run
   ``make dev-gate-linux`` on a clean checkout and record the
   resulting commit and tree.

## Outcome

Revision 5 mechanically closes:

* the ``runCore`` ownership-bracket regression,
* the partial-acquisition window inside ``startAsync``,
* the exceptional drain-settlement ordering (kill → bounded wait →
  drain-settle → dispose, with a single shared settle deadline),
* the structured drain-timeout promotion to ``CleanupFailure``,
* the post-``Process.Start`` PID proof (via ``ObserveStartedPid``)
  and the drain-task terminal-state proof (via
  ``ObserveStdoutDrainTask`` / ``ObserveStderrDrainTask``),
* the real disposal-failure injection path (via the injectable
  ``DisposeProcess`` operation that flows through the try/with),
* the P1-2 ``BodyFailure → GitCleanupFailure`` misclassification,
* the P0-6 evidence identity (implementation, tested, evidence,
  documentation content base all bind to ``e2b3367``; the
  previous-revision endpoint field is renamed to
  ``revision4_documentation_endpoint_commit_oid``).

P0-2 (descendant-PID proof), P0-5 (mutation accounting), P1-1
(exact parity identity), P1-3 (pending-test honesty), the P0-3
never-throw assertion, and the canonical-gate coverage gap remain.
Each outstanding item has a single concrete next step recorded
above.

The current verdict is **PARTIAL → MATERIAL PROGRESS (revision 5)**.
Work continues within this correction ACT — no CORRECTION02 was
created — until every P0/P1 defect is mechanically closed and the
canonical gate exercises the full source-policy test suite.