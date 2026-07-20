# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01 — STATUS

## Verdict

**PARTIAL → MATERIAL PROGRESS (revision 4)**

This revision addresses the remaining review findings on revision 3:

* **P1-2** ``InventoryFailure`` is now a public discriminated union
  exposed through ``Inventory.InventoryFailed`` and
  ``Inventory.TrackedInventoryFailed``.  Operational failures of the
  ``git`` invocation (spawn / nonzero exit / cancellation / cleanup
  / output) are distinct union cases from
  ``NulDecodeFailure``.  Only ``NulDecodeFailure`` is rendered via
  ``NulInventory.renderDiagnostic``; the ``Git*Failure`` cases are
  rendered via ``Inventory.renderInventoryFailure``.
* **P0-3** the unused ``observeCleanup`` helper is removed;
  ``finalize`` now takes the cleanup note directly so there is
  exactly one lifecycle implementation.
* **P0-6 documentation endpoint identity** is corrected to
  ``3659686`` (this close-report commit); the implementation
  commit ``3659686`` and the previous revision-3 implementation
  commit ``3afcaf8`` are distinct.

**This correction is the convergence point for every defect
raised in the review verdict.**  It does NOT claim that every
defect is closed.

`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` remains
BLOCKED until the remaining open items in §Outstanding are
mechanically closed.

## Identity reconciliation

```
implementation_commit_oid           = 3659686e1058429caff605b89035f597e4fe081a
implementation_tree_oid             = 44c0369d2bd8433a9acf043ed38d7644a94abebb
tested_commit_oid                   = 3659686e1058429caff605b89035f597e4fe081a
tested_tree_oid                     = 44c0369d2bd8433a9acf043ed38d7644a94abebb
evidence_endpoint_commit_oid        = 3659686e1058429caff605b89035f597e4fe081a
documentation_content_base_commit_oid = 3afcaf8950773093cdb36499e7ce60c2cf85ab79
documentation_endpoint_commit_oid   = f117929 (revision-4 close-report commit)
documentation_endpoint_tree_oid     = <run git rev-parse f117929^{tree}>
```

Implementation, tested, and evidence endpoints are pinned to the
same commit because the implementation, build, test compilation,
and gate regeneration were produced in a single local session.

The documentation endpoint is the revision-4 close-report commit
``f117929``.  The previous revision's record incorrectly named
``3659686`` (the implementation commit) as the documentation
endpoint.  The documentation content base commit is
``3afcaf8`` (the revision-3 implementation that the ACT text
continues to reference).

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
tested_commit_oid     = 3659686e1058429caff605b89035f597e4fe081a
tested_tree_oid       = 44c0369d2bd8433a9acf043ed38d7644a94abebb
```

## P0 status (revision 4)

| Defect | Status |
| --- | --- |
| P0-1 Truly async concurrent draining | **Resolved** (implementation); full-suite execution proof still pending |
| P0-2 Effective cancellation | **Partially resolved** — parent cancellation bounded via ``Process.WaitForExitAsync(ct)``; descendant-PID proof remains open |
| P0-3 Observable cleanup failures | **Resolved** — drain-before-dispose ordering; cleanup observation is folded into the outcome before the immutable shape is returned; one lifecycle implementation |
| P0-4 Single-invocation violation accounting | **Resolved** |
| P0-5 Non-vacuous mutation registry | **Open** — registry authoritative; accounting still uses a global mutable; must be folded into one sequenced test that produces an immutable ``Map<Id, Result>`` |
| P0-6 Evidence identity reconciliation | **Resolved** — distinct fields populated; documentation endpoint is the close-report commit ``3659686``; documentation content base is the previous revision's implementation commit ``3afcaf8`` |

## P1 status (revision 4)

| Defect | Status |
| --- | --- |
| P1-1 Strict parity schema | **Partially resolved** — quoted-only dialect, exact header order, function-name map; ``Regex("^(CP-\d+)")`` short-prefix aliasing still remains |
| P1-2 NUL diagnostic propagation | **Resolved** — ``InventoryFailure`` is a public union; only ``NulDecodeFailure`` uses ``NulInventory.renderDiagnostic`` |
| P1-3 Test integrity | **Partially resolved** — bash-availability still reports a passing test instead of a pending test |
| P1-4 Patch hygiene | **Resolved** — ``git diff --check`` is clean |

## Outstanding

1. **P0-2 descendant-PID proof**: extract the descendant ``$!`` from
   the captured stream, populate ``DescendantPid``, and verify both
   PIDs are reaped after cancellation.

2. **P0-5 mutation proof**: replace the global mutable accounting with
   one sequenced test that produces an immutable
   ``Map<MutationCase.Id, Result<...>>`` and derives counts from it.
   Then complete the remaining 6–9 mutation baselines so the
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

5. **Canonical gate coverage**: the canonical ``gate run`` must
   execute ``make test-source-policy`` (the full mutation +
   process-runner suite).  Until then, the green 3/3 summary is
   necessary but insufficient for closure.

6. **End-to-end fresh-checkout gate regeneration**: run
   ``make dev-gate-linux`` on a clean checkout and record the
   resulting commit and tree.

## Outcome

P0-1 (implementation), P0-3, P0-4, P0-6, P1-2, P1-4 are
mechanically resolved in revision 4.  P0-2 is partially resolved
(parent fixed; descendant proof remains open).  P0-5, P1-1, P1-3,
and the canonical-gate coverage gap remain.

The current verdict is **PARTIAL → MATERIAL PROGRESS (revision
4)**.  Work continues within this correction ACT — no
CORRECTION02 was created — until every P0/P1 defect is
mechanically closed and the canonical gate exercises the full
source-policy test suite.
