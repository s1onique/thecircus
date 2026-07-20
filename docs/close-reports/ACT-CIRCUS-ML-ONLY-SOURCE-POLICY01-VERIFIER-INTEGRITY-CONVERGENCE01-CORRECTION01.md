# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01 — Close Report

## Verdict

**PARTIAL → MATERIAL PROGRESS (revision 6)**

Revision 6 adds P0-3 mechanical proof (31/31 ProcessRunner tests with exact
outcome assertions) and rebinds evidence identity.  It does NOT claim that
every defect is closed.

`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` remains BLOCKED
until the remaining open items in §Outstanding are mechanically closed.

This revision's delta against revision 5:

* **P0-3 — exception-safe ownership mechanical proof.**  ``inspectTerminal``
  checks `IsCanceled`, then `IsFaulted`, then accesses `Result` — making it
  total for all terminal task states.  Tests now require exact outcome
  classifications: drain timeout → `CleanupFailure`, inner output error →
  `OutputFailure`, faulted drain → `OutputFailure`, cancelled drain →
  `OutputFailure`.  ``Task.FromCanceled`` fixed to include `CancellationToken`
  argument.  ``TaskStatus`` assertion corrected for never-completing TCS tasks.

* **P0-6 — evidence identity rebinding.**  Implementation and evidence
  rebind to revision-6 implementation commit.  Documentation endpoint
  rebinds to revision-6 close-report commit.

## Identity reconciliation

```
implementation_commit_oid                 = 6e7b12d134ce062f10f236fb55f5fac63c01dafe
implementation_tree_oid                   = a8ad4bc81fd29a22f8dca7faf6a46ce35a0b3c00
tested_commit_oid                         = 6e7b12d134ce062f10f236fb55f5fac63c01dafe
tested_tree_oid                           = a8ad4bc81fd29a22f8dca7faf6a46ce35a0b3c00
evidence_endpoint_commit_oid              = 6e7b12d134ce062f10f236fb55f5fac63c01dafe
documentation_content_base_commit_oid     = 6e7b12d134ce062f10f236fb55f5fac63c01dafe
revision4_documentation_endpoint_commit_oid = f117929 (revision-4 close-report commit)
preceding_documentation_commit_oid        = 7434729 (revision-6 predecessor; superseded by this revision)
```

Implementation, tested, evidence, and documentation content base are
pinned to the same commit because the implementation, build, test
compilation, and test execution were produced in a single local
session.

The ``revision4_documentation_endpoint_commit_oid`` field remains for
historical traceability.  The ``preceding_documentation_commit_oid`` is the
revision-6 predecessor; this revision's documentation endpoint commit
is recorded externally in the delivery envelope, not embedded in the
document itself.

## Required fields

```
full_suite_status     = fail
tests_passed          = 153 (Circus.Tooling.Tests; revision-6 implementation)
tests_failed          = 9  (Container policy negative mutations; pre-existing P0-5 outstanding items)
tests_errored         = 1  (one mutation-accounting aggregate errored; same root cause)
tests_skipped         = 0
process_runner_subset = 31 of 31 passing (including 11 failure-injection tests:
                       - injected startAsync access failure (now expects BodyFailure,
                         NOT SpawnFailure, since Process.Start already succeeded),
                       - injected startAsync stdout-drain partial acquisition
                         (settles the partial drain with RanToCompletion),
                       - injected startAsync observer partial acquisition
                         (settles BOTH partial drains with RanToCompletion),
                       - injected wait failure on long-running child
                         (both drain tasks RanToCompletion),
                       - injected drain failure,
                       - injected DisposeProcess catch-and-record,
                       - injection reset / cross-test pollution check,
                       - ContextCleanupFailure: stdout never completes inside startAsync,
                       - exhausted deadline: stdout never completes, stderr is already terminal,
                       - terminal drain carrying inner Result.Error,
                       - faulted drain task via IsFaulted branch,
                       - cancelled drain task via IsCanceled branch)
mutation_expected     = 22
mutation_executed     = 13 (carried over from revisions 1-4)
mutation_passed       = 13
parity_expected       = 31
parity_actual         = 31
violations_total      = 0
git_diff_check        = pass (verified locally for the implementation commit range)
gate_status           = not re-run with fresh checkout on revision 6
working_tree_status   = clean (this report was committed separately)
```

The ProcessRunner-focused subset (31 tests, including 11 failure-injection
tests) passes 31/31 against the implementation tree
``a8ad4bc81fd29a22f8dca7faf6a46ce35a0b3c00``.  The full
Circus.Tooling.Tests suite runs 153/163 — the 10 non-passing entries
(9 failed + 1 errored) are the pre-existing P0-5
mutation-accounting cases that this ACT acknowledges as outstanding.

## P0 status (revision 6)

| Defect | Status |
| --- | --- |
| P0-1 Truly async concurrent draining | **Implementation resolved**; canonical proof pending |
| P0-2 Effective cancellation | **Partial** — parent cancellation bounded via ``Process.WaitForExitAsync(ct)``; descendant-PID proof remains open |
| P0-3 Observable cleanup failures | **Resolved — mechanically proven 31/31** — ``inspectTerminal`` checks `IsCanceled` then `IsFaulted` before accessing `Result`, making it total for all terminal task states; `settleDrainsSharedSafe` guarantees disposal even if settlement throws; tests require exact outcome classifications |
| P0-4 Single-invocation violation accounting | **Resolved** |
| P0-5 Non-vacuous mutation registry | **Open** — registry authoritative; accounting still uses a global mutable; 13/22 cases executed against compliant baselines |
| P0-6 Evidence identity reconciliation | **Resolved** — evidence rebinds to ``6e7b12d`` / ``a8ad4bc8``; ``preceding_documentation_commit_oid`` records the revision-6 predecessor; external delivery records the documentation endpoint |

## P1 status (revision 6)

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

P0-2 (descendant-PID proof), P0-5 (mutation accounting), P1-1
(exact parity identity), P1-3 (pending-test honesty), and the
canonical-gate coverage gap remain.  Each outstanding item has a
single concrete next step recorded above.

The current verdict is **PARTIAL → MATERIAL PROGRESS (revision 6)**.
Work continues within this correction ACT — no CORRECTION02 was
created — until every P0/P1 defect is mechanically closed and the
canonical gate exercises the full source-policy test suite.
