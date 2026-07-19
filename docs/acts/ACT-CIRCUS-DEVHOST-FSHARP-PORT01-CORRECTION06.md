# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06

## Status

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06 — CLOSED**

Cold-start installation failures are failure-atomic again. Both the
installation-failure and verification-failure branches now select recovery
from the immutable `hadPrevious` snapshot: an upgrade restores the previous
installation, while a cold start removes any candidate observable at the
final path. If that removal fails, the returned failure identifies the exact
retained-candidate path.

## Title

Restore cold-start rollback, prove final-path absence, and attach
implementation-bound detached evidence.

## Scope

This is the narrow successor to the PARTIAL verdict on CORRECTION05. It does
only the following:

1. invoke cold-start recovery whenever `hadPrevious = false`;
2. remove the final candidate after an effect-then-throw move failure;
3. remove the final candidate after installed-tree verification fails;
4. report `rollback incomplete; failed candidate retained at <absolute path>`
   if candidate deletion fails;
5. strengthen the existing cold-start move-failure test;
6. add cold-start verification-failure and delete-failure tests; and
7. bundle detached gate evidence bound to the implementation commit and tree.

Frontend CA, PostgreSQL fixtures, and API/Testcontainers remain out of scope.

## Implementation

`extractAtomicWith` now has one explicit recovery selector:

```fsharp
let recoverFailure () =
    if hadPrevious then
        let notes, previousRecovered, previousPreserved = restorePrevious ()
        notes, previousRecovered, previousPreserved
    else
        let notes = restoreColdStart ()
        notes, false, false
```

Both failure branches call `recoverFailure`. `reportOutcome` separately checks
the cold-start invariant after recovery. If `absoluteFinal` still exists, it
returns an `ExtractionFailure` whose primary label is:

```text
rollback incomplete; failed candidate retained at <absoluteFinal>
```

The existing upgrade rollback and retained-previous-copy behavior is
unchanged.

## Tests

The archive tests now prove all three cold-start outcomes directly:

| scenario | required observation |
| --- | --- |
| candidate move mutates, then throws | `Error`; final directory absent; `new.txt` absent; no `.circus-install-*` directory |
| install succeeds, verification fails | `Error`; final directory absent; `new.txt` absent; no `.circus-install-*` directory |
| verification fails and final deletion throws | `Error`; deletion attempted; exact absolute retained path reported; `new.txt` remains observable only because deletion failed |

The complete DevHost Expecto suite passes **33/33**, with 0 ignored, 0 failed,
and 0 errored.

## Commit and tree boundary

* **Starting commit (CORRECTION05):**
  `12bd4436f92059664d14a1d1224238efcda559d2`
* **Starting tree OID:**
  `e2dbce6d245abfdd4d48a44b0102770cadef0dce`
* **Implementation commit:**
  `ef6fe7bfcedf7ef3182f0f84684efd5de7a493c8`
* **Implementation tree OID / tested tree OID:**
  `880509bdb2e48d81c1c541ed17b6e2ce2e48b724`

This ACT, its close report, and the bundled evidence are committed in a
subsequent documentation-only child of the implementation commit. The
implementation identity is therefore exact without asking either document to
embed its own changing commit hash.

## Detached evidence

The evidence was captured from a clean detached worktree at the exact
implementation commit above. The closure bundle contains:

* [`../close-reports/evidence/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06-gate-summary.json`](../close-reports/evidence/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06-gate-summary.json) — the detached JSON artifact;
* [`../close-reports/evidence/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06-evidence.txt`](../close-reports/evidence/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06-evidence.txt) — a machine-readable binding and result transcript.

The transcript includes full `HEAD`, `HEAD^{tree}`, `tested_tree_oid`, source
presence, aggregate counters, all three check names/statuses, the DevHost
result, the targeted Leamas digest result, and clean detached-worktree status.
