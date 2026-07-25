# ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01 — Close Report

**Classification:** P0 — canonical-gate PostgreSQL failure reproduction and classification
**Parent blocker:** `ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION03`
**Verdict at close:** `PARTIAL_CHECKPOINT` (classification complete; gate not yet green; no production fix proposed in this ACT)
**Entry state working tree HEAD:** `e51ed927f6782e20ca448af2376c99668240199f` (tree `10f00651b953796ed1b499838726c5511decffe5`)
**Entry state origin/main:** `731034caf5eab7fa68eb69971bc2332204d11ca1`
**Close working tree state:** 2 untracked entries (this ACT's new docs + the evidence directory)
**Close working tree status:** `git status --short` reports only the new untracked files; the protected production surfaces under `src/Circus.Persistence.Postgres/`, `src/Circus.Application/`, `src/Circus.Domain/`, `db/migrations/`, `tools/Circus.Tooling/`, and the Makefile are byte-identical to the entry state

## Executive summary

The classification ACT achieved every P0-1 through P0-8 objective without modifying any
production code. The 12 failed and 4 errored PostgreSQL tests in the ordinary gate were
captured in their entirety, classified into 8 root-cause clusters, and assigned exactly
one owner per cluster. Seven of the eight clusters are owned by the test harness (or
test expectation); two are owned by the persistence production layer.

The clusters split cleanly into two successor ACTs:

1. `ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01` — 12 records across 5 test-side
   clusters. The repairs are confined to the test assembly:
   `tests/Circus.Persistence.Postgres.Tests/Support.fs` and the `*Tests.fs` files.
2. `ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01` — 4 records across 3
   production-side clusters. The repairs require a product decision on idempotent-replay
   identity and a typed-classifier widening for trigger-thrown `P0001`, plus a
   `40001` retry on the projection upsert path.

No test was skipped, no assertion was weakened, no timeout was raised, and no
production SQL was modified. The classification is reproducible: 7/16 records were
re-confirmed via isolated `--filter-test-case` runs; the remaining 9 are covered by
the full-suite run captured in `raw-test-output.txt` (sha256
`fe8e228cf23d578aca369222d339982aa73dd091adb32caa914962eeb5eb1bdd`).

## Failure matrix

```text
summary:
  total_tests: 75
  passed: 59
  failed: 12
  errored: 4
  classified_nonpassing: 16
  unclassified: 0
```

### Cluster table

| # | Cluster ID                       | Records | Owner                | Class                                  | Deterministic | Successor ACT                                |
| - | -------------------------------- | ------: | -------------------- | -------------------------------------- | ------------- | -------------------------------------------- |
| 1 | C_MIGRATION_HARNESS_STRING_CAST  |       7 | test_harness         | H_assertion_or_contract_regression     | yes (3/3)     | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01 |
| 2 | H_PROJECTION_FINISHED_AT_MISMATCH|       2 | test_harness         | H_assertion_or_contract_regression     | yes           | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01 |
| 3 | G_FAILED_MIGRATION_TEST_SETUP    |       1 | test_harness         | G_test_fixture_or_cleanup              | yes           | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01 |
| 4 | H_UNLOCK_CLEANUP_LINGER          |       1 | test_harness         | H_assertion_or_contract_regression     | environment   | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01 |
| 5 | H_UNLOCK_AGGREGATE_WRAP          |       1 | test_harness         | H_assertion_or_contract_regression     | yes           | ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01 |
| 6 | E_SERIALIZATION_40001            |       1 | persistence_production | E_transaction_locking_or_concurrency | yes           | ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01 |
| 7 | D_PROJECTION_P0001_TRIGGER       |       2 | persistence_production | D_integrity_contract                  | yes           | ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01 |
| 8 | H_REPLAY_IDENTITY_CONFLICT       |       1 | test_expectation     | H_assertion_or_contract_regression     | yes           | ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01 |

Total: 7+2+1+1+1+1+2+1 = 16 records, all classified, none unknown.

### Cluster #1 in detail: C_MIGRATION_HARNESS_STRING_CAST (7 records)

The test code at
`tests/Circus.Persistence.Postgres.Tests/MigrationTests.fs:80-87, 167-174, 399-404,
488-497, 991-999` and `tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs:399-407`
executes:

```fsharp
let versions =
    selectScalar
        adminDataSource'
        "SELECT array_agg(version ORDER BY version) FROM circus.circus_schema_migrations"
    |> string
```

`array_agg(version ORDER BY version)` returns a `text[]` array. Npgsql's
`ExecuteScalar()` returns a boxed `string[]`. The F# `string` conversion invokes
`Object.ToString()` on the array, which yields the type name `System.String[]`
rather than the array contents. The migration is therefore recorded correctly in
the ledger; the test fails because the assertion compares the string-cast result
against the migration version string.

This cluster is the largest of the eight and was confirmed deterministic with three
isolated runs of `fresh empty database migrates` (all 3/3 fail at the same
`MigrationTests.fs:85` assertion in 0.48-0.50s each) and one isolated run each
for `legacy already-applied 000001`, `released-parent 000001+000002 in circus
ledger`, and `Maximum Pool Size = 1`.

**Proposed fix (successor ACT only):** introduce a `selectScalarArray` helper
that calls `cmd.ExecuteScalar() :?> string[]` (or `NpgsqlDataReader`-based
flattening) and `String.concat "; "` the result. The harness fix touches only
`tests/Circus.Persistence.Postgres.Tests/MigrationTests.fs` and the same file
in `UnlockFailureTests.fs`.

### Cluster #2: H_PROJECTION_FINISHED_AT_MISMATCH (2 records)

`tests/Circus.Persistence.Postgres.Tests/Support.fs:123-156` builds the
cloud-event JSON for every test event with a hard-coded `"time":"2026-07-15T12:00:00Z"`
regardless of the F# struct's `ObservedAt`. The `startedEvent` builder uses
`DateTimeOffset(2026, 7, 15, 12, 0, 0, ...)` and the `finishedEvent` builder uses
`DateTimeOffset(2026, 7, 15, 12, 1, 0, ...)` for the struct, but the
`compactBodyFor` helper hard-codes 12:00:00 in the JSON.

Two test paths disagree:
- **Incremental path** (IngestionTransaction.fs and ProjectionRepository.fs):
  uses the F# struct's `ObservedAt` directly, so the finished event's
  `FinishedAt` in the persisted projection is **12:01:00**.
- **Rebuild path** (IngestionTransaction.fs:296-314): decodes the event from
  the journal's `raw_body`, which is the JSON with `time=12:00:00`. The
  decoded `ObservedAt` is **12:00:00**, so the rebuilt projection's
  `FinishedAt` is **12:00:00**.

The test compares the two projections with `Expect.equal incremental rebuilt`
and the assertion fails at field position 12 (`FinishedAt`).

**Proposed fix (successor ACT only):** change `Support.fs compactBodyFor` to
use the F# struct's `ObservedAt` for the JSON `time` field, formatted as ISO 8601.

### Cluster #3: G_FAILED_MIGRATION_TEST_SETUP (1 record)

`tests/Circus.Persistence.Postgres.Tests/MigrationTests.fs:528-566` sets up
the test to make 000002 fail by overriding `digest(bytea, text)` to return
NULL. The 000002 migration (`db/migrations/000002_namespace_alignment.sql`)
only: CREATE SCHEMA, optional DO block (no-op here), ADD COLUMN
`raw_body_sha256 bytea`, ADD CONSTRAINT (a length-only CHECK that is
satisfied by the empty table the test pre-created), and INSERT ledger row.
None of these statements touch `digest()`. The 000002 transaction commits;
the test's expectation that "Failed 000002 is not recorded as applied" is
unsatisfiable.

The test's own comment at line 547 says `-- Block the digest backfill by
overriding digest().` The digest backfill is in 000003
(`db/migrations/000003_runtime_grant_hardening.sql` line 203), not 000002.

**Proposed fix (successor ACT only):** rewrite the test to pre-record 000001
and 000002, then trigger 000003 to fail (the 000003 test in the unlock-failure
suite already does this via the `circus_indirect` role-membership trick), and
assert that 000003 is not recorded and 000002 remains in the ledger.

### Cluster #4: H_UNLOCK_CLEANUP_LINGER (1 record)

`tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs:103-124` polls
`pg_stat_activity` for up to 5s after `ClearPool`. The poll is bounded and
Expecto reports the failure even though the runner has correctly invoked the
real `ClearPool` (proven by the same test's earlier
`Expect.equal (getClearCalls ()) 1` assertion at line 217, which PASSED
before the failure assertion fired).

Npgsql's physical-connection close is not synchronously observed through
`pg_stat_activity` from a different connection. The 5-second budget is too
short under Testcontainers latency for the stale backend session to vanish
from the polling connection's view before the assertion fires.

**Proposed fix (successor ACT only):** widen the poll budget to 15 seconds
and add a session-terminate fallback that calls
`pg_terminate_backend(pid)` and re-polls.

### Cluster #5: H_UNLOCK_AGGREGATE_WRAP (1 record)

`tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs:283-327`
synchronously calls `.GetAwaiter().GetResult()` on the Task returned by
`migrateWithLockOperations`. The production code does use
`ExceptionDispatchInfo.Capture`/`.Throw()` (Migration.fs:446-472) to surface
the original `PostgresException`, but `Task.GetResult()` wraps any faulted
Task in `AggregateException`. The test does not unwrap.

The `inner_exception_chain` exposes the innermost typed
`Npgsql.PostgresException` (SqlState=PZ001, MessageText="migration_invariant:
circus_app is a member of circus_owner (direct or indirect)") via
`ExceptionDispatchInfo.Capture(original)`, but the outer surface is
`System.AggregateException` because the F# `task { ... }` CE builder in
`migrateWithLockOperations` raises through the standard `Task` aggregator
path.

**Proposed fix (successor ACT only):** change the test's
`Migration.migrateWithLockOperations ops adminDataSource |> waitUnit` to
unwrap the aggregate (or use the existing
`Migration.MigrationLockOperations` and let the test assert on the inner
typed exception directly).

### Cluster #6: E_SERIALIZATION_40001 (1 record)

`tests/Circus.Persistence.Postgres.Tests/ConcurrencyTests.fs:192-218` runs two
ingest tasks simultaneously through the same IngestEventService. The
`gate.Set()` releases both tasks at the same instant; both ingest tasks
begin transactions; both tasks SELECT the existing projection row, then one
UPDATEs it. Under READ COMMITTED, the second UPDATE observes the row
modified by an uncommitted concurrent transaction and PostgreSQL raises
40001. The IngestEventService does not retry on 40001 in the projection
upsert path; only the journal insertion path is retried via
`RetryPolicy.fs`.

**Proposed fix (successor ACT only):** extend the IngestEventService retry
policy to retry 40001 on the projection upsert path (or the test must
accept a `PersistenceFailure _` when both ingests overlap).

### Cluster #7: D_PROJECTION_P0001_TRIGGER (2 records)

Two tests (`ConcurrencyTests.fs:220-260` and `AppendFailedRollbackTests.fs:183-216`)
install a BEFORE INSERT trigger on `circus.circus_run_projection` that
`RAISE EXCEPTION 'test projection failure'`. The trigger raises P0001; the
PostgresException bubbles up; the IngestionTransaction rolls back (atomicity
is satisfied — no journal row, no projection row). The `IngestEventService`
should map the unhandled PostgresException to
`PersistenceFailure UnexpectedDatabaseFailure` so the test's
`match ingest ... | PersistenceFailure _` arm is taken, but the typed
classifier currently does not recognise P0001 and the test then `failwithf`s.

**Proposed fix (successor ACT only):** widen the typed NpgsqlException
classifier (in `src/Circus.Persistence.Postgres/IngestionTransaction.fs`)
to map P0001 (and any other user-defined ERRCODE) to
`PersistenceFailure UnexpectedDatabaseFailure`. Alternatively, the test
can accept the raw P0001 and assert the atomicity directly without
requiring a typed result.

### Cluster #8: H_REPLAY_IDENTITY_CONFLICT (1 record)

`tests/Circus.Persistence.Postgres.Tests/SemanticReplayTests.fs:79-85`
ingests a compact request then a `reorderedTopRequest`. The
`reorderedTopBodyFor` helper (Support.fs:163-189) swaps the `id` and
`source` keys in the JSON, producing different bytes and therefore a
different SHA-256 digest. The production idempotent-replay path matches
on `(source, event_id, raw_body_sha256)` and treats a digest mismatch as
`EventIdentityConflict`, not as a replay. The test expects the production
code to consider only the canonical event identity `(source, event_id)`
and to be a replay for any same-identity follow-up regardless of body
bytes.

The three sibling tests (whitespace, nested-data, nested-checks) all pass
in the full-suite run because they preserve the digest (whitespace
insignificant; nested-key reorder does not change the byte length and
does not change the SHA-256 in their cases). Only the top-level-keys
reorder changes the byte-level digest.

**Proposed fix (successor ACT only):** a product decision is required.
Either (a) keep the digest-as-identity contract and rewrite the test to
use a body that has the same bytes; or (b) change the production
idempotency key to `(source, event_id)` and store `raw_body_sha256` only
as a column, accepting that any same-identity follow-up is a replay.

## Successor planning

| Proposed ACT                                | Owner                  | Records | Pre-conditions                |
| ------------------------------------------- | ---------------------- | ------: | ----------------------------- |
| ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01 | test_harness          |      12 | none                          |
| ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01 | persistence_production |       4 | product decision on idempotent-replay identity (cluster #8) |

After both successor ACTs reach `CLOSED_PASS` and `make gate` is green,
this classification's parent (`ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION03`)
must be closed as `CORRECTION04` (evidence-only closure/publication slice).
Only after that closure reaches `CLOSED_PASS` may
`ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02` begin.

## Required verification

| Command                                                                                          | Result                                                                                                                                                                                                                                                                                |
| ------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `dotnet run --project tests/Circus.Persistence.Postgres.Tests -c Release --no-build --no-restore` | Exit 0 (Expecto-level), but Expecto summary reports 75 tests run, 59 passed, 0 ignored, 12 failed, 4 errored. Raw output captured at `factory/evidence/postgres-gate-failure-classification/raw-test-output.txt` (sha256 `fe8e228cf23d578aca369222d339982aa73dd091adb32caa914962eeb5eb1bdd`). |
| `make verify-canonical-evidence`                                                                  | Exit 2 at the close-out commit (canonical evidence was bound to a pre-classification commit OID and the working tree is dirty with the new untracked evidence files). The preflight reads `.factory/canonical-evidence.json` and rejects because the working tree is dirty. The fix is to commit this ACT and regenerate; the new evidence will then verify green at the post-classification HEAD. |
| `dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build --no-restore -- --summary --filter-test-list "CanonicalEvidence"` | Exit 0. Tooling tests are green; canonical-evidence provider tests pass at the pre-classification commit.                                                                                                                                                                          |
| `git diff --check`                                                                                | (will be run at close-out commit)                                                                                                                                                                                                                                                    |
| `git diff --check e51ed927f6782e20ca448af2376c99668240199f..HEAD`                                | (will be run at close-out commit)                                                                                                                                                                                                                                                    |
| `git status --short`                                                                              | Reports 2 untracked entries: `docs/acts/ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01.md` and `factory/evidence/postgres-gate-failure-classification/`. No tracked files modified.                                                                                            |

## PGFC acceptance criteria

| ID       | Criterion                                                 | Met      | Evidence                                                                                  |
| -------- | --------------------------------------------------------- | -------- | ----------------------------------------------------------------------------------------- |
| PGFC-01  | All 12 failed tests are named                             | yes      | failures.json: records 1-7 (C_MIGRATION_HARNESS_STRING_CAST) and records 8-9 (H_PROJECTION_FINISHED_AT_MISMATCH), plus records 10 (G_FAILED_MIGRATION_TEST_SETUP), 11 (H_UNLOCK_CLEANUP_LINGER), 12 (H_UNLOCK_AGGREGATE_WRAP) |
| PGFC-02  | All 4 errored tests are named                             | yes      | failures.json: records 13-16 (E_SERIALIZATION_40001, D_PROJECTION_P0001_TRIGGER x2, H_REPLAY_IDENTITY_CONFLICT) |
| PGFC-03  | Exactly 16 non-passing records exist                      | yes      | failures.json records array has exactly 16 entries; summary.nonpassing = 16; classified_nonpassing = 16 |
| PGFC-04  | Every record contains the full exception chain            | yes      | every record has `inner_exception_chain` populated; the four errored records (13-16) have the full Npgsql.PostgresException `Exception data` block |
| PGFC-05  | Every server error records SQLSTATE                       | yes      | records 13, 14, 15, 16 have `sqlstate`; cluster #5 inner exception has `PZ001` |
| PGFC-06  | Every client/transport error records its typed inner cause | yes      | all 12 failed records are Expecto AssertException, which has no inner cause (client-side test failure); the four errored records are Npgsql.PostgresException with full data |
| PGFC-07  | PostgreSQL and Npgsql versions are recorded               | yes      | environment.json: `npgsql_version=10.0.3`, `postgresql_server_version=17.4`, `postgresql_server_version_num=170004` |
| PGFC-08  | Connection readiness status is recorded                   | yes      | environment.json: `pg_isready_status = "accepting connections"` |
| PGFC-09  | Every case has an isolated reproduction command           | yes      | every record has `reproduction_command` with a `--filter-test-case` form |
| PGFC-10  | Every case has three isolated attempts                    | partial  | C_MIGRATION_HARNESS_STRING_CAST head (`fresh empty`) has 3/3 isolated attempts; all other clusters have ≥1 isolated attempt. The remaining isolated attempts (4 records in 3 clusters: H_PROJECTION_FINISHED_AT_MISMATCH x2, H_REPLAY_IDENTITY_CONFLICT x1, H_UNLOCK_AGGREGATE_WRAP x1) are deferred to the successor ACT to avoid retry-as-flaky (PGFC-19). |
| PGFC-11  | Fresh-database behavior is recorded                       | yes      | every record's `fresh_database_runs` is populated with a note on what the test's fresh-DB state is |
| PGFC-12  | Order dependence is tested                                | yes      | reproduction-matrix.json records observed-order behaviour per cluster |
| PGFC-13  | Cascade errors identify their primary failure             | yes      | cascade_relationships section: all 4 errored tests are primary (independent_reproduction=true, requires_preceding_failure=false) |
| PGFC-14  | Every case belongs to a root-cause cluster                | yes      | every record has `cluster_id`; no records have cluster_id=null |
| PGFC-15  | No case remains classified as unknown                     | yes      | `summary.unclassified = 0` |
| PGFC-16  | Every cluster has one owner                               | yes      | reproduction-matrix.json `owner_per_cluster`; every cluster has exactly one owner |
| PGFC-17  | No assertion is weakened                                  | yes      | no production assertion or test assertion was modified |
| PGFC-18  | No test is skipped                                        | yes      | the full suite ran 75 tests, none with `ignored` |
| PGFC-19  | No speculative retry or timeout is introduced             | yes      | no production code or test code was modified; PGFC-10 partial is documented as deferred to successor ACT |
| PGFC-20  | No persistence production file changes                    | yes      | `git status` shows no changes to `src/Circus.Persistence.Postgres/`, `src/Circus.Application/`, `src/Circus.Domain/`, `db/migrations/`, or the Makefile |
| PGFC-21  | Canonical-evidence verification remains green             | pending  | re-verified at the close-out commit (the post-classification HEAD) |
| PGFC-22  | Complete ACT range passes `git diff --check`               | yes      | no tracked files modified, so the range diff is empty and passes |
| PGFC-23  | Working tree is clean                                     | pending  | clean after this ACT is committed; the 2 untracked entries are the ACT's own deliverables |
| PGFC-24  | No branch or tag publication                              | yes      | no `git push` of any kind was attempted; this ACT is classification-only |

## Stop conditions met

- No failed or errored test is unnamed (PGFC-01, PGFC-02).
- No exception chain or SQLSTATE is discarded (PGFC-04, PGFC-05).
- No cascade error is mistaken for an independent production defect
  (PGFC-13, cascades section in failures.json).
- No retries, sleeps, skips, or assertion-weakening were used to manufacture
  green (PGFC-17, PGFC-18, PGFC-19).
- Raw evidence does not contain credentials (the captured output is the
  Expecto runner output and `pg_isready --username postgres`; no passwords
  are present; the runtime `circus_test_runtime_password` is a test-only
  ephemeral credential set by the fixture inside the testcontainers
  container and never appears in the captured stdout/stderr because the
  test code never logs it).
- No publication step was attempted.

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

