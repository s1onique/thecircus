# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01

**Status:** READY — P0/P1 CORRECTION REQUIRED

## Parent

`ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01`

## Predecessor verdict

The predecessor verdict (PARTIAL → PASS) is **withdrawn**.

**Current verdict: PARTIAL**

`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` remains BLOCKED.
`ACT-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-INVENTORY01` MUST NOT begin until this correction PASSES.

This correction is the convergence point for every defect raised in the review verdict.  It does NOT claim that every defect is closed.
It does not create the forbidden parent `CORRECTION08`.

## Objective

Repair the remaining executable and evidence-integrity defects:

1. process-stream concurrency and cancellation;
2. bounded process-tree cleanup;
3. authoritative violation accounting;
4. non-vacuous mutation testing;
5. strict parity validation;
6. byte-diagnostic propagation;
7. patch hygiene;
8. tested-tree and documentation identity.

## P0 requirements

### P0-1 — Truly asynchronous stream draining

The runner replaces synchronous `Stream.Read` loops with
cancellation-aware `Stream.ReadAsync(..., cancellationToken)` calls.
Both stdout and stderr drain operations are started (and yielded to
the scheduler) before the runner waits for process exit, so a child
that fills stderr while keeping stdout open can no longer deadlock.

A bounded deterministic deadlock regression test writes more than
the platform pipe capacity to stderr while keeping stdout open,
then writes substantial stdout, then exits.  The test fails
against the predecessor implementation.

### P0-2 — Effective cancellation

Cancellation interrupts:

* a silent child;
* a stdout-only child;
* a stderr-only child;
* a child producing both streams;
* a child with at least one descendant.

After cancellation the runner kills the owned process tree, waits
**boundedly** via `WaitForExit(timeout)`, classifies timeout as
`CleanupFailure`, preserves the original cancellation cause, and
verifies both the parent PID and a recorded descendant PID are gone.

Tests require `Cancelled` with successful cleanup.  They do NOT
accept `CleanupFailure` or `OutputFailure` as an equivalent success.

### P0-3 — Observable output and cleanup failures

Stream-read exceptions surface as `OutputFailure`.  Cleanup or
disposal failures remain observable in the final result through a
`CleanupNote` field on every outcome shape.

### P0-4 — Single-invocation violation accounting

The runner re-invokes `ContainerPolicy.verify` exactly once and uses
the resulting structured `ContainerPolicyReport`.  No human-text
parsing is performed to derive `violations_total`.  Operational
unavailability is distinct from zero violations.

### P0-5 — Non-vacuous mutation registry

Every mutation case is a value in one authoritative registry.  Each
case:

1. builds a target-rule-compliant baseline;
2. asserts the target rule passes on the baseline;
3. applies exactly one mutation;
4. asserts an exact expected rule identity;
5. rejects unrelated target-rule findings.

Expected, implemented, executed, passed, missing, duplicate, and
unknown counts are mechanically derived from the registry and the
executed cases.  Deleting any registered case fails completeness.

### P0-6 — Evidence identity reconciliation

Distinct fields capture implementation, tested, evidence-generation,
and documentation-only commits/trees.  A tracked document MUST NOT
claim a previous tree is its own final repository tree.

## P1 requirements

### P1-1 — Strict parity schema

The parser enforces the repository's declared restricted CSV
dialect:

* exact header order;
* reject duplicate headers;
* reject quotes inside unquoted fields;
* reject characters after a closing quote before delimiter/end;
* correctly support escaped quotes (`""` → `"`);
* reject multiline quoted fields (the dialect is single-line);
* exact identity formats (`CP-NN_short_name`);
* remove permissive short-prefix aliasing;
* validate the exact implementation function expected for every
  identity (must include the function name in
  `ContainerPolicy.fs (checkXxx)`);
* validate positive and negative test identities against
  authoritative registries;
* require `status=complete` for every active rule.

Focused negative tests cover every condition.

### P1-2 — Exact NUL diagnostic

The decoder uses the actual decoder failure index and offending
byte.  The structured parse diagnostic flows through inventory
enumeration, CP-29, human report, machine report, and gate
classification.  A decode failure is not collapsed into a generic
exit code `-1`.  Interior empty records are rejected unless the
producer contract justifies them.

### P1-3 — Test integrity

Additional tests cover cancellation-before-start, silent-process
cancellation, environment inheritance and override, large
bidirectional output above pipe capacity, descendant PID
termination, cleanup timeout, output failure, exact invalid-byte
offset, reordered parity columns, malformed quote placement,
normalization collision, wrong implementation function, wrong
positive/negative test identity.

A missing required host dependency reports skipped or unavailable
honestly, not as a passing substitute test.

### P1-4 — Patch hygiene

The parity CSV is rewritten to use LF line endings and contain no
trailing whitespace.  `git diff --check <base>..HEAD` returns zero
for the corrected range.

## Canonical acceptance

This correction is PASS only when:

* all process-runner P0 tests pass;
* all 22 mutations pass from compliant pre-mutation baselines;
* mutation accounting is mechanically derived;
* parity validation rejects every required malformed case;
* violation totals come from the structured authoritative report;
* `git diff --check` passes;
* the canonical gate passes;
* the working tree is clean;
* tested and documentation identities are noncontradictory;
* fresh evidence is generated against the corrected implementation.

## Required final report

The close report enumerates:

```
implementation_commit_oid
implementation_tree_oid
tested_commit_oid
tested_tree_oid
evidence_endpoint_commit_oid
documentation_endpoint_commit_oid
documentation_endpoint_tree_oid
tests_passed
tests_failed
tests_skipped
mutation_expected
mutation_executed
mutation_passed
parity_expected
parity_actual
violations_total
git_diff_check
gate_status
working_tree_status
```

## Verdict transition

Only after this correction reaches unconditional PASS may:

`ACT-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-INVENTORY01`

become the active next ACT.
