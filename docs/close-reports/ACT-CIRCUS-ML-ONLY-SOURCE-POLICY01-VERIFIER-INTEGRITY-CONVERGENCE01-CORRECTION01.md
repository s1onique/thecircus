# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01 — Close Report

## Verdict

**PARTIAL → MATERIAL PROGRESS (revision 5)**

Revision 5 is the convergence point for the P0-3 and P0-6 review
findings.  It does NOT claim that every defect is closed.

`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` remains BLOCKED
until the remaining open items in §Outstanding are mechanically closed.

This revision's delta against revision 4:

* **P0-3 — exception-safe ownership.**  `runCore` now executes the body
  inside an inner ``try/with`` that captures exceptions into a mutable
  ``bodyResult``, while an outer ``try/finally`` guarantees that
  drain-settlement, kill, bounded wait, and dispose complete BEFORE the
  public outcome is constructed.  The cleanup order is:
  1. ``settleDrain stdoutDrain`` (bounded to ``DrainSettleTimeout``).
  2. ``settleDrain stderrDrain`` (bounded to ``DrainSettleTimeout``).
  3. ``killTree`` (Process.Kill(entireProcessTree=true)).
  4. ``waitBounded`` (Process.WaitForExit bounded to ``CleanupTimeout``).
  5. ``disposeProc`` (honours ``InjectDisposeFailure``; skips the real
     ``Process.Dispose`` call when the hook is active so the
     catch-and-record branch is faithfully exercised).
  Five ``mutable internal`` failure-injection hooks
  (``InjectStartAsyncFailure``, ``InjectStartAsyncAccessFailure``,
  ``InjectWaitFailure``, ``InjectDrainFailure``,
  ``InjectDisposeFailure``) and one observation hook
  (``ObserveStartedPid``) are exposed so the bracket can be exercised
  by tests.  ``startAsync`` is exception-safe across the
  post-``Process.Start`` context-construction window; ``ObserveStartedPid``
  fires immediately after ``proc.Id`` so a focused test can capture the
  PID that the bracket releases.

* **P1-2 — inventory failure distinction.**  ``InventoryFailure`` gains a
  new ``GitBodyFailure of detail: string`` case.  ``fromOutcome`` now
  maps ``BodyFailure`` to ``GitBodyFailure`` (truthful) instead of
  ``GitCleanupFailure`` (which conflated body-stage exceptions with
  cleanup-stage exceptions).

* **P0-6 — evidence identity.**  ``implementation_commit_oid`` rebinds
  to this revision's implementation commit, distinct from the
  previous-revision close-report commit.  ``tested_commit_oid`` and
  ``tested_tree_oid`` identify the commit actually fully executed
  against.  ``documentation_content_base_commit_oid`` rebinds to the
  implementation commit whose tree the docs evaluate — no longer the
  earlier mixed commit.  The unresolved ``<run git rev-parse ...>``
  placeholder is removed; the previous-revision endpoint field is
  renamed to ``previous_documentation_endpoint_commit_oid``.

## Identity reconciliation

```
implementation_commit_oid               = ab4c40db21df80a14fadf8496f09309e79eb79c7
implementation_tree_oid                 = 7a651a2f14bcbd995ef6cb11864b0db4bec18640
tested_commit_oid                       = ab4c40db21df80a14fadf8496f09309e79eb79c7
tested_tree_oid                         = 7a651a2f14bcbd995ef6cb11864b0db4bec18640
evidence_endpoint_commit_oid            = ab4c40db21df80a14fadf8496f09309e79eb79c7
documentation_content_base_commit_oid   = ab4c40db21df80a14fadf8496f09309e79eb79c7
previous_documentation_endpoint_commit_oid = f117929 (revision-4 close-report commit)
```

Implementation, tested, evidence, and documentation content base are
pinned to the same commit because the implementation, build, test
compilation, and test execution were produced in a single local session.

The documentation content base is now the implementation commit
``ab4c40d`` (not the earlier mixed commit ``2eb9696``).  The
previous-revision close report lives at ``f117929`` (revision 4); its
tree is not embedded in this document to avoid the self-referential
placeholder that the review verdict flagged.

## Required fields

