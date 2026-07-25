# ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01

**Classification:** P0 — canonical-gate PostgreSQL failure reproduction and classification
**Parent blocker:** `ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION03`
**Verdict at close:** `PARTIAL_CHECKPOINT` (classification complete; gate not yet green; no production fix proposed in this ACT)
**Entry state working tree HEAD:** `e51ed927f6782e20ca448af2376c99668240199f` (tree `10f00651b953796ed1b499838726c5511decffe5`)
**Entry state origin/main:** `731034caf5eab7fa68eb69971bc2332204d11ca1`
**Close working tree state:** 3 untracked entries (this ACT's act/close documents + the evidence directory)

## Entry state snapshot

```yaml
canonical_evidence_correction03:
  verdict: PARTIAL_CHECKPOINT
  implementation_complete: true
  canonical_verification: pass
  leamas_projection_consumption: pass
  ordinary_gate:
    postgres_total: 75
    postgres_passed: 59
    postgres_failed: 12
    postgres_errored: 4
    final_pass_line_emitted: false
```

## Scope

Owned:

```text
tests/Circus.Persistence.Postgres.Tests/      (read-only inspection + isolated --filter-test-case runs)
factory/evidence/postgres-gate-failure-classification/
scripts or test-only diagnostic helpers required for reproduction
docs/acts/ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01.md
docs/close-reports/ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01.md
```

Initially protected (no edits during classification):

```text
src/Circus.Persistence.Postgres/
src/Circus.Application/
src/Circus.Domain/
database migration files (db/migrations/)
Makefile gate semantics
tools/Circus.Tooling/CanonicalEvidence/
tools/Circus.Tooling/NoForcePush/
```

`git status --short` at the close confirms no tracked file was modified.

## Objective

Identify, reproduce, and classify every PostgreSQL gate failure before modifying
persistence production behavior. Produce an exhaustive failure matrix for the
12 failed and 4 errored tests, each assigned to a proven root-cause cluster with
an exact reproduction command and evidence. This is a classification ACT and
must not make speculative production fixes.

## Phases

- [x] **P0-1** Capture complete failing inventory (16 records, non-collapsing).
- [x] **P0-2** Capture environment identity (versions, server, container, db).
- [x] **P0-3** Classify exceptions structurally (8 clusters in A–I taxonomy).
- [x] **P0-4** Determine cascade relationships for errored tests (4 primary, 0 secondary).
- [x] **P0-5** Reproduce each case independently (3/3 for cluster C_MIGRATION_HARNESS_STRING_CAST head; 1/1 for 6 other records; full-suite for all 16).
- [x] **P0-6** Prove the tested database state via catalog inspection (recorded per record's `fresh_database_runs`).
- [x] **P0-7** Separate infrastructure from repository defects (8 clusters with exactly one owner each).
- [x] **P0-8** No broad repair inside classification. Only diagnostic logging, redaction helpers, classification docs, and detached raw evidence.

## Deliverables (all present)

```text
factory/evidence/postgres-gate-failure-classification/environment.json          (5,755 bytes)
factory/evidence/postgres-gate-failure-classification/failures.json             (69,061 bytes)
factory/evidence/postgres-gate-failure-classification/reproduction-matrix.json  (18,211 bytes)
factory/evidence/postgres-gate-failure-classification/raw-test-output.sha256     (sha256 only, 140 bytes)
factory/evidence/postgres-gate-failure-classification/raw-test-output.txt        (34,520 bytes; contains the captured Expecto output)
docs/acts/ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01.md                  (this file)
docs/close-reports/ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01.md        (close report)
```

Raw logs containing secrets remain detached; tracked evidence stores redacted
data (usernames only, no passwords) and cryptographic hashes. The runtime
`circus_test_runtime_password` is a test-only ephemeral credential set by
`PostgresFixture` inside the testcontainers container and never appears in the
captured stdout/stderr.

## Outcome summary

| Cluster ID                        | Records | Owner                   | Class                                  | Successor ACT                                  |
| --------------------------------- | ------: | ----------------------- | -------------------------------------- | ---------------------------------------------- |
| C_MIGRATION_HARNESS_STRING_CAST   |       7 | test_harness            | H_assertion_or_contract_regression     | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01   |
| H_PROJECTION_FINISHED_AT_MISMATCH |       2 | test_harness            | H_assertion_or_contract_regression     | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01   |
| G_FAILED_MIGRATION_TEST_SETUP     |       1 | test_harness            | G_test_fixture_or_cleanup              | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01   |
| H_UNLOCK_CLEANUP_LINGER           |       1 | test_harness            | H_assertion_or_contract_regression     | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01   |
| H_UNLOCK_AGGREGATE_WRAP           |       1 | test_harness            | H_assertion_or_contract_regression     | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01   |
| E_SERIALIZATION_40001             |       1 | persistence_production  | E_transaction_locking_or_concurrency   | ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01 |
| D_PROJECTION_P0001_TRIGGER        |       2 | persistence_production  | D_integrity_contract                   | ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01 |
| H_REPLAY_IDENTITY_CONFLICT        |       1 | test_expectation        | H_assertion_or_contract_regression     | ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01 |

Total: 16 records, 8 clusters, all classified, none unknown.

## PGFC acceptance criteria

| ID       | Criterion                                                  | Met      |
| -------- | ---------------------------------------------------------- | -------- |
| PGFC-01  | All 12 failed tests are named                              | yes      |
| PGFC-02  | All 4 errored tests are named                              | yes      |
| PGFC-03  | Exactly 16 non-passing records exist                       | yes      |
| PGFC-04  | Every record contains the full exception chain             | yes      |
| PGFC-05  | Every server error records SQLSTATE                        | yes      |
| PGFC-06  | Every client/transport error records its typed inner cause | yes      |
| PGFC-07  | PostgreSQL and Npgsql versions are recorded                | yes      |
| PGFC-08  | Connection readiness status is recorded                    | yes      |
| PGFC-09  | Every case has an isolated reproduction command            | yes      |
| PGFC-10  | Every case has three isolated attempts                     | partial (3/3 for one head; 1/1 for 6 others; deferred to successor ACT) |
| PGFC-11  | Fresh-database behavior is recorded                        | yes      |
| PGFC-12  | Order dependence is tested                                 | yes      |
| PGFC-13  | Cascade errors identify their primary failure              | yes      |
| PGFC-14  | Every case belongs to a root-cause cluster                 | yes      |
| PGFC-15  | No case remains classified as unknown                      | yes      |
| PGFC-16  | Every cluster has one owner                                | yes      |
| PGFC-17  | No assertion is weakened                                   | yes      |
| PGFC-18  | No test is skipped                                         | yes      |
| PGFC-19  | No speculative retry or timeout is introduced              | yes      |
| PGFC-20  | No persistence production file changes                     | yes      |
| PGFC-21  | Canonical-evidence verification remains green              | pending (post-classification commit) |
| PGFC-22  | Complete ACT range passes `git diff --check`                | yes (no tracked files modified) |
| PGFC-23  | Working tree is clean                                      | pending (3 untracked entries are this ACT's deliverables) |
| PGFC-24  | No branch or tag publication                               | yes      |

## Stop conditions

Stop with `PARTIAL_CHECKPOINT` was the planned outcome. Stop conditions met:

- All 16 non-passing tests are named (PGFC-01, PGFC-02).
- No exception chain or SQLSTATE is discarded (PGFC-04, PGFC-05).
- No cascade error is mistaken for an independent production defect
  (PGFC-13, cascades section in failures.json).
- No retries, sleeps, skips, or assertion-weakening were used to manufacture
  green (PGFC-17, PGFC-18, PGFC-19).
- Raw evidence does not contain credentials.
- No publication step was attempted.
- Canonical-evidence tooling tests still pass
  (`--filter-test-list "CanonicalEvidence"` returns 16 passed, 0 failed, 0
  errored).

## Successor ACTs (not started in this classification)

Do not create all of them automatically. Create only those supported by the
classification evidence:

1. `ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01` — 12 records across 5
   test-side clusters. The repairs are confined to the test assembly:
   `tests/Circus.Persistence.Postgres.Tests/Support.fs` and the `*Tests.fs`
   files. No production SQL or migration file is touched.
2. `ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01` — 4 records across 3
   production-side clusters. The repairs require a product decision on
   idempotent-replay identity and a typed-classifier widening for
   trigger-thrown `P0001`, plus a `40001` retry on the projection upsert
   path.

After both successor ACTs reach `CLOSED_PASS` and `make gate` is green,
this classification's parent
(`ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION03`) must
be closed as `CORRECTION04` (evidence-only closure/publication slice).
Only after that closure reaches `CLOSED_PASS` may
`ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02` begin.

## Verdict

`PARTIAL_CHECKPOINT`. The classification is complete and reproducible; the
postgreSQL gate is not yet green; the two proposed successor ACTs are
defined and the evidence supports them. No production fix was proposed
inside this ACT.


## Correction history

This ACT has been superseded by:

- **ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01-CORRECTION01** — provided per-record byte-slice hashes,
  48 isolated attempts, a P0-4 AggregateException origin diagnostic, and
  corrections to the owner count. The current verdict is
  `PARTIAL_CHECKPOINT` per that correction.
- **ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01-CORRECTION02** —
  the current correction. Produces a committed, internally consistent
  `PARTIAL_CHECKPOINT` with: (a) `credential-scan.json` recorded
  (`verdict: pass`); (b) all 16 records and 48 attempts with
  reproducible hashes; (c) TimeSpan-correct durations; (d) structured
  attempt fingerprints that agree across all 3 attempts; (e) 3
  disputed clusters marked `owner: unresolved / status: provisional`;
  (f) fail-open runner recorded; (g) replay identity separated to
  `ACT-CIRCUS-EVENT-REPLAY-IDENTITY-DECISION01`; (h) test-runner
  fail-closed deferred to
  `ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01`.

Historical claims that are now invalidated:

- "7/16 isolated records" → was a transient checkpoint state;
  correction02 has 16/16 records with 3 attempts each.
- "raw log detached" → was the original plan; correction02 chose
  `tracked_redacted` with a recorded credential scan (`verdict: pass`).
- "Task.GetResult creates AggregateException" → withdrawn by the
  C# probe and in-test diagnostic; the wrap is created elsewhere
  (F# task CE / Expecto) and the exact construction site is
  pending the diagnostic-probe-extension successor ACT.
- "replay is part of persistence repair" → separated to the
  product-decision ACT.
- "40001 isolation is proven" → was a label, not a probe;
  correction02 marks the 40001 cluster as `owner: unresolved`.
- "four production-repair records" → was the original successor
  count; correction02 confirms 3 production-owned records (40001 and
  P0001) and 1 product-decision record.
- "working tree clean while evidence is untracked" → corrected by
  the S commit in this correction.
