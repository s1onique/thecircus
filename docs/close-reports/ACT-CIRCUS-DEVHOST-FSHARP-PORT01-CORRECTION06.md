# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06 — Close Report

## Verdict

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06 — CLOSED**

The CORRECTION05 cold-start regression is closed. Failed and unverified
cold-start candidates are removed from the final path; failed removal is no
longer hidden behind a generic installation error and instead reports the
exact retained path. The strengthened and new tests pass from the committed
implementation tree, and the detached gate artifact is included in the
closure bundle.

## Immutable implementation boundary

| field | value |
| --- | --- |
| starting commit | `12bd4436f92059664d14a1d1224238efcda559d2` |
| starting tree OID | `e2dbce6d245abfdd4d48a44b0102770cadef0dce` |
| implementation commit / detached `HEAD` | `ef6fe7bfcedf7ef3182f0f84684efd5de7a493c8` |
| implementation tree / detached `HEAD^{tree}` | `880509bdb2e48d81c1c541ed17b6e2ce2e48b724` |
| artifact `tested_tree_oid` | `880509bdb2e48d81c1c541ed17b6e2ce2e48b724` |
| evidence source status | `present` |

This report is part of a documentation-only child commit whose parent is the
implementation commit. No self-referential closure-commit placeholder is
used; the report binds evidence to the immutable implementation commit and
tree shown above.

## Changes

* `tools/Circus.DevHost/Archives.fs`
  * adds `recoverFailure`, selected by `hadPrevious`;
  * calls it from installation-failure and verification-failure branches;
  * invokes `restoreColdStart` for cold starts;
  * emits `rollback incomplete; failed candidate retained at <absoluteFinal>`
    if the final candidate still exists after cleanup.
* `tests/Circus.DevHost.Tests/ArchivesTests.fs`
  * strengthens the effect-then-throw cold-start test to assert both final
    directory and `new.txt` absence;
  * adds a cold-start verification-failure test with the same absence
    assertions;
  * adds a failed-delete test that proves cleanup was attempted and that the
    exact retained final path is reported.
* CORRECTION05 ACT and close report
  * replace commit placeholders with the exact CORRECTION05 commit and tree;
  * record the reviewer's PARTIAL verdict and unavailable uploaded evidence
    rather than preserving the superseded CLOSED claim.
* `docs/close-reports/evidence/`
  * includes the detached gate-summary JSON and a concise machine-readable
    transcript for this correction.

## Evidence captured from the detached implementation worktree

| check | result |
| --- | --- |
| locked restore | pass |
| DevHost build | pass — 0 warnings, 0 errors |
| DevHost Expecto suite | pass — 33 total, 33 passed, 0 ignored, 0 failed, 0 errored |
| cold-start effect-then-throw final-path absence | pass |
| cold-start verification-failure final-path absence | pass |
| cold-start failed-delete path reporting | pass |
| detached gate summary | pass — 3/3 |
| `container-publication-policy` | pass, exit 0 |
| `executable-shell-tests` | pass, exit 0 |
| `action-pin-mutation-test` | pass, exit 0 |
| `tested_tree_oid == HEAD^{tree}` | pass |
| Leamas targeted digest | pass — source present, overall pass, 3 passed, 0 failed, 0 unavailable |
| detached worktree after evidence run | clean |

## Bundled evidence

* [`evidence/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06-gate-summary.json`](evidence/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06-gate-summary.json)
  is the exact detached artifact. Its SHA-256 is
  `0928cb3818f83669d0b2ae80ea2314896d03d12d49b57618e033f7d176343e24`.
* [`evidence/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06-evidence.txt`](evidence/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION06-evidence.txt)
  mechanically records commit/tree equality, artifact fields, all check
  statuses, DevHost counters, Leamas results, and worktree cleanliness.

## Qualification

This closure establishes the DevHost installer correction and its detached
evidence only. The host used for this work had no accessible Docker daemon,
so this report does **not** claim a new run of the repository's container- and
PostgreSQL-dependent native `make gate`; it does claim and bundle the
requested three-check detached gate-summary acceptance chain and DevHost
suite run against the exact implementation commit.