```
full_suite_status    = fail
tests_passed          = 150 (Circus.Tooling.Tests; revision-5 implementation)
tests_failed          = 10  (Container policy negative mutations; pre-existing P0-5 outstanding items)
tests_skipped         = 0
tests_errored         = 0
process_runner_subset = 23 of 23 passing (including six failure-injection tests)
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

The ProcessRunner-focused subset (23 tests, including the six new
failure-injection tests) passes 23/23 against the implementation
tree ``7a651a2f14bcbd995ef6cb11864b0db4bec18640``.  The full
Circus.Tooling.Tests suite runs 150/160 — the 10 failures are the
pre-existing P0-5 mutation-accounting cases that this ACT
acknowledges as outstanding.

## P0 status (revision 5)

| Defect | Status |
| --- | --- |
| P0-1 Truly async concurrent draining | **Implementation resolved**; canonical proof pending |
| P0-2 Effective cancellation | **Partial** — parent cancellation bounded via ``Process.WaitForExitAsync(ct)``; descendant-PID proof remains open |
| P0-3 Observable cleanup failures | **Partial — bracket, drain settlement, real dispose-failure path, and PID observation hook all in place; descendant-PID proof and explicit never-throw assertion remain open (see Outstanding)** |
| P0-4 Single-invocation violation accounting | **Resolved** |
| P0-5 Non-vacuous mutation registry | **Open** — registry authoritative; accounting still uses a global mutable; 13/22 cases executed against compliant baselines |
| P0-6 Evidence identity reconciliation | **Resolved** — distinct fields populated; documentation content base is the implementation commit ``ab4c40d``; previous-revision endpoint field renamed; the self-referential ``<run git rev-parse ...>`` placeholder is removed |

## P1 status (revision 5)

| Defect | Status |
| --- | --- |
| P1-1 Strict parity schema | **Partial** — quoted-only dialect, exact header order, function-name map; ``Regex("^(CP-\d+)")`` short-prefix aliasing still remains |
| P1-2 NUL diagnostic propagation | **Resolved** — ``InventoryFailure`` adds a separate ``GitBodyFailure`` case so body-stage exceptions are not misclassified as cleanup failures; only ``NulDecodeFailure`` uses ``NulInventory.renderDiagnostic`` |
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

3. **P0-5 mutation proof**: replace the global mutable accounting with
   one sequenced test that produces an immutable
   ``Map<MutationCase.Id, Result<...>>`` and derives counts from it.
   Then complete the remaining 9 mutation baselines so the
   authoritative registry reaches 22/22 mechanically.

4. **P1-1 exact parity identity**: replace
   ``Regex("^(CP-\d+)")`` with strict exact equality against the
   production rule metadata.  Also enforce that
   ``legacy_check_id`` matches ``CP-NN_<short_name>`` (no
   normalization) and that ``fsharp_check_id`` matches ``CP-NN``
   exactly.

5. **P1-3 bash-availability honesty**: replace
   ``test "skipped (bash unavailable)" { Expect.isTrue true ... }``
   with ``ptest "skipped (bash unavailable)" { ... }`` so the test is
   pending rather than passing.

6. **Canonical gate coverage**: the canonical ``gate run`` must
   execute ``make test-source-policy`` (the full mutation +
   process-runner suite).  Until then, the green 3/3 summary is
   necessary but insufficient for closure.

7. **End-to-end fresh-checkout gate regeneration**: run
   ``make dev-gate-linux`` on a clean checkout and record the
   resulting commit and tree.

## Outcome

Revision 5 mechanically closes:

* the P0-3 ownership-bracket regression,
* the exceptional drain-settlement gap,
* the post-``Process.Start`` PID proof (via ``ObserveStartedPid``),
* the real disposal-failure injection path,
* the P1-2 ``BodyFailure → GitCleanupFailure`` misclassification,
* the P0-6 evidence identity (implementation, tested, evidence,
  documentation content base all bind to ``ab4c40d``; the
  previous-revision endpoint field is renamed).

P0-2 (descendant-PID proof), P0-5 (mutation accounting), P1-1 (exact
parity identity), P1-3 (pending-test honesty), the P0-3 never-throw
assertion, and the canonical-gate coverage gap remain.  Each
outstanding item has a single concrete next step recorded above.

The current verdict is **PARTIAL → MATERIAL PROGRESS (revision 5)**.
Work continues within this correction ACT — no CORRECTION02 was
created — until every P0/P1 defect is mechanically closed and the
canonical gate exercises the full source-policy test suite.